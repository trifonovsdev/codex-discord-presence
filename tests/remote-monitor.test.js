'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const monitorPath = path.resolve(__dirname, '..', 'src', 'remote-monitor.py');

function runMonitor(home, threadId, { withoutFchmod = false } = {}) {
  const previousUmask = process.umask(0o022);
  try {
    const args = withoutFchmod
      ? [
        '-c',
        [
          'import os, runpy, sys',
          'monitor_path, thread_id = sys.argv[1:3]',
          'if hasattr(os, "fchmod"): del os.fchmod',
          'sys.argv = [monitor_path, thread_id]',
          'runpy.run_path(monitor_path, run_name="__main__")',
        ].join('\n'),
        monitorPath,
        threadId,
      ]
      : [monitorPath, threadId];
    return spawnSync('python3', args, {
      encoding: 'utf8',
      env: {
        ...process.env,
        HOME: home,
        USERPROFILE: home,
        CODEX_HOME: path.join(home, '.codex'),
      },
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
    assert.equal(fs.statSync(cacheDirectory).isDirectory(), true, 'the cache directory is created under the test profile');
    assert.equal(fs.statSync(cachePath).isFile(), true, 'the cache file is created under the test profile');
    if (process.platform !== 'win32') {
      assert.equal(fs.statSync(cacheDirectory).mode & 0o777, 0o700, 'cached session metadata directory is private');
      assert.equal(fs.statSync(cachePath).mode & 0o777, 0o600, 'cached paths are readable only by the remote account');
    }
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

test('cache writing works when os.fchmod is unavailable', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-no-fchmod-'));
  const threadId = '0199c000-0000-4000-8000-00000000000c';
  const sessions = path.join(home, '.codex', 'sessions', '2026', '01', '01');
  fs.mkdirSync(sessions, { recursive: true });
  fs.writeFileSync(
    path.join(sessions, `rollout-${threadId}.jsonl`),
    `${JSON.stringify({ type: 'session_meta', payload: { cwd: home } })}\n`,
  );

  try {
    const result = runMonitor(home, threadId, { withoutFchmod: true });
    assert.equal(result.status, 0, result.stderr);
    const cachePath = path.join(home, '.local', 'state', 'codex-discord-presence', `${threadId}.json`);
    assert.equal(fs.existsSync(cachePath), true, 'the cache is still written on platforms without fchmod');
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('remote monitor ignores visualization roots in a Codex context', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-remote-'));
  const threadId = '0199c000-0000-4000-8000-0000000000ab';
  const sessions = path.join(home, '.codex', 'sessions');
  fs.mkdirSync(sessions, { recursive: true });
  const records = [
    { type: 'session_meta', payload: { cwd: home } },
    { type: 'turn_context', payload: { cwd: home, workspace_roots: [home, `${home}/.codex/visualizations/2026/09/04/${threadId}`] } },
  ];
  fs.writeFileSync(path.join(sessions, `rollout-${threadId}.jsonl`), records.map(JSON.stringify).join('\n') + '\n');
  try {
    const result = runMonitor(home, threadId);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(result.stdout).project, null, 'an internal context directory is not a repository');
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});
