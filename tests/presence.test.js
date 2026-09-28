'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');

const { buildActivity, MAX_FIELD } = require('../src/presence');
const { PRIVACY_PRESETS } = require('../src/config');

const base = { activityName: 'Coding with Codex', largeImageKey: 'codex', largeImageText: 'OpenAI Codex' };

test('the card publishes a customizable Discord activity name', () => {
  const privacy = { ...PRIVACY_PRESETS.standard, preset: 'standard' };
  const custom = buildActivity({ ...base, activityName: 'Reviewing with Codex', privacy });
  assert.equal(custom.name, 'Reviewing with Codex');
  assert.equal(custom.type, 0);

  const invalid = buildActivity({ ...base, activityName: 'x', privacy });
  assert.equal(invalid.name, 'Coding with Codex');
});

test('the card is written in one language, not a mix of two', () => {
  const english = buildActivity({ ...base, project: 'store', file: 'src/index.ts', privacy: { ...PRIVACY_PRESETS.standard, preset: 'standard' }, language: 'en' });
  assert.equal(english.details, 'Project: store');
  assert.equal(english.state, 'Editing: src/index.ts');

  const detailed = buildActivity({ ...base, project: 'store', file: 'src/index.ts', privacy: { ...PRIVACY_PRESETS.detailed, preset: 'detailed' }, language: 'en' });
  assert.equal(detailed.details, 'Project: store', 'presets must not silently switch language');
});

test('russian is available for the whole card, not just parts of it', () => {
  const card = buildActivity({ ...base, project: 'store', file: 'src/index.ts', privacy: { ...PRIVACY_PRESETS.standard, preset: 'standard' }, language: 'ru' });
  assert.equal(card.details, 'Проект: store');
  assert.equal(card.state, 'Файл: src/index.ts');
});

test('a task title replaces the fake local-project fallback only when the user opts in', () => {
  const privateCard = buildActivity({
    ...base,
    project: null,
    task: 'Обновить дизайн приложения',
    privacy: { ...PRIVACY_PRESETS.standard, preset: 'standard' },
    language: 'ru',
  });
  assert.equal(privateCard.details, 'Работает в Codex');
  assert.equal(privateCard.details.includes('Обновить'), false);

  const sharedCard = buildActivity({
    ...base,
    project: null,
    task: 'Обновить дизайн приложения',
    privacy: { ...PRIVACY_PRESETS.standard, preset: 'standard', showTaskTitle: true },
    language: 'ru',
  });
  assert.equal(sharedCard.details, 'Задача: Обновить дизайн приложения');
});

test('the minimal preset hides the file name', () => {
  const card = buildActivity({ ...base, project: 'store', file: 'src/secret-client.ts', privacy: { ...PRIVACY_PRESETS.minimal, preset: 'minimal' }, language: 'en' });
  assert.equal(card.state, 'Working privately');
  assert.equal(card.state.includes('secret-client'), false);
});

test('filename mode drops the directory portion', () => {
  const card = buildActivity({ ...base, project: 'store', file: 'src/deep/index.ts', privacy: { ...PRIVACY_PRESETS.standard, preset: 'standard', fileMode: 'name' }, language: 'en' });
  assert.equal(card.state, 'Editing: index.ts');
});

test('hiding the project also hides it from the assets tooltip', () => {
  const privacy = { ...PRIVACY_PRESETS.detailed, preset: 'detailed', showProject: false };
  const card = buildActivity({ ...base, project: 'internal-tool', workspace: 'Production', privacy, language: 'en' });
  assert.equal(card.details.includes('internal-tool'), false);
  assert.equal(card.assets.large_text.includes('Production'), false);
});

test('the detailed preset names the workspace in the tooltip', () => {
  const privacy = { ...PRIVACY_PRESETS.detailed, preset: 'detailed' };
  const card = buildActivity({ ...base, project: 'store', workspace: 'Production', privacy, language: 'en' });
  assert.equal(card.assets.large_text, 'OpenAI Codex · Production');
});

test('the timer is only attached when it is allowed and known', () => {
  const privacy = { ...PRIVACY_PRESETS.standard, preset: 'standard' };
  assert.equal(buildActivity({ ...base, privacy, startedAt: 1700000000 }).timestamps.start, 1700000000);
  assert.equal(buildActivity({ ...base, privacy, startedAt: null }).timestamps, undefined);
  assert.equal(buildActivity({ ...base, privacy: { ...privacy, showTimer: false }, startedAt: 1700000000 }).timestamps, undefined);
});

test('over-long values are clamped to what Discord accepts', () => {
  const privacy = { ...PRIVACY_PRESETS.standard, preset: 'standard' };
  const card = buildActivity({ ...base, project: 'p'.repeat(400), file: 'f'.repeat(400), privacy });
  assert.ok(card.details.length <= MAX_FIELD);
  assert.ok(card.state.length <= MAX_FIELD);
});

test('an unknown project reports an honest generic state instead of inventing a local project', () => {
  const privacy = { ...PRIVACY_PRESETS.standard, preset: 'standard' };
  const english = buildActivity({ ...base, project: null, file: null, privacy, language: 'en' });
  const russian = buildActivity({ ...base, project: null, file: null, privacy, language: 'ru' });
  assert.equal(english.details, 'Working in Codex');
  assert.equal(english.state, 'Active Codex session');
  assert.equal(russian.details, 'Работает в Codex');
  assert.equal(russian.state, 'Активная сессия Codex');
});

test('Claude Code cards speak about Claude Code, in both languages', () => {
  const privacy = { ...PRIVACY_PRESETS.standard, preset: 'standard' };
  const claude = { agent: 'claude', activityName: 'Coding with Claude Code', largeImageKey: 'https://example.com/claude.png', largeImageText: '' };
  const idle = buildActivity({ ...claude, privacy });
  assert.equal(idle.name, 'Coding with Claude Code');
  assert.equal(idle.details, 'Working in Claude Code');
  assert.equal(idle.state, 'Active Claude Code session');
  assert.equal(idle.assets.large_image, 'https://example.com/claude.png');
  assert.equal(idle.assets.large_text, 'Claude Code');

  const russian = buildActivity({ ...claude, privacy, language: 'ru', project: 'store', file: 'src/a.ts' });
  assert.equal(russian.details, 'Проект: store');
  assert.equal(russian.state, 'Файл: src/a.ts');
  assert.equal(buildActivity({ ...claude, privacy, language: 'ru' }).details, 'Работает в Claude Code');

  const hidden = buildActivity({ ...claude, privacy: { ...privacy, showProject: false } });
  assert.equal(hidden.details, 'Claude Code');
  assert.equal(buildActivity({ ...claude, activityName: 'x', privacy }).name, 'Coding with Claude Code', 'invalid names fall back per agent');
});
