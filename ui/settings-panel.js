// The in-page Settings panel (replaces the old settings window).

import { state, on, emit, applySettings } from './store.js';
import { invoke } from './backend.js';
import { h } from './dom.js';
import { GLOW_COLORS } from './glow.js';

const PRESETS_HELP = 'One per line, up to six: Name = command arguments. Put NAME=value pairs before the command to set environment variables for that button only, for example "Work Claude = CLAUDE_CONFIG_DIR=C:\\work claude" or "Codex fork = my-codex --model o3". A command with claude or codex in its name gets the agent status in the pill.';
const HOTKEY_HELP = 'Opens and closes the notch from any app. Ctrl, Alt, Shift and Win with a letter, a digit, F1 to F24 or Space, such as Alt+Shift+N or Ctrl+Alt+F12. Leave empty for none.';

const select = (name, options) => h('select', { name },
  options.map(([value, label]) => h('option', { value }, label)));

const row = (label, control) => h('div', { class: 'row' }, h('span', { class: 'row-label' }, label), control);

const check = (name, label, title) => h('label', { class: 'check', title }, h('input', { type: 'checkbox', name }), h('span', {}, label));

export function initSettingsPanel(pane) {
  const swatches = Object.entries(GLOW_COLORS).map(([name, [r, g, b]]) => h('label', {
    class: 'swatch', title: name, style: `--swatch:rgb(${r} ${g} ${b})`,
  }, h('input', { type: 'radio', name: 'accentColor', value: name }), h('span', {})));

  const intensity = h('input', { type: 'range', name: 'glowIntensity', min: 25, max: 200, step: 5 });
  const intensityText = h('span', { class: 'slider-value' });
  const presets = h('textarea', { name: 'terminalPresets', rows: 4, wrap: 'off', spellcheck: 'false' });
  const form = h('form', { class: 'settings-form', autocomplete: 'off' },
    h('h3', {}, 'General'),
    check('expandOnHover', 'Open the notch when the pointer hovers over it'),
    check('hideInFullscreen', 'Hide while a game or other fullscreen app is showing'),
    check('autoUpdate', 'Install updates automatically',
      'Checks GitHub for new releases, installs them in the background and restarts Notch when it is not in use.'),
    row('Position', select('position', [['topCenter', 'Top of the screen'], ['taskbarLeft', 'Taskbar, far left']])),
    row('Style', select('style', [['notch', 'Notch (attached to the screen edge)'], ['island', 'Dynamic Island (floating pill)']])),
    row('Theme', select('theme', [['dark', 'Dark'], ['light', 'Light'], ['system', 'Follow system']])),
    row('Hotkey', h('input', { type: 'text', name: 'openHotkey', title: HOTKEY_HELP, spellcheck: 'false' })),
    row('Accent', h('div', { class: 'swatches' }, swatches)),
    h('h3', {}, 'Show in the pill'),
    check('glowEffects', 'Glow effects (colour and rhythm follow what is happening)'),
    h('div', { class: 'row indent', 'data-needs': 'glowEffects' }, h('span', { class: 'row-label' }, 'Brightness'), intensity, intensityText),
    h('h3', {}, 'Terminal buttons'),
    h('p', { class: 'help' }, PRESETS_HELP),
    presets);

  const status = h('span', { class: 'version' });
  const save = h('button', { class: 'btn primary', type: 'submit' }, 'Save');
  const cancel = h('button', { class: 'btn', type: 'button', onclick: closePanel }, 'Cancel');
  pane.append(form, h('div', { class: 'settings-actions' }, status, cancel, save));
  save.setAttribute('form', 'settings-form');
  form.id = 'settings-form';

  const field = name => form.elements[name];

  function closePanel() {
    state.settingsOpen = false;
    emit('ui');
  }

  function syncDependents() {
    intensityText.textContent = `${intensity.value}%`;
    intensity.disabled = !field('glowEffects').checked;
    form.querySelector('[data-needs]').classList.toggle('disabled', intensity.disabled);
  }

  function load() {
    const settings = state.settings;
    for (const element of form.elements) {
      const { name, type } = element;
      if (!name || !(name in settings)) {
        continue;
      }
      if (type === 'checkbox') {
        element.checked = settings[name];
      } else if (type === 'radio') {
        element.checked = settings[name].toLowerCase() === element.value.toLowerCase();
      } else if (name === 'terminalPresets') {
        element.value = settings[name].join('\n');
      } else {
        element.value = settings[name];
      }
    }
    status.textContent = `Notch ${state.version}`;
    syncDependents();
  }

  form.addEventListener('input', syncDependents);
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const data = new FormData(form);
    const checked = name => field(name).checked;
    const settings = {
      ...state.settings,
      position: data.get('position'),
      style: data.get('style'),
      theme: data.get('theme'),
      openHotkey: data.get('openHotkey').trim(),
      accentColor: data.get('accentColor'),
      expandOnHover: checked('expandOnHover'),
      hideInFullscreen: checked('hideInFullscreen'),
      autoUpdate: checked('autoUpdate'),
      glowEffects: checked('glowEffects'),
      glowIntensity: Number(data.get('glowIntensity')),
      terminalPresets: data.get('terminalPresets').split('\n').map(line => line.trim()).filter(Boolean),
    };
    try {
      applySettings(await invoke('save_settings', { settings }));
      closePanel();
    } catch (error) {
      status.textContent = `Could not save: ${error}`;
    }
  });

  on('settings', load);
  let wasOpen = false;
  on('ui', () => {
    if (state.settingsOpen && !wasOpen) {
      load();
    }
    wasOpen = state.settingsOpen;
  });
  load();
}
