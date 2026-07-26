'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');

const { DesktopSelection, parseDesktopLogLine } = require('../src/desktop-selection');

const THREAD_A = '0199c000-0000-4000-8000-00000000000a';
const THREAD_B = '0199c000-0000-4000-8000-00000000000b';

function routeLine(time, threadId, kind = 'local') {
  return `${time} INFO ownerRoutePath=/${kind}/${threadId} loaded`;
}

function cwdLine(time, cwd) {
  return `${time} INFO starting session cwd=${cwd}`;
}

test('route and cwd lines are parsed into ordered events', () => {
  const events = parseDesktopLogLine(routeLine('2026-01-01T10:00:00.000Z', THREAD_A, 'remote'));
  assert.deepEqual(events, [{ type: 'route', at: Date.parse('2026-01-01T10:00:00.000Z'), kind: 'remote', threadId: THREAD_A }]);
  assert.deepEqual(parseDesktopLogLine('no timestamp here'), []);
});

test('home directories and .git paths are not mistaken for workspaces', () => {
  assert.deepEqual(parseDesktopLogLine(cwdLine('2026-01-01T10:00:00.000Z', '/root')), []);
  assert.deepEqual(parseDesktopLogLine(cwdLine('2026-01-01T10:00:00.000Z', '/home/dev')), []);
  assert.deepEqual(parseDesktopLogLine(cwdLine('2026-01-01T10:00:00.000Z', 'C:\\repo\\.git\\worktrees')), []);
  assert.equal(parseDesktopLogLine(cwdLine('2026-01-01T10:00:00.000Z', '/srv/store')).length, 1);
});

test('a cwd logged right after a route is attached to that task', () => {
  const selection = new DesktopSelection();
  selection.ingest([
    ...parseDesktopLogLine(routeLine('2026-01-01T10:00:00.000Z', THREAD_A)),
    ...parseDesktopLogLine(cwdLine('2026-01-01T10:00:01.000Z', '/srv/store')),
  ]);
  assert.equal(selection.selected().threadId, THREAD_A);
  assert.equal(selection.selected().project, 'store');
});

test('a cwd logged long after a route belongs to no task', () => {
  const selection = new DesktopSelection();
  selection.ingest([
    ...parseDesktopLogLine(routeLine('2026-01-01T10:00:00.000Z', THREAD_A)),
    ...parseDesktopLogLine(cwdLine('2026-01-01T10:05:00.000Z', '/srv/other')),
  ]);
  assert.equal(selection.selected().project, null);
});

test('switching tasks is reported once and keeps per-task state', () => {
  const selection = new DesktopSelection();
  selection.ingest([
    ...parseDesktopLogLine(routeLine('2026-01-01T10:00:00.000Z', THREAD_A)),
    ...parseDesktopLogLine(cwdLine('2026-01-01T10:00:01.000Z', '/srv/store')),
  ]);

  const switched = selection.ingest([
    ...parseDesktopLogLine(routeLine('2026-01-01T10:01:00.000Z', THREAD_B, 'remote')),
    ...parseDesktopLogLine(cwdLine('2026-01-01T10:01:01.000Z', '/srv/api')),
  ]);
  assert.equal(switched, true);
  assert.equal(selection.selected().threadId, THREAD_B);
  assert.equal(selection.selected().kind, 'remote');
  assert.equal(selection.threadState(THREAD_A).project, 'store', 'the previous task keeps its resolved project');

  assert.equal(selection.ingest([...parseDesktopLogLine(routeLine('2026-01-01T10:02:00.000Z', THREAD_B))]), false);
});

test('batches are ordered internally so interleaved log files still resolve', () => {
  const selection = new DesktopSelection();
  selection.ingest([
    ...parseDesktopLogLine(cwdLine('2026-01-01T10:00:01.000Z', '/srv/store')),
    ...parseDesktopLogLine(routeLine('2026-01-01T10:00:00.000Z', THREAD_A)),
  ]);
  assert.equal(selection.selected().project, 'store');
});

test('an out-of-order stale route does not steal the selection', () => {
  const selection = new DesktopSelection();
  selection.ingest(parseDesktopLogLine(routeLine('2026-01-01T10:05:00.000Z', THREAD_B)));
  selection.ingest(parseDesktopLogLine(routeLine('2026-01-01T10:00:00.000Z', THREAD_A)));
  assert.equal(selection.selected().threadId, THREAD_B);
});

test('thread state is bounded so a long-running daemon cannot leak memory', () => {
  const selection = new DesktopSelection({ threadLimit: 10 });
  for (let index = 0; index < 200; index += 1) {
    const threadId = `0199c000-0000-4000-8000-${String(index).padStart(12, '0')}`;
    const at = new Date(Date.UTC(2026, 0, 1, 10, 0, index)).toISOString().replace('Z', 'Z');
    selection.ingest(parseDesktopLogLine(routeLine(at, threadId)));
  }
  assert.ok(selection.threads.size <= 10, `expected at most 10 retained threads, got ${selection.threads.size}`);
  assert.ok(selection.selected().threadId.endsWith('000000000199'), selection.selected().threadId);
});
