'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { configuredRemotes, remoteForCwd } = require('../src/remotes');

test('longest workspace root chooses the remote host', () => {
  const remotes = configuredRemotes({ remote: { hosts: [
    { name: 'general', host: 'dev@general', roots: ['/srv'] },
    { name: 'store', host: 'dev@store', roots: ['/srv/apps/store'] },
  ] } }, '~/.local/share/CodexDiscordPresence/remote-monitor.py');
  assert.equal(remoteForCwd('/srv/apps/store/client', remotes)?.name, 'store');
  assert.equal(remoteForCwd('/srv/api', remotes)?.name, 'general');
  assert.equal(remoteForCwd('C:\\projects\\local', remotes), null);
});

test('ambiguous unmatched workspaces do not guess between hosts', () => {
  const remotes = configuredRemotes({ remote: { hosts: [
    { name: 'one', host: 'one', roots: ['/one'] },
    { name: 'two', host: 'two', roots: ['/two'] },
  ] } }, '~/.local/share/CodexDiscordPresence/remote-monitor.py');
  assert.equal(remoteForCwd('/unknown/project', remotes), null);
});

test('single legacy host remains backwards compatible', () => {
  const remotes = configuredRemotes({ remote: { host: 'root@example.com', monitorPath: '~/.local/monitor.py' } }, '~/.local/monitor.py');
  assert.equal(remoteForCwd('/any/remote/project', remotes)?.host, 'root@example.com');
});
