'use strict';

const http = require('http');
const path = require('path');
const { spawn } = require('child_process');

const HOST = '127.0.0.1';
const PORT = 37642;
const DAEMON = path.join(__dirname, 'daemon.js');
const CONFIG_PATH = path.join(__dirname, 'config.json');

function configuredPort() {
  try {
    const config = JSON.parse(require('fs').readFileSync(CONFIG_PATH, 'utf8'));
    const value = Number(config.port);
    return Number.isInteger(value) && value > 0 && value < 65536 ? value : PORT;
  } catch {
    return PORT;
  }
}

function post(payload) {
  return new Promise((resolve, reject) => {
    const body = Buffer.from(JSON.stringify(payload), 'utf8');
    const request = http.request({
      host: HOST,
      port: configuredPort(),
      path: '/hook',
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'content-length': body.length,
      },
      timeout: 800,
    }, (response) => {
      response.resume();
      response.on('end', () => response.statusCode === 204 ? resolve() : reject(new Error('Bad status')));
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

async function main() {
  let raw = '';
  process.stdin.setEncoding('utf8');
  for await (const chunk of process.stdin) raw += chunk;

  const payload = raw.trim()
    ? JSON.parse(raw)
    : { hook_event_name: process.argv[2] || 'UserPromptSubmit', cwd: process.cwd() };

  try {
    await post(payload);
  } catch {
    startDaemon();
    for (let attempt = 0; attempt < 8; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 250));
      try {
        await post(payload);
        return;
      } catch {}
    }
  }
}

main().catch(() => process.exit(0));
