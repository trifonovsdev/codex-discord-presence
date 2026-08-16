'use strict';

const fs = require('fs');
const path = require('path');

// Windows path semantics are used on purpose: the daemon only runs on Windows,
// and `path.win32` also understands the POSIX paths that arrive from remote SSH
// workspaces. Pinning the flavour keeps the heuristics testable off-Windows.
const win = path.win32;

const MAX_PROJECT = 60;
const MAX_FILE = 90;
const REPOSITORY_CACHE_TTL_MS = 60_000;
const REPOSITORY_CACHE_LIMIT = 512;

const WORKSPACE_CONTAINER = /^(?:projects?|repos?|repositories|workspace|workspaces|code|dev|development|source|desktop|documents|github|gitlab|bitbucket)$/i;
const SYSTEM_ROOT = /^(?:users|windows|program files|programdata|appdata)$/i;
const SOURCE_DIRECTORY = /^(?:src|source|app|packages?)$/i;
const EDIT_TOOL = /apply_patch|edit|write/;
const PATCH_HEADER = /(?:^|\r?\n|\\n)\*\*\*\s+(?:Add|Update|Delete) File:\s*([^\r\n]*?)(?=\\n|\r?\n|["']|$)/gi;
const FILE_KEY = /^(?:file|file_path|filepath|filename|path|target|destination)$/i;

const defaultFileSystem = {
  exists: (value) => fs.existsSync(value),
  isDirectory: (value) => {
    try {
      return fs.statSync(value).isDirectory();
    } catch {
      return false;
    }
  },
};

/** Splits any mix of `/` and `\` into non-empty segments. */
function segments(value) {
  return String(value ?? '').replaceAll('\\', '/').split('/').filter(Boolean);
}

function isFilesystemRoot(value) {
  if (!value) return true;
  const resolved = win.resolve(String(value));
  return resolved === win.parse(resolved).root;
}

/**
 * Roots that are technically a directory but never a useful project name:
 * drive roots, `/`, and the bare home directory of a remote account.
 */
function isAnyFilesystemRoot(value) {
  const clean = String(value ?? '').replaceAll('\\', '/').replace(/\/+$/, '');
  return clean === ''
    || clean === '/'
    || /^[A-Za-z]:$/.test(clean)
    || /^\/root$/.test(clean)
    || /^\/home\/[^/]+$/.test(clean)
    || /^[A-Za-z]:\/Users\/[^/]+$/i.test(clean);
}

function isWorkspaceContainer(name) {
  return WORKSPACE_CONTAINER.test(String(name ?? ''));
}

/**
 * Truncates from the left on a separator boundary so the tail of the path —
 * the part that identifies the file — always stays readable.
 */
function shortenPath(value, max = MAX_FILE) {
  const text = String(value ?? '');
  if (text.length <= max) return text;
  const tail = text.slice(-(max - 2));
  const boundary = tail.indexOf('/');
  return `…/${boundary >= 0 && boundary < 24 ? tail.slice(boundary + 1) : tail}`;
}

function projectNameFromCwd(cwd) {
  const name = segments(cwd).at(-1);
  return name && !isWorkspaceContainer(name) ? name.slice(0, MAX_PROJECT) : null;
}

/** Project name implied by a hook payload's working directory, or null. */
function projectFromCwd(cwd) {
  if (typeof cwd !== 'string' || isAnyFilesystemRoot(cwd)) return null;
  const name = win.basename(cwd.replace(/[\\/]+$/, ''));
  return name && name !== '.' && !isWorkspaceContainer(name) ? name.slice(0, MAX_PROJECT) : null;
}

const repositoryCache = new Map();

function cachedGitLookup(directory, fileSystem, now) {
  const cached = repositoryCache.get(directory);
  if (cached && now - cached.at < REPOSITORY_CACHE_TTL_MS) return cached.found;
  const found = fileSystem.exists(win.join(directory, '.git'));
  if (repositoryCache.size >= REPOSITORY_CACHE_LIMIT) repositoryCache.clear();
  repositoryCache.set(directory, { found, at: now });
  return found;
}

/**
 * Walks up from an edited file looking for the enclosing git repository. This
 * is what makes nested checkouts (`~/Documents/GitHub/<repo>`) resolve to the
 * repository instead of the container folder.
 */
function repositoryProjectFromFile(filePath, cwd, { fileSystem = defaultFileSystem, now = Date.now() } = {}) {
  if (!filePath || !cwd) return null;
  let candidate = win.isAbsolute(filePath) ? filePath : win.resolve(cwd, filePath);

  if (!fileSystem.exists(candidate) || !fileSystem.isDirectory(candidate)) {
    candidate = win.dirname(candidate);
  }

  const seen = new Set();
  while (candidate && candidate !== win.parse(candidate).root && !seen.has(candidate)) {
    seen.add(candidate);
    if (cachedGitLookup(candidate, fileSystem, now)) return win.basename(candidate).slice(0, MAX_PROJECT);
    candidate = win.dirname(candidate);
  }
  return null;
}

function sessionRoots(state) {
  return [...(state.workspaceRoots || []), state.cwd]
    .filter((value) => typeof value === 'string' && value.trim() && !isAnyFilesystemRoot(value))
    .sort((left, right) => String(right).length - String(left).length);
}

function projectFromContainerPath(rootName, filePath) {
  if (!isWorkspaceContainer(rootName)) return null;
  const parts = segments(filePath);
  const first = parts[0];
  if (parts.length <= 1 || !first || isWorkspaceContainer(first) || SOURCE_DIRECTORY.test(first) || SYSTEM_ROOT.test(first)) return null;
  return first.slice(0, MAX_PROJECT);
}

/**
 * Best-effort project name for a transcript state. Returns null when nothing
 * better than a generic container folder could be found, so the caller can
 * substitute a localised placeholder.
 */
function projectFromSession(state, options = {}) {
  const repositoryProject = repositoryProjectFromFile(state.lastFile, state.cwd, options);
  if (repositoryProject) return repositoryProject;

  const roots = sessionRoots(state);

  const relativeParts = segments(state.lastFile);

  if (roots.length) {
    const rootName = win.basename(roots[0].replace(/[\\/]+$/, ''));
    if (isWorkspaceContainer(rootName)) {
      return projectFromContainerPath(rootName, state.lastFile);
    }
    return rootName.slice(0, MAX_PROJECT) || null;
  }

  const containerIndex = relativeParts.findLastIndex(isWorkspaceContainer);
  if (containerIndex >= 0 && relativeParts[containerIndex + 1]) return relativeParts[containerIndex + 1].slice(0, MAX_PROJECT);

  const openAiIndex = relativeParts.findIndex((part, index) => /^openai$/i.test(part) && /^local$/i.test(relativeParts[index - 1] || ''));
  if (openAiIndex >= 0 && relativeParts[openAiIndex + 1]) return relativeParts[openAiIndex + 1].slice(0, MAX_PROJECT);

  const sourceIndex = relativeParts.findLastIndex((part) => SOURCE_DIRECTORY.test(part));
  if (sourceIndex > 0) return relativeParts[sourceIndex - 1].slice(0, MAX_PROJECT);

  if (relativeParts.length > 1 && !SYSTEM_ROOT.test(relativeParts[0])) return relativeParts[0].slice(0, MAX_PROJECT);

  return null;
}

/** Uses concrete repository evidence first, Codex metadata second, and path heuristics last. */
function resolveSessionProject(state, contextProject, options = {}) {
  const [root] = sessionRoots(state);
  const containerProject = root
    ? projectFromContainerPath(win.basename(root.replace(/[\\/]+$/, '')), state.lastFile)
    : null;
  return repositoryProjectFromFile(state.lastFile, state.cwd, options)
    || contextProject
    || containerProject
    || projectFromSession(state, options);
}

/** Strips the project prefix so the card shows a repository-relative path. */
function fileForProject(filePath, project) {
  if (!filePath) return null;
  const parts = segments(filePath);
  const projectIndex = parts.findLastIndex((part) => part.toLowerCase() === String(project ?? '').toLowerCase());
  if (projectIndex >= 0 && parts[projectIndex + 1]) return shortenPath(parts.slice(projectIndex + 1).join('/'));
  return shortenPath(String(filePath));
}

/** Normalises a tool-supplied path, rejecting patch fragments and code noise. */
function displayPath(filePath, cwd) {
  let value = String(filePath ?? '').trim().replace(/^['"]|['"]$/g, '');
  if (!value) return null;
  if (/\*\*\*|[{};]/.test(value) || /^[+-]\s/.test(value)) return null;

  if (win.isAbsolute(value) && cwd) {
    const relative = win.relative(cwd, value);
    if (relative && !relative.startsWith('..') && !win.isAbsolute(relative)) value = relative;
  }

  return shortenPath(value.replaceAll('\\', '/'));
}

/** Pulls the most recently edited file out of a tool-call payload. */
function extractEditedFile(payload) {
  const tool = String(payload?.tool_name ?? '').toLowerCase();
  if (!EDIT_TOOL.test(tool)) return null;

  const candidates = [];
  const strings = [];

  function visit(value, key = '', depth = 0) {
    if (depth > 5 || value == null) return;
    if (typeof value === 'string') {
      strings.push(value);
      if (FILE_KEY.test(key)) candidates.push(value);
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
  visit(payload?.tool_input);

  const patchFiles = strings.flatMap((value) => [...value.matchAll(PATCH_HEADER)]);
  if (patchFiles.length) return displayPath(patchFiles.at(-1)[1], payload.cwd);
  return candidates.length ? displayPath(candidates.at(-1), payload.cwd) : null;
}

/** Normalises a Codex transcript record into `{ tool_name, tool_input }`. */
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
  return { tool_name: containsPatch ? 'apply_patch' : String(payload.name || ''), tool_input: input };
}

module.exports = {
  MAX_FILE,
  MAX_PROJECT,
  displayPath,
  extractEditedFile,
  fileForProject,
  isAnyFilesystemRoot,
  isFilesystemRoot,
  isWorkspaceContainer,
  projectFromCwd,
  projectFromSession,
  resolveSessionProject,
  projectNameFromCwd,
  repositoryProjectFromFile,
  segments,
  shortenPath,
  toolPayloadFromRecord,
  __testing: { repositoryCache, defaultFileSystem },
};
