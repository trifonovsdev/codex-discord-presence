'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { readConfig, patchConfig, DEFAULT_CONFIG } = require('../src/config');

function withConfig(contents) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-config-'));
  const configPath = path.join(directory, 'config.json');
  if (contents !== undefined) fs.writeFileSync(configPath, contents);
  return { configPath, cleanup: () => fs.rmSync(directory, { recursive: true, force: true }) };
}

test('a missing config falls back to the documented defaults', () => {
  const { configPath, cleanup } = withConfig(undefined);
  try {
    const { config, warnings } = readConfig(configPath);
    assert.equal(config.port, DEFAULT_CONFIG.port);
    assert.equal(config.language, 'en');
    assert.equal(config.activityName, 'Coding with Codex');
    assert.deepEqual(warnings, []);
  } finally {
    cleanup();
  }
});

test('the Discord activity name is normalized and bounded before publication', () => {
  const normalized = withConfig(JSON.stringify({ activityName: '  Reviewing\nwith\tCodex  ' }));
  try {
    const { config, warnings } = readConfig(normalized.configPath);
    assert.equal(config.activityName, 'Reviewing with Codex');
    assert.equal(warnings.length, 1);
  } finally {
    normalized.cleanup();
  }

  const invalid = withConfig(JSON.stringify({ activityName: 'x' }));
  try {
    const { config, warnings } = readConfig(invalid.configPath);
    assert.equal(config.activityName, DEFAULT_CONFIG.activityName);
    assert.ok(warnings.some((warning) => warning.includes('activityName')));
  } finally {
    invalid.cleanup();
  }

  const long = withConfig(JSON.stringify({ activityName: 'a'.repeat(200) }));
  try {
    const { config, warnings } = readConfig(long.configPath);
    assert.equal(config.activityName.length, 128);
    assert.ok(warnings.some((warning) => warning.includes('activityName')));
  } finally {
    long.cleanup();
  }

  const unicodeBoundary = withConfig(JSON.stringify({ activityName: `${'a'.repeat(127)}😀` }));
  try {
    const { config } = readConfig(unicodeBoundary.configPath);
    assert.equal(config.activityName.isWellFormed(), true, 'truncation must not split a Unicode surrogate pair');
    assert.ok(config.activityName.length <= 128);
  } finally {
    unicodeBoundary.cleanup();
  }
});

test('out-of-range and malformed fields fall back instead of reaching the daemon', () => {
  const { configPath, cleanup } = withConfig(JSON.stringify({
    port: 0,
    clientId: 'not-a-snowflake',
    appProcess: 'evil.exe; rm -rf /',
    language: 'klingon',
    remote: { pollIntervalMs: 10, monitorPath: '/tmp/$(whoami)' },
  }));
  try {
    const { config, warnings } = readConfig(configPath);
    assert.equal(config.port, DEFAULT_CONFIG.port, 'port 0 would bind a random port the tray cannot find');
    assert.equal(config.clientId, DEFAULT_CONFIG.clientId);
    assert.equal(config.appProcess, DEFAULT_CONFIG.appProcess);
    assert.equal(config.language, 'en');
    assert.equal(config.remote.pollIntervalMs, DEFAULT_CONFIG.remote.pollIntervalMs);
    assert.equal(config.remote.monitorPath, DEFAULT_CONFIG.remote.monitorPath);
    assert.ok(warnings.length >= 3, 'rejected fields are reported');
  } finally {
    cleanup();
  }
});

test('a privacy preset supplies defaults that explicit fields still override', () => {
  const { configPath, cleanup } = withConfig(JSON.stringify({ privacy: { preset: 'minimal' } }));
  try {
    const { config } = readConfig(configPath);
    assert.equal(config.privacy.showFile, false);
    assert.equal(config.privacy.showTaskTitle, false, 'prompt-derived task titles stay private by default');
    assert.equal(config.privacy.fileMode, 'name');
  } finally {
    cleanup();
  }

  const explicit = withConfig(JSON.stringify({ privacy: { preset: 'minimal', showFile: true } }));
  try {
    assert.equal(readConfig(explicit.configPath).config.privacy.showFile, true);
  } finally {
    explicit.cleanup();
  }

  const taskTitle = withConfig(JSON.stringify({ privacy: { preset: 'standard', showTaskTitle: true } }));
  try {
    assert.equal(readConfig(taskTitle.configPath).config.privacy.showTaskTitle, true);
  } finally {
    taskTitle.cleanup();
  }
});

test('patchConfig merges into the existing document atomically', () => {
  const { configPath, cleanup } = withConfig(JSON.stringify({ port: 41000, privacy: { preset: 'minimal' } }));
  try {
    patchConfig(configPath, { presenceEnabled: false });
    const written = JSON.parse(fs.readFileSync(configPath, 'utf8'));
    assert.equal(written.presenceEnabled, false);
    assert.equal(written.port, 41000, 'unrelated settings survive the patch');
    assert.equal(written.privacy.preset, 'minimal');
    assert.equal(fs.existsSync(`${configPath}.tmp`), false, 'the temporary file is renamed, not left behind');
  } finally {
    cleanup();
  }
});

test('patchConfig refuses to overwrite a config it cannot parse', () => {
  const { configPath, cleanup } = withConfig('{ this is not json');
  try {
    assert.throws(() => patchConfig(configPath, { presenceEnabled: false }), /unreadable config/);
    assert.equal(fs.readFileSync(configPath, 'utf8'), '{ this is not json', 'user settings are left untouched');
  } finally {
    cleanup();
  }
});

test('patchConfig creates the file when none exists yet', () => {
  const { configPath, cleanup } = withConfig(undefined);
  try {
    patchConfig(configPath, { presenceEnabled: true });
    assert.equal(JSON.parse(fs.readFileSync(configPath, 'utf8')).presenceEnabled, true);
  } finally {
    cleanup();
  }
});
