'use strict';

// Discord rejects `details`/`state` shorter than 2 or longer than 128 characters.
const MIN_FIELD = 2;
const MAX_FIELD = 128;

const STRINGS = {
  en: {
    genericDetails: 'Working in Codex',
    fallbackState: 'Active Codex session',
    hiddenFileState: 'Working privately',
    hiddenProject: 'Codex Desktop',
    localWorkspace: 'Local',
    details: (project) => `Project: ${project}`,
    taskDetails: (task) => `Task: ${task}`,
    state: (file) => `Editing: ${file}`,
  },
  ru: {
    genericDetails: 'Работает в Codex',
    fallbackState: 'Активная сессия Codex',
    hiddenFileState: 'Работает приватно',
    hiddenProject: 'Codex Desktop',
    localWorkspace: 'Локально',
    details: (project) => `Проект: ${project}`,
    taskDetails: (task) => `Задача: ${task}`,
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
  task = null,
  file = null,
  workspace = null,
  privacy,
  language = 'en',
  startedAt = null,
  largeImageKey = '',
  largeImageText = '',
} = {}) {
  const text = stringsFor(language);
  let details = text.genericDetails;
  if (privacy.showProject && project) details = text.details(project);
  else if (privacy.showTaskTitle && task) details = text.taskDetails(task);
  else if (!privacy.showProject) details = text.hiddenProject;

  let visibleState;
  if (!privacy.showFile) visibleState = text.hiddenFileState;
  else if (!file) visibleState = text.fallbackState;
  else {
    const visibleFile = privacy.fileMode === 'name'
      ? String(file).replaceAll('\\', '/').split('/').at(-1) || file
      : file;
    visibleState = text.state(visibleFile);
  }

  const activity = {
    details: clamp(details, text.genericDetails),
    state: clamp(visibleState, text.fallbackState),
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
