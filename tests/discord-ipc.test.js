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
    assert.equal(ipc.published, false, 'queued activity is not published before Discord reports READY');
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.equal(discord.frames.length, 1, 'nothing is published before Discord reports READY');

    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    await waitFor(() => discord.frames.length >= 2, 'the first activity update');
    assert.equal(discord.frames[1].body.cmd, 'SET_ACTIVITY');
    assert.equal(discord.frames[1].body.args.activity.details, 'Project: a');
    assert.equal(ipc.published, false, 'a socket write is not treated as a Discord acknowledgement');
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: discord.frames[1].body.nonce });
    await waitFor(() => ipc.published, 'the first activity acknowledgement');

    // Discord disconnects clients that ignore its keepalive.
    discord.send(OP_PING, { nonce: 'keepalive-1' });
    await waitFor(() => discord.frames.some((item) => item.op === OP_PONG), 'a PONG reply');
    assert.equal(discord.frames.find((item) => item.op === OP_PONG).body.nonce, 'keepalive-1');

    const before = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length;
    ipc.setActivity({ details: 'Project: b' });
    assert.equal(ipc.published, false, 'a changed desired activity is pending until its own acknowledgement');
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
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: discord.frames[1].body.nonce });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(ipc.published, false, 'a stale acknowledgement cannot confirm a newer payload');
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: published.body.nonce });
    await waitFor(() => ipc.published, 'the coalesced activity acknowledgement');

    ipc.setActivity(null, { immediate: true });
    assert.equal(ipc.published, false, 'clearing presence is pending until Discord acknowledges it');
    await waitFor(
      () => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1).body.args.activity === null,
      'an immediate clear',
    );
    const cleared = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: cleared.body.nonce });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(ipc.published, false, 'an acknowledged clear means no card is published');
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

test('unacknowledged payload tracking stays bounded', async (t) => {
  if (process.platform === 'win32') return t.skip('named pipes are covered by the Windows integration build');

  const discord = startFakeDiscord();
  await discord.listen();
  const ipc = new DiscordIpc({ clientId: '1', candidates: [discord.socketPath], minUpdateIntervalMs: 0 });

  try {
    ipc.connect();
    await waitFor(() => discord.frames.length >= 1, 'the handshake');
    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    await waitFor(() => ipc.ready, 'Discord READY');

    for (let index = 0; index < 80; index += 1) {
      ipc.setActivity({ details: `Project: ${index}` }, { immediate: true });
    }

    assert.equal(ipc.pendingAcks.size <= 32, true, 'missing ACKs cannot grow memory without bound');
  } finally {
    ipc.destroy();
    discord.close();
  }
});

test('reverting to an older activity still waits for the corrective acknowledgement', async (t) => {
  if (process.platform === 'win32') return t.skip('named pipes are covered by the Windows integration build');

  const discord = startFakeDiscord();
  await discord.listen();
  const ipc = new DiscordIpc({ clientId: '1', candidates: [discord.socketPath], minUpdateIntervalMs: 0 });

  try {
    ipc.connect();
    await waitFor(() => discord.frames.length >= 1, 'the handshake');
    ipc.setActivity({ details: 'Project: a' });
    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === 1, 'activity A');
    const firstA = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: firstA.body.nonce });
    await waitFor(() => ipc.published, 'activity A acknowledgement');

    ipc.setActivity({ details: 'Project: b' }, { immediate: true });
    ipc.setActivity({ details: 'Project: a' }, { immediate: true });
    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === 3, 'B and corrective A');
    const updates = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY');
    const pendingB = updates.at(-2);
    const correctiveA = updates.at(-1);
    assert.equal(ipc.published, false, 'the old A acknowledgement cannot confirm the newer corrective A send');

    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: pendingB.body.nonce });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(ipc.published, false, 'the intervening B acknowledgement cannot confirm A');
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: correctiveA.body.nonce });
    await waitFor(() => ipc.published, 'the corrective A acknowledgement');
  } finally {
    ipc.destroy();
    discord.close();
  }
});

test('a rejected activity becomes an explicit error and clears on the next payload', async (t) => {
  if (process.platform === 'win32') return t.skip('named pipes are covered by the Windows integration build');

  const discord = startFakeDiscord();
  await discord.listen();
  const ipc = new DiscordIpc({ clientId: '1', candidates: [discord.socketPath], minUpdateIntervalMs: 0 });

  try {
    ipc.connect();
    await waitFor(() => discord.frames.length >= 1, 'the handshake');
    ipc.setActivity({ details: 'Project: rejected' });
    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === 1, 'the rejected activity');
    const rejected = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);
    discord.send(OP_FRAME, { evt: 'ERROR', nonce: rejected.body.nonce, data: { message: 'Invalid activity payload' } });
    await waitFor(() => ipc.lastError, 'the activity error');
    assert.equal(ipc.lastError, 'Invalid activity payload');
    assert.equal(ipc.published, false);

    ipc.setActivity({ details: 'Project: corrected' }, { immediate: true });
    assert.equal(ipc.lastError, null, 'a new desired payload clears the prior rejection');
    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === 2, 'the corrected activity');
    const corrected = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: corrected.body.nonce });
    await waitFor(() => ipc.published, 'the corrected activity acknowledgement');
  } finally {
    ipc.destroy();
    discord.close();
  }
});

test('an error for a sent payload cannot reject a newer queued payload', async (t) => {
  if (process.platform === 'win32') return t.skip('named pipes are covered by the Windows integration build');

  const discord = startFakeDiscord();
  await discord.listen();
  const ipc = new DiscordIpc({ clientId: '1', candidates: [discord.socketPath], minUpdateIntervalMs: 150 });

  try {
    ipc.connect();
    await waitFor(() => discord.frames.length >= 1, 'the handshake');
    ipc.setActivity({ details: 'Project: sent' });
    discord.send(OP_FRAME, { evt: 'READY', data: {} });
    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === 1, 'the sent payload');
    const sent = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);

    ipc.setActivity({ details: 'Project: queued' });
    discord.send(OP_FRAME, { evt: 'ERROR', nonce: sent.body.nonce, data: { message: 'Old payload failed' } });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(ipc.lastError, null, 'an older error is not attributed to the newer desired payload');

    await waitFor(() => discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').length === 2, 'the queued payload');
    const queued = discord.frames.filter((item) => item.body.cmd === 'SET_ACTIVITY').at(-1);
    discord.send(OP_FRAME, { cmd: 'SET_ACTIVITY', nonce: queued.body.nonce });
    await waitFor(() => ipc.published, 'the queued payload acknowledgement');
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
