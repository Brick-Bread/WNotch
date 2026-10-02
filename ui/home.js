// The Home tab: a placeholder card with the time and the running agent sessions.

import { state, on } from './store.js';
import { showSession } from './terminal-tab.js';
import { h } from './dom.js';

const STATE_LABELS = { idle: 'Idle', working: 'Working', needsInput: 'Needs input', done: 'Done' };

export function initHome(pane) {
  const time = h('div', { class: 'clock-time' });
  const date = h('div', { class: 'clock-date' });
  const agents = h('div', { class: 'agent-list' });
  pane.append(
    h('div', { class: 'card clock' }, time, date),
    h('div', { class: 'card agents' }, h('h3', {}, 'Agent sessions'), agents));

  function renderClock() {
    const now = new Date();
    time.textContent = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    date.textContent = now.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long' });
  }

  function renderAgents() {
    const running = state.sessions.filter(session => session.agent !== 'none' && !session.exited);
    if (running.length === 0) {
      agents.replaceChildren(h('div', { class: 'agent-empty' }, 'No agents running.'));
      return;
    }
    agents.replaceChildren(...running.map(session => h('button', {
      class: 'agent-row',
      onclick: () => showSession(session.id),
    },
    h('span', { class: `dot ${session.state}` }),
    h('span', { class: 'agent-name' }, `${session.glyph} ${session.displayName}`),
    h('span', { class: 'agent-folder' }, session.folderName),
    h('span', { class: 'agent-state' }, STATE_LABELS[session.state] ?? session.state))));
  }

  on('sessions', renderAgents);
  on('ui', renderClock);
  setInterval(() => {
    if (state.expanded && state.tab === 'home') {
      renderClock();
    }
  }, 1000);
  renderClock();
  renderAgents();
}
