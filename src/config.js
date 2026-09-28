'use strict';

const fs = require('fs');

const { AGENT_PREFERENCES } = require('./agents');

const HOST_PATTERN = /^[A-Za-z0-9._@:-]+$/;
const MONITOR_PATH_PATTERN = /^[A-Za-z0-9_./~-]+$/;
const PROCESS_PATTERN = /^[A-Za-z0-9_.-]+$/;
const CLIENT_ID_PATTERN = /^[0-9]{5,32}$/;

const PRESETS = ['minimal', 'standard', 'detailed'];
const FILE_MODES = ['name', 'relative'];
const LANGUAGES = ['en', 'ru'];

const DEFAULT_MONITOR_PATH = '~/.local/share/CodexDiscordPresence/remote-monitor.py';
const DEFAULT_ACTIVITY_NAME = 'Coding with Codex';
const DEFAULT_CLAUDE_ACTIVITY_NAME = 'Coding with Claude Code';
// Discord renders external HTTPS images in Rich Presence, so the Claude Code
// artwork works without uploading an asset to the shared Discord application.
const DEFAULT_CLAUDE_IMAGE = 'https://raw.githubusercontent.com/trifonovsdev/codex-discord-presence/main/assets/discord/claude-code.png';
const MIN_ACTIVITY_NAME = 2;
const MAX_ACTIVITY_NAME = 128;
const MAX_IMAGE_KEY = 256;
const IMAGE_KEY_PATTERN = /^(?:[A-Za-z0-9_.-]{1,64}|https:\/\/[^\s"'<>]{8,248})$/;

const DEFAULT_CONFIG = Object.freeze({
  clientId: '1526968377048956938',
  port: 37642,
  language: 'en',
  activityName: DEFAULT_ACTIVITY_NAME,
  largeImageKey: 'codex',
  largeImageText: 'OpenAI Codex',
  appProcess: 'ChatGPT',
  presenceEnabled: true,
  privacy: Object.freeze({
    preset: 'standard',
    showProject: true,
    showTaskTitle: false,
    showFile: true,
    showTimer: true,
    fileMode: 'relative',
  }),
  remote: Object.freeze({
    host: '',
    hosts: [],
    monitorPath: DEFAULT_MONITOR_PATH,
    pollIntervalMs: 7000,
  }),
  agents: Object.freeze({
    preferred: 'auto',
    codex: Object.freeze({ enabled: true }),
    claude: Object.freeze({
      enabled: true,
      activityName: DEFAULT_CLAUDE_ACTIVITY_NAME,
      largeImageKey: DEFAULT_CLAUDE_IMAGE,
      largeImageText: 'Claude Code',
      idleMinutes: 10,
      hooks: true,
      remote: true,
    }),
  }),
});

const PRIVACY_PRESETS = Object.freeze({
  minimal: Object.freeze({ showProject: true, showTaskTitle: false, showFile: false, showTimer: true, fileMode: 'name' }),
  standard: Object.freeze({ showProject: true, showTaskTitle: false, showFile: true, showTimer: true, fileMode: 'relative' }),
  detailed: Object.freeze({ showProject: true, showTaskTitle: false, showFile: true, showTimer: true, fileMode: 'relative' }),
});

function pickString(value, pattern, fallback) {
  const text = String(value ?? '').trim();
  return pattern.test(text) ? text : fallback;
}

function pickEnum(value, allowed, fallback) {
  const text = String(value ?? '').trim().toLowerCase();
  return allowed.includes(text) ? text : fallback;
}

function pickBoolean(value, fallback) {
  return typeof value === 'boolean' ? value : fallback;
}

function pickInteger(value, { min, max, fallback }) {
  const number = Number(value);
  if (!Number.isInteger(number) || number < min || number > max) return fallback;
  return number;
}

function pickImageKey(value, fallback) {
  if (value === undefined || value === null) return fallback;
  const text = String(value).trim();
  if (!text) return '';
  return IMAGE_KEY_PATTERN.test(text) && text.length <= MAX_IMAGE_KEY ? text : fallback;
}

function objectOrEmpty(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function truncateUtf16Safely(value, maxLength) {
  let end = Math.min(value.length, maxLength);
  const lastCodeUnit = value.charCodeAt(end - 1);
  const nextCodeUnit = value.charCodeAt(end);
  const splitsSurrogatePair = lastCodeUnit >= 0xD800 && lastCodeUnit <= 0xDBFF &&
    nextCodeUnit >= 0xDC00 && nextCodeUnit <= 0xDFFF;
  if (splitsSurrogatePair) end -= 1;
  return value.slice(0, end);
}

function normalizeActivityName(value, fallback = DEFAULT_ACTIVITY_NAME) {
  if (typeof value !== 'string') return fallback;
  const normalized = value
    .toWellFormed()
    .replace(/\p{Cc}+/gu, ' ')
    .replace(/\s+/gu, ' ')
    .trim();
  if (normalized.length < MIN_ACTIVITY_NAME) return fallback;
  return truncateUtf16Safely(normalized, MAX_ACTIVITY_NAME);
}

/**
 * Reads the user config and returns a fully validated document. Invalid
 * individual fields fall back to their default instead of taking the whole
 * daemon down, so a hand-edited config.json can never brick the service.
 * `warnings` lists every field that was rejected so the caller can log it.
 */
function readConfig(configPath) {
  const warnings = [];
  let raw = {};

  try {
    const text = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) raw = parsed;
    else warnings.push('config.json is not a JSON object; defaults are in use');
  } catch (error) {
    if (error.code !== 'ENOENT') warnings.push(`config.json could not be read (${error.message}); defaults are in use`);
  }

  const privacyRaw = objectOrEmpty(raw.privacy);
  const remoteRaw = objectOrEmpty(raw.remote);
  const agentsRaw = objectOrEmpty(raw.agents);
  const codexRaw = objectOrEmpty(agentsRaw.codex);
  const claudeRaw = objectOrEmpty(agentsRaw.claude);
  const claudeDefaults = DEFAULT_CONFIG.agents.claude;
  const preset = pickEnum(privacyRaw.preset, PRESETS, DEFAULT_CONFIG.privacy.preset);
  const presetDefaults = PRIVACY_PRESETS[preset];

  const config = {
    clientId: pickString(raw.clientId, CLIENT_ID_PATTERN, DEFAULT_CONFIG.clientId),
    port: pickInteger(raw.port, { min: 1, max: 65535, fallback: DEFAULT_CONFIG.port }),
    language: pickEnum(raw.language, LANGUAGES, DEFAULT_CONFIG.language),
    activityName: raw.activityName === undefined
      ? DEFAULT_CONFIG.activityName
      : normalizeActivityName(raw.activityName),
    largeImageKey: pickImageKey(raw.largeImageKey, DEFAULT_CONFIG.largeImageKey),
    largeImageText: String(raw.largeImageText ?? DEFAULT_CONFIG.largeImageText).slice(0, 128),
    appProcess: pickString(raw.appProcess, PROCESS_PATTERN, DEFAULT_CONFIG.appProcess).replace(/\.exe$/i, ''),
    presenceEnabled: pickBoolean(raw.presenceEnabled, DEFAULT_CONFIG.presenceEnabled),
    privacy: {
      preset,
      showProject: pickBoolean(privacyRaw.showProject, presetDefaults.showProject),
      showTaskTitle: pickBoolean(privacyRaw.showTaskTitle, presetDefaults.showTaskTitle),
      showFile: pickBoolean(privacyRaw.showFile, presetDefaults.showFile),
      showTimer: pickBoolean(privacyRaw.showTimer, presetDefaults.showTimer),
      fileMode: pickEnum(privacyRaw.fileMode, FILE_MODES, presetDefaults.fileMode),
    },
    remote: {
      host: pickString(remoteRaw.host, HOST_PATTERN, ''),
      hosts: Array.isArray(remoteRaw.hosts) ? remoteRaw.hosts : [],
      monitorPath: pickString(remoteRaw.monitorPath, MONITOR_PATH_PATTERN, DEFAULT_MONITOR_PATH),
      pollIntervalMs: pickInteger(remoteRaw.pollIntervalMs, { min: 3000, max: 3_600_000, fallback: DEFAULT_CONFIG.remote.pollIntervalMs }),
    },
    agents: {
      preferred: pickEnum(agentsRaw.preferred, AGENT_PREFERENCES, DEFAULT_CONFIG.agents.preferred),
      codex: {
        enabled: pickBoolean(codexRaw.enabled, DEFAULT_CONFIG.agents.codex.enabled),
      },
      claude: {
        enabled: pickBoolean(claudeRaw.enabled, claudeDefaults.enabled),
        activityName: claudeRaw.activityName === undefined
          ? claudeDefaults.activityName
          : normalizeActivityName(claudeRaw.activityName, claudeDefaults.activityName),
        largeImageKey: pickImageKey(claudeRaw.largeImageKey, claudeDefaults.largeImageKey),
        largeImageText: String(claudeRaw.largeImageText ?? claudeDefaults.largeImageText).slice(0, 128),
        idleMinutes: pickInteger(claudeRaw.idleMinutes, { min: 1, max: 240, fallback: claudeDefaults.idleMinutes }),
        hooks: pickBoolean(claudeRaw.hooks, claudeDefaults.hooks),
        remote: pickBoolean(claudeRaw.remote, claudeDefaults.remote),
      },
    },
  };

  if (raw.port !== undefined && config.port !== Number(raw.port)) warnings.push(`port ${JSON.stringify(raw.port)} is out of range; using ${config.port}`);
  if (raw.appProcess !== undefined && config.appProcess !== String(raw.appProcess).replace(/\.exe$/i, '')) warnings.push('appProcess contains unsupported characters; using the default');
  if (raw.language !== undefined && config.language !== String(raw.language).toLowerCase()) warnings.push(`language ${JSON.stringify(raw.language)} is not supported; using ${config.language}`);
  if (raw.activityName !== undefined && (typeof raw.activityName !== 'string' || config.activityName !== raw.activityName)) {
    warnings.push('activityName was normalized to a single line between 2 and 128 characters');
  }

  if (raw.largeImageKey !== undefined && config.largeImageKey !== String(raw.largeImageKey).trim()) {
    warnings.push('largeImageKey must be an asset key or an https:// URL; using the default');
  }
  if (agentsRaw.preferred !== undefined && config.agents.preferred !== String(agentsRaw.preferred).toLowerCase()) {
    warnings.push(`agents.preferred ${JSON.stringify(agentsRaw.preferred)} is not supported; using ${config.agents.preferred}`);
  }
  if (claudeRaw.activityName !== undefined && (typeof claudeRaw.activityName !== 'string' || config.agents.claude.activityName !== claudeRaw.activityName)) {
    warnings.push('agents.claude.activityName was normalized to a single line between 2 and 128 characters');
  }
  if (claudeRaw.largeImageKey !== undefined && config.agents.claude.largeImageKey !== String(claudeRaw.largeImageKey).trim()) {
    warnings.push('agents.claude.largeImageKey must be an asset key or an https:// URL; using the default');
  }
  if (claudeRaw.idleMinutes !== undefined && config.agents.claude.idleMinutes !== Number(claudeRaw.idleMinutes)) {
    warnings.push(`agents.claude.idleMinutes must be between 1 and 240; using ${config.agents.claude.idleMinutes}`);
  }

  return { config, warnings };
}

/**
 * Merges `patch` into the on-disk config and writes it atomically.
 *
 * Refuses to write when the existing file cannot be parsed: the previous
 * implementation swallowed the parse error and replaced the whole document
 * with the patch, silently wiping every user setting.
 */
function patchConfig(configPath, patch) {
  let document = {};
  try {
    const text = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON.parse(text);
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('config.json is not a JSON object');
    }
    document = parsed;
  } catch (error) {
    if (error.code !== 'ENOENT') {
      throw new Error(`refusing to overwrite an unreadable config.json: ${error.message}`);
    }
  }

  Object.assign(document, patch);
  const temporaryPath = `${configPath}.tmp`;
  fs.writeFileSync(temporaryPath, `${JSON.stringify(document, null, 2)}\n`, 'utf8');
  fs.renameSync(temporaryPath, configPath);
  return document;
}

module.exports = {
  readConfig,
  patchConfig,
  DEFAULT_CONFIG,
  DEFAULT_MONITOR_PATH,
  DEFAULT_ACTIVITY_NAME,
  DEFAULT_CLAUDE_ACTIVITY_NAME,
  DEFAULT_CLAUDE_IMAGE,
  normalizeActivityName,
  PRIVACY_PRESETS,
  PRESETS,
  FILE_MODES,
  LANGUAGES,
};
