'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const net = require('node:net');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { DiscordIpc, frame, ipcCandidates, OP_FRAME, OP_PING, OP_PONG } = require('../src/discord-ipc');

/** Stand-in for the Discord client: accepts one connection and records frames. */
function startFakeDiscord() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-ipc-'));
  const socketPath = path.join(directory, 'discord-ipc-0');
  const frames = [];
  let client = null;
  let buffer = Buffer.alloc(0);

  const server = net.createServer((socket) => {
    client = socket;
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 8) {
        const op = buffer.readInt32LE(0);
        const length = buffer.readInt32LE(4);
        if (buffer.length < 8 + length) return;
        frames.push({ op, body: JSON.parse(buffer.subarray(8, 8 + length).toString('utf8')) });
        buffer = buffer.subarray(8 + length);
      }
    });
  });

  return {
    socketPath,
    frames,
    listen: () => new Promise((resolve) => server.listen(socketPath, resolve)),
    send: (op, payload) => client.write(frame(op, payload)),
    close: () => {
      client?.destroy();
      server.close();
      fs.rmSync(directory, { recursive: true, force: true });
    },
  };
}

async function waitFor(predicate, message, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`timed out waiting for ${message}`);
}

test('the ipc client handshakes, answers pings, and rate-limits updates', async (t) => {
  if (process.platform === 'win32') return t.skip('named pipes are covered by the Windows integration build');

  const discord = startFakeDiscord();
  await discord.listen();
  const ipc = new DiscordIpc({ clientId: '1526968377048956938', candidates: [discord.socketPath], minUpdateIntervalMs: 400 });

  try {
    ipc.connect();
    await waitFor(() => discord.frames.length >= 1, 'the handshake');
    assert.equal(discord.frames[0].op, 0);
    assert.equal(discord.frames[0].body.client_id, '1526968377048956938');
    assert.equal(discord.frames[0].body.v, 1);

    ipc.setActivity({ details: 'Project: a' });
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.equal(discord.frames.length, 1, 'nothing is published before Discord reports READY');

    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    await waitFor(() => discord.frames.length >= 2, 'the first activity update');
    assert.equal(discord.frames[1].body.cmd, 'SET_ACTIVITY');
    assert.equal(discord.frames[1].body.args.activity.details, 'Project: a');

    // Discord disconnects clients that ignore its keepalive.
    discord.send(OP_PING, { nonce: 'keepalive-1' });
    await waitFor(() => discord.frames.some((item) => item.op === OP_PONG), 'a PONG reply');
    assert.equal(discord.frames.find((item) => item.op === OP_PONG).body.nonce, 'keepalive-1');

    const before = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length;
    ipc.setActivity({ details: 'Project: b' });
    ipc.setActivity({ details: 'Project: c' });
    ipc.setActivity({ details: 'Project: d' });
    await new Promise((resolve) => setTimeout(resolve, 120));
    assert.equal(
      discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length,
      before,
      'bursts are coalesced inside the rate-limit window',
    );

    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === before + 1, 'the coalesced update');
    const published = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);
    assert.equal(published.body.args.activity.details, 'Project: d', 'the newest state wins');

    ipc.setActivity(null, { immediate: true });
    await waitFor(
      () => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1).body.args.activity === null,
      'an immediate clear',
    );
  } finally {
    ipc.destroy();
    discord.close();
  }
});

test('identical activities are not re-published', async (t) => {
  if (process.platform === 'win32') return t.skip('named pipes are covered by the Windows integration build');

  const discord = startFakeDiscord();
  await discord.listen();
  const ipc = new DiscordIpc({ clientId: '1', candidates: [discord.socketPath], minUpdateIntervalMs: 10 });

  try {
    ipc.connect();
    await waitFor(() => discord.frames.length >= 1, 'the handshake');
    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    ipc.setActivity({ details: 'Project: a' });
    await waitFor(() => discord.frames.length >= 2, 'the first update');

    ipc.setActivity({ details: 'Project: a' });
    await new Promise((resolve) => setTimeout(resolve, 120));
    assert.equal(discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length, 1);
  } finally {
    ipc.destroy();
    discord.close();
  }
});

test('the client probes every candidate endpoint before backing off', () => {
  const windows = ipcCandidates('win32');
  assert.equal(windows.length, 10);
  assert.equal(windows[0], '\\\\?\\pipe\\discord-ipc-0');
  assert.equal(ipcCandidates('linux')[3].endsWith('discord-ipc-3'), true);
});
