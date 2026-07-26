'use strict';

const { isAnyFilesystemRoot, projectNameFromCwd } = require('./codex-paths');

const TIMESTAMP = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)/;
const ROUTE = /ownerRoutePath=\/(local|remote)\/([^\s?#]+)/i;
const CWD = /\bcwd=("[^"]+"|'[^']+'|\S+)/i;
const GIT_DIRECTORY = /[\\/]\.git(?:[\\/]|$)/i;

// A cwd line only belongs to a route if it was logged right after it.
const ROUTE_WINDOW_MS = 10_000;
const DEFAULT_THREAD_LIMIT = 200;

/** Parses one Codex Desktop log line into zero or more ordered events. */
function parseDesktopLogLine(line) {
  const timestamp = TIMESTAMP.exec(line)?.[1];
  if (!timestamp) return [];
  const at = Date.parse(timestamp);
  if (!Number.isFinite(at)) return [];

  const events = [];
  const route = ROUTE.exec(line);
  if (route) events.push({ type: 'route', at, kind: route[1].toLowerCase(), threadId: route[2] });

  const cwdMatch = CWD.exec(line);
  if (cwdMatch) {
    const cwd = cwdMatch[1].replace(/^['"]|['"]$/g, '');
    if (cwd && !GIT_DIRECTORY.test(cwd) && !isAnyFilesystemRoot(cwd)) events.push({ type: 'cwd', at, cwd });
  }
  return events;
}

/**
 * Tracks which Codex Desktop task is currently selected.
 *
 * The previous implementation kept every event ever seen (capped at 30 000),
 * re-sorted that array and replayed it from scratch every two seconds. This
 * version folds each polling batch into persistent state, so the work is
 * proportional to the new log lines rather than to the whole session history.
 */
class DesktopSelection {
  constructor({ threadLimit = DEFAULT_THREAD_LIMIT } = {}) {
    this.threadLimit = threadLimit;
    this.threads = new Map();
    this.activeRoute = null;
    this.selectedThreadId = null;
    this.selectedRouteKind = null;
    this.selectedRouteAt = 0;
  }

  /**
   * Folds one polling batch in. Returns true when the selected task changed.
   * The batch is sorted locally because a single poll can pick up interleaved
   * lines from several rotating log files.
   */
  ingest(events) {
    if (!events.length) return false;
    const ordered = [...events].sort((left, right) => left.at - right.at || (left.type === 'route' ? -1 : 1));
    let switched = false;

    for (const event of ordered) {
      if (event.type === 'route') {
        this.activeRoute = event;
        const state = this.#thread(event.threadId);
        state.kind = event.kind;
        state.lastSelectedAt = event.at;
        if (event.at >= this.selectedRouteAt) {
          switched = switched || event.threadId !== this.selectedThreadId;
          this.selectedThreadId = event.threadId;
          this.selectedRouteKind = event.kind;
          this.selectedRouteAt = event.at;
        }
        continue;
      }

      if (event.type === 'cwd' && this.activeRoute) {
        const delta = event.at - this.activeRoute.at;
        if (delta < 0 || delta > ROUTE_WINDOW_MS) continue;
        const state = this.#thread(this.activeRoute.threadId);
        if (state.cwdRouteDelta == null || delta < state.cwdRouteDelta) {
          state.cwd = event.cwd;
          state.project = projectNameFromCwd(event.cwd);
          state.cwdAt = event.at;
          state.cwdRouteDelta = delta;
        }
      }
    }

    this.#prune();
    return switched;
  }

  selected() {
    if (!this.selectedThreadId) return null;
    return { ...this.#thread(this.selectedThreadId), threadId: this.selectedThreadId, kind: this.selectedRouteKind, at: this.selectedRouteAt };
  }

  threadState(threadId) {
    return threadId ? this.threads.get(threadId) ?? null : null;
  }

  updateThread(threadId, patch) {
    if (!threadId) return null;
    const state = this.#thread(threadId);
    Object.assign(state, patch);
    return state;
  }

  knownProjects() {
    return Object.fromEntries([...this.threads].filter(([, state]) => state.project).map(([threadId, state]) => [threadId, state.project]));
  }

  #thread(threadId) {
    let state = this.threads.get(threadId);
    if (!state) {
      state = { kind: null, cwd: null, project: null, lastFile: null, lastSelectedAt: 0, cwdAt: 0, cwdRouteDelta: null };
      this.threads.set(threadId, state);
    }
    return state;
  }

  /** Keeps only the most recently selected threads so memory stays bounded. */
  #prune() {
    if (this.threads.size <= this.threadLimit) return;
    const ordered = [...this.threads].sort((left, right) => (right[1].lastSelectedAt || 0) - (left[1].lastSelectedAt || 0));
    this.threads = new Map(ordered.slice(0, this.threadLimit));
    if (this.selectedThreadId && !this.threads.has(this.selectedThreadId)) this.#thread(this.selectedThreadId);
  }
}

module.exports = { DesktopSelection, parseDesktopLogLine, ROUTE_WINDOW_MS };
