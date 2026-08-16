'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');

const {
  displayPath,
  extractEditedFile,
  fileForProject,
  isAnyFilesystemRoot,
  isFilesystemRoot,
  projectFromCwd,
  projectFromSession,
  resolveSessionProject,
  repositoryProjectFromFile,
  shortenPath,
  toolPayloadFromRecord,
} = require('../src/codex-paths');

/** Minimal in-memory filesystem so repository detection is testable anywhere. */
function fakeFileSystem(paths) {
  const set = new Set(paths.map((value) => value.replaceAll('/', '\\')));
  return {
    exists: (value) => set.has(value.replaceAll('/', '\\')),
    isDirectory: (value) => set.has(value.replaceAll('/', '\\')),
  };
}

test('filesystem roots are never treated as project names', () => {
  assert.equal(isFilesystemRoot('C:\\'), true);
  assert.equal(isFilesystemRoot(''), true);
  assert.equal(isFilesystemRoot('C:\\projects\\demo'), false);
  assert.equal(isAnyFilesystemRoot('/root'), true);
  assert.equal(isAnyFilesystemRoot('/home/dev'), true);
  assert.equal(isAnyFilesystemRoot('C:\\Users\\dev'), true);
  assert.equal(isAnyFilesystemRoot('/srv/store'), false);
});

test('the enclosing git repository wins over the containing folder', () => {
  const fileSystem = fakeFileSystem(['C:\\Users\\dev\\GitHub\\store', 'C:\\Users\\dev\\GitHub\\store\\.git']);
  const project = repositoryProjectFromFile('store/api/index.js', 'C:\\Users\\dev\\GitHub', { fileSystem, now: 1 });
  assert.equal(project, 'store');
});

test('repository lookups stop at the drive root instead of looping', () => {
  const fileSystem = fakeFileSystem([]);
  assert.equal(repositoryProjectFromFile('a/b/c.js', 'C:\\work', { fileSystem, now: 2 }), null);
});

test('a workspace container folder resolves to the checkout inside it', () => {
  const project = projectFromSession(
    { cwd: 'C:\\Users\\dev\\Documents\\GitHub', workspaceRoots: [], lastFile: 'codex-discord-presence/tray/Program.cs' },
    { fileSystem: fakeFileSystem([]), now: 3 },
  );
  assert.equal(project, 'codex-discord-presence');
});

test('a workspace container without repository evidence is not presented as a project', () => {
  const project = projectFromSession(
    { cwd: 'C:\\Users\\dev\\Documents\\GitHub', workspaceRoots: [], lastFile: null },
    { fileSystem: fakeFileSystem([]), now: 31 },
  );
  assert.equal(project, null);
});

test('session resolution prefers repository evidence, then authoritative metadata, then cwd heuristics', () => {
  const metadataWins = resolveSessionProject(
    { cwd: 'D:\\work\\checkout-alias', workspaceRoots: [], lastFile: null },
    'canonical-repository',
    { fileSystem: fakeFileSystem([]), now: 32 },
  );
  assert.equal(metadataWins, 'canonical-repository');

  const metadataBeatsGenericSourceFolder = resolveSessionProject(
    { cwd: 'D:\\Projects', workspaceRoots: [], lastFile: 'src/index.js' },
    'canonical-repository',
    { fileSystem: fakeFileSystem([]), now: 34 },
  );
  assert.equal(metadataBeatsGenericSourceFolder, 'canonical-repository');

  const metadataBeatsContainerGuess = resolveSessionProject(
    { cwd: 'D:\\Projects', workspaceRoots: [], lastFile: 'checkout-alias/src/index.js' },
    'canonical-repository',
    { fileSystem: fakeFileSystem([]), now: 35 },
  );
  assert.equal(metadataBeatsContainerGuess, 'canonical-repository');

  const fileSystem = fakeFileSystem(['D:\\work\\nested-repository', 'D:\\work\\nested-repository\\.git']);
  const repositoryWins = resolveSessionProject(
    { cwd: 'D:\\work', workspaceRoots: [], lastFile: 'nested-repository/src/index.js' },
    'outer-repository',
    { fileSystem, now: 33 },
  );
  assert.equal(repositoryWins, 'nested-repository');
});

test('an unresolvable session returns null so the caller can localise it', () => {
  const project = projectFromSession({ cwd: '', workspaceRoots: [], lastFile: '' }, { fileSystem: fakeFileSystem([]), now: 4 });
  assert.equal(project, null);
});

test('POSIX workspace roots from remote sessions resolve too', () => {
  const project = projectFromSession(
    { cwd: '/srv/apps/store', workspaceRoots: ['/srv/apps/store'], lastFile: 'src/index.ts' },
    { fileSystem: fakeFileSystem([]), now: 5 },
  );
  assert.equal(project, 'store');
});

test('project name comes from the working directory of a hook payload', () => {
  assert.equal(projectFromCwd('C:\\projects\\demo'), 'demo');
  assert.equal(projectFromCwd('/srv/store/'), 'store');
  assert.equal(projectFromCwd('C:\\'), null);
  assert.equal(projectFromCwd('/root'), null);
  assert.equal(projectFromCwd('/home/dev'), null);
  assert.equal(projectFromCwd('C:\\Users\\dev'), null);
  assert.equal(projectFromCwd('C:\\Projects'), null);
  assert.equal(projectFromCwd('C:\\Users\\dev\\Documents\\GitHub'), null);
  assert.equal(projectFromCwd(undefined), null);
});

test('home directory roots never leak the account name through session fallback', () => {
  const options = { fileSystem: fakeFileSystem([]), now: 6 };
  assert.equal(projectFromSession({ cwd: '/root', workspaceRoots: [], lastFile: '' }, options), null);
  assert.equal(projectFromSession({ cwd: '/home/dev', workspaceRoots: [], lastFile: '' }, options), null);
  assert.equal(projectFromSession({ cwd: 'C:\\Users\\dev', workspaceRoots: [], lastFile: '' }, options), null);
});

test('the project prefix is stripped from the displayed file', () => {
  assert.equal(fileForProject('codex-discord-presence/tray/Program.cs', 'codex-discord-presence'), 'tray/Program.cs');
  assert.equal(fileForProject('src/demo.js', 'demo'), 'src/demo.js');
  assert.equal(fileForProject(null, 'demo'), null);
});

test('long paths are truncated on a separator instead of mid-segment', () => {
  const long = `${'directory/'.repeat(20)}Program.cs`;
  const short = shortenPath(long, 40);
  assert.ok(short.length <= 40, `expected <= 40 characters, got ${short.length}`);
  assert.ok(short.startsWith('…/'), short);
  assert.ok(short.endsWith('Program.cs'), short);
  assert.equal(short.includes('directorydirectory'), false);
});

test('absolute tool paths are made relative to the working directory', () => {
  assert.equal(displayPath('C:\\projects\\demo\\src\\index.js', 'C:\\projects\\demo'), 'src/index.js');
  assert.equal(displayPath('"src/index.js"', 'C:\\projects\\demo'), 'src/index.js');
  assert.equal(displayPath('*** Update File: src/index.js', 'C:\\projects\\demo'), null, 'patch fragments are not paths');
  assert.equal(displayPath('  ', 'C:\\projects\\demo'), null);
});

test('the last file of an apply_patch payload is the one shown', () => {
  const file = extractEditedFile({
    tool_name: 'apply_patch',
    cwd: 'C:\\projects\\demo',
    tool_input: '*** Update File: src/first.js\n*** Update File: src/second.js\n',
  });
  assert.equal(file, 'src/second.js');
});

test('non-editing tools never move the card', () => {
  assert.equal(extractEditedFile({ tool_name: 'shell', cwd: 'C:\\demo', tool_input: { command: 'ls src/index.js' } }), null);
});

test('transcript records are recognised as patches even when the tool is renamed', () => {
  const payload = toolPayloadFromRecord({
    type: 'response_item',
    payload: { type: 'function_call', name: 'container.exec', arguments: '{"cmd":"*** Update File: src/x.ts"}' },
  });
  assert.equal(payload.tool_name, 'apply_patch');
  assert.equal(toolPayloadFromRecord({ type: 'event_msg', payload: {} }), null);
});
