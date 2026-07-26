'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');

const { buildActivity, MAX_FIELD } = require('../src/presence');
const { PRIVACY_PRESETS } = require('../src/config');

const base = { largeImageKey: 'codex', largeImageText: 'OpenAI Codex' };

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

test('the minimal preset hides the file name', () => {
  const card = buildActivity({ ...base, project: 'store', file: 'src/secret-client.ts', privacy: { ...PRIVACY_PRESETS.minimal, preset: 'minimal' }, language: 'en' });
  assert.equal(card.state, 'Editing: Working in Codex');
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

test('an unknown project falls back to a placeholder rather than an empty card', () => {
  const privacy = { ...PRIVACY_PRESETS.standard, preset: 'standard' };
  assert.equal(buildActivity({ ...base, project: null, file: null, privacy, language: 'en' }).details, 'Project: Local task');
  assert.equal(buildActivity({ ...base, project: null, file: null, privacy, language: 'ru' }).details, 'Проект: Локальная задача');
});
