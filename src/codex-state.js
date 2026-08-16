'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const { isAnyFilesystemRoot, projectFromCwd } = require('./codex-paths');

const THREAD_ID = /^[0-9a-f-]{20,64}$/i;
const MAX_TASK_TITLE = 96;
const EXTENDED_WINDOWS_PREFIX = '\\\\?\\';

function stateDatabasePath(codexHome) {
  let names;
  try {
    names = fs.readdirSync(codexHome);
  } catch {
    return null;
  }

  return names
    .map((name) => ({ name, version: Number(/^state_(\d+)\.sqlite$/i.exec(name)?.[1]) }))
    .filter((item) => Number.isSafeInteger(item.version))
    .sort((left, right) => right.version - left.version)
    .map((item) => path.join(codexHome, item.name))
    .at(0) || null;
}

function normalizeCwd(value) {
  const cwd = String(value ?? '').trim();
  return cwd.startsWith(EXTENDED_WINDOWS_PREFIX) ? cwd.slice(EXTENDED_WINDOWS_PREFIX.length) : cwd;
}

function repositoryName(origin) {
  const value = String(origin ?? '')
    .trim()
    .split(/[?#]/, 1)[0]
    .replace(/[\\/]+$/, '')
    .replace(/\.git$/i, '');
  if (!value) return null;
  const name = value.split(/[\\/:]/).filter(Boolean).at(-1);
  return name?.slice(0, 60) || null;
}

function sanitizeTaskTitle(value) {
  const firstLine = String(value ?? '').split(/\r?\n/, 1)[0]
    .replace(/!?(?:\[([^\]]+)\])\([^)]*\)/g, '$1')
    .replace(/[`*_~]+/g, '')
    .replace(/\s+/g, ' ')
    .trim();
  return firstLine ? firstLine.slice(0, MAX_TASK_TITLE) : null;
}

/**
 * Reads only non-content metadata for the task selected by Codex Desktop.
 * Opening read-only keeps the app's SQLite store authoritative and untouched.
 */
function readThreadContext(threadId, { codexHome } = {}) {
  if (!THREAD_ID.test(String(threadId ?? '')) || !codexHome) return null;
  const databasePath = stateDatabasePath(codexHome);
  if (!databasePath) return null;

  let database;
  try {
    database = new DatabaseSync(databasePath, { readOnly: true });
    const available = new Set(database.prepare('PRAGMA table_info(threads)').all().map((column) => column.name));
    if (!available.has('id')) return null;

    const selected = ['title', 'name', 'cwd', 'git_origin_url'].filter((column) => available.has(column));
    if (!selected.length) return null;
    const row = database.prepare(`SELECT ${selected.join(', ')} FROM threads WHERE id = ? LIMIT 1`).get(threadId);
    if (!row) return null;

    const cwd = normalizeCwd(row.cwd);
    return {
      threadId,
      title: sanitizeTaskTitle(row.name || row.title),
      cwd: cwd || null,
      project: repositoryName(row.git_origin_url) || (isAnyFilesystemRoot(cwd) ? null : projectFromCwd(cwd)),
    };
  } catch {
    return null;
  } finally {
    try { database?.close(); } catch {}
  }
}

module.exports = { readThreadContext, sanitizeTaskTitle, __testing: { normalizeCwd, repositoryName, stateDatabasePath } };
