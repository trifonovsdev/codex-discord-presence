'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { createLogger } = require('../src/logger');

test('the presence log rotates instead of growing without bound', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-log-'));
  const logPath = path.join(directory, 'presence.log');

  try {
    const log = createLogger(logPath, { maxBytes: 400 });
    for (let index = 0; index < 200; index += 1) log(`line ${index} ${'x'.repeat(40)}`);

    assert.ok(fs.statSync(logPath).size <= 400, 'the active log stays under the cap');
    assert.ok(fs.existsSync(`${logPath}.1`), 'exactly one previous generation is kept');
    assert.equal(fs.readdirSync(directory).length, 2, 'rotation does not accumulate generations');
    assert.ok(fs.readFileSync(logPath, 'utf8').includes('line 199'), 'the newest line survives');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('logging never throws when the destination is unwritable', () => {
  const log = createLogger(path.join(os.tmpdir(), 'codex-presence-missing-dir', 'presence.log'));
  assert.doesNotThrow(() => log('this must not take the daemon down'));
});
