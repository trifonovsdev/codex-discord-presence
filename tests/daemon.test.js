'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

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

test('daemon exposes v2 health, hooks, remotes, and pause control', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-test-'));
  const configPath = path.join(root, 'config.json');
  const port = 38000 + Math.floor(Math.random() * 1000);
  fs.writeFileSync(configPath, JSON.stringify({
    port,
    presenceEnabled: true,
    remote: {
      hosts: [
        { name: 'server-a', host: 'user@server-a', roots: ['/srv/a'] },
        { name: 'server-b', host: 'user@server-b', roots: ['/srv/b'] },
      ],
    },
  }));
  const child = spawn(process.execPath, [daemonPath], {
    env: { ...process.env, CODEX_HOME: root, CODEX_PRESENCE_CONFIG: configPath, CODEX_PRESENCE_TEST: '1' },
    stdio: 'ignore',
    windowsHide: true,
  });

  try {
    const initial = await waitForHealth(port);
    assert.equal(initial.ok, true);
    assert.equal(initial.version, '2.0.1');
    assert.deepEqual(initial.remoteHosts, ['server-a', 'server-b']);

    const hookResponse = await fetch(`http://127.0.0.1:${port}/hook`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        hook_event_name: 'PostToolUse',
        session_id: '00000000-0000-0000-0000-000000000000',
        cwd: 'C:\\projects\\demo',
        tool_name: 'apply_patch',
        tool_input: '*** Update File: src/demo.js\n',
      }),
    });
    assert.equal(hookResponse.status, 204);
    const afterHook = await waitForHealth(port);
    assert.equal(afterHook.project, 'demo');
    assert.equal(afterHook.file, 'src/demo.js');

    const nestedHookResponse = await fetch(`http://127.0.0.1:${port}/hook`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        hook_event_name: 'PostToolUse',
        session_id: '00000000-0000-0000-0000-000000000000',
        cwd: 'C:\\Users\\dev\\Documents\\GitHub',
        tool_name: 'apply_patch',
        tool_input: '*** Update File: codex-discord-presence/tray/Program.cs\n',
      }),
    });
    assert.equal(nestedHookResponse.status, 204);
    const afterNestedHook = await waitForHealth(port);
    assert.equal(afterNestedHook.project, 'codex-discord-presence');
    assert.equal(afterNestedHook.file, 'tray/Program.cs');

    const pause = await fetch(`http://127.0.0.1:${port}/control`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ action: 'pause' }),
    });
    assert.equal(pause.status, 200);
    assert.equal((await pause.json()).presenceEnabled, false);
    assert.equal((await waitForHealth(port)).presenceEnabled, false);
  } finally {
    try { await fetch(`http://127.0.0.1:${port}/control`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ action: 'shutdown' }) }); } catch {}
    await new Promise((resolve) => child.once('exit', resolve));
    fs.rmSync(root, { recursive: true, force: true });
  }
});
