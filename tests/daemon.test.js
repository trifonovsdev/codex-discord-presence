'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawn } = require('node:child_process');
const http = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const daemonPath = path.resolve(__dirname, '..', 'src', 'daemon.js');

async function waitForHealth(port) {
  for (let attempt = 0; attempt < 40; attempt += 1) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/health`);
      if (response.ok) return response.json();
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('daemon did not become healthy');
}

const json = (port, urlPath, body, headers = {}) => fetch(`http://127.0.0.1:${port}${urlPath}`, {
  method: 'POST',
  headers: { 'content-type': 'application/json', ...headers },
  body: JSON.stringify(body),
});

/**
 * Raw request helper: `fetch` refuses to set a Host header, so rebinding
 * scenarios have to be driven through the low level client.
 */
function rawRequest(port, options) {
  return new Promise((resolve, reject) => {
    const request = http.request({ host: '127.0.0.1', port, ...options }, (response) => {
      response.resume();
      response.on('end', () => resolve(response.statusCode));
    });
    request.on('error', reject);
    request.end();
  });
}

/** Boots a daemon against a throwaway config and tears it down afterwards. */
async function withDaemon(configPatch, run) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-test-'));
  const configPath = path.join(root, 'config.json');
  const port = 38000 + Math.floor(Math.random() * 1000);
  fs.writeFileSync(configPath, JSON.stringify({ port, presenceEnabled: true, ...configPatch }));

  const child = spawn(process.execPath, [daemonPath], {
    env: { ...process.env, CODEX_HOME: root, CODEX_PRESENCE_CONFIG: configPath, CODEX_PRESENCE_TEST: '1' },
    stdio: 'ignore',
    windowsHide: true,
  });

  try {
    await run({ port, root, configPath });
  } finally {
    try {
      await json(port, '/control', { action: 'shutdown' });
    } catch {}
    await new Promise((resolve) => {
      // A daemon that ignores the shutdown request must not hang the suite.
      const kill = setTimeout(() => child.kill('SIGKILL'), 3000);
      child.once('exit', () => {
        clearTimeout(kill);
        resolve();
      });
    });
    fs.rmSync(root, { recursive: true, force: true });
  }
}

test('daemon exposes health, hooks, remotes, and pause control', async () => {
  await withDaemon({
    remote: {
      hosts: [
        { name: 'server-a', host: 'user@server-a', roots: ['/srv/a'] },
        { name: 'server-b', host: 'user@server-b', roots: ['/srv/b'] },
      ],
    },
  }, async ({ port, root, configPath }) => {
    const initial = await waitForHealth(port);
    assert.equal(initial.ok, true);
    assert.equal(initial.version, '2.3.0');
    assert.equal(initial.project, null, 'health must expose unresolved state instead of a fake project');
    assert.equal(initial.details, 'Working in Codex');
    assert.equal(initial.taskTitleShared, false);
    assert.equal(initial.rpcPublished, false, 'health distinguishes a connected socket from an acknowledged card');
    assert.equal(initial.rpcError, null, 'health exposes Discord publish failures as an explicit state');
    assert.deepEqual(initial.remoteHosts, ['server-a', 'server-b']);

    const hookResponse = await json(port, '/hook', {
      hook_event_name: 'PostToolUse',
      session_id: '00000000-0000-0000-0000-000000000000',
      cwd: 'C:\\projects\\demo',
      tool_name: 'apply_patch',
      tool_input: '*** Update File: src/demo.js\n',
    });
    assert.equal(hookResponse.status, 204);
    const afterHook = await waitForHealth(port);
    assert.equal(afterHook.project, 'demo');
    assert.equal(afterHook.file, 'src/demo.js');
    assert.ok(Date.parse(afterHook.lastHookAt), 'Doctor can distinguish registered hooks from hooks that actually fired');

    const metadataThreadId = '00000000-0000-0000-0000-000000000001';
    const stateDatabase = new DatabaseSync(path.join(root, 'state_5.sqlite'));
    stateDatabase.exec('CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, cwd TEXT, git_origin_url TEXT)');
    stateDatabase.prepare('INSERT INTO threads VALUES (?, ?, ?, ?)').run(
      metadataThreadId,
      'Ship the polished release',
      'C:\\work\\checkout-alias',
      'https://github.com/acme/canonical-repository.git',
    );
    stateDatabase.close();

    const metadataHookResponse = await json(port, '/hook', {
      hook_event_name: 'SessionStart',
      session_id: metadataThreadId,
      cwd: 'C:\\work\\checkout-alias',
    });
    assert.equal(metadataHookResponse.status, 204);
    const afterMetadataHook = await waitForHealth(port);
    assert.equal(afterMetadataHook.project, 'canonical-repository', 'authoritative repository metadata wins over a cwd alias');
    assert.equal(afterMetadataHook.file, null);
    assert.equal(afterMetadataHook.task, null, 'prompt-derived task metadata remains private without opt-in');

    const nestedHookResponse = await json(port, '/hook', {
      hook_event_name: 'PostToolUse',
      session_id: '00000000-0000-0000-0000-000000000000',
      cwd: 'C:\\Users\\dev\\Documents\\GitHub',
      tool_name: 'apply_patch',
      tool_input: '*** Update File: codex-discord-presence/tray/Program.cs\n',
    });
    assert.equal(nestedHookResponse.status, 204);
    const afterNestedHook = await waitForHealth(port);
    assert.equal(afterNestedHook.project, 'codex-discord-presence');
    assert.equal(afterNestedHook.file, 'tray/Program.cs');

    const privateSessionResponse = await json(port, '/hook', {
      hook_event_name: 'SessionStart',
      session_id: '00000000-0000-0000-0000-000000000003',
      cwd: '/root',
    });
    assert.equal(privateSessionResponse.status, 204);
    const afterPrivateSession = await waitForHealth(port);
    assert.equal(afterPrivateSession.project, null, 'a new unresolved session cannot inherit the previous repository');
    assert.equal(afterPrivateSession.file, null);
    assert.equal(afterPrivateSession.details, 'Working in Codex');

    const pause = await json(port, '/control', { action: 'pause' });
    assert.equal(pause.status, 200);
    assert.equal((await pause.json()).presenceEnabled, false);
    assert.equal((await waitForHealth(port)).presenceEnabled, false);

    const persisted = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    assert.equal(persisted.presenceEnabled, false);
    assert.equal(persisted.port, Number(port), 'pausing must not discard the rest of the config');
  });
});

test('task metadata is exposed to the tray only after explicit opt-in', async () => {
  await withDaemon({ privacy: { showTaskTitle: true } }, async ({ port, root }) => {
    await waitForHealth(port);
    const threadId = '00000000-0000-0000-0000-000000000002';
    const stateDatabase = new DatabaseSync(path.join(root, 'state_5.sqlite'));
    stateDatabase.exec('CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, cwd TEXT, git_origin_url TEXT)');
    stateDatabase.prepare('INSERT INTO threads VALUES (?, ?, ?, ?)').run(
      threadId,
      'Visible only by explicit choice',
      'C:\\work\\demo',
      'https://github.com/acme/demo.git',
    );
    stateDatabase.close();

    const response = await json(port, '/hook', { hook_event_name: 'SessionStart', session_id: threadId, cwd: 'C:\\work\\demo' });
    assert.equal(response.status, 204);
    const health = await waitForHealth(port);
    assert.equal(health.taskTitleShared, true);
    assert.equal(health.task, 'Visible only by explicit choice');
  });
});

test('a hand-edited config never takes the daemon down', async () => {
  await withDaemon({ appProcess: 'bad name!', language: 'klingon', privacy: { preset: 'nonsense' } }, async ({ port }) => {
    const health = await waitForHealth(port);
    assert.equal(health.ok, true);
    assert.equal(health.language, 'en');
    assert.ok(health.configWarnings.length > 0, 'rejected fields are surfaced to Doctor');
  });
});

test('the control surface rejects requests a browser could make', async () => {
  await withDaemon({}, async ({ port }) => {
    await waitForHealth(port);

    const crossOrigin = await json(port, '/control', { action: 'pause' }, { origin: 'https://example.com' });
    assert.equal(crossOrigin.status, 403, 'a page on any site could otherwise pause presence');

    const fetchMetadata = await json(port, '/control', { action: 'pause' }, { 'sec-fetch-site': 'cross-site' });
    assert.equal(fetchMetadata.status, 403);

    const rebound = await rawRequest(port, { path: '/health', headers: { host: 'attacker.example.com' } });
    assert.equal(rebound, 403, 'DNS rebinding must not reach the health snapshot');
    assert.equal(await rawRequest(port, { path: '/health', headers: { host: `127.0.0.1:${port}` } }), 200);

    assert.equal((await waitForHealth(port)).presenceEnabled, true, 'none of the rejected requests took effect');
  });
});

test('malformed and oversized bodies are refused, not truncated', async () => {
  await withDaemon({}, async ({ port }) => {
    await waitForHealth(port);

    const formEncoded = await fetch(`http://127.0.0.1:${port}/control`, {
      method: 'POST',
      headers: { 'content-type': 'text/plain' },
      body: JSON.stringify({ action: 'pause' }),
    });
    assert.equal(formEncoded.status, 415);

    const oversized = await fetch(`http://127.0.0.1:${port}/hook`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ cwd: 'C:\\demo', padding: 'x'.repeat(300 * 1024) }),
    });
    assert.equal(oversized.status, 413);

    const invalid = await fetch(`http://127.0.0.1:${port}/control`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{ not json',
    });
    assert.equal(invalid.status, 400);

    const unknown = await json(port, '/control', { action: 'self-destruct' });
    assert.equal(unknown.status, 400);

    assert.equal((await waitForHealth(port)).presenceEnabled, true);
  });
});
