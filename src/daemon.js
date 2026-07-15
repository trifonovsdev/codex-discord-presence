'use strict';

const http = require('http');
const net = require('net');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { execFile } = require('child_process');
const { randomUUID } = require('crypto');
const { configuredRemotes, remoteForCwd: selectRemoteForCwd } = require('./remotes');

const VERSION = '2.0.0';

const CONFIG_PATH = process.env.CODEX_PRESENCE_CONFIG || path.join(__dirname, 'config.json');
const TEST_MODE = process.env.CODEX_PRESENCE_TEST === '1';
const DEFAULT_CONFIG = {
  clientId: '1526968377048956938',
  port: 37642,
  largeImageKey: 'codex',
  largeImageText: 'OpenAI Codex',
  appProcess: 'ChatGPT',
  presenceEnabled: true,
  privacy: {
    preset: 'standard',
    showProject: true,
    showFile: true,
    showTimer: true,
    fileMode: 'relative',
  },
  remote: {
    host: '',
    hosts: [],
    monitorPath: '~/.local/share/CodexDiscordPresence/remote-monitor.py',
    pollIntervalMs: 7000,
  },
};

function loadConfig() {
  let userConfig = {};
  try {
    userConfig = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
  } catch (error) {
    if (error.code !== 'ENOENT') throw new Error(`Invalid config.json: ${error.message}`);
  }
  return {
    ...DEFAULT_CONFIG,
    ...userConfig,
    privacy: { ...DEFAULT_CONFIG.privacy, ...(userConfig.privacy || {}) },
    remote: { ...DEFAULT_CONFIG.remote, ...(userConfig.remote || {}) },
  };
}

const CONFIG = loadConfig();
const CLIENT_ID = String(CONFIG.clientId || DEFAULT_CONFIG.clientId);
const PORT = Number.isInteger(Number(CONFIG.port)) ? Number(CONFIG.port) : DEFAULT_CONFIG.port;
const HOST = '127.0.0.1';
const LOG_PATH = path.join(__dirname, 'presence.log');
const USER_HOME = process.env.USERPROFILE || os.homedir();
const CODEX_HOME = process.env.CODEX_HOME || path.join(USER_HOME, '.codex');
const SESSIONS_DIR = path.join(CODEX_HOME, 'sessions');
const PACKAGES_DIR = path.join(process.env.LOCALAPPDATA || path.join(USER_HOME, 'AppData', 'Local'), 'Packages');
const REMOTE_POLL_INTERVAL = Math.max(3000, Number(CONFIG.remote.pollIntervalMs) || DEFAULT_CONFIG.remote.pollIntervalMs);
const APP_PROCESS = /^[A-Za-z0-9_.-]+$/.test(String(CONFIG.appProcess || ''))
  ? String(CONFIG.appProcess).replace(/\.exe$/i, '')
  : DEFAULT_CONFIG.appProcess;

let rpc = null;
let rpcReady = false;
let reconnectTimer = null;
let inputBuffer = Buffer.alloc(0);
let pendingPresence = null;
let lastPresenceJson = '';
let lastRpcAck = null;
let updateTimer = null;
let currentSessionId = null;
let currentProject = 'Локальная задача';
let currentFile = '—';
let appIsRunning = true;
let codexStartedAt = null;
let presenceEnabled = CONFIG.presenceEnabled !== false;
let activeSessionPath = null;
let presenceSource = 'startup';
const sessionStates = new Map();
const desktopLogStates = new Map();
const desktopEvents = [];
const threadDesktopStates = new Map();
let selectedThreadId = null;
let selectedRouteKind = null;
let selectedRouteAt = 0;
let remotePollRunning = false;
let lastRemotePollAt = null;
let lastRemoteError = null;
let lastLoggedRemoteError = null;
let selectedRemoteName = null;

const PRIVACY_PRESETS = {
  minimal: { showProject: true, showFile: false, showTimer: true, fileMode: 'name' },
  standard: { showProject: true, showFile: true, showTimer: true, fileMode: 'relative' },
  detailed: { showProject: true, showFile: true, showTimer: true, fileMode: 'relative' },
};

function privacySettings() {
  const preset = PRIVACY_PRESETS[String(CONFIG.privacy.preset || '').toLowerCase()] || PRIVACY_PRESETS.standard;
  return { ...preset, ...CONFIG.privacy };
}

const REMOTE_HOSTS = configuredRemotes(CONFIG, DEFAULT_CONFIG.remote.monitorPath);

function remoteForCwd(cwd) {
  return selectRemoteForCwd(cwd, REMOTE_HOSTS);
}

function log(message) {
  const line = `${new Date().toISOString()} ${message}\n`;
  try {
    fs.appendFileSync(LOG_PATH, line, 'utf8');
  } catch {}
}

function frame(op, payload) {
  const body = Buffer.from(JSON.stringify(payload), 'utf8');
  const header = Buffer.alloc(8);
  header.writeInt32LE(op, 0);
  header.writeInt32LE(body.length, 4);
  return Buffer.concat([header, body]);
}

function writeRpc(op, payload) {
  if (!rpc || rpc.destroyed) return false;
  try {
    rpc.write(frame(op, payload));
    return true;
  } catch (error) {
    log(`RPC write failed: ${error.message}`);
    return false;
  }
}

function handleRpcData(chunk) {
  inputBuffer = Buffer.concat([inputBuffer, chunk]);
  while (inputBuffer.length >= 8) {
    const op = inputBuffer.readInt32LE(0);
    const length = inputBuffer.readInt32LE(4);
    if (inputBuffer.length < 8 + length) return;

    const raw = inputBuffer.subarray(8, 8 + length).toString('utf8');
    inputBuffer = inputBuffer.subarray(8 + length);

    if (op !== 1) continue;
    try {
      const message = JSON.parse(raw);
      if (message.evt === 'READY') {
        rpcReady = true;
        log('Discord RPC ready');
        if (presenceEnabled) flushPresence(true);
        else clearPresence();
      } else if (message.evt === 'ERROR') {
        log(`Discord RPC error: ${raw}`);
      } else if (message.cmd === 'SET_ACTIVITY') {
        lastRpcAck = new Date().toISOString();
        log('Presence updated in Discord');
      }
    } catch (error) {
      log(`RPC JSON error: ${error.message}`);
    }
  }
}

function tryPipe(index = 0) {
  if (index > 9) {
    scheduleReconnect();
    return;
  }

  const socket = net.createConnection(`\\\\?\\pipe\\discord-ipc-${index}`);
  let connected = false;

  socket.once('connect', () => {
    connected = true;
    rpc = socket;
    rpcReady = false;
    inputBuffer = Buffer.alloc(0);
    socket.on('data', handleRpcData);
    socket.on('close', handleRpcClose);
    socket.on('error', (error) => log(`Discord socket error: ${error.message}`));
    writeRpc(0, { v: 1, client_id: CLIENT_ID });
  });

  socket.once('error', () => {
    if (!connected) tryPipe(index + 1);
  });
}

function handleRpcClose() {
  rpc = null;
  rpcReady = false;
  log('Discord RPC disconnected');
  scheduleReconnect();
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    tryPipe();
  }, 5000);
}

function currentActivity() {
  const privacy = privacySettings();
  const visibleProject = privacy.showProject ? currentProject : 'Codex Desktop';
  let visibleFile = currentFile;
  if (!privacy.showFile) visibleFile = 'Working in Codex';
  else if (privacy.fileMode === 'name') visibleFile = String(currentFile).replaceAll('\\', '/').split('/').at(-1) || currentFile;
  const activity = {
    details: privacy.preset === 'detailed' ? `Project: ${visibleProject}` : `Проект: ${visibleProject}`,
    state: privacy.preset === 'detailed' ? `Editing: ${visibleFile}` : `Файл: ${visibleFile}`,
    instance: false,
  };
  if (CONFIG.largeImageKey) {
    activity.assets = {
      large_image: String(CONFIG.largeImageKey),
      large_text: String(CONFIG.largeImageText || 'OpenAI Codex'),
    };
  }
  if (codexStartedAt && privacy.showTimer) activity.timestamps = { start: codexStartedAt };
  return activity;
}

function queuePresence() {
  pendingPresence = currentActivity();
  clearTimeout(updateTimer);
  updateTimer = setTimeout(() => flushPresence(false), 900);
}

function flushPresence(force) {
  if (!rpcReady || !pendingPresence || !appIsRunning || !presenceEnabled) return;
  const json = JSON.stringify(pendingPresence);
  if (!force && json === lastPresenceJson) return;

  writeRpc(1, {
    cmd: 'SET_ACTIVITY',
    args: { pid: process.pid, activity: pendingPresence },
    nonce: randomUUID(),
  });
  lastPresenceJson = json;
}

function clearPresence() {
  if (!rpcReady) return;
  writeRpc(1, {
    cmd: 'SET_ACTIVITY',
    args: { pid: process.pid, activity: null },
    nonce: randomUUID(),
  });
  lastPresenceJson = '';
}

function persistPresenceEnabled(enabled) {
  try {
    let document = {};
    try { document = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8')); } catch {}
    document.presenceEnabled = enabled;
    const temporaryPath = `${CONFIG_PATH}.tmp`;
    fs.writeFileSync(temporaryPath, `${JSON.stringify(document, null, 2)}\n`, 'utf8');
    fs.renameSync(temporaryPath, CONFIG_PATH);
  } catch (error) {
    log(`Could not persist presence state: ${error.message}`);
  }
}

function setPresenceEnabled(enabled) {
  presenceEnabled = Boolean(enabled);
  persistPresenceEnabled(presenceEnabled);
  if (presenceEnabled) queuePresence();
  else clearPresence();
  log(`Presence ${presenceEnabled ? 'enabled' : 'paused'} by local control`);
}

function projectFrom(payload) {
  if (!payload || typeof payload.cwd !== 'string') return currentProject;
  if (isFilesystemRoot(payload.cwd)) return currentProject;
  const clean = payload.cwd.replace(/[\\/]+$/, '');
  const name = path.basename(clean);
  return name && name !== '.' ? name.slice(0, 60) : 'Локальная задача';
}

function isFilesystemRoot(value) {
  if (!value) return true;
  const resolved = path.resolve(value);
  return resolved === path.parse(resolved).root;
}

function projectFromSession(state) {
  const roots = [...(state.workspaceRoots || []), state.cwd]
    .filter((value) => typeof value === 'string' && value.trim() && !isFilesystemRoot(value));
  if (roots.length) {
    const clean = roots[0].replace(/[\\/]+$/, '');
    return path.basename(clean).slice(0, 60) || 'Локальная задача';
  }

  const parts = String(state.lastFile || '').replaceAll('\\', '/').split('/').filter(Boolean);
  const containerIndex = parts.findLastIndex((part) => /^(?:projects?|repos?|repositories|workspace|workspaces|code|dev|development|source|desktop|documents)$/i.test(part));
  if (containerIndex >= 0 && parts[containerIndex + 1]) return parts[containerIndex + 1].slice(0, 60);

  const openAiIndex = parts.findIndex((part, index) => /^openai$/i.test(part) && /^local$/i.test(parts[index - 1] || ''));
  if (openAiIndex >= 0 && parts[openAiIndex + 1]) return parts[openAiIndex + 1].slice(0, 60);

  const sourceIndex = parts.findLastIndex((part) => /^(?:src|source|app|packages?)$/i.test(part));
  if (sourceIndex > 0) return parts[sourceIndex - 1].slice(0, 60);

  if (parts.length > 1 && !/^(?:users|windows|program files|programdata|appdata)$/i.test(parts[0])) {
    return parts[0].slice(0, 60);
  }

  return 'Локальная задача';
}

function fileForProject(filePath, project) {
  const parts = String(filePath || '—').replaceAll('\\', '/').split('/').filter(Boolean);
  const projectIndex = parts.findLastIndex((part) => part.toLowerCase() === String(project).toLowerCase());
  if (projectIndex >= 0 && parts[projectIndex + 1]) return parts.slice(projectIndex + 1).join('/').slice(-90);
  return String(filePath || '—');
}

function displayPath(filePath, cwd) {
  let value = String(filePath || '').trim().replace(/^['"]|['"]$/g, '');
  if (!value) return null;
  if (/\*\*\*|[{};]/.test(value) || /^[+-]\s/.test(value)) return null;

  if (path.isAbsolute(value) && cwd) {
    const relative = path.relative(cwd, value);
    if (relative && !relative.startsWith('..') && !path.isAbsolute(relative)) value = relative;
  }

  value = value.replaceAll('\\', '/');
  if (value.length > 90) value = `…/${value.slice(-88)}`;
  return value;
}

function extractEditedFile(payload) {
  const tool = String(payload.tool_name || '').toLowerCase();
  if (!/apply_patch|edit|write/.test(tool)) return null;

  const input = payload.tool_input;
  const candidates = [];
  const strings = [];
  function visit(value, key = '', depth = 0) {
    if (depth > 5 || value == null) return;
    if (typeof value === 'string') {
      strings.push(value);
      if (/^(?:file|file_path|filepath|filename|path|target|destination)$/i.test(key)) candidates.push(value);
      return;
    }
    if (Array.isArray(value)) {
      for (const item of value) visit(item, key, depth + 1);
      return;
    }
    if (typeof value === 'object') {
      for (const [childKey, childValue] of Object.entries(value)) visit(childValue, childKey, depth + 1);
    }
  }
  visit(input);

  const patchFiles = strings.flatMap((value) => [
    ...value.matchAll(/(?:^|\r?\n|\\n)\*\*\*\s+(?:Add|Update|Delete) File:\s*([^\r\n]*?)(?=\\n|\r?\n|["']|$)/gi),
  ]);
  if (patchFiles.length) return displayPath(patchFiles.at(-1)[1], payload.cwd);

  return candidates.length ? displayPath(candidates.at(-1), payload.cwd) : null;
}

function listSessionFiles(dir, output = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return output;
  }

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) listSessionFiles(fullPath, output);
    else if (entry.isFile() && entry.name.endsWith('.jsonl')) {
      try {
        const stat = fs.statSync(fullPath);
        output.push({ path: fullPath, mtimeMs: stat.mtimeMs, size: stat.size });
      } catch {}
    }
  }
  return output;
}

function listDesktopLogFiles() {
  const output = [];
  let packages = [];
  try {
    packages = fs.readdirSync(PACKAGES_DIR, { withFileTypes: true });
  } catch {
    return output;
  }

  const cutoff = Date.now() - 3 * 24 * 60 * 60 * 1000;
  for (const entry of packages) {
    if (!entry.isDirectory() || !/^OpenAI\.Codex_/i.test(entry.name)) continue;
    const logsRoot = path.join(PACKAGES_DIR, entry.name, 'LocalCache', 'Local', 'Codex', 'Logs');
    const candidates = [];
    listFiles(logsRoot, candidates);
    for (const filePath of candidates) {
      if (!filePath.endsWith('.log')) continue;
      try {
        const stat = fs.statSync(filePath);
        if (stat.mtimeMs >= cutoff) output.push({ path: filePath, size: stat.size, mtimeMs: stat.mtimeMs });
      } catch {}
    }
  }
  return output.sort((a, b) => a.mtimeMs - b.mtimeMs).slice(-100);
}

function listFiles(dir, output) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) listFiles(fullPath, output);
    else if (entry.isFile()) output.push(fullPath);
  }
}

function parseDesktopLogLine(line) {
  const timestamp = line.match(/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)/)?.[1];
  if (!timestamp) return;
  const at = Date.parse(timestamp);
  if (!Number.isFinite(at)) return;

  const route = line.match(/ownerRoutePath=\/(local|remote)\/([^\s?#]+)/i);
  if (route) {
    desktopEvents.push({ type: 'route', at, kind: route[1].toLowerCase(), threadId: route[2] });
  }

  const cwdMatch = line.match(/\bcwd=("[^"]+"|'[^']+'|\S+)/i);
  if (cwdMatch) {
    const cwd = cwdMatch[1].replace(/^['"]|['"]$/g, '');
    if (cwd && !/[\\/]\.git(?:[\\/]|$)/i.test(cwd) && !isAnyFilesystemRoot(cwd)) {
      desktopEvents.push({ type: 'cwd', at, cwd });
    }
  }
}

function isAnyFilesystemRoot(value) {
  const clean = String(value || '').replaceAll('\\', '/').replace(/\/+$/, '');
  return clean === '' || clean === '/' || /^[A-Za-z]:$/.test(clean) || /^\/root$/.test(clean) || /^\/home\/[^/]+$/.test(clean);
}

function readDesktopLogDelta(fileInfo) {
  let state = desktopLogStates.get(fileInfo.path);
  if (!state || fileInfo.size < state.offset) {
    state = { offset: 0, remainder: '' };
    desktopLogStates.set(fileInfo.path, state);
  }
  if (fileInfo.size === state.offset) return false;

  const length = fileInfo.size - state.offset;
  const buffer = Buffer.alloc(length);
  const descriptor = fs.openSync(fileInfo.path, 'r');
  try {
    fs.readSync(descriptor, buffer, 0, length, state.offset);
  } finally {
    fs.closeSync(descriptor);
  }
  state.offset = fileInfo.size;
  const lines = `${state.remainder}${buffer.toString('utf8')}`.split(/\r?\n/);
  state.remainder = lines.pop() || '';
  for (const line of lines) parseDesktopLogLine(line);
  return true;
}

function projectNameFromCwd(cwd) {
  const clean = String(cwd || '').replaceAll('\\', '/').replace(/\/+$/, '');
  return clean.split('/').filter(Boolean).at(-1)?.slice(0, 60) || 'Локальная задача';
}

function rebuildDesktopSelection() {
  desktopEvents.sort((a, b) => a.at - b.at || (a.type === 'route' ? -1 : 1));
  if (desktopEvents.length > 30_000) desktopEvents.splice(0, desktopEvents.length - 30_000);

  let activeRoute = null;
  for (const event of desktopEvents) {
    if (event.type === 'route') {
      activeRoute = event;
      const state = threadDesktopStates.get(event.threadId) || {};
      state.kind = event.kind;
      state.lastSelectedAt = event.at;
      threadDesktopStates.set(event.threadId, state);
      continue;
    }
    if (event.type === 'cwd' && activeRoute) {
      const routeDelta = event.at - activeRoute.at;
      if (routeDelta < 0 || routeDelta > 10_000) continue;
      const state = threadDesktopStates.get(activeRoute.threadId) || {};
      if (state.cwdRouteDelta == null || routeDelta < state.cwdRouteDelta) {
        state.cwd = event.cwd;
        state.project = projectNameFromCwd(event.cwd);
        state.cwdAt = event.at;
        state.cwdRouteDelta = routeDelta;
      }
      threadDesktopStates.set(activeRoute.threadId, state);
    }
  }

  const latestRoute = [...desktopEvents].reverse().find((event) => event.type === 'route');
  if (!latestRoute) return false;
  const changed = latestRoute.threadId !== selectedThreadId;
  selectedThreadId = latestRoute.threadId;
  selectedRouteKind = latestRoute.kind;
  selectedRouteAt = latestRoute.at;

  const state = threadDesktopStates.get(selectedThreadId) || {};
  if (!state.project) return changed;
  const nextFile = state.lastFile || '—';
  if (changed || state.project !== currentProject || nextFile !== currentFile) {
    currentProject = state.project;
    currentFile = nextFile;
    presenceSource = 'desktop-route';
    queuePresence();
    log(`Desktop route -> thread=${selectedThreadId}, project=${currentProject}, file=${currentFile}`);
  }
  return changed;
}

function syncDesktopSelection() {
  let changed = false;
  for (const fileInfo of listDesktopLogFiles()) {
    try {
      changed = readDesktopLogDelta(fileInfo) || changed;
    } catch (error) {
      log(`Desktop log monitor error: ${error.message}`);
    }
  }
  if (changed || selectedThreadId) {
    const switched = rebuildDesktopSelection();
    if (switched) setTimeout(syncRemoteFile, 0);
  }
}

function syncRemoteFile() {
  if (remotePollRunning || !selectedThreadId || !/^[0-9a-f-]{20,64}$/i.test(selectedThreadId)) return;
  const requestedThreadId = selectedThreadId;
  const desktopState = threadDesktopStates.get(requestedThreadId);
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
    [
      '-T',
      '-o', 'BatchMode=yes',
      '-o', 'ConnectTimeout=6',
      remote.host,
      'python3', remote.monitorPath, requestedThreadId,
    ],
    { windowsHide: true, timeout: 10_000, maxBuffer: 1024 * 1024 },
    (error, stdout, stderr) => {
      remotePollRunning = false;
      lastRemotePollAt = new Date().toISOString();
      if (error) {
        lastRemoteError = String(stderr || error.message).trim().slice(-240);
        if (lastRemoteError !== lastLoggedRemoteError) {
          log(`Remote monitor error: ${lastRemoteError}`);
          lastLoggedRemoteError = lastRemoteError;
        }
        return;
      }

      let result;
      try {
        const line = String(stdout).trim().split(/\r?\n/).filter(Boolean).at(-1);
        result = JSON.parse(line || '{}');
      } catch (parseError) {
        lastRemoteError = `Invalid remote response: ${parseError.message}`;
        if (lastRemoteError !== lastLoggedRemoteError) {
          log(lastRemoteError);
          lastLoggedRemoteError = lastRemoteError;
        }
        return;
      }

      if (!result.ok || result.threadId !== requestedThreadId) {
        lastRemoteError = String(result.error || 'remote-session-mismatch');
        return;
      }

      lastRemoteError = null;
      lastLoggedRemoteError = null;
      const state = threadDesktopStates.get(requestedThreadId) || {};
      if (result.cwd) state.cwd = result.cwd;
      if (result.project) state.project = String(result.project).slice(0, 60);
      if (result.file) state.lastFile = String(result.file).slice(-90);
      threadDesktopStates.set(requestedThreadId, state);

      if (selectedThreadId !== requestedThreadId) return;
      const nextProject = state.project || currentProject;
      const nextFile = state.lastFile || '\u2014';
      if (nextProject !== currentProject || nextFile !== currentFile || presenceSource !== 'desktop-route+remote-session') {
        currentProject = nextProject;
        currentFile = nextFile;
        presenceSource = 'desktop-route+remote-session';
        queuePresence();
        log(`Remote session -> thread=${requestedThreadId}, project=${currentProject}, file=${currentFile}`);
      }
    },
  );
}

function toolPayloadFromRecord(record) {
  const payload = record?.payload;
  if (record?.type !== 'response_item' || !payload) return null;
  if (!['function_call', 'custom_tool_call'].includes(payload.type)) return null;

  let input = payload.arguments ?? payload.input;
  if (typeof input === 'string') {
    try {
      input = JSON.parse(input);
    } catch {}
  }

  const serialized = typeof input === 'string' ? input : JSON.stringify(input || {});
  const containsPatch = /\*\*\*\s+(?:Add|Update|Delete) File:/i.test(serialized);
  return {
    tool_name: containsPatch ? 'apply_patch' : String(payload.name || ''),
    tool_input: input,
  };
}

function processSessionRecord(state, line) {
  let record;
  try {
    record = JSON.parse(line);
  } catch {
    return;
  }

  const payload = record.payload || {};
  if (record.type === 'session_meta' && typeof payload.cwd === 'string') state.cwd = payload.cwd;
  if (record.type === 'turn_context') {
    if (typeof payload.cwd === 'string') state.cwd = payload.cwd;
    if (Array.isArray(payload.workspace_roots)) state.workspaceRoots = payload.workspace_roots;
  }

  const toolPayload = toolPayloadFromRecord(record);
  if (!toolPayload) return;
  const editedFile = extractEditedFile({
    ...toolPayload,
    cwd: state.cwd,
  });
  if (editedFile) state.lastFile = editedFile;
}

function readSessionDelta(fileInfo) {
  let state = sessionStates.get(fileInfo.path);
  if (!state || fileInfo.size < state.offset) {
    state = {
      offset: 0,
      remainder: '',
      cwd: null,
      workspaceRoots: [],
      lastFile: '—',
    };
    sessionStates.set(fileInfo.path, state);
  }

  if (fileInfo.size === state.offset) return { state, changed: false };
  const length = fileInfo.size - state.offset;
  const buffer = Buffer.alloc(length);
  const descriptor = fs.openSync(fileInfo.path, 'r');
  try {
    fs.readSync(descriptor, buffer, 0, length, state.offset);
  } finally {
    fs.closeSync(descriptor);
  }
  state.offset = fileInfo.size;

  const lines = `${state.remainder}${buffer.toString('utf8')}`.split(/\r?\n/);
  state.remainder = lines.pop() || '';
  for (const line of lines) if (line) processSessionRecord(state, line);
  return { state, changed: true };
}

function syncActiveSession() {
  const files = listSessionFiles(SESSIONS_DIR).sort((a, b) => b.mtimeMs - a.mtimeMs);
  const latest = selectedThreadId
    ? files.find((file) => path.basename(file.path).includes(selectedThreadId))
    : files[0];
  if (!latest) return;

  let result;
  try {
    result = readSessionDelta(latest);
  } catch (error) {
    log(`Session monitor error: ${error.message}`);
    return;
  }
  if (!result.changed && activeSessionPath === latest.path) return;

  const nextProject = projectFromSession(result.state);
  const nextFile = fileForProject(result.state.lastFile, nextProject);
  const switched = activeSessionPath !== latest.path;
  activeSessionPath = latest.path;

  if (switched || nextProject !== currentProject || nextFile !== currentFile) {
    currentProject = nextProject;
    currentFile = nextFile;
    const desktopState = selectedThreadId ? (threadDesktopStates.get(selectedThreadId) || {}) : null;
    if (desktopState) {
      desktopState.cwd = result.state.cwd || desktopState.cwd;
      desktopState.project = nextProject;
      desktopState.lastFile = nextFile;
      threadDesktopStates.set(selectedThreadId, desktopState);
    }
    presenceSource = selectedThreadId ? 'desktop-route+session' : 'session-monitor';
    queuePresence();
    log(`Session monitor -> project=${currentProject}, file=${currentFile}`);
  }
}

function handleHook(payload) {
  const event = String(payload.hook_event_name || payload.event || '');
  const payloadThreadId = payload.session_id || payload.thread_id || null;
  if (selectedThreadId && payloadThreadId !== selectedThreadId) {
    log(`Ignored background hook ${event || 'unknown'} for thread=${payloadThreadId || 'unknown'}`);
    return;
  }
  currentProject = projectFrom(payload);

  if (payload.session_id && payload.session_id !== currentSessionId) {
    currentSessionId = payload.session_id;
    currentFile = '—';
  }

  const editedFile = extractEditedFile(payload);
  if (editedFile) {
    const inferredProject = projectFromSession({
      cwd: payload.cwd,
      workspaceRoots: [],
      lastFile: editedFile,
    });
    if (inferredProject !== 'Локальная задача') currentProject = inferredProject;
    currentFile = fileForProject(editedFile, currentProject);
  }

  presenceSource = 'hook';
  queuePresence();
  log(`Hook ${event || 'unknown'} -> project=${currentProject}, file=${currentFile}`);
}

function checkCodexApp() {
  const command = `$p = Get-Process -Name ${APP_PROCESS} -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1; if ($p) { ([DateTimeOffset]$p.StartTime).ToUnixTimeSeconds() }`;
  execFile(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', command],
    { windowsHide: true, timeout: 8000 },
    (error, stdout) => {
      if (error) {
        log(`Codex process check failed: ${error.message}`);
        return;
      }

      const startedAt = Number.parseInt(String(stdout).trim(), 10);
      const running = Number.isSafeInteger(startedAt) && startedAt > 0;
      const runningChanged = running !== appIsRunning;
      const startChanged = running && startedAt !== codexStartedAt;
      if (!runningChanged && !startChanged) return;

      appIsRunning = running;
      codexStartedAt = running ? startedAt : null;
      if (running) {
        queuePresence();
        log(`Codex app timer started at ${new Date(codexStartedAt * 1000).toISOString()}`);
      } else {
        clearPresence();
        log('Codex app closed; presence timer cleared');
      }
    },
  );
}

const server = http.createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({
      ok: true,
      version: VERSION,
      rpcReady,
      presenceEnabled,
      project: currentProject,
      file: currentFile,
      source: presenceSource,
      codexStartedAt: codexStartedAt ? new Date(codexStartedAt * 1000).toISOString() : null,
      activeSession: activeSessionPath ? path.basename(activeSessionPath) : null,
      selectedThreadId,
      selectedRouteKind,
      selectedRouteAt: selectedRouteAt ? new Date(selectedRouteAt).toISOString() : null,
      remotePollRunning,
      lastRemotePollAt,
      lastRemoteError,
      remoteConfigured: REMOTE_HOSTS.length > 0,
      remoteHosts: REMOTE_HOSTS.map((remote) => remote.name),
      selectedRemote: selectedRemoteName,
      knownThreadProjects: Object.fromEntries(
        [...threadDesktopStates.entries()]
          .filter(([, state]) => state.project)
          .map(([threadId, state]) => [threadId, state.project]),
      ),
      details: pendingPresence?.details || null,
      lastRpcAck,
    }));
    return;
  }

  if (req.method === 'POST' && req.url === '/control') {
    let body = '';
    req.setEncoding('utf8');
    req.on('data', (chunk) => {
      if (body.length < 16_384) body += chunk;
    });
    req.on('end', () => {
      try {
        const action = String(JSON.parse(body || '{}').action || '').toLowerCase();
        if (action === 'pause') setPresenceEnabled(false);
        else if (action === 'resume') setPresenceEnabled(true);
        else if (action === 'toggle') setPresenceEnabled(!presenceEnabled);
        else if (action === 'shutdown') {
          res.writeHead(202, { 'content-type': 'application/json' });
          res.end(JSON.stringify({ ok: true, action }));
          shutdown();
          return;
        } else {
          res.writeHead(400, { 'content-type': 'application/json' });
          res.end(JSON.stringify({ ok: false, error: 'unknown-action' }));
          return;
        }
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ ok: true, action, presenceEnabled }));
      } catch (error) {
        res.writeHead(400, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ ok: false, error: error.message }));
      }
    });
    return;
  }

  if (req.method !== 'POST' || req.url !== '/hook') {
    res.writeHead(404);
    res.end();
    return;
  }

  let body = '';
  req.setEncoding('utf8');
  req.on('data', (chunk) => {
    if (body.length < 1_000_000) body += chunk;
  });
  req.on('end', () => {
    try {
      handleHook(JSON.parse(body || '{}'));
      res.writeHead(204);
      res.end();
    } catch (error) {
      res.writeHead(400);
      res.end();
      log(`Bad hook payload: ${error.message}`);
    }
  });
});

server.on('error', (error) => {
  if (error.code === 'EADDRINUSE') process.exit(0);
  log(`HTTP server error: ${error.message}`);
});

server.listen(PORT, HOST, () => {
  log(`Presence daemon started on ${HOST}:${PORT}`);
  queuePresence();
  if (!TEST_MODE) {
    syncDesktopSelection();
    syncActiveSession();
    tryPipe();
    checkCodexApp();
    setInterval(checkCodexApp, 15_000).unref();
    setInterval(syncDesktopSelection, 2000).unref();
    setInterval(syncActiveSession, 2500).unref();
    setInterval(syncRemoteFile, REMOTE_POLL_INTERVAL).unref();
  }
});

function shutdown() {
  clearPresence();
  setTimeout(() => process.exit(0), 150).unref();
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
