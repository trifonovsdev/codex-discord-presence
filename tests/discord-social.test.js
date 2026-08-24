'use strict';

const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const { PassThrough } = require('node:stream');
const { test } = require('node:test');

const publisherPath = path.resolve(__dirname, '..', 'src', 'discord-publisher.js');

function fakeBridgeProcess() {
  const child = new EventEmitter();
  const input = [];
  child.stdin = {
    destroyed: false,
    write(value) {
      input.push(JSON.parse(String(value).trim()));
      return true;
    },
    end() {
      this.destroyed = true;
    },
  };
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.killed = false;
  child.kill = () => {
    child.killed = true;
    child.emit('exit', 0, null);
    return true;
  };
  return { child, input };
}

test('Social SDK bridge publishes and acknowledges the custom activity name', async () => {
  assert.ok(fs.existsSync(publisherPath), 'the Social SDK publisher transport must exist');
  const { DiscordSocial } = require(publisherPath);
  const bridge = fakeBridgeProcess();
  const publisher = new DiscordSocial({
    clientId: '1526968377048956938',
    bridgePath: 'C:\\CodexPresence\\CodexPresence.exe',
    spawnProcess: () => bridge.child,
    minUpdateIntervalMs: 0,
  });

  publisher.setActivity({ name: 'Reviewing with Codex', type: 0, details: 'Project: demo' });
  publisher.connect();
  bridge.child.stdout.write(`${JSON.stringify({ event: 'ready', sdkVersion: '1.9.16441' })}\n`);

  assert.equal(publisher.ready, true);
  assert.equal(publisher.transport, 'social-sdk');
  assert.equal(bridge.input.length, 1);
  assert.equal(bridge.input[0].activity.name, 'Reviewing with Codex');
  assert.equal(publisher.published, false, 'a pipe write is not a Discord acknowledgement');

  bridge.child.stdout.write(`${JSON.stringify({ event: 'ack', id: bridge.input[0].id })}\n`);
  assert.equal(publisher.published, true);
  assert.ok(Date.parse(publisher.lastAck));

  publisher.setActivity(null, { immediate: true });
  assert.equal(bridge.input.at(-1).activity, null);
  bridge.child.stdout.write(`${JSON.stringify({ event: 'ack', id: bridge.input.at(-1).id })}\n`);
  assert.equal(publisher.published, false, 'an acknowledged clear means no card is published');
  publisher.destroy();
  assert.equal(bridge.child.killed, true);
});

test('Social SDK bridge exposes native publish errors and recovers on the next update', () => {
  assert.ok(fs.existsSync(publisherPath), 'the Social SDK publisher transport must exist');
  const { DiscordSocial } = require(publisherPath);
  const bridge = fakeBridgeProcess();
  const publisher = new DiscordSocial({
    clientId: '1',
    bridgePath: 'bridge.exe',
    spawnProcess: () => bridge.child,
    minUpdateIntervalMs: 0,
  });

  publisher.connect();
  bridge.child.stdout.write('{"event":"ready"}\n');
  publisher.setActivity({ name: 'Coding with Codex', details: 'Project: rejected' });
  bridge.child.stdout.write(`${JSON.stringify({ event: 'error', id: bridge.input.at(-1).id, message: 'Invalid activity' })}\n`);
  assert.equal(publisher.lastError, 'Invalid activity');
  assert.equal(publisher.published, false);

  publisher.setActivity({ name: 'Coding with Codex', details: 'Project: fixed' }, { immediate: true });
  assert.equal(publisher.lastError, null);
  bridge.child.stdout.write(`${JSON.stringify({ event: 'ack', id: bridge.input.at(-1).id })}\n`);
  assert.equal(publisher.published, true);
  publisher.destroy();
});

test('publisher factory prefers the bundled Social SDK and keeps legacy RPC as fallback', () => {
  assert.ok(fs.existsSync(publisherPath), 'the Social SDK publisher transport must exist');
  const { createDiscordPublisher, DiscordSocial } = require(publisherPath);
  const { DiscordIpc } = require('../src/discord-ipc');

  const social = createDiscordPublisher({
    clientId: '1',
    platform: 'win32',
    baseDirectory: 'C:\\CodexPresence',
    existsSync: () => true,
  });
  assert.ok(social instanceof DiscordSocial);

  const fallback = createDiscordPublisher({
    clientId: '1',
    platform: 'linux',
    baseDirectory: '/opt/codex-presence',
    existsSync: () => false,
  });
  assert.ok(fallback instanceof DiscordIpc);
  assert.equal(fallback.transport, 'legacy-rpc');
});

test('a fatal native bridge startup failure is visible instead of hanging on waiting', () => {
  const { DiscordSocial } = require(publisherPath);
  const bridge = fakeBridgeProcess();
  const publisher = new DiscordSocial({
    clientId: '1',
    bridgePath: 'bridge.exe',
    spawnProcess: () => bridge.child,
  });

  publisher.connect();
  bridge.child.stdout.write('{"event":"fatal","message":"discord_partner_sdk.dll is missing"}\n');
  assert.match(publisher.lastError, /discord_partner_sdk\.dll is missing/);
  assert.equal(publisher.ready, false);
  assert.equal(bridge.child.killed, true);
  publisher.destroy();
});
