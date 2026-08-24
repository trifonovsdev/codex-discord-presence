'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');

const repository = path.resolve(__dirname, '..');
const pinnedNodeSha256 = 'f2aa33b35b75aca5f3f7b85675a6f6423201053e9381911e64961f3bda2528ab';
const pinnedDiscordSdkSha256 = '46170463bf263045972fde1ccaa51b380eb1443541036a809d24f4e6a9f9c388';

function read(relativePath) {
  return fs.readFileSync(path.join(repository, relativePath), 'utf8');
}

function capture(relativePath, pattern, label) {
  const match = read(relativePath).match(pattern);
  assert.ok(match, `Could not find ${label} in ${relativePath}`);
  return match[1];
}

function captureVersionTuple(relativePath, pattern, label) {
  const match = read(relativePath).match(pattern);
  assert.ok(match, `Could not find ${label} in ${relativePath}`);
  return match.slice(1).join('.');
}

test('release version stays aligned across shipped surfaces', () => {
  const expected = JSON.parse(read('package.json')).version;
  const versions = {
    'daemon runtime': capture('src/daemon.js', /const VERSION = '([^']+)'/, 'daemon version'),
    'tray project': capture('tray/CodexPresence.Tray.csproj', /<Version>([^<]+)<\/Version>/, 'tray version'),
    'tray fallback': capture('tray/AppCoordinator.cs', /:\s*"(\d+\.\d+\.\d+)";/, 'tray fallback version'),
    'updater fallback': captureVersionTuple(
      'tray/UpdateService.cs',
      /new Version\((\d+),\s*(\d+),\s*(\d+)\)/,
      'updater fallback version',
    ),
    'installer default': capture('installer/CodexPresence.iss', /#define MyAppVersion "([^"]+)"/, 'installer version'),
    'build default': capture('build-release.ps1', /\[string\]\$Version = '([^']+)'/, 'build version'),
    'README example': capture('README.md', /build-release\.ps1 -Version (\d+\.\d+\.\d+)/, 'README version'),
  };

  const workflowDefault = read('.github/workflows/release.yml').match(/default:\s*['"]?(\d+\.\d+\.\d+)/)?.[1];
  if (workflowDefault) versions['release workflow default'] = workflowDefault;

  const expectedVersions = Object.fromEntries(Object.keys(versions).map((label) => [label, expected]));
  assert.deepEqual(versions, expectedVersions);
});

test('CI validates with the bundled Node major', () => {
  const packageDocument = JSON.parse(read('package.json'));

  assert.equal(packageDocument.engines.node, '>=24');
  assert.match(read('README.md'), /Node\.js 24\+/);
  assert.match(read('.github/workflows/ci.yml'), /node-version:\s*['"]?24['"]?/);
});

test('release waits for the reusable validation workflow', () => {
  const ci = read('.github/workflows/ci.yml');
  const release = read('.github/workflows/release.yml');

  assert.match(ci, /workflow_call:/);
  assert.match(release, /uses:\s*\.\/\.github\/workflows\/ci\.yml/);
  assert.match(release, /needs:\s*validate/);
});

test('release toolchain inputs are immutable and stay on the supported Inno major', () => {
  const workflows = `${read('.github/workflows/ci.yml')}\n${read('.github/workflows/release.yml')}`;

  assert.doesNotMatch(workflows, /uses:\s*actions\/(?:checkout|setup-node|setup-python|setup-dotnet)@v\d+/);
  assert.match(workflows, /actions\/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1/);
  assert.match(workflows, /actions\/setup-node@820762786026740c76f36085b0efc47a31fe5020/);
  assert.match(workflows, /actions\/setup-python@ece7cb06caefa5fff74198d8649806c4678c61a1/);
  assert.match(workflows, /actions\/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1/);
  assert.match(workflows, /JRSoftware\.InnoSetup.*--version 6\.7\.3.*--source winget/);
});

test('release only publishes new artifacts from matching tags', () => {
  const workflow = read('.github/workflows/release.yml');

  assert.doesNotMatch(workflow, /workflow_dispatch/);
  assert.doesNotMatch(workflow, /--clobber/);
  assert.match(workflow, /GITHUB_REF_TYPE/);
  assert.match(workflow, /already exists/i);
  assert.match(workflow, /\$global:LASTEXITCODE\s*=\s*0/);
});

test('release build verifies the cached Node archive before extraction', () => {
  const script = read('build-release.ps1');
  const checksumIndex = script.indexOf('Get-FileHash -LiteralPath $nodeArchive');
  const extractionIndex = script.indexOf('Expand-Archive -LiteralPath $nodeArchive');

  assert.match(script, /SHASUMS256\.txt/);
  assert.ok(checksumIndex >= 0, 'Node archive hash is not calculated');
  assert.ok(extractionIndex >= 0, 'Node archive is not extracted');
  assert.ok(checksumIndex < extractionIndex, 'Node archive must be verified before extraction');
  assert.match(script, /checksum mismatch/i);
  assert.match(script, new RegExp(`\\[string\\]\\$NodeSha256 = '${pinnedNodeSha256}'`));
  assert.match(script, /manifest checksum .* pinned checksum/i);
});

test('release build verifies the pinned Social SDK before staging it', () => {
  const script = read('build-release.ps1');
  const checksumIndex = script.indexOf('Get-FileHash -LiteralPath $discordSdkBinary');
  const copyIndex = script.indexOf("Copy-Item -LiteralPath $discordSdkBinary");

  assert.ok(checksumIndex >= 0, 'Discord Social SDK hash is not calculated');
  assert.ok(copyIndex >= 0, 'Discord Social SDK is not staged');
  assert.ok(checksumIndex < copyIndex, 'Discord Social SDK must be verified before staging');
  assert.match(script, new RegExp(`\\[string\\]\\$DiscordSdkSha256 = '${pinnedDiscordSdkSha256}'`));
  assert.match(script, /DiscordSdkCommit.*[0-9a-f]{40}/i);
  assert.match(script, /Discord Social SDK binary checksum mismatch/);
  assert.match(script, /Discord Social SDK notices checksum mismatch/);
});

test('release build rejects unsafe version strings before constructing paths', () => {
  const script = read('build-release.ps1');
  const validationIndex = script.indexOf("if ($Version -notmatch");
  const artifactsIndex = script.indexOf("$artifacts = Join-Path");

  assert.ok(validationIndex >= 0, 'release version is not validated');
  assert.ok(validationIndex < artifactsIndex, 'release version must be validated before path construction');
  assert.match(script, /if \(\$NodeVersion -notmatch/);
});

test('release build requires the requested version to match package.json', () => {
  const script = read('build-release.ps1');

  assert.match(script, /package\.json/);
  assert.match(script, /must match package\.json/i);
});
