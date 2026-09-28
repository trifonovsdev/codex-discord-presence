'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');

const { newTailState, readNewLines } = require('./tail');
const { sanitizeTaskTitle } = require('./codex-state');
const {
  MAX_PROJECT,
  claudeWorktreeProject,
  displayPath,
  fileForProject,
  projectFromCwd,
  repositoryProjectFromFile,
} = require('./codex-paths');

const SESSION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EDIT_TOOLS = new Set(['Edit', 'MultiEdit', 'Write', 'NotebookEdit']);
const DEFAULT_IDLE_MS = 10 * 60_000;
// A new transcript is read from its tail: everything the card needs (cwd,
// title, latest edit, current burst of activity) is near the end of the file.
const INITIAL_TAIL_BYTES = 2 * 1024 * 1024;
const FULL_SCAN_INTERVAL_MS = 30_000;
const SESSION_LIMIT = 24;
const REGISTRY_FILE_LIMIT = 256;
// A SessionEnd hook wins over transcript records flushed right after it.
const END_GRACE_MS = 5000;
const REMOTE_STALE_MS = 2 * 60_000;
// Claude Code's own memory, plans and transcripts are not the user's work.
const CLAUDE_INTERNAL_PATH = /(?:^|[\\/])\.claude[\\/](?!worktrees[\\/])/i;

/** `CLAUDE_CONFIG_DIR` relocates everything Claude Code keeps under `~/.claude`. */
function claudeHomeDirectory(env = process.env) {
  return env.CLAUDE_CONFIG_DIR || path.join(env.USERPROFILE || os.homedir(), '.claude');
}

/** Claude Code stores transcripts under `projects/<cwd with every non-alphanumeric replaced by ->`. */
function projectDirectoryName(cwd) {
  return String(cwd ?? '').replace(/[^A-Za-z0-9]/g, '-');
}

/** Where a Claude Code client renders: desktop app, IDE extension, terminal or SDK. */
function surfaceFromEntrypoint(entrypoint) {
  const value = String(entrypoint ?? '').toLowerCase();
  if (!value) return null;
  if (value.includes('desktop')) return 'desktop';
  if (/vscode|jetbrains|ide|cursor|windsurf/.test(value)) return 'ide';
  if (value.includes('sdk')) return 'sdk';
  if (value === 'cli' || value.includes('terminal')) return 'terminal';
  return 'other';
}

function timestampOf(record) {
  const at = Date.parse(record?.timestamp);
  return Number.isFinite(at) ? at : null;
}

/**
 * A prompt typed by a person, as opposed to tool results, meta records,
 * sidechain (subagent) traffic, or system notifications. Prompts are the
 * signal that the user is looking at this session right now.
 */
function isHumanPrompt(record) {
  if (record?.type !== 'user' || record.isMeta || record.isSidechain) return false;
  const kind = record.origin?.kind;
  if (kind && kind !== 'human') return false;
  const content = record.message?.content;
  if (typeof content === 'string') return content.trim().length > 0;
  if (!Array.isArray(content)) return false;
  return content.length > 0 && !content.some((block) => block?.type === 'tool_result');
}

function isInternalPath(value) {
  return CLAUDE_INTERNAL_PATH.test(String(value ?? ''));
}

/**
 * Repository-relative path for the card. A file outside every known root is
 * reduced to its last two segments so a home directory never leaks.
 */
function displayFile(file, cwd, project) {
  const shown = displayPath(file, cwd);
  if (!shown) return null;
  const relative = fileForProject(shown, project);
  if (!/^(?:[A-Za-z]:)?\//.test(relative)) return relative;
  return relative.split('/').filter(Boolean).slice(-2).join('/') || null;
}

/** Path of the last file changed by an edit tool in an assistant record, or null. */
function editedFileFromRecord(record) {
  if (record?.type !== 'assistant') return null;
  const content = record.message?.content;
  if (!Array.isArray(content)) return null;
  let found = null;
  for (const block of content) {
    if (block?.type !== 'tool_use' || !EDIT_TOOLS.has(block.name)) continue;
    const input = block.input || {};
    const value = input.file_path ?? input.notebook_path;
    if (typeof value === 'string' && value.trim() && !isInternalPath(value)) found = value.trim();
  }
  return found;
}

function editedFileFromHook(payload) {
  if (!EDIT_TOOLS.has(String(payload?.tool_name ?? ''))) return null;
  const input = payload.tool_input || {};
  const value = input.file_path ?? input.notebook_path;
  return typeof value === 'string' && value.trim() && !isInternalPath(value) ? value.trim() : null;
}

/** Repository-aware project name for a session: edited file first, then its cwd. */
function resolveProject(cwd, file, options) {
  return claudeWorktreeProject(file)
    || (file && cwd ? repositoryProjectFromFile(file, cwd, options) : null)
    || claudeWorktreeProject(cwd)
    || (cwd ? repositoryProjectFromFile(cwd, cwd, options) : null)
    || projectFromCwd(cwd)
    || null;
}

function defaultIsProcessAlive(pid) {
  if (!Number.isSafeInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM: the process exists but belongs to another security context.
    return error.code === 'EPERM';
  }
}

function newSession(id) {
  return {
    id,
    path: null,
    tail: null,
    cwd: null,
    title: null,
    registryTitle: null,
    file: null,
    firstAt: null,
    lastAt: null,
    promptAt: null,
    endedAt: null,
    hookAt: null,
    busy: false,
    entrypoint: null,
  };
}

/**
 * Follows Claude Code sessions on this machine (and, through `applyRemote`,
 * on SSH workspaces) and reduces them to the one the user is focused on.
 *
 * Claude Code has no "selected task" concept, so focus is inferred: the
 * session that most recently received a prompt from a person wins, and a
 * session is active while it is working (`busy` in the live registry) or has
 * produced activity within the idle window. Background sessions that keep
 * running never steal the card from the conversation the user is in.
 */
class ClaudeCodeMonitor extends EventEmitter {
  constructor({
    home = claudeHomeDirectory(),
    idleMs = DEFAULT_IDLE_MS,
    now = () => Date.now(),
    log = () => {},
    isProcessAlive = defaultIsProcessAlive,
    initialTailBytes = INITIAL_TAIL_BYTES,
    projectOptions = {},
  } = {}) {
    super();
    this.home = home;
    this.idleMs = idleMs;
    this.now = now;
    this.log = log;
    this.isProcessAlive = isProcessAlive;
    this.initialTailBytes = initialTailBytes;
    this.projectOptions = projectOptions;
    this.sessions = new Map();
    this.remotes = new Map();
    this.runStartedAt = null;
    this.lastFullScanAt = 0;
    this.lastHookAt = null;
    this.lastSignature = '';
    this.current = null;
  }

  get projectsDirectory() {
    return path.join(this.home, 'projects');
  }

  get registryDirectory() {
    return path.join(this.home, 'sessions');
  }

  /** True when this machine has a Claude Code profile at all. */
  get installed() {
    return fs.existsSync(this.home);
  }

  poll() {
    const now = this.now();
    const registry = this.#readRegistry();
    const paths = new Map();

    for (const entry of registry.values()) {
      if (!entry.cwd) continue;
      paths.set(entry.sessionId, path.join(this.projectsDirectory, projectDirectoryName(entry.cwd), `${entry.sessionId}.jsonl`));
    }
    for (const session of this.sessions.values()) if (session.path) paths.set(session.id, session.path);
    if (!this.sessions.size || now - this.lastFullScanAt >= FULL_SCAN_INTERVAL_MS) {
      this.lastFullScanAt = now;
      for (const [id, filePath] of this.#recentTranscripts(now)) if (!paths.has(id)) paths.set(id, filePath);
    }

    for (const [id, filePath] of paths) {
      let stat;
      try {
        stat = fs.statSync(filePath);
      } catch {
        continue;
      }
      const known = this.sessions.get(id);
      // Never start tracking a transcript that has been quiet for longer than the idle window.
      if (!known && !registry.get(id)?.busy && now - stat.mtimeMs > this.idleMs) continue;
      const session = known || this.#session(id);
      this.#tail(session, filePath, stat.size);
    }

    for (const session of this.sessions.values()) {
      const entry = registry.get(session.id);
      session.busy = entry?.busy === true;
      if (entry?.entrypoint) session.entrypoint = entry.entrypoint;
      if (entry?.cwd && !session.cwd) session.cwd = entry.cwd;
      if (entry?.title) session.registryTitle = entry.title;
      if (session.busy) this.#touch(session, now);
    }

    this.#prune(now);
    return this.#update(now);
  }

  /** Applies a Claude Code hook payload. Returns true when the card should change. */
  handleHook(payload) {
    const id = String(payload?.session_id ?? '');
    if (!SESSION_ID.test(id)) return false;
    const now = this.now();
    const event = String(payload.hook_event_name || '');
    const session = this.sessions.get(id) || this.#session(id);
    this.lastHookAt = now;
    session.hookAt = now;
    if (typeof payload.cwd === 'string' && payload.cwd.trim()) session.cwd = payload.cwd;

    const transcript = typeof payload.transcript_path === 'string' ? payload.transcript_path : '';
    if (!session.path && path.isAbsolute(transcript) && path.basename(transcript) === `${id}.jsonl`) {
      try {
        const { size } = fs.statSync(transcript);
        this.#tail(session, transcript, size);
      } catch {}
    }

    if (event === 'SessionEnd') {
      session.endedAt = now;
      session.busy = false;
      return this.#update(now);
    }

    if (event === 'SessionStart' || event === 'UserPromptSubmit') {
      session.endedAt = null;
      session.promptAt = now;
    }
    const file = editedFileFromHook(payload);
    if (file) session.file = file;
    this.#touch(session, now);
    return this.#update(now);
  }

  /**
   * Stores the focused session reported by the SSH helper on `host`.
   * Remote clocks are mapped onto the local one through the helper's `now`.
   */
  applyRemote(host, result, receivedAt = this.now()) {
    if (!result?.ok) {
      this.remotes.delete(host);
      return this.#update(receivedAt);
    }
    const offset = Number.isFinite(result.now) ? receivedAt - result.now : 0;
    const shift = (value) => (Number.isFinite(value) && value > 0 ? value + offset : null);
    this.remotes.set(host, {
      receivedAt,
      active: result.active === true,
      sessionId: typeof result.sessionId === 'string' ? result.sessionId : null,
      cwd: typeof result.cwd === 'string' ? result.cwd : null,
      project: typeof result.project === 'string' && result.project.trim() ? result.project.trim().slice(0, MAX_PROJECT) : null,
      file: typeof result.file === 'string' && result.file.trim() ? displayPath(result.file, null) : null,
      title: sanitizeTaskTitle(result.title),
      busy: result.busy === true,
      entrypoint: typeof result.entrypoint === 'string' ? result.entrypoint : null,
      startedAt: shift(result.startedAt),
      focusAt: shift(result.focusAt),
      activityAt: shift(result.activityAt),
    });
    return this.#update(receivedAt);
  }

  clearRemote(host) {
    if (!this.remotes.delete(host)) return false;
    return this.#update(this.now());
  }

  /** The focused session, or `{ active: false }` when Claude Code is idle. */
  snapshot() {
    return this.current || { active: false };
  }

  /** Every session considered active right now, local and remote. */
  candidates(now = this.now()) {
    const output = [];
    for (const session of this.sessions.values()) {
      if (!this.#isActive(session, now)) continue;
      const project = resolveProject(session.cwd, session.file, this.projectOptions);
      output.push({
        sessionId: session.id,
        workspace: null,
        cwd: session.cwd,
        project,
        file: session.file ? displayFile(session.file, session.cwd, project) : null,
        title: sanitizeTaskTitle(session.title || session.registryTitle),
        busy: session.busy,
        surface: surfaceFromEntrypoint(session.entrypoint),
        source: session.hookAt && session.hookAt >= (session.lastAt ?? 0) ? 'claude-hook' : 'claude-transcript',
        startedAt: session.firstAt,
        focusAt: session.promptAt,
        activityAt: session.lastAt,
      });
    }
    for (const [host, remote] of this.remotes) {
      if (!remote.active || now - remote.receivedAt > REMOTE_STALE_MS) continue;
      output.push({
        sessionId: remote.sessionId,
        workspace: host,
        cwd: remote.cwd,
        project: remote.project,
        file: remote.file,
        title: remote.title,
        busy: remote.busy,
        surface: surfaceFromEntrypoint(remote.entrypoint),
        source: 'claude-remote',
        startedAt: remote.startedAt,
        focusAt: remote.focusAt,
        activityAt: remote.activityAt ?? remote.receivedAt,
      });
    }
    return output;
  }

  #session(id) {
    const session = newSession(id);
    this.sessions.set(id, session);
    return session;
  }

  #isActive(session, now) {
    if (session.endedAt && !session.busy && (session.lastAt ?? 0) <= session.endedAt + END_GRACE_MS) return false;
    return session.busy || (session.lastAt !== null && now - session.lastAt <= this.idleMs);
  }

  #touch(session, at) {
    if (!Number.isFinite(at)) return;
    if (session.lastAt === null || at - session.lastAt > this.idleMs) {
      // A gap longer than the idle window starts a new burst of work.
      if (session.lastAt === null || at > session.lastAt) session.firstAt = at;
    }
    if (session.lastAt === null || at > session.lastAt) session.lastAt = at;
  }

  #tail(session, filePath, size) {
    if (session.path !== filePath || !session.tail) {
      const offset = Math.max(0, size - this.initialTailBytes);
      session.path = filePath;
      session.tail = newTailState({ offset, skipPartial: offset > 0 });
    }
    let lines;
    try {
      lines = readNewLines(filePath, session.tail, size);
    } catch (error) {
      this.log(`Claude Code transcript error: ${error.message}`);
      return;
    }
    for (const line of lines || []) this.#record(session, line);
  }

  #record(session, line) {
    let record;
    try {
      record = JSON.parse(line);
    } catch {
      return;
    }
    if (!record || typeof record !== 'object') return;

    if (record.type === 'custom-title' && typeof record.customTitle === 'string') session.title = record.customTitle;
    else if (record.type === 'summary' && typeof record.summary === 'string' && !session.title) session.title = record.summary;
    if (typeof record.cwd === 'string' && record.cwd && !record.isSidechain) session.cwd = record.cwd;
    if (typeof record.entrypoint === 'string') session.entrypoint = record.entrypoint;

    const at = timestampOf(record);
    if (at === null) return;
    this.#touch(session, at);
    if (isHumanPrompt(record) && (session.promptAt === null || at > session.promptAt)) session.promptAt = at;
    const file = editedFileFromRecord(record);
    if (file) session.file = file;
  }

  #readRegistry() {
    const output = new Map();
    let names;
    try {
      names = fs.readdirSync(this.registryDirectory).filter((name) => /^\d+\.json$/.test(name)).slice(0, REGISTRY_FILE_LIMIT);
    } catch {
      return output;
    }
    for (const name of names) {
      let entry;
      try {
        entry = JSON.parse(fs.readFileSync(path.join(this.registryDirectory, name), 'utf8'));
      } catch {
        continue;
      }
      if (!entry || !SESSION_ID.test(String(entry.sessionId ?? ''))) continue;
      if (!this.isProcessAlive(Number(entry.pid))) continue;
      output.set(entry.sessionId, {
        sessionId: entry.sessionId,
        cwd: typeof entry.cwd === 'string' ? entry.cwd : null,
        busy: entry.status === 'busy',
        entrypoint: typeof entry.entrypoint === 'string' ? entry.entrypoint : null,
        title: typeof entry.name === 'string' ? entry.name : null,
      });
    }
    return output;
  }

  #recentTranscripts(now) {
    const output = new Map();
    let directories;
    try {
      directories = fs.readdirSync(this.projectsDirectory, { withFileTypes: true });
    } catch {
      return output;
    }
    for (const directory of directories) {
      if (!directory.isDirectory()) continue;
      const folder = path.join(this.projectsDirectory, directory.name);
      let files;
      try {
        files = fs.readdirSync(folder);
      } catch {
        continue;
      }
      for (const name of files) {
        const id = name.endsWith('.jsonl') ? name.slice(0, -6) : '';
        if (!SESSION_ID.test(id)) continue;
        const filePath = path.join(folder, name);
        try {
          if (now - fs.statSync(filePath).mtimeMs <= this.idleMs) output.set(id, filePath);
        } catch {}
      }
    }
    return output;
  }

  #prune(now) {
    for (const [id, session] of this.sessions) {
      if (!session.busy && (session.lastAt === null || now - session.lastAt > this.idleMs * 3)) this.sessions.delete(id);
    }
    if (this.sessions.size <= SESSION_LIMIT) return;
    const ordered = [...this.sessions.values()].sort((left, right) => (right.lastAt ?? 0) - (left.lastAt ?? 0));
    this.sessions = new Map(ordered.slice(0, SESSION_LIMIT).map((session) => [session.id, session]));
  }

  #update(now) {
    const candidates = this.candidates(now);
    const focused = candidates.sort((left, right) =>
      (right.focusAt ?? 0) - (left.focusAt ?? 0) || (right.activityAt ?? 0) - (left.activityAt ?? 0))[0];

    if (!focused) this.runStartedAt = null;
    else if (!this.runStartedAt) {
      const starts = candidates.map((item) => item.startedAt).filter((value) => Number.isFinite(value) && value <= now);
      this.runStartedAt = starts.length ? Math.min(...starts) : now;
    }

    this.current = focused
      ? {
        active: true,
        ...focused,
        startedAt: this.runStartedAt,
        focusAt: focused.focusAt ?? focused.activityAt,
        sessions: candidates.length,
      }
      : { active: false };

    const { activityAt, focusAt, sessions, ...stable } = this.current;
    const signature = JSON.stringify(stable);
    if (signature === this.lastSignature) return false;
    this.lastSignature = signature;
    this.emit('change', this.current);
    return true;
  }
}

module.exports = {
  ClaudeCodeMonitor,
  claudeHomeDirectory,
  displayFile,
  editedFileFromHook,
  editedFileFromRecord,
  isHumanPrompt,
  projectDirectoryName,
  resolveProject,
  surfaceFromEntrypoint,
  DEFAULT_IDLE_MS,
  EDIT_TOOLS,
};
