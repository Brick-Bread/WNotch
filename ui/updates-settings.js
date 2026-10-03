// The "Update Notch" button and its status line, mounted into the settings panel's
// <section id="settings-updates">.

import { invoke, listen } from './backend.js';
import { h } from './dom.js';

const BUTTON_TITLE = 'Checks GitHub for a newer Notch, installs it even if automatic updates are off, and restarts Notch.';

/** Fills `section` with the update button and what the updater last said. */
export function mountUpdates(section) {
  const status = h('p', { class: 'help update-status', role: 'status', 'aria-live': 'polite', hidden: true });
  const button = h('button', { class: 'btn', type: 'button', title: BUTTON_TITLE }, 'Update Notch');

  function show(update) {
    status.textContent = update.text ?? '';
    status.hidden = !update.text;
    button.disabled = Boolean(update.busy);
    button.textContent = update.busy ? 'Updating…' : 'Update Notch';
  }

  button.addEventListener('click', async () => {
    show({ busy: true, text: 'Checking GitHub for a newer Notch…' });
    try {
      show({ text: await invoke('check_updates_now'), busy: false });
    } catch (error) {
      show({ text: `Could not update: ${error}`, busy: false });
    }
  });

  section.append(
    h('h3', {}, 'Updates'),
    h('div', { class: 'updates-row' }, button),
    status);

  show({});
  invoke('update_status').then(update => update && show(update), () => {});
  listen('update-status', show);
}
