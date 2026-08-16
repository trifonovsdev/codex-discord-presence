'use strict';

const http = require('http');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { execFile } = require('child_process');
const { StringDecoder } = require('string_decoder');

const { readConfig, patchConfig } = require('./config');
const { createLogger } = require('./logger');
const { DiscordIpc } = require('./discord-ipc');
const { DesktopSelection, parseDesktopLogLine } = require('./desktop-selection');
const { buildActivity, stringsFor } = require('./presence');
const { readThreadContext } = require('./codex-state');
const { configuredRemotes, remoteForCwd: selectRemoteForCwd } = require('./remotes');
const {
  displayPath,
  extractEditedFile,
  fileForProject,
  projectFromCwd,
  resolveSessionProject,
  toolPayloadFromRecord,
} = require('./codex-paths');

const VERSION = '2.3.0';

const CONFIG_PATH = process.env.CODEX_PRESENCE_CONFIG || path.join(__dirname, 'config.json');
const TEST_MODE = process.env.CODEX_PRESENCE_TEST === '1';

const HOST = '127.0.0.1';
const LOG_PATH = path.join(__dirname, 'presence.log');
const USER_HOME = process.env.USERPROFILE || os.homedir();
const CODEX_HOME = process.env.CODEX_HOME || path.join(USER_HOME, '.codex');
const SESSIONS_DIR = path.join(CODEX_HOME, 'sessions');
const PACKAGES_DIR = path.join(process.env.LOCALAPPDATA || path.join(USER_HOME, 'AppData', 'Local'), 'Packages');

const DESKTOP_LOG_MAX_AGE_MS = 3 * 24 * 60 * 60 * 1000;
const DESKTOP_LOG_FILE_LIMIT = 100;
const SESSION_STATE_LIMIT = 64;
const THREAD_CONTEXT_REFRESH_MS = 30_000;
const READ_CHUNK_BYTES = 1024 * 1024;
const READ_BUDGET_BYTES = 8 * 1024 * 1024;
const MAX_PARTIAL_LINE_BYTES = 1024 * 1024;
const HOOK_BODY_LIMIT = 256 * 1024;
const CONTROL_BODY_LIMIT = 8 * 1024;
const APP_POLL_INTERVAL_MS = 15_000;
const DESKTOP_POLL_INTERVAL_MS = 2000;
const SESSION_POLL_INTERVAL_MS = 2500;
const THREAD_ID = /^[0-9a-f-]{20,64}$/i;

const log = createLogger(LOG_PATH);
const { config: CONFIG, warnings: CONFIG_WARNINGS } = readConfig(CONFIG_PATH);
for (const warning of CONFIG_WARNINGS) log(`Config warning: ${warning}`);

const PORT = CONFIG.port;
const APP_PROCESS = CONFIG.appProcess;
const REMOTE_POLL_INTERVAL = CONFIG.remote.pollIntervalMs;
const REMOTE_HOSTS = configuredRemotes(CONFIG, CONFIG.remote.monitorPath);
const TEXT = stringsFor(CONFIG.language);

const selection = new DesktopSelection();
const sessionStates = new Map();
const desktopLogStates = new Map();

let currentProject = null;
let currentFile = null;
let currentTaskTitle = null;
let currentSessionId = null;
let activeSessionPath = null;
let presenceSource = 'startup';
let presenceEnabled = CONFIG.presenceEnabled;
let appIsRunning = true;
let appSignature = '';
let codexStartedAt = null;
let updateTimer = null;
let remotePollRunning = false;
let lastRemotePollAt = null;
let lastRemoteError = null;
let lastLoggedRemoteError = null;
let selectedRemoteName = null;
let lastHookAt = null;
let publishedDesktopRouteKey = null;

const ipc = new DiscordIpc({ clientId: CONFIG.clientId, log });
ipc.on('ready', () => (presenceEnabled ? queuePresence(true) : ipc.setActivity(null, { immediate: true })));

function remoteForCwd(cwd) {
  return selectRemoteForCwd(cwd, REMOTE_HOSTS);
}

// ── Presence ────────────────────────────────────────────────────────────────

function currentActivity() {
  return buildActivity({
    project: currentProject,
    task: currentTaskTitle,
    file: currentFile,
    workspace: selectedRemoteName,
    privacy: CONFIG.privacy,
    language: CONFIG.language,
    startedAt: codexStartedAt,
    largeImageKey: CONFIG.largeImageKey,
    largeImageText: CONFIG.largeImageText,
  });
}

/** Coalesces bursts of activity so a single edit does not fan out into updates. */
function queuePresence(immediate = false) {
  clearTimeout(updateTimer);
  if (immediate) {
    publishPresence();
    return;
  }
  updateTimer = setTimeout(publishPresence, 900);
  updateTimer.unref?.();
}

function publishPresence() {
  if (!presenceEnabled || !appIsRunning) {
    ipc.setActivity(null, { immediate: true });
    return;
  }
  ipc.setActivity(currentActivity());
}

function setPresenceEnabled(enabled) {
  presenceEnabled = Boolean(enabled);
  try {
    patchConfig(CONFIG_PATH, { presenceEnabled });
  } catch (error) {
    log(`Could not persist presence state: ${error.message}`);
  }
  if (presenceEnabled) queuePresence(true);
  else ipc.setActivity(null, { immediate: true });
  log(`Presence ${presenceEnabled ? 'enabled' : 'paused'} by local control`);
}

/**
 * Commits a new project/file pair and refreshes the card.
 *
 * `claimSource` is only set by the remote monitor: it has to be able to take
 * ownership of a card that the transcript monitor already populated with the
 * same values. Every other monitor leaves a more specific source in place.
 */
function applyActivity(project, file, source, { claimSource = false, replaceProject = false, taskTitle = currentTaskTitle } = {}) {
  const nextProject = replaceProject ? (project || null) : (project || currentProject);
  const nextFile = file ?? null;
  const nextTaskTitle = taskTitle || null;
  const changed = nextProject !== currentProject
    || nextFile !== currentFile
    || nextTaskTitle !== currentTaskTitle
    || (claimSource && presenceSource !== source);
  if (!changed) return false;
  currentProject = nextProject;
  currentFile = nextFile;
  currentTaskTitle = nextTaskTitle;
  presenceSource = source;
  queuePresence();
  return true;
}

// ── Incremental file tailing ────────────────────────────────────────────────

function newTailState() {
  return { offset: 0, remainder: '', decoder: new StringDecoder('utf8'), lastSeen: Date.now() };
}

/**
 * Reads everything appended since the previous call and returns whole lines.
 *
 * Reads are chunked and budgeted: the first pass over a multi-megabyte Codex
 * log used to be allocated as one contiguous buffer, and a truncated log used
 * to be re-read from the start.
 */
function readNewLines(filePath, state, size) {
  state.lastSeen = Date.now();
  if (size < state.offset) {
    state.offset = 0;
    state.remainder = '';
    state.decoder = new StringDecoder('utf8');
  }
  if (size === state.offset) return null;

  const lines = [];
  const buffer = Buffer.allocUnsafe(READ_CHUNK_BYTES);
  const descriptor = fs.openSync(filePath, 'r');
  try {
    let budget = READ_BUDGET_BYTES;
    while (state.offset < size && budget > 0) {
      const length = Math.min(READ_CHUNK_BYTES, size - state.offset, budget);
      const read = fs.readSync(descriptor, buffer, 0, length, state.offset);
      if (read <= 0) break;
      state.offset += read;
      budget -= read;

      const parts = `${state.remainder}${state.decoder.write(buffer.subarray(0, read))}`.split(/\r?\n/);
      state.remainder = parts.pop() ?? '';
      if (state.remainder.length > MAX_PARTIAL_LINE_BYTES) state.remainder = '';
      for (const part of parts) if (part) lines.push(part);
    }
  } finally {
    fs.closeSync(descriptor);
  }
  return lines;
}

function listFiles(dir, output) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return output;
  }
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) listFiles(fullPath, output);
    else if (entry.isFile()) output.push(fullPath);
  }
  return output;
}

function statFiles(paths, filter) {
  const output = [];
  for (const filePath of paths) {
    if (!filter(filePath)) continue;
    try {
      const stat = fs.statSync(filePath);
      output.push({ path: filePath, size: stat.size, mtimeMs: stat.mtimeMs });
    } catch {}
  }
  return output;
}

// ── Codex Desktop route monitor ─────────────────────────────────────────────

function listDesktopLogFiles() {
  let packages = [];
  try {
    packages = fs.readdirSync(PACKAGES_DIR, { withFileTypes: true });
  } catch {
    return [];
  }

  const cutoff = Date.now() - DESKTOP_LOG_MAX_AGE_MS;
  const candidates = [];
  for (const entry of packages) {
    if (!entry.isDirectory() || !/^OpenAI\.Codex_/i.test(entry.name)) continue;
    listFiles(path.join(PACKAGES_DIR, entry.name, 'LocalCache', 'Local', 'Codex', 'Logs'), candidates);
  }

  return statFiles(candidates, (filePath) => filePath.endsWith('.log'))
    .filter((file) => file.mtimeMs >= cutoff)
    .sort((left, right) => left.mtimeMs - right.mtimeMs)
    .slice(-DESKTOP_LOG_FILE_LIMIT);
}

function syncDesktopSelection() {
  const files = listDesktopLogFiles();
  const live = new Set(files.map((file) => file.path));
  for (const key of desktopLogStates.keys()) if (!live.has(key)) desktopLogStates.delete(key);

  const events = [];
  for (const file of files) {
    let state = desktopLogStates.get(file.path);
    if (!state) {
      state = newTailState();
      desktopLogStates.set(file.path, state);
    }
    try {
      const lines = readNewLines(file.path, state, file.size);
      if (lines) for (const line of lines) events.push(...parseDesktopLogLine(line));
    } catch (error) {
      log(`Desktop log monitor error: ${error.message}`);
    }
  }

  const switched = selection.ingest(events);
  let selected = selection.selected();
  if (!selected) return;

  // `ownerRoutePath` is also emitted by embedded/sidebar views. It cannot
  // replace the visible activity until a nearby cwd line proves navigation.
  if (!selection.selectedRouteConfirmed()) return;
  const routeKey = `${selected.threadId}:${selected.at}`;
  const newlyConfirmed = routeKey !== publishedDesktopRouteKey;
  if (newlyConfirmed) selectedRemoteName = null;

  const now = Date.now();
  if (newlyConfirmed || switched || now - (selected.contextReadAt || 0) >= THREAD_CONTEXT_REFRESH_MS) {
    const context = readThreadContext(selected.threadId, { codexHome: CODEX_HOME });
    selection.updateThread(selected.threadId, {
      contextReadAt: now,
      ...(context?.cwd && !selected.cwd ? { cwd: context.cwd } : {}),
      ...(context?.project ? { project: context.project } : {}),
      taskTitle: context?.title || null,
    });
    selected = selection.selected();
  }

  if ((newlyConfirmed || selected.project || selected.taskTitle || selected.lastFile) && applyActivity(
    selected.project,
    selected.lastFile,
    'desktop-route',
    { replaceProject: newlyConfirmed, taskTitle: selected.taskTitle || null },
  )) {
    log(`Desktop route -> thread=${selected.threadId}, project=${currentProject}, file=${currentFile ?? '—'}`);
  }
  publishedDesktopRouteKey = routeKey;
  if (newlyConfirmed) setImmediate(syncRemoteFile);
}

// ── Codex transcript monitor ────────────────────────────────────────────────

function processSessionRecord(state, line) {
  let record;
  try {
    record = JSON.parse(line);
  } catch {
    return;
  }

  const payload = record.payload || {};
  if (record.type === 'session_meta') {
    if (typeof payload.cwd === 'string') state.cwd = payload.cwd;
    state.threadId = payload.id || payload.session_id || state.threadId;
  }
  if (record.type === 'turn_context') {
    if (typeof payload.cwd === 'string') state.cwd = payload.cwd;
    if (Array.isArray(payload.workspace_roots)) state.workspaceRoots = payload.workspace_roots;
  }

  const toolPayload = toolPayloadFromRecord(record);
  if (!toolPayload) return;
  const editedFile = extractEditedFile({ ...toolPayload, cwd: state.cwd });
  if (editedFile) state.lastFile = editedFile;
}

function pruneSessionStates() {
  if (sessionStates.size <= SESSION_STATE_LIMIT) return;
  const ordered = [...sessionStates].sort((left, right) => right[1].lastSeen - left[1].lastSeen);
  sessionStates.clear();
  for (const [key, value] of ordered.slice(0, SESSION_STATE_LIMIT)) sessionStates.set(key, value);
}

function syncActiveSession() {
  const threadId = selection.confirmedSelected()?.threadId ?? null;
  // Narrowing by name before stat()ing matters: users accumulate thousands of
  // transcripts, and this runs every couple of seconds.
  const transcripts = listFiles(SESSIONS_DIR, []).filter((filePath) => filePath.endsWith('.jsonl'));
  const candidates = threadId ? transcripts.filter((filePath) => path.basename(filePath).includes(threadId)) : transcripts;
  const [latest] = statFiles(candidates, () => true).sort((left, right) => right.mtimeMs - left.mtimeMs);
  if (!latest) return;

  let state = sessionStates.get(latest.path);
  if (!state) {
    state = { ...newTailState(), cwd: null, workspaceRoots: [], lastFile: null, threadId: null, taskTitle: null, contextProject: null, contextReadAt: 0 };
    sessionStates.set(latest.path, state);
  }

  let lines;
  try {
    lines = readNewLines(latest.path, state, latest.size);
  } catch (error) {
    log(`Session monitor error: ${error.message}`);
    return;
  }
  pruneSessionStates();

  const switched = activeSessionPath !== latest.path;
  if (!lines && !switched) return;
  for (const line of lines || []) processSessionRecord(state, line);
  activeSessionPath = latest.path;

  const contextThreadId = threadId || state.threadId;
  const now = Date.now();
  if (contextThreadId && now - state.contextReadAt >= THREAD_CONTEXT_REFRESH_MS) {
    const context = readThreadContext(contextThreadId, { codexHome: CODEX_HOME });
    state.contextReadAt = now;
    state.taskTitle = context?.title || null;
    state.contextProject = context?.project || null;
    if (!state.cwd && context?.cwd) state.cwd = context.cwd;
  }

  const nextProject = resolveSessionProject(state, state.contextProject);
  const nextFile = fileForProject(state.lastFile, nextProject);
  const source = threadId ? 'desktop-route+session' : 'session-monitor';
  if (applyActivity(nextProject, nextFile, source, {
    replaceProject: switched,
    taskTitle: state.taskTitle || (switched ? null : currentTaskTitle),
  })) {
    if (threadId) selection.updateThread(threadId, { cwd: state.cwd || selection.threadState(threadId)?.cwd, project: currentProject, lastFile: currentFile });
    log(`Session monitor -> project=${currentProject ?? '—'}, file=${currentFile ?? '—'}`);
  }
}

// ── Remote workspace monitor ────────────────────────────────────────────────

function reportRemoteError(message) {
  lastRemoteError = message;
  if (lastRemoteError === lastLoggedRemoteError) return;
  log(`Remote monitor error: ${lastRemoteError}`);
  lastLoggedRemoteError = lastRemoteError;
}

function syncRemoteFile() {
  const confirmed = selection.confirmedSelected();
  const requestedThreadId = confirmed?.threadId;
  const requestedRouteAt = confirmed?.at;
  if (!requestedThreadId || !THREAD_ID.test(requestedThreadId)) return;
  if (remotePollRunning) return;

  const desktopState = selection.threadState(requestedThreadId);
  if (!desktopState?.cwd || !desktopState.cwd.startsWith('/')) {
    selectedRemoteName = null;
    return;
  }

  const remote = remoteForCwd(desktopState.cwd);
  if (!remote) {
    selectedRemoteName = null;
    if (REMOTE_HOSTS.length > 1) lastRemoteError = 'No remote host matches the selected workspace root';
    return;
  }
  selectedRemoteName = remote.name;

  remotePollRunning = true;
  execFile(
    'ssh.exe',
    ['-T', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=6', remote.host, 'python3', remote.monitorPath, requestedThreadId],
    { windowsHide: true, timeout: 10_000, maxBuffer: 1024 * 1024 },
    (error, stdout, stderr) => {
      remotePollRunning = false;
      const stillConfirmed = selection.confirmedSelected();
      if (stillConfirmed?.threadId !== requestedThreadId || stillConfirmed.at !== requestedRouteAt) {
        setImmediate(syncRemoteFile);
        return;
      }

      lastRemotePollAt = new Date().toISOString();
      if (error) {
        reportRemoteError(String(stderr || error.message).trim().slice(-240));
        return;
      }

      let result;
      try {
        const line = String(stdout).trim().split(/\r?\n/).filter(Boolean).at(-1);
        result = JSON.parse(line || '{}');
      } catch (parseError) {
        reportRemoteError(`Invalid remote response: ${parseError.message}`);
        return;
      }

      if (!result.ok || result.threadId !== requestedThreadId) {
        lastRemoteError = String(result.error || 'remote-session-mismatch');
        return;
      }

      lastRemoteError = null;
      lastLoggedRemoteError = null;
      const hasProject = Object.hasOwn(result, 'project');
      const state = selection.updateThread(requestedThreadId, {
        ...(result.cwd ? { cwd: result.cwd } : {}),
        ...(hasProject ? { project: result.project ? String(result.project).slice(0, 60) : null } : {}),
        ...(result.file ? { lastFile: displayPath(result.file, result.cwd) } : {}),
      });

      if (applyActivity(state.project, state.lastFile, 'desktop-route+remote-session', {
        claimSource: true,
        replaceProject: hasProject,
        taskTitle: state.taskTitle || currentTaskTitle,
      })) {
        log(`Remote session -> thread=${requestedThreadId}, project=${currentProject ?? '—'}, file=${currentFile ?? '—'}`);
      }
    },
  );
}

// ── Codex Desktop process monitor ───────────────────────────────────────────

/**
 * Detects whether Codex Desktop is running with `tasklist`, a native binary
 * that returns in a few milliseconds. PowerShell — which costs a few hundred
 * milliseconds of CPU per launch and used to run every fifteen seconds — is
 * now only spawned when the set of process ids actually changes and the
 * session start timestamp has to be re-read.
 */
function checkCodexApp() {
  execFile(
    'tasklist.exe',
    ['/NH', '/FO', 'CSV', '/FI', `IMAGENAME eq ${APP_PROCESS}.exe`],
    { windowsHide: true, timeout: 8000 },
    (error, stdout) => {
      if (error) {
        log(`Codex process check failed: ${error.message}`);
        return;
      }

      const pids = [...String(stdout).matchAll(new RegExp(`^"${APP_PROCESS}\\.exe","(\\d+)"`, 'gim'))]
        .map((match) => Number(match[1]))
        .sort((left, right) => left - right);
      const signature = pids.join(',');

      if (!pids.length) {
        if (!appIsRunning) return;
        appIsRunning = false;
        appSignature = '';
        codexStartedAt = null;
        ipc.setActivity(null, { immediate: true });
        log('Codex app closed; presence timer cleared');
        return;
      }

      appIsRunning = true;
      if (signature === appSignature && codexStartedAt) return;
      appSignature = signature;
      readProcessStart();
    },
  );
}

/** Reads the start time of the oldest matching process, which anchors the session timer. */
function readProcessStart() {
  const command = `$p = Get-Process -Name ${APP_PROCESS} -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1; if ($p) { ([DateTimeOffset]$p.StartTime).ToUnixTimeSeconds() }`;
  execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, timeout: 8000 }, (error, stdout) => {
    if (error) {
      log(`Codex start time lookup failed: ${error.message}`);
      return;
    }
    const startedAt = Number.parseInt(String(stdout).trim(), 10);
    if (!Number.isSafeInteger(startedAt) || startedAt <= 0 || startedAt === codexStartedAt) return;
    codexStartedAt = startedAt;
    queuePresence(true);
    log(`Codex app timer started at ${new Date(codexStartedAt * 1000).toISOString()}`);
  });
}

// ── Hook endpoint ───────────────────────────────────────────────────────────

function handleHook(payload) {
  const event = String(payload.hook_event_name || payload.event || '');
  const payloadThreadId = payload.session_id || payload.thread_id || null;
  const selectedThreadId = selection.confirmedSelected()?.threadId ?? null;
  if (selectedThreadId && payloadThreadId !== selectedThreadId) {
    log(`Ignored background hook ${event || 'unknown'} for thread=${payloadThreadId || 'unknown'}`);
    return;
  }
  lastHookAt = new Date().toISOString();

  const context = payloadThreadId ? readThreadContext(payloadThreadId, { codexHome: CODEX_HOME }) : null;
  const sessionChanged = Boolean(payloadThreadId && payloadThreadId !== currentSessionId);
  let project = context?.project || projectFromCwd(payload.cwd) || (sessionChanged ? null : currentProject);
  let file = sessionChanged ? null : currentFile;
  if (sessionChanged) currentSessionId = payloadThreadId;

  const editedFile = extractEditedFile(payload);
  if (editedFile) {
    project = resolveSessionProject({ cwd: payload.cwd, workspaceRoots: [], lastFile: editedFile }, context?.project) || project;
    file = fileForProject(editedFile, project);
  }

  if (applyActivity(project, file, 'hook', {
    replaceProject: sessionChanged,
    taskTitle: context?.title || (sessionChanged ? null : currentTaskTitle),
  })) {
    log(`Hook ${event || 'unknown'} -> project=${currentProject ?? '—'}, file=${currentFile ?? '—'}`);
  }
}

// ── HTTP control surface ────────────────────────────────────────────────────

const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '::1', '[::1]']);

/**
 * The daemon binds to loopback, but a browser on the same machine can still
 * reach it. Requiring a loopback Host header blocks DNS rebinding, and
 * rejecting requests that carry a browser Origin blocks drive-by CSRF against
 * `/control`.
 */
function isTrustedRequest(req) {
  const hostHeader = String(req.headers.host || '');
  const host = hostHeader.startsWith('[') ? hostHeader.slice(0, hostHeader.indexOf(']') + 1) : hostHeader.split(':')[0];
  if (!LOOPBACK_HOSTS.has(host.toLowerCase())) return false;
  if (req.headers.origin) return false;
  if (String(req.headers['sec-fetch-site'] || 'none') !== 'none') return false;
  return true;
}

function sendJson(res, status, body, { close = false } = {}) {
  const payload = JSON.stringify(body);
  const headers = {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(payload),
    'x-content-type-options': 'nosniff',
    'cache-control': 'no-store',
  };
  // Closing the connection stops an oversized upload without cutting off the
  // response the way destroying the socket outright would.
  if (close) headers.connection = 'close';
  res.writeHead(status, headers);
  res.end(payload);
}

/** Reads a JSON body, rejecting anything oversized instead of truncating it. */
function readJsonBody(req, res, limit) {
  return new Promise((resolve) => {
    if (!String(req.headers['content-type'] || '').toLowerCase().startsWith('application/json')) {
      sendJson(res, 415, { ok: false, error: 'unsupported-media-type' }, { close: true });
      resolve(null);
      return;
    }

    let size = 0;
    let rejected = false;
    const chunks = [];
    req.on('data', (chunk) => {
      if (rejected) return;
      size += chunk.length;
      if (size > limit) {
        rejected = true;
        chunks.length = 0;
        sendJson(res, 413, { ok: false, error: 'payload-too-large' }, { close: true });
        resolve(null);
        return;
      }
      chunks.push(chunk);
    });
    req.on('error', () => resolve(null));
    req.on('end', () => {
      if (res.writableEnded) return;
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}'));
      } catch (error) {
        sendJson(res, 400, { ok: false, error: `invalid-json: ${error.message}` });
        resolve(null);
      }
    });
  });
}

function healthSnapshot() {
  const selected = selection.selected();
  return {
    ok: true,
    version: VERSION,
    language: CONFIG.language,
    rpcReady: ipc.ready,
    rpcPublished: ipc.published,
    rpcError: ipc.lastError,
    presenceEnabled,
    project: currentProject,
    task: CONFIG.privacy.showTaskTitle ? currentTaskTitle : null,
    taskTitleShared: CONFIG.privacy.showTaskTitle,
    file: currentFile,
    source: presenceSource,
    codexRunning: appIsRunning,
    codexStartedAt: codexStartedAt ? new Date(codexStartedAt * 1000).toISOString() : null,
    activeSession: activeSessionPath ? path.basename(activeSessionPath) : null,
    selectedThreadId: selected?.threadId ?? null,
    selectedRouteKind: selected?.kind ?? null,
    selectedRouteAt: selected?.at ? new Date(selected.at).toISOString() : null,
    remotePollRunning,
    lastRemotePollAt,
    lastRemoteError,
    lastHookAt,
    remoteConfigured: REMOTE_HOSTS.length > 0,
    remoteHosts: REMOTE_HOSTS.map((remote) => remote.name),
    selectedRemote: selectedRemoteName,
    knownThreadProjects: selection.knownProjects(),
    configWarnings: CONFIG_WARNINGS,
    details: presenceEnabled && appIsRunning ? currentActivity().details : null,
    lastRpcAck: ipc.lastAck,
  };
}

async function handleControl(req, res) {
  const body = await readJsonBody(req, res, CONTROL_BODY_LIMIT);
  if (body === null) return;

  const action = String(body.action || '').toLowerCase();
  if (action === 'shutdown') {
    sendJson(res, 202, { ok: true, action });
    shutdown();
    return;
  }
  if (action === 'pause') setPresenceEnabled(false);
  else if (action === 'resume') setPresenceEnabled(true);
  else if (action === 'toggle') setPresenceEnabled(!presenceEnabled);
  else {
    sendJson(res, 400, { ok: false, error: 'unknown-action' });
    return;
  }
  sendJson(res, 200, { ok: true, action, presenceEnabled });
}

const server = http.createServer((req, res) => {
  if (!isTrustedRequest(req)) {
    sendJson(res, 403, { ok: false, error: 'forbidden' });
    return;
  }

  if (req.method === 'GET' && req.url === '/health') {
    sendJson(res, 200, healthSnapshot());
    return;
  }

  if (req.method === 'POST' && req.url === '/control') {
    handleControl(req, res).catch((error) => {
      log(`Control error: ${error.message}`);
      if (!res.writableEnded) sendJson(res, 500, { ok: false, error: 'internal-error' });
    });
    return;
  }

  if (req.method === 'POST' && req.url === '/hook') {
    readJsonBody(req, res, HOOK_BODY_LIMIT).then((body) => {
      if (body === null) return;
      try {
        handleHook(body);
        res.writeHead(204);
        res.end();
      } catch (error) {
        log(`Hook handling failed: ${error.message}`);
        sendJson(res, 400, { ok: false, error: error.message });
      }
    });
    return;
  }

  sendJson(res, 404, { ok: false, error: 'not-found' });
});

server.on('error', (error) => {
  if (error.code === 'EADDRINUSE') {
    log(`Port ${PORT} is already in use; another daemon is running`);
    process.exit(0);
  }
  log(`HTTP server error: ${error.message}`);
});

server.listen(PORT, HOST, () => {
  log(`Presence daemon ${VERSION} started on ${HOST}:${PORT}`);
  queuePresence();
  if (TEST_MODE) return;

  syncDesktopSelection();
  syncActiveSession();
  ipc.connect();
  checkCodexApp();
  for (const [handler, interval] of [
    [checkCodexApp, APP_POLL_INTERVAL_MS],
    [syncDesktopSelection, DESKTOP_POLL_INTERVAL_MS],
    [syncActiveSession, SESSION_POLL_INTERVAL_MS],
    [syncRemoteFile, REMOTE_POLL_INTERVAL],
  ]) {
    setInterval(handler, interval).unref();
  }
});

let shuttingDown = false;
function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  clearTimeout(updateTimer);
  ipc.setActivity(null, { immediate: true });
  server.close();
  setTimeout(() => {
    ipc.destroy();
    process.exit(0);
  }, 200).unref();
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('uncaughtException', (error) => log(`Uncaught exception: ${error.stack || error.message}`));
process.on('unhandledRejection', (reason) => log(`Unhandled rejection: ${reason}`));
