'use strict';

const net = require('net');
const os = require('os');
const path = require('path');
const { EventEmitter } = require('events');
const { randomUUID } = require('crypto');

const OP_HANDSHAKE = 0;
const OP_FRAME = 1;
const OP_CLOSE = 2;
const OP_PING = 3;
const OP_PONG = 4;

const PIPE_COUNT = 10;
const HEADER_BYTES = 8;
const MAX_FRAME_BYTES = 1024 * 1024;
const MAX_PENDING_ACKS = 32;
const CONNECT_TIMEOUT_MS = 2000;

// Discord accepts five SET_ACTIVITY commands per twenty seconds. Staying just
// above that keeps the card responsive without ever being rate limited.
const MIN_UPDATE_INTERVAL_MS = 4200;

/** Candidate IPC endpoints, in the order Discord itself scans them. */
function ipcCandidates(platform = process.platform) {
  const indexes = Array.from({ length: PIPE_COUNT }, (_, index) => index);
  if (platform === 'win32') return indexes.map((index) => `\\\\?\\pipe\\discord-ipc-${index}`);

  const base = process.env.XDG_RUNTIME_DIR || process.env.TMPDIR || process.env.TMP || os.tmpdir();
  return indexes.map((index) => path.join(base, `discord-ipc-${index}`));
}

function frame(op, payload) {
  const body = Buffer.from(JSON.stringify(payload), 'utf8');
  const header = Buffer.alloc(HEADER_BYTES);
  header.writeInt32LE(op, 0);
  header.writeInt32LE(body.length, 4);
  return Buffer.concat([header, body]);
}

/**
 * Minimal Discord IPC client.
 *
 * Compared to the inline implementation it replaces, this one answers PING
 * frames (Discord drops clients that stay silent), backs off exponentially
 * instead of retrying every five seconds forever, throttles activity updates
 * to Discord's documented rate limit, and never leaks half-open sockets while
 * probing the ten candidate pipes.
 */
class DiscordIpc extends EventEmitter {
  constructor({
    clientId,
    log = () => {},
    candidates = ipcCandidates(),
    minUpdateIntervalMs = MIN_UPDATE_INTERVAL_MS,
    connectTimeoutMs = CONNECT_TIMEOUT_MS,
  }) {
    super();
    this.clientId = String(clientId);
    this.log = log;
    this.candidates = candidates;
    this.minUpdateIntervalMs = minUpdateIntervalMs;
    this.connectTimeoutMs = connectTimeoutMs;
    this.transport = 'legacy-rpc';

    this.socket = null;
    this.ready = false;
    this.closed = false;
    this.buffer = Buffer.alloc(0);
    this.desired = null;
    this.lastSentJson = '';
    this.lastAckedJson = '';
    this.pendingAcks = new Map();
    this.sendSequence = 0;
    this.lastAckedSequence = 0;
    this.lastSentAt = 0;
    this.lastAck = null;
    this.lastError = null;
    this.flushTimer = null;
    this.reconnectTimer = null;
    this.attempt = 0;
  }

  /** True only when Discord acknowledged the activity that is desired now. */
  get published() {
    if (!this.ready || !this.lastAck || this.desired === null) return false;
    return this.lastAckedSequence === this.sendSequence &&
      this.lastAckedJson === JSON.stringify(this.desired);
  }

  connect() {
    if (this.closed || this.socket) return;
    this.#probe(0);
  }

  /**
   * Queues an activity (or null to clear) for delivery to Discord.
   * `immediate` bypasses the rate-limit window; it is reserved for pausing and
   * shutting down, where a delayed clear would leave a stale card behind.
   */
  setActivity(activity, { immediate = false } = {}) {
    if (JSON.stringify(activity ?? null) !== JSON.stringify(this.desired ?? null)) this.lastError = null;
    this.desired = activity;
    if (!immediate) {
      this.#scheduleFlush();
      return;
    }
    clearTimeout(this.flushTimer);
    this.flushTimer = null;
    this.#flush();
  }

  destroy() {
    this.closed = true;
    clearTimeout(this.flushTimer);
    clearTimeout(this.reconnectTimer);
    this.flushTimer = null;
    this.reconnectTimer = null;
    this.#teardown();
  }

  #probe(index) {
    if (this.closed) return;
    if (index >= this.candidates.length) {
      this.#scheduleReconnect();
      return;
    }

    const socket = net.createConnection(this.candidates[index]);
    let settled = false;

    const giveUp = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.removeAllListeners();
      socket.destroy();
      this.#probe(index + 1);
    };

    const timer = setTimeout(giveUp, this.connectTimeoutMs);

    socket.once('error', giveUp);
    socket.once('connect', () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.removeListener('error', giveUp);
      this.#adopt(socket);
    });
  }

  #adopt(socket) {
    this.socket = socket;
    this.ready = false;
    this.buffer = Buffer.alloc(0);
    socket.on('data', (chunk) => this.#consume(chunk));
    socket.on('error', (error) => this.log(`Discord socket error: ${error.message}`));
    socket.once('close', () => {
      if (this.socket !== socket) return;
      this.#teardown();
      this.log('Discord RPC disconnected');
      this.emit('disconnected');
      this.#scheduleReconnect();
    });
    this.#write(OP_HANDSHAKE, { v: 1, client_id: this.clientId });
  }

  #teardown() {
    const socket = this.socket;
    this.socket = null;
    this.ready = false;
    this.buffer = Buffer.alloc(0);
    this.lastSentJson = '';
    this.lastAckedJson = '';
    this.pendingAcks.clear();
    this.sendSequence = 0;
    this.lastAckedSequence = 0;
    this.lastError = null;
    if (!socket) return;
    socket.removeAllListeners();
    socket.destroy();
  }

  #scheduleReconnect() {
    if (this.closed || this.reconnectTimer) return;
    this.attempt = Math.min(this.attempt + 1, 6);
    const base = Math.min(30_000, 1000 * 2 ** this.attempt);
    const delay = base + Math.floor(Math.random() * 500);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.#probe(0);
    }, delay);
    this.reconnectTimer.unref?.();
  }

  #write(op, payload) {
    if (!this.socket || this.socket.destroyed) return false;
    try {
      this.socket.write(frame(op, payload));
      return true;
    } catch (error) {
      this.log(`RPC write failed: ${error.message}`);
      return false;
    }
  }

  #consume(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= HEADER_BYTES) {
      const op = this.buffer.readInt32LE(0);
      const length = this.buffer.readInt32LE(4);

      if (length < 0 || length > MAX_FRAME_BYTES) {
        this.log(`Discarding malformed Discord frame (op=${op}, length=${length})`);
        this.#teardown();
        this.#scheduleReconnect();
        return;
      }
      if (this.buffer.length < HEADER_BYTES + length) return;

      const raw = this.buffer.subarray(HEADER_BYTES, HEADER_BYTES + length).toString('utf8');
      this.buffer = this.buffer.subarray(HEADER_BYTES + length);
      this.#handleFrame(op, raw);
    }
  }

  #handleFrame(op, raw) {
    if (op === OP_PING) {
      // Discord terminates clients that do not answer its keepalive.
      try {
        this.#write(OP_PONG, JSON.parse(raw));
      } catch {
        this.#write(OP_PONG, {});
      }
      return;
    }
    if (op === OP_CLOSE) {
      this.log(`Discord closed the connection: ${raw}`);
      this.#teardown();
      this.#scheduleReconnect();
      return;
    }
    if (op !== OP_FRAME) return;

    let message;
    try {
      message = JSON.parse(raw);
    } catch (error) {
      this.log(`RPC JSON error: ${error.message}`);
      return;
    }

    if (message.evt === 'READY') {
      this.ready = true;
      this.attempt = 0;
      this.lastSentJson = '';
      this.log('Discord RPC ready');
      this.emit('ready');
      this.#scheduleFlush();
      return;
    }
    if (message.evt === 'ERROR') {
      const pending = this.pendingAcks.get(String(message.nonce || ''));
      this.pendingAcks.delete(String(message.nonce || ''));
      if (pending?.sequence === this.sendSequence && pending.json === JSON.stringify(this.desired ?? null)) {
        this.lastError = String(message.data?.message || message.message || 'Discord rejected the activity update').slice(0, 512);
        this.emit('activityError', this.lastError);
      }
      this.log(`Discord RPC error: ${raw}`);
      return;
    }
    if (message.cmd === 'SET_ACTIVITY') {
      const pending = this.pendingAcks.get(String(message.nonce || ''));
      if (!pending) return;
      this.pendingAcks.delete(String(message.nonce));
      if (pending.sequence < this.lastAckedSequence) return;

      this.lastAckedSequence = pending.sequence;
      this.lastAck = new Date().toISOString();
      this.lastAckedJson = pending.json;
      if (pending.sequence === this.sendSequence) this.lastError = null;
      for (const [nonce, queued] of this.pendingAcks) {
        if (queued.sequence <= this.lastAckedSequence) this.pendingAcks.delete(nonce);
      }
      this.emit('ack', this.lastAck);
    }
  }

  #scheduleFlush() {
    if (!this.ready || this.flushTimer) return;
    const wait = Math.max(0, this.minUpdateIntervalMs - (Date.now() - this.lastSentAt));
    this.flushTimer = setTimeout(() => {
      this.flushTimer = null;
      this.#flush();
    }, wait);
    this.flushTimer.unref?.();
  }

  #flush() {
    if (!this.ready) return;
    const json = JSON.stringify(this.desired ?? null);
    if (json === this.lastSentJson) return;
    const nonce = randomUUID();
    const sent = this.#write(OP_FRAME, {
      cmd: 'SET_ACTIVITY',
      args: { pid: process.pid, activity: this.desired },
      nonce,
    });
    if (!sent) return;
    const sequence = ++this.sendSequence;
    this.pendingAcks.set(nonce, { json, sequence });
    while (this.pendingAcks.size > MAX_PENDING_ACKS) {
      const oldestNonce = this.pendingAcks.keys().next().value;
      this.pendingAcks.delete(oldestNonce);
    }
    this.lastSentJson = json;
    this.lastSentAt = Date.now();
  }
}

module.exports = { DiscordIpc, ipcCandidates, frame, OP_HANDSHAKE, OP_FRAME, OP_CLOSE, OP_PING, OP_PONG };
