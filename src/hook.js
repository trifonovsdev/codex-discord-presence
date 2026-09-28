'use strict';

const http = require('http');
const path = require('path');
const { spawn } = require('child_process');
const { readConfig } = require('./config');

const HOST = '127.0.0.1';
const DAEMON = path.join(__dirname, 'daemon.js');
const CONFIG_PATH = process.env.CODEX_PRESENCE_CONFIG || path.join(__dirname, 'config.json');
const REQUEST_TIMEOUT_MS = 800;
const CLAUDE_REQUEST_TIMEOUT_MS = 400;
const RETRY_ATTEMPTS = 8;
const RETRY_DELAY_MS = 250;

// The agent spawns this script once per hook event, so the port is resolved a
// single time per process instead of on every retry.
const PORT = readConfig(CONFIG_PATH).config.port;

/** `--agent claude` marks hooks registered in Claude Code settings. */
function agentFromArguments(argv) {
  const index = argv.indexOf('--agent');
  const value = index >= 0 ? String(argv[index + 1] || '').toLowerCase() : '';
  return value === 'claude' ? 'claude' : 'codex';
}

function post(payload, timeout = REQUEST_TIMEOUT_MS) {
  return new Promise((resolve, reject) => {
    const body = Buffer.from(JSON.stringify(payload), 'utf8');
    const request = http.request({
      host: HOST,
      port: PORT,
      path: '/hook',
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'content-length': body.length,
      },
      timeout,
    }, (response) => {
      response.resume();
      response.on('end', () => (response.statusCode === 204 ? resolve() : reject(new Error(`Unexpected status ${response.statusCode}`))));
    });
    request.on('timeout', () => request.destroy(new Error('Timeout')));
    request.on('error', reject);
    request.end(body);
  });
}

function startDaemon() {
  const child = spawn(process.execPath, [DAEMON], {
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
    cwd: __dirname,
  });
  child.unref();
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {
  const agent = agentFromArguments(process.argv.slice(2));
  let raw = '';
  process.stdin.setEncoding('utf8');
  for await (const chunk of process.stdin) raw += chunk;

  const payload = raw.trim()
    ? JSON.parse(raw)
    : { hook_event_name: (agent === 'codex' && process.argv[2]) || 'UserPromptSubmit', cwd: process.cwd() };
  payload.agent = agent;

  // Claude Code waits for every hook before it continues. Deliver once and
  // never block a turn: the transcript monitor catches up on anything missed.
  if (agent === 'claude') {
    try {
      await post(payload, CLAUDE_REQUEST_TIMEOUT_MS);
    } catch {
      startDaemon();
    }
    return;
  }

  try {
    await post(payload);
    return;
  } catch {
    startDaemon();
  }

  for (let attempt = 0; attempt < RETRY_ATTEMPTS; attempt += 1) {
    await delay(RETRY_DELAY_MS);
    try {
      await post(payload);
      return;
    } catch {}
  }
}

main().catch(() => process.exit(0));
