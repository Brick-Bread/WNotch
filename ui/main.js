import { state, on, emit, applySettings } from './store.js';
import { invoke, listen } from './backend.js';
import { $ } from './dom.js';
import { initTheme } from './theme.js';
import { initIsland, keepOpenWhile } from './island.js';
import { initHome } from './home.js';
import { initTerminalTab, terminalHasFocus } from './terminal-tab.js';
import { initSettingsPanel } from './settings-panel.js';

const expandedView = $('#expanded');
const tabButtons = [...document.querySelectorAll('.tab')];

function renderView() {
  const view = state.settingsOpen ? 'settings' : state.tab;
  expandedView.dataset.view = view;
  for (const button of tabButtons) {
    const selected = !state.settingsOpen && button.dataset.tab === state.tab;
    button.classList.toggle('active', selected);
    button.setAttribute('aria-selected', String(selected));
  }
  $('#gear').classList.toggle('active', state.settingsOpen);
}

const initial = await invoke('get_state');
Object.assign(state, initial);
listen('activities', activities => {
  state.activities = activities;
  emit('activities');
});
listen('settings', applySettings);
listen('open-settings', () => {
  state.settingsOpen = true;
  emit('ui');
});

initTheme();
initHome($('#pane-home'));
initTerminalTab($('#pane-terminal'));
initSettingsPanel($('#pane-settings'));
keepOpenWhile(terminalHasFocus);
initIsland();

for (const button of tabButtons) {
  button.addEventListener('click', () => {
    state.tab = button.dataset.tab;
    state.settingsOpen = false;
    emit('ui');
  });
}
$('#gear').addEventListener('click', () => {
  state.settingsOpen = !state.settingsOpen;
  emit('ui');
});
on('ui', renderView);
renderView();
