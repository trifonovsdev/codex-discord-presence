'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');

const { selectAgent } = require('../src/agents');

const agents = (codex, claude) => ({
  codex: { enabled: true, active: true, focusAt: null, activityAt: null, ...codex },
  claude: { enabled: true, active: true, focusAt: null, activityAt: null, ...claude },
});

test('nothing is published while every agent is idle or disabled', () => {
  assert.equal(selectAgent({ agents: agents({ active: false }, { active: false }) }), null);
  assert.equal(selectAgent({ agents: agents({ enabled: false }, { active: false }) }), null);
});

test('the agent the user interacted with last owns the card', () => {
  assert.equal(selectAgent({ agents: agents({ focusAt: 100 }, { focusAt: 200 }) }), 'claude');
  assert.equal(selectAgent({ agents: agents({ focusAt: 300 }, { focusAt: 200, activityAt: 900 }) }), 'codex', 'background activity never beats a prompt');
  assert.equal(selectAgent({ agents: agents({ activityAt: 5 }, { activityAt: 9 }) }), 'claude', 'activity breaks focus ties');
  assert.equal(selectAgent({ agents: agents({}, {}), current: 'claude' }), 'claude', 'a full tie keeps the current owner');
});

test('a preferred agent wins whenever it is active', () => {
  assert.equal(selectAgent({ preferred: 'codex', agents: agents({ focusAt: 1 }, { focusAt: 9 }) }), 'codex');
  assert.equal(selectAgent({ preferred: 'codex', agents: agents({ active: false }, { focusAt: 9 }) }), 'claude');
  assert.equal(selectAgent({ preferred: 'claude', agents: agents({ focusAt: 9 }, { enabled: false }) }), 'codex');
});
