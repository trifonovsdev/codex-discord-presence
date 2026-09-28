'use strict';

const { DEFAULT_ACTIVITY_NAME, DEFAULT_CLAUDE_ACTIVITY_NAME, normalizeActivityName } = require('./config');

// Discord rejects `details`/`state` shorter than 2 or longer than 128 characters.
const MIN_FIELD = 2;
const MAX_FIELD = 128;

const AGENT_TEXT = {
  codex: { name: 'Codex', surface: 'Codex Desktop', image: 'OpenAI Codex' },
  claude: { name: 'Claude Code', surface: 'Claude Code', image: 'Claude Code' },
};

function englishStrings({ name, surface }) {
  return {
    genericDetails: `Working in ${name}`,
    fallbackState: `Active ${name} session`,
    hiddenFileState: 'Working privately',
    hiddenProject: surface,
    localWorkspace: 'Local',
    details: (project) => `Project: ${project}`,
    taskDetails: (task) => `Task: ${task}`,
    state: (file) => `Editing: ${file}`,
  };
}

function russianStrings({ name, surface }) {
  return {
    genericDetails: `Работает в ${name}`,
    fallbackState: `Активная сессия ${name}`,
    hiddenFileState: 'Работает приватно',
    hiddenProject: surface,
    localWorkspace: 'Локально',
    details: (project) => `Проект: ${project}`,
    taskDetails: (task) => `Задача: ${task}`,
    state: (file) => `Файл: ${file}`,
  };
}

const STRINGS = Object.fromEntries(Object.entries(AGENT_TEXT).map(([agent, text]) => [
  agent,
  { en: englishStrings(text), ru: russianStrings(text) },
]));

function stringsFor(language, agent = 'codex') {
  const byLanguage = STRINGS[agent] || STRINGS.codex;
  return byLanguage[language] || byLanguage.en;
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
  activityName = DEFAULT_ACTIVITY_NAME,
  project = null,
  task = null,
  file = null,
  workspace = null,
  privacy,
  language = 'en',
  startedAt = null,
  largeImageKey = '',
  largeImageText = '',
  agent = 'codex',
} = {}) {
  const text = stringsFor(language, agent);
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
    name: normalizeActivityName(activityName, agent === 'claude' ? DEFAULT_CLAUDE_ACTIVITY_NAME : DEFAULT_ACTIVITY_NAME),
    type: 0,
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
      large_text: `${largeImageText || (AGENT_TEXT[agent] || AGENT_TEXT.codex).image}${workspaceSuffix}`.slice(0, MAX_FIELD),
    };
  }

  if (startedAt && privacy.showTimer) activity.timestamps = { start: startedAt };
  return activity;
}

module.exports = { buildActivity, stringsFor, STRINGS, AGENT_TEXT, MAX_FIELD };
