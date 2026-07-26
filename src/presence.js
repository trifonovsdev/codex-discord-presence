'use strict';

// Discord rejects `details`/`state` shorter than 2 or longer than 128 characters.
const MIN_FIELD = 2;
const MAX_FIELD = 128;

const STRINGS = {
  en: {
    fallbackProject: 'Local task',
    fallbackFile: 'Getting started',
    hiddenFile: 'Working in Codex',
    hiddenProject: 'Codex Desktop',
    localWorkspace: 'Local',
    details: (project) => `Project: ${project}`,
    state: (file) => `Editing: ${file}`,
  },
  ru: {
    fallbackProject: 'Локальная задача',
    fallbackFile: 'Начинаем',
    hiddenFile: 'Работает в Codex',
    hiddenProject: 'Codex Desktop',
    localWorkspace: 'Локально',
    details: (project) => `Проект: ${project}`,
    state: (file) => `Файл: ${file}`,
  },
};

function stringsFor(language) {
  return STRINGS[language] || STRINGS.en;
}

function clamp(value, fallback) {
  const text = String(value ?? '').trim();
  const safe = text.length >= MIN_FIELD ? text : String(fallback);
  return safe.slice(0, MAX_FIELD);
}

/**
 * Builds the Discord activity payload for the current state.
 *
 * `project` and `file` may be null; the localised placeholder is substituted
 * here so the rest of the daemon never has to carry display strings around.
 */
function buildActivity({
  project = null,
  file = null,
  workspace = null,
  privacy,
  language = 'en',
  startedAt = null,
  largeImageKey = '',
  largeImageText = '',
} = {}) {
  const text = stringsFor(language);
  const visibleProject = privacy.showProject ? (project || text.fallbackProject) : text.hiddenProject;

  let visibleFile;
  if (!privacy.showFile) visibleFile = text.hiddenFile;
  else if (!file) visibleFile = text.fallbackFile;
  else if (privacy.fileMode === 'name') visibleFile = String(file).replaceAll('\\', '/').split('/').at(-1) || file;
  else visibleFile = file;

  const activity = {
    details: clamp(text.details(visibleProject), text.details(text.fallbackProject)),
    state: clamp(text.state(visibleFile), text.state(text.fallbackFile)),
    instance: false,
  };

  if (largeImageKey) {
    const workspaceSuffix = privacy.preset === 'detailed' && privacy.showProject
      ? ` · ${workspace || text.localWorkspace}`
      : '';
    activity.assets = {
      large_image: String(largeImageKey),
      large_text: `${largeImageText || 'OpenAI Codex'}${workspaceSuffix}`.slice(0, MAX_FIELD),
    };
  }

  if (startedAt && privacy.showTimer) activity.timestamps = { start: startedAt };
  return activity;
}

module.exports = { buildActivity, stringsFor, STRINGS, MAX_FIELD };
