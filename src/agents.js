'use strict';

const AGENTS = Object.freeze(['codex', 'claude']);
const AGENT_PREFERENCES = Object.freeze(['auto', ...AGENTS]);

const AGENT_LABELS = Object.freeze({
  codex: 'Codex',
  claude: 'Claude Code',
});

/**
 * Chooses which agent owns the Discord card.
 *
 * `agents` maps an agent id to `{ enabled, active, focusAt, activityAt }`.
 * A preferred agent wins whenever it is active. In `auto` mode the agent the
 * user interacted with last wins — a prompt, a task switch, or a new session
 * — so a background agent that keeps working cannot take the card over.
 * Ties keep the current owner to avoid flapping.
 */
function selectAgent({ preferred = 'auto', agents = {}, current = null } = {}) {
  const available = AGENTS.filter((id) => agents[id]?.enabled && agents[id]?.active);
  if (!available.length) return null;
  if (preferred !== 'auto' && available.includes(preferred)) return preferred;
  if (available.length === 1) return available[0];

  const score = (id) => [agents[id].focusAt ?? 0, agents[id].activityAt ?? 0];
  return available.reduce((best, id) => {
    const [bestFocus, bestActivity] = score(best);
    const [focus, activity] = score(id);
    if (focus !== bestFocus) return focus > bestFocus ? id : best;
    if (activity !== bestActivity) return activity > bestActivity ? id : best;
    return id === current ? id : best;
  });
}

module.exports = { AGENTS, AGENT_LABELS, AGENT_PREFERENCES, selectAgent };
