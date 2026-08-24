'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { EventEmitter } = require('node:events');
const { StringDecoder } = require('node:string_decoder');

const { DiscordIpc } = require('./discord-ipc');

const MIN_UPDATE_INTERVAL_MS = 4200;
const ACK_TIMEOUT_MS = 12_000;
const MAX_BRIDGE_LINE_BYTES = 1024 * 1024;
const MAX_PENDING_ACKS = 32;

/**
 * Publishes Rich Presence through the Discord Social SDK bridge hosted by the
 * WinUI executable. The bridge is a separate process so a native SDK failure
 * cannot take down the tray application or the activity monitor.
 */
class DiscordSocial extends EventEmitter {
  constructor({
    clientId,
    bridgePath,
    log = () => {},
    spawnProcess = spawn,
    minUpdateIntervalMs = MIN_UPDATE_INTERVAL_MS,
    ackTimeoutMs = ACK_TIMEOUT_MS,
  }) {
    super();
    this.clientId = String(clientId);
    this.bridgePath = bridgePath;
    this.log = log;
    this.spawnProcess = spawnProcess;
    this.minUpdateIntervalMs = minUpdateIntervalMs;
    this.ackTimeoutMs = Number.isFinite(ackTimeoutMs)
      ? Math.max(1, ackTimeoutMs)
      : ACK_TIMEOUT_MS;
    this.transport = 'social-sdk';

    this.child = null;
    this.ready = false;
    this.closed = false;
    this.desired = null;
    this.lastSentJson = '';
    this.lastAckedJson = '';
    this.pendingAcks = new Map();
    this.sendSequence = 0;
    this.lastAckedSequence = 0;
    this.lastSentAt = 0;
    this.lastAck = null;
    this.lastError = null;
    this.sdkVersion = null;
    this.stdoutBuffer = '';
    this.stdoutDecoder = new StringDecoder('utf8');
    this.flushTimer = null;
    this.retryTimer = null;
    this.reconnectTimer = null;
    this.ackTimer = null;
    this.attempt = 0;
  }

  get published() {
    if (!this.ready || !this.lastAck || this.desired === null) return false;
    return this.lastAckedSequence === this.sendSequence
      && this.lastAckedJson === JSON.stringify(this.desired);
  }

  connect() {
    if (this.closed || this.child) return;
    this.#startBridge();
  }

  setActivity(activity, { immediate = false } = {}) {
    const next = activity ?? null;
    if (JSON.stringify(next) !== JSON.stringify(this.desired ?? null)) this.lastError = null;
    this.desired = next;
    if (immediate) {
      clearTimeout(this.flushTimer);
      this.flushTimer = null;
      this.#flush();
      return;
    }
    this.#scheduleFlush();
  }

  destroy() {
    if (this.closed) return;
    this.closed = true;
    clearTimeout(this.flushTimer);
    clearTimeout(this.retryTimer);
    clearTimeout(this.reconnectTimer);
    clearTimeout(this.ackTimer);
    this.flushTimer = null;
    this.retryTimer = null;
    this.reconnectTimer = null;
    this.ackTimer = null;

    const child = this.child;
    this.#resetBridgeState();
    if (!child) return;
    child.removeAllListeners();
    child.stdout?.removeAllListeners();
    child.stderr?.removeAllListeners();
    try { child.stdin?.end(); } catch {}
    try { child.kill(); } catch {}
  }

  #startBridge() {
    let child;
    try {
      child = this.spawnProcess(this.bridgePath, ['--discord-bridge', this.clientId], {
        cwd: path.dirname(this.bridgePath),
        windowsHide: true,
        stdio: ['pipe', 'pipe', 'pipe'],
      });
    } catch (error) {
      this.#bridgeFailure(`Social SDK bridge could not start: ${error.message}`);
      return;
    }

    this.child = child;
    this.stdoutBuffer = '';
    this.stdoutDecoder = new StringDecoder('utf8');
    child.stdout?.on('data', (chunk) => this.#consumeStdout(chunk));
    child.stderr?.on('data', (chunk) => {
      const message = String(chunk).trim();
      if (message) this.log(`Social SDK bridge: ${message.slice(0, 512)}`);
    });
    child.once('error', (error) => this.#bridgeFailure(`Social SDK bridge error: ${error.message}`, child));
    child.once('exit', (code, signal) => {
      if (this.child !== child) return;
      this.#resetBridgeState();
      if (this.closed) return;
      const reason = signal ? `signal ${signal}` : `code ${code ?? 'unknown'}`;
      this.log(`Social SDK bridge exited (${reason})`);
      this.emit('disconnected');
      this.#scheduleReconnect();
    });
  }

  #bridgeFailure(message, child = this.child) {
    this.lastError = message.slice(0, 512);
    this.log(this.lastError);
    this.emit('activityError', this.lastError);
    if (child && this.child === child) {
      this.#resetBridgeState();
      child.removeAllListeners();
      child.stdout?.removeAllListeners();
      child.stderr?.removeAllListeners();
      try { child.kill(); } catch {}
    }
    this.#scheduleReconnect();
  }

  #resetBridgeState() {
    this.child = null;
    this.ready = false;
    this.sdkVersion = null;
    this.stdoutBuffer = '';
    this.lastSentJson = '';
    this.lastAckedJson = '';
    this.pendingAcks.clear();
    this.sendSequence = 0;
    this.lastAckedSequence = 0;
    clearTimeout(this.flushTimer);
    clearTimeout(this.retryTimer);
    clearTimeout(this.ackTimer);
    this.flushTimer = null;
    this.retryTimer = null;
    this.ackTimer = null;
  }

  #scheduleReconnect() {
    if (this.closed || this.reconnectTimer) return;
    this.attempt = Math.min(this.attempt + 1, 6);
    const delay = Math.min(30_000, 1000 * 2 ** this.attempt) + Math.floor(Math.random() * 500);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.#startBridge();
    }, delay);
    this.reconnectTimer.unref?.();
  }

  #consumeStdout(chunk) {
    this.stdoutBuffer += this.stdoutDecoder.write(chunk);
    if (Buffer.byteLength(this.stdoutBuffer, 'utf8') > MAX_BRIDGE_LINE_BYTES) {
      this.#bridgeFailure('Social SDK bridge sent an oversized response');
      return;
    }

    const lines = this.stdoutBuffer.split(/\r?\n/);
    this.stdoutBuffer = lines.pop() ?? '';
    for (const line of lines) {
      if (!line) continue;
      let message;
      try {
        message = JSON.parse(line);
      } catch (error) {
        this.log(`Social SDK bridge JSON error: ${error.message}`);
        continue;
      }
      this.#handleMessage(message);
    }
  }

  #handleMessage(message) {
    if (message.event === 'fatal') {
      this.#bridgeFailure(String(message.message || 'Social SDK bridge failed to initialize'));
      return;
    }

    if (message.event === 'ready') {
      this.ready = true;
      this.attempt = 0;
      this.sdkVersion = typeof message.sdkVersion === 'string' ? message.sdkVersion.slice(0, 64) : null;
      this.lastSentJson = '';
      this.log(`Discord Social SDK ready${this.sdkVersion ? ` (v${this.sdkVersion})` : ''}`);
      this.emit('ready');
      this.#flush();
      return;
    }

    const id = Number(message.id);
    if (!Number.isSafeInteger(id)) return;
    const pending = this.pendingAcks.get(id);
    if (!pending) return;
    this.pendingAcks.delete(id);
    if (pending.sequence === this.sendSequence) {
      clearTimeout(this.ackTimer);
      this.ackTimer = null;
    }

    if (message.event === 'error') {
      if (pending.sequence === this.sendSequence && pending.json === JSON.stringify(this.desired ?? null)) {
        this.lastError = String(message.message || 'Discord rejected the activity update').slice(0, 512);
        this.emit('activityError', this.lastError);
        if (message.retryable === true) this.#scheduleRetry(Number(message.retryAfterMs));
      }
      this.log(`Discord Social SDK error: ${this.lastError || 'unknown error'}`);
      return;
    }

    if (message.event !== 'ack' || pending.sequence < this.lastAckedSequence) return;
    this.lastAckedSequence = pending.sequence;
    this.lastAckedJson = pending.json;
    this.lastAck = new Date().toISOString();
    if (pending.sequence === this.sendSequence) this.lastError = null;
    for (const [pendingId, queued] of this.pendingAcks) {
      if (queued.sequence <= this.lastAckedSequence) this.pendingAcks.delete(pendingId);
    }
    this.emit('ack', this.lastAck);
  }

  #scheduleRetry(requestedDelay) {
    if (this.closed || this.retryTimer) return;
    const delay = Number.isFinite(requestedDelay)
      ? Math.max(this.minUpdateIntervalMs, Math.min(30_000, requestedDelay))
      : Math.max(this.minUpdateIntervalMs, 5000);
    this.lastSentJson = '';
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.#flush();
    }, delay);
    this.retryTimer.unref?.();
  }

  #armAcknowledgementTimeout(id) {
    clearTimeout(this.ackTimer);
    this.ackTimer = setTimeout(() => {
      this.ackTimer = null;
      const pending = this.pendingAcks.get(id);
      if (!pending || pending.sequence !== this.sendSequence) return;
      this.pendingAcks.delete(id);
      if (pending.json !== JSON.stringify(this.desired ?? null)) return;

      this.lastError = 'Discord did not acknowledge the presence update in time.';
      this.lastSentJson = '';
      this.log(this.lastError);
      this.emit('activityError', this.lastError);
      this.#scheduleRetry(this.ackTimeoutMs);
    }, this.ackTimeoutMs);
    this.ackTimer.unref?.();
  }

  #scheduleFlush() {
    if (!this.ready || this.flushTimer) return;
    const wait = Math.max(0, this.minUpdateIntervalMs - (Date.now() - this.lastSentAt));
    if (wait === 0) {
      this.#flush();
      return;
    }
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null;
      this.#flush();
    }, wait);
    this.flushTimer.unref?.();
  }

  #flush() {
    if (!this.ready || !this.child?.stdin || this.child.stdin.destroyed) return;
    const json = JSON.stringify(this.desired ?? null);
    if (json === this.lastSentJson) return;
    const id = ++this.sendSequence;
    const payload = `${JSON.stringify({ id, activity: this.desired })}\n`;
    try {
      this.child.stdin.write(payload);
    } catch (error) {
      this.#bridgeFailure(`Social SDK bridge write failed: ${error.message}`);
      return;
    }
    this.pendingAcks.set(id, { json, sequence: id });
    while (this.pendingAcks.size > MAX_PENDING_ACKS) {
      this.pendingAcks.delete(this.pendingAcks.keys().next().value);
    }
    this.lastSentJson = json;
    this.lastSentAt = Date.now();
    this.#armAcknowledgementTimeout(id);
  }
}

function createDiscordPublisher({
  clientId,
  log = () => {},
  platform = process.platform,
  baseDirectory = path.resolve(__dirname, '..'),
  existsSync = fs.existsSync,
  bridgePath = process.env.CODEX_PRESENCE_SOCIAL_BRIDGE || path.join(baseDirectory, 'CodexPresence.exe'),
  sdkPath = process.env.CODEX_PRESENCE_SOCIAL_SDK || path.join(baseDirectory, 'discord_partner_sdk.dll'),
  ...options
}) {
  if (platform === 'win32' && existsSync(bridgePath) && existsSync(sdkPath)) {
    return new DiscordSocial({ clientId, bridgePath, log, ...options });
  }

  const fallback = new DiscordIpc({ clientId, log, ...options });
  fallback.transport = 'legacy-rpc';
  if (platform === 'win32') log('Discord Social SDK is unavailable; using legacy RPC fallback');
  return fallback;
}

module.exports = { DiscordSocial, createDiscordPublisher };
