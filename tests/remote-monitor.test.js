'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const monitorPath = path.resolve(__dirname, '..', 'src', 'remote-monitor.py');

function runMonitor(home, threadId) {
  const previousUmask = process.umask(0o022);
  try {
    return spawnSync('python3', [monitorPath, threadId], {
      encoding: 'utf8',
      env: { ...process.env, HOME: home, CODEX_HOME: path.join(home, '.codex') },
    });
  } finally {
    process.umask(previousUmask);
  }
}

test('remote home-root sessions resolve the edited repository instead of reporting root', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-remote-'));
  const threadId = '0199c000-0000-4000-8000-00000000000a';
  const sessions = path.join(home, '.codex', 'sessions', '2026', '01', '01');
  fs.mkdirSync(path.join(home, 'codex-discord-presence', '.git'), { recursive: true });
  fs.mkdirSync(sessions, { recursive: true });
  const records = [
    { type: 'session_meta', payload: { cwd: home } },
    { type: 'turn_context', payload: { cwd: home, workspace_roots: [home] } },
    {
      type: 'response_item',
      payload: {
        type: 'custom_tool_call',
        name: 'exec',
        input: '*** Update File: codex-discord-presence/src/daemon.js\n',
      },
    },
  ];
  fs.writeFileSync(
    path.join(sessions, `rollout-${threadId}.jsonl`),
    `${records.map((record) => JSON.stringify(record)).join('\n')}\n`,
  );

  try {
    const result = runMonitor(home, threadId);
    assert.equal(result.status, 0, result.stderr);
    const payload = JSON.parse(result.stdout.trim());
    assert.equal(payload.ok, true);
    assert.equal(payload.project, 'codex-discord-presence');
    assert.equal(payload.file, 'codex-discord-presence/src/daemon.js');
    const cacheDirectory = path.join(home, '.local', 'state', 'codex-discord-presence');
    const cachePath = path.join(cacheDirectory, `${threadId}.json`);
    assert.equal(fs.statSync(cacheDirectory).mode & 0o777, 0o700, 'cached session metadata directory is private');
    assert.equal(fs.statSync(cachePath).mode & 0o777, 0o600, 'cached paths are readable only by the remote account');
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('a remote account home without repository evidence stays anonymous', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-remote-home-'));
  const threadId = '0199c000-0000-4000-8000-00000000000b';
  const sessions = path.join(home, '.codex', 'sessions', '2026', '01', '01');
  fs.mkdirSync(sessions, { recursive: true });
  fs.writeFileSync(
    path.join(sessions, `rollout-${threadId}.jsonl`),
    `${JSON.stringify({ type: 'session_meta', payload: { cwd: home } })}\n`,
  );

  try {
    const result = runMonitor(home, threadId);
    assert.equal(result.status, 0, result.stderr);
    const payload = JSON.parse(result.stdout.trim());
    assert.equal(payload.ok, true);
    assert.equal(payload.project, null);
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});
