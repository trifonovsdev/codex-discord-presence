'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  ClaudeCodeMonitor,
  displayFile,
  editedFileFromRecord,
  isHumanPrompt,
  projectDirectoryName,
  surfaceFromEntrypoint,
} = require('../src/claude-code');
const { claudeWorktreeProject } = require('../src/codex-paths');

const MINUTE = 60_000;
const BASE = Date.parse('2026-09-28T10:00:00.000Z');
const monitorPath = path.resolve(__dirname, '..', 'src', 'remote-monitor.py');

function tempHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'claude-presence-'));
}

const iso = (at) => new Date(at).toISOString();
const prompt = (at, text = 'Ship it', extra = {}) => ({
  type: 'user', timestamp: iso(at), message: { role: 'user', content: text }, origin: { kind: 'human' }, ...extra,
});
const edit = (at, filePath, name = 'Edit') => ({
  type: 'assistant',
  timestamp: iso(at),
  message: { content: [{ type: 'text', text: 'ok' }, { type: 'tool_use', name, input: { file_path: filePath, old_string: 'a', new_string: 'b' } }] },
});
const toolResult = (at) => ({ type: 'user', timestamp: iso(at), message: { content: [{ type: 'tool_result', content: 'done' }] } });

/** Writes a transcript the way Claude Code lays it out on disk. */
function writeSession(home, { id, cwd, records, mtime }) {
  const directory = path.join(home, 'projects', projectDirectoryName(cwd));
  fs.mkdirSync(directory, { recursive: true });
  const filePath = path.join(directory, `${id}.jsonl`);
  const withCwd = records.map((record) => ('timestamp' in record ? { cwd, sessionId: id, entrypoint: 'cli', ...record } : record));
  fs.writeFileSync(filePath, `${withCwd.map((record) => JSON.stringify(record)).join('\n')}\n`);
  if (mtime) fs.utimesSync(filePath, new Date(mtime), new Date(mtime));
  return filePath;
}

function writeRegistry(home, { pid, id, cwd, status = 'idle', entrypoint = 'claude-desktop' }) {
  fs.mkdirSync(path.join(home, 'sessions'), { recursive: true });
  fs.writeFileSync(path.join(home, 'sessions', `${pid}.json`), JSON.stringify({ pid, sessionId: id, cwd, status, entrypoint, kind: 'interactive' }));
}

test('transcript records are classified like Claude Code writes them', () => {
  assert.equal(isHumanPrompt(prompt(BASE)), true);
  assert.equal(isHumanPrompt(toolResult(BASE)), false, 'tool results are not the user typing');
  assert.equal(isHumanPrompt(prompt(BASE, 'x', { isMeta: true })), false);
  assert.equal(isHumanPrompt(prompt(BASE, 'x', { isSidechain: true })), false, 'subagent traffic is not focus');
  assert.equal(isHumanPrompt(prompt(BASE, 'x', { origin: { kind: 'task-notification' } })), false);
  assert.equal(editedFileFromRecord(edit(BASE, '/srv/app/src/a.ts')), '/srv/app/src/a.ts');
  assert.equal(editedFileFromRecord(edit(BASE, '/srv/app/notes.ipynb', 'Read')), null);
  assert.equal(editedFileFromRecord(edit(BASE, '/home/dev/.claude/projects/x/memory/notes.md')), null, 'Claude memory is not the user\'s work');
  assert.equal(projectDirectoryName('/root/app/.claude/worktrees/wt'), '-root-app--claude-worktrees-wt');
  assert.equal(projectDirectoryName('C:\\Users\\dev\\app'), 'C--Users-dev-app');
  assert.equal(surfaceFromEntrypoint('claude-desktop-3p'), 'desktop');
  assert.equal(surfaceFromEntrypoint('claude-vscode'), 'ide');
  assert.equal(surfaceFromEntrypoint('cli'), 'terminal');
});

test('worktrees and paths outside the project stay readable and private', () => {
  assert.equal(claudeWorktreeProject('/root/store/.claude/worktrees/brave-otter-1a2b/src/a.ts'), 'store');
  assert.equal(claudeWorktreeProject('C:\\work\\store\\.claude\\worktrees\\wt'), 'store');
  assert.equal(claudeWorktreeProject('/root/store/src'), null);
  assert.equal(displayFile('/home/dev/work/store/src/a.ts', '/home/dev', 'store'), 'src/a.ts');
  assert.equal(displayFile('/etc/nginx/sites/app.conf', '/home/dev/store', 'store'), 'sites/app.conf');
});

test('the focused session follows the latest human prompt, not background work', () => {
  const home = tempHome();
  let now = BASE;
  try {
    writeSession(home, {
      id: '11111111-1111-4111-8111-111111111111',
      cwd: '/work/store',
      records: [prompt(BASE - 9 * MINUTE), edit(BASE - 8 * MINUTE, '/work/store/src/cart.ts'), { type: 'custom-title', customTitle: 'Cart **refactor**' }],
      mtime: BASE,
    });
    writeSession(home, {
      id: '22222222-2222-4222-8222-222222222222',
      cwd: '/work/api',
      records: [prompt(BASE - 20 * MINUTE), edit(BASE - MINUTE, '/work/api/server.js'), toolResult(BASE - 30_000)],
      mtime: BASE,
    });
    const monitor = new ClaudeCodeMonitor({ home, now: () => now, isProcessAlive: () => false });
    const changes = [];
    monitor.on('change', (state) => changes.push(state));
    monitor.poll();

    const focused = monitor.snapshot();
    assert.equal(focused.active, true);
    assert.equal(focused.sessionId, '11111111-1111-4111-8111-111111111111', 'the more recent prompt wins over newer tool traffic');
    assert.equal(focused.project, 'store');
    assert.equal(focused.file, 'src/cart.ts');
    assert.equal(focused.title, 'Cart refactor');
    assert.equal(focused.surface, 'terminal');
    assert.equal(focused.sessions, 2);
    assert.equal(focused.startedAt, BASE - 9 * MINUTE, 'the timer starts with the earliest burst still in progress');
    assert.equal(changes.length, 1);

    now += 30_000;
    assert.equal(monitor.poll(), false, 'an unchanged card does not emit');

    now = BASE + 11 * MINUTE;
    monitor.poll();
    assert.equal(monitor.snapshot().active, false, 'quiet sessions go idle after the idle window');
    assert.equal(monitor.snapshot().startedAt, undefined);
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('busy sessions in the live registry stay active and old transcripts are ignored', () => {
  const home = tempHome();
  const now = BASE;
  try {
    const id = '33333333-3333-4333-8333-333333333333';
    writeSession(home, { id, cwd: '/srv/long-run', records: [prompt(BASE - 60 * MINUTE)], mtime: BASE - 50 * MINUTE });
    writeSession(home, {
      id: '44444444-4444-4444-8444-444444444444',
      cwd: '/srv/forgotten',
      records: [prompt(BASE - 90 * MINUTE)],
      mtime: BASE - 90 * MINUTE,
    });
    writeRegistry(home, { pid: 4242, id, cwd: '/srv/long-run', status: 'busy' });
    writeRegistry(home, { pid: 4343, id: '44444444-4444-4444-8444-444444444444', cwd: '/srv/forgotten', status: 'busy' });

    const monitor = new ClaudeCodeMonitor({ home, now: () => now, isProcessAlive: (pid) => pid === 4242 });
    monitor.poll();
    const state = monitor.snapshot();
    assert.equal(state.active, true);
    assert.equal(state.sessionId, id);
    assert.equal(state.busy, true);
    assert.equal(state.surface, 'desktop');
    assert.equal(state.project, 'long-run');
    assert.equal(state.sessions, 1, 'a dead process in the registry is not a live session');
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('hooks update the card instantly and SessionEnd releases it', () => {
  const home = tempHome();
  let now = BASE;
  try {
    const monitor = new ClaudeCodeMonitor({ home, now: () => now, isProcessAlive: () => false });
    const id = '55555555-5555-4555-8555-555555555555';
    assert.equal(monitor.handleHook({ session_id: 'not-a-session', hook_event_name: 'SessionStart' }), false);
    assert.equal(monitor.handleHook({ session_id: id, hook_event_name: 'SessionStart', cwd: '/work/docs' }), true);
    assert.equal(monitor.snapshot().project, 'docs');
    assert.equal(monitor.snapshot().focusAt, now);

    now += 5000;
    monitor.handleHook({ session_id: id, hook_event_name: 'PostToolUse', cwd: '/work/docs', tool_name: 'Write', tool_input: { file_path: '/work/docs/guide.md' } });
    assert.equal(monitor.snapshot().file, 'guide.md');
    assert.equal(monitor.snapshot().source, 'claude-hook');

    now += 5000;
    monitor.handleHook({ session_id: id, hook_event_name: 'SessionEnd', cwd: '/work/docs' });
    assert.equal(monitor.snapshot().active, false);
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('remote sessions join focus selection on the local clock', () => {
  const home = tempHome();
  const now = BASE;
  try {
    const monitor = new ClaudeCodeMonitor({ home, now: () => now, isProcessAlive: () => false });
    monitor.handleHook({ session_id: '66666666-6666-4666-8666-666666666666', hook_event_name: 'UserPromptSubmit', cwd: 'C:\\work\\local-app' });
    assert.equal(monitor.snapshot().project, 'local-app');

    // The server clock runs one minute behind; its prompt is still the newer one.
    monitor.applyRemote('vds', {
      ok: true,
      now: now - MINUTE,
      active: true,
      sessionId: '77777777-7777-4777-8777-777777777777',
      cwd: '/root/store',
      project: 'store',
      file: 'src/index.ts',
      title: 'Remote work',
      busy: true,
      entrypoint: 'claude-desktop',
      startedAt: now - 11 * MINUTE,
      focusAt: now - MINUTE + 1000,
      activityAt: now - MINUTE + 2000,
    }, now);
    const state = monitor.snapshot();
    assert.equal(state.workspace, 'vds');
    assert.equal(state.project, 'store');
    assert.equal(state.source, 'claude-remote');
    assert.equal(state.focusAt, now + 1000);

    monitor.applyRemote('vds', { ok: false, error: 'offline' }, now);
    assert.equal(monitor.snapshot().workspace, null, 'an unreachable server releases the card');
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});

test('the SSH helper reports the focused remote Claude Code session', () => {
  const home = tempHome();
  try {
    const claudeHome = path.join(home, '.claude');
    const repository = path.join(home, 'store');
    fs.mkdirSync(path.join(repository, '.git'), { recursive: true });
    const now = Date.now();
    writeSession(claudeHome, {
      id: '88888888-8888-4888-8888-888888888888',
      cwd: home,
      records: [
        prompt(now - 3 * MINUTE),
        edit(now - 2 * MINUTE, path.join(repository, 'src', 'cart.ts')),
        { type: 'custom-title', customTitle: 'Checkout' },
      ],
    });
    const run = () => spawnSync('python3', [monitorPath, '--claude', '600'], {
      encoding: 'utf8',
      env: { ...process.env, HOME: home, USERPROFILE: home, CLAUDE_CONFIG_DIR: claudeHome },
    });

    const first = run();
    assert.equal(first.status, 0, first.stderr);
    const result = JSON.parse(first.stdout.trim());
    assert.equal(result.ok, true);
    assert.equal(result.active, true);
    assert.equal(result.project, 'store');
    assert.equal(result.file, 'src/cart.ts');
    assert.equal(result.title, 'Checkout');
    assert.ok(Math.abs(result.focusAt - (now - 3 * MINUTE)) < 1000);
    assert.ok(Number.isFinite(result.now));

    const cached = JSON.parse(run().stdout.trim());
    assert.deepEqual({ ...cached, now: 0 }, { ...result, now: 0 }, 'incremental reads keep the same answer');

    const idle = spawnSync('python3', [monitorPath, '--claude', '60'], {
      encoding: 'utf8',
      env: { ...process.env, HOME: home, USERPROFILE: home, CLAUDE_CONFIG_DIR: path.join(home, 'missing') },
    });
    assert.deepEqual(
      (({ ok, active }) => ({ ok, active }))(JSON.parse(idle.stdout.trim())),
      { ok: true, active: false },
    );
  } finally {
    fs.rmSync(home, { recursive: true, force: true });
  }
});
