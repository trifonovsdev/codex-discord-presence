'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const { readThreadContext, sanitizeTaskTitle } = require('../src/codex-state');

function withStateDatabase(run) {
  const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-state-'));
  const databasePath = path.join(codexHome, 'state_5.sqlite');
  const database = new DatabaseSync(databasePath);
  database.exec(`
    CREATE TABLE threads (
      id TEXT PRIMARY KEY,
      title TEXT NOT NULL,
      cwd TEXT NOT NULL,
      git_origin_url TEXT,
      git_branch TEXT,
      thread_source TEXT
    )
  `);
  try {
    run({ codexHome, database });
  } finally {
    database.close();
    fs.rmSync(codexHome, { recursive: true, force: true });
  }
}

test('selected thread metadata supplies the repository when route logs only know a task id', () => {
  withStateDatabase(({ codexHome, database }) => {
    const threadId = '0199c000-0000-4000-8000-00000000000a';
    database.prepare('INSERT INTO threads VALUES (?, ?, ?, ?, ?, ?)').run(
      threadId,
      'Обновить дизайн приложения',
      String.raw`\\?\C:\Users\dev\Projects\codex-discord-presence`,
      'https://github.com/trifonovsdev/codex-discord-presence.git',
      'main',
      'user',
    );

    assert.deepEqual(readThreadContext(threadId, { codexHome }), {
      threadId,
      title: 'Обновить дизайн приложения',
      cwd: String.raw`C:\Users\dev\Projects\codex-discord-presence`,
      project: 'codex-discord-presence',
    });
  });
});

test('task titles are single-line, markdown-free, and bounded for Discord', () => {
  const title = sanitizeTaskTitle('  Fix the presence fallback\n\n[repository](https://example.com/private)  ');
  assert.equal(title, 'Fix the presence fallback');
  assert.ok(sanitizeTaskTitle('x'.repeat(300)).length <= 96);
});

test('missing or incompatible Codex state is a soft failure', () => {
  const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-presence-state-empty-'));
  try {
    assert.equal(readThreadContext('0199c000-0000-4000-8000-00000000000a', { codexHome }), null);
    assert.equal(readThreadContext('not-a-thread-id', { codexHome }), null);
  } finally {
    fs.rmSync(codexHome, { recursive: true, force: true });
  }
});

test('a Windows user profile is context, not a made-up project name', () => {
  withStateDatabase(({ codexHome, database }) => {
    const threadId = '0199c000-0000-4000-8000-00000000000b';
    database.prepare('INSERT INTO threads VALUES (?, ?, ?, ?, ?, ?)').run(
      threadId,
      'Plan the next release',
      String.raw`C:\Users\dev`,
      null,
      null,
      'user',
    );
    assert.equal(readThreadContext(threadId, { codexHome }).project, null);
  });
});

test('repository metadata cannot leak URL credentials through the Discord project field', () => {
  withStateDatabase(({ codexHome, database }) => {
    const threadId = '0199c000-0000-4000-8000-00000000000c';
    database.prepare('INSERT INTO threads VALUES (?, ?, ?, ?, ?, ?)').run(
      threadId,
      'Keep secrets private',
      String.raw`C:\Users\dev\Projects\safe-repo`,
      'https://github.com/acme/safe-repo.git?access_token=TOP_SECRET#fragment',
      'main',
      'user',
    );
    assert.equal(readThreadContext(threadId, { codexHome }).project, 'safe-repo');
  });
});
