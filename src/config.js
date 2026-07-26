'use strict';

const fs = require('fs');

const HOST_PATTERN = /^[A-Za-z0-9._@:-]+$/;
const MONITOR_PATH_PATTERN = /^[A-Za-z0-9_./~-]+$/;
const PROCESS_PATTERN = /^[A-Za-z0-9_.-]+$/;
const CLIENT_ID_PATTERN = /^[0-9]{5,32}$/;

const PRESETS = ['minimal', 'standard', 'detailed'];
const FILE_MODES = ['name', 'relative'];
const LANGUAGES = ['en', 'ru'];

const DEFAULT_MONITOR_PATH = '~/.local/share/CodexDiscordPresence/remote-monitor.py';

const DEFAULT_CONFIG = Object.freeze({
  clientId: '1526968377048956938',
  port: 37642,
  language: 'en',
  largeImageKey: 'codex',
  largeImageText: 'OpenAI Codex',
  appProcess: 'ChatGPT',
  presenceEnabled: true,
  privacy: Object.freeze({
    preset: 'standard',
    showProject: true,
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
});

const PRIVACY_PRESETS = Object.freeze({
  minimal: Object.freeze({ showProject: true, showFile: false, showTimer: true, fileMode: 'name' }),
  standard: Object.freeze({ showProject: true, showFile: true, showTimer: true, fileMode: 'relative' }),
  detailed: Object.freeze({ showProject: true, showFile: true, showTimer: true, fileMode: 'relative' }),
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

  const privacyRaw = raw.privacy && typeof raw.privacy === 'object' ? raw.privacy : {};
  const remoteRaw = raw.remote && typeof raw.remote === 'object' ? raw.remote : {};
  const preset = pickEnum(privacyRaw.preset, PRESETS, DEFAULT_CONFIG.privacy.preset);
  const presetDefaults = PRIVACY_PRESETS[preset];

  const config = {
    clientId: pickString(raw.clientId, CLIENT_ID_PATTERN, DEFAULT_CONFIG.clientId),
    port: pickInteger(raw.port, { min: 1, max: 65535, fallback: DEFAULT_CONFIG.port }),
    language: pickEnum(raw.language, LANGUAGES, DEFAULT_CONFIG.language),
    largeImageKey: String(raw.largeImageKey ?? DEFAULT_CONFIG.largeImageKey).slice(0, 64),
    largeImageText: String(raw.largeImageText ?? DEFAULT_CONFIG.largeImageText).slice(0, 128),
    appProcess: pickString(raw.appProcess, PROCESS_PATTERN, DEFAULT_CONFIG.appProcess).replace(/\.exe$/i, ''),
    presenceEnabled: pickBoolean(raw.presenceEnabled, DEFAULT_CONFIG.presenceEnabled),
    privacy: {
      preset,
      showProject: pickBoolean(privacyRaw.showProject, presetDefaults.showProject),
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
  };

  if (raw.port !== undefined && config.port !== Number(raw.port)) warnings.push(`port ${JSON.stringify(raw.port)} is out of range; using ${config.port}`);
  if (raw.appProcess !== undefined && config.appProcess !== String(raw.appProcess).replace(/\.exe$/i, '')) warnings.push('appProcess contains unsupported characters; using the default');
  if (raw.language !== undefined && config.language !== String(raw.language).toLowerCase()) warnings.push(`language ${JSON.stringify(raw.language)} is not supported; using ${config.language}`);

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
  PRIVACY_PRESETS,
  PRESETS,
  FILE_MODES,
  LANGUAGES,
};
