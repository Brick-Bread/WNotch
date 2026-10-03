// The command-line switches that act on the page: --tab=, --settings and --open=.
// (--pin-open lives in island.js; --style=, --position= and --display= are the backend's.)

import { state, emit } from './store.js';
import { invoke } from './backend.js';
import { open } from './island.js';

/** Applies this run's switches once the page is built. Names that match nothing are ignored. */
export async function applyStartupOptions() {
  const options = state.startupOptions;
  if (!options) {
    return;
  }

  const tab = options.tab?.toLowerCase();
  if (tab && [...document.querySelectorAll('.tab')].some(button => button.dataset.tab === tab)) {
    state.tab = tab;
  }

  const profile = options.open && state.profiles.find(p => p.id.toLowerCase() === options.open.toLowerCase());
  if (profile) {
    state.tab = 'terminal';
    await invoke('open_session', { profileId: profile.id, folder: state.folder });
  }

  if (options.settings) {
    state.settingsOpen = true;
    await open();
  }
  emit('ui');
}
