// The in-page Settings panel: the same sections and fields as the C# settings window.
// Plugins and Updates are mounted from their own files into the marked sections.

import { state, on, emit, applySettings } from './store.js';
import { invoke, listen } from './backend.js';
import { h } from './dom.js';
import { GLOW_COLORS } from './glow.js';
import { readTimerPresets } from './timer-presets.js';
import { mountUpdates } from './updates-settings.js';

const MIN_GLOW = 25;
const MAX_GLOW = 200;
const MAX_TERMINAL_PRESETS = 6;
const NO_ACCENT = 'None';

const PRESETS_HELP = 'One per line, up to six: Name = command arguments. Put NAME=value pairs before the command to set environment variables for that button only, for example "Work Claude = CLAUDE_CONFIG_DIR=C:\\work claude" or "Codex fork = my-codex --model o3". A command with claude or codex in its name gets the agent status in the pill.';
const TIMERS_HELP = 'One per line, up to six: a length such as 5m, 1:30 or 1h20m, with a name in front if you like (Tea 3m). The + button on the timer card takes any other length.';
const CALENDAR_HELP = 'One iCalendar (.ics) link per line. Google Calendar and Outlook both offer a private link under their sharing settings.';
const HOTKEY_HELP = 'Opens and closes the notch from any app. Ctrl, Alt, Shift and Win with a letter, a digit, F1 to F24 or Space, such as Alt+Shift+N or Ctrl+Alt+F12. Leave empty for none.';
const SHELF_HELP = 'When off, files dropped on the pill still go on the shelf; the notch just stays closed.';
const UPDATE_HELP = 'Checks GitHub for new releases, installs them in the background and restarts Notch when it is not in use.';

/** Settings that are lists of lines, shown one per line in a box. */
const LINE_FIELDS = ['terminalPresets', 'timerPresets', 'calendarFeeds'];

/** The pomodoro lengths, which keep their old value when the box holds nothing sensible. */
const MINUTE_FIELDS = ['pomodoroFocusMinutes', 'pomodoroShortBreakMinutes', 'pomodoroLongBreakMinutes'];

/** The "Show in the pill" switches, in the order the C# window lists them. */
const PILL_CHECKS = [
  ['showMedia', 'Media that is playing'],
  ['showVolume', 'Volume and audio output changes'],
  ['showBrightness', 'Brightness changes'],
  ['showPower', 'Charging and low battery'],
  ['showBluetooth', 'Bluetooth devices connecting'],
  ['showCapsLock', 'Caps Lock switching on or off'],
];

const select = (name, options) => h('select', { name },
  options.map(([value, label]) => h('option', { value }, label)));

const row = (label, ...controls) => h('div', { class: 'row' }, h('span', { class: 'row-label' }, label), ...controls);

const check = (name, label, title) => h('label', { class: 'check', title }, h('input', { type: 'checkbox', name }), h('span', {}, label));

const lines = (name, rows) => h('textarea', { name, rows, wrap: 'off', spellcheck: 'false' });

const heading = text => h('h3', {}, text);

const help = text => h('p', { class: 'help' }, text);

/** Whole minutes from 1 to 600, else the previous value. */
export function readMinutes(text, previous) {
  const minutes = /^\d+$/.test(text.trim()) ? Number(text.trim()) : NaN;
  return minutes >= 1 && minutes <= 600 ? minutes : previous;
}

/** Trimmed non-empty lines, each only once (the case does not matter). */
export function distinctLines(text) {
  const seen = new Set();
  return text.split(/[\r\n]+/).map(line => line.trim()).filter(line => {
    const key = line.toLowerCase();
    if (!line || seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

export function initSettingsPanel(pane) {
  const windows = state.platform === 'windows';
  const swatches = [
    ...Object.entries(GLOW_COLORS).map(([name, [r, g, b]]) => h('label', {
      class: 'swatch', title: name, style: `--swatch:rgb(${r} ${g} ${b})`,
    }, h('input', { type: 'radio', name: 'accentColor', value: name }), h('span', {}))),
    // The last swatch switches the accent off; it is drawn in the text colour.
    h('label', { class: 'swatch', title: 'No accent colour', style: '--swatch:var(--text)' },
      h('input', { type: 'radio', name: 'accentColor', value: NO_ACCENT }), h('span', {})),
  ];

  const display = select('displayIndex', [['', 'Primary display']]);
  const intensity = h('input', { type: 'range', name: 'glowIntensity', min: MIN_GLOW, max: MAX_GLOW, step: 5 });
  const intensityText = h('span', { class: 'slider-value' });
  const hotkeyNote = h('p', { class: 'help warn', role: 'status', hidden: true });
  const hotkey = h('input', { type: 'text', name: 'openHotkey', title: HOTKEY_HELP, spellcheck: 'false' });
  const autostart = h('input', { type: 'checkbox', name: 'startWithSystem' });

  const form = h('form', { class: 'settings-form', autocomplete: 'off' },
    heading('General'),
    h('label', { class: 'check' }, autostart, h('span', {}, windows ? 'Start with Windows' : 'Start with the system')),
    check('expandOnHover', 'Open the notch when the pointer hovers over it'),
    check('shelfOpensOnDrag', 'Open the Shelf when files are dragged onto the pill', SHELF_HELP),
    check('hideInFullscreen', 'Hide while a game or other fullscreen app is showing'),
    check('autoUpdate', 'Install updates automatically', UPDATE_HELP),
    row('Display', display),
    row('Position', select('position', [['topCenter', 'Top of the screen'], ['taskbarLeft', 'Taskbar, far left']])),
    row('Style', select('style', [['notch', 'Notch (attached to the screen edge)'], ['island', 'Dynamic Island (floating pill)']])),
    row('Theme', select('theme', [['dark', 'Dark'], ['light', 'Light'], ['system', windows ? 'Follow Windows' : 'Follow system']])),
    row('Hotkey', hotkey),
    hotkeyNote,
    row('Accent', h('div', { class: 'swatches' }, swatches)),

    heading('Show in the pill'),
    check('glowEffects', 'Glow effects (colour and rhythm follow what is happening)'),
    h('div', { class: 'row indent', 'data-needs': 'glowEffects' }, h('span', { class: 'row-label' }, 'Brightness'), intensity, intensityText),
    PILL_CHECKS.map(([name, label]) => check(name, label)),

    heading('Terminal buttons'),
    help(PRESETS_HELP),
    lines('terminalPresets', 4),

    heading('Timer presets'),
    help(TIMERS_HELP),
    lines('timerPresets', 4),

    heading('Pomodoro (minutes)'),
    h('div', { class: 'row pomodoro' },
      ['Focus', 'Short break', 'Long break'].flatMap((label, i) => [
        h('label', { for: `pomodoro-${i}` }, label),
        h('input', { type: 'text', id: `pomodoro-${i}`, name: MINUTE_FIELDS[i], class: 'mini', inputmode: 'numeric', maxlength: 3 }),
      ])),

    heading('Calendar feeds'),
    help(CALENDAR_HELP),
    lines('calendarFeeds', 3),

    // Mount points for the modules that own these sections; each fills in its own heading.
    h('section', { id: 'settings-plugins' }),
    h('section', { id: 'settings-updates' }));
  mountUpdates(form.querySelector('#settings-updates'));

  const status = h('span', { class: 'version', role: 'status' });
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

  /** A message the user has to acknowledge, as the C# window's message boxes were. Resolves when dismissed. */
  function notice(message) {
    return new Promise(resolve => {
      const ok = h('button', { class: 'btn primary', type: 'button' }, 'OK');
      const overlay = h('div', { class: 'modal', role: 'alertdialog', 'aria-modal': 'true', 'aria-label': 'Notch' },
        h('div', { class: 'modal-box' }, h('p', { class: 'modal-text' }, message), ok));
      const dismiss = () => {
        overlay.remove();
        resolve();
      };
      ok.addEventListener('click', dismiss);
      overlay.addEventListener('keydown', event => {
        if (event.key === 'Escape' || event.key === 'Enter') {
          event.preventDefault();
          event.stopPropagation();
          dismiss();
        }
      });
      pane.append(overlay);
      ok.focus();
    });
  }

  function syncDependents() {
    intensityText.textContent = `${intensity.value}%`;
    intensity.disabled = !field('glowEffects').checked;
    form.querySelector('[data-needs]').classList.toggle('disabled', intensity.disabled);
  }

  function showHotkeyProblem(problem) {
    hotkeyNote.textContent = problem ?? '';
    hotkeyNote.hidden = !problem;
  }

  /** The "Primary display" entry plus one per display; a saved index that is gone selects the primary. */
  async function listDisplays(chosen) {
    const displays = (await invoke('list_displays')) ?? [];
    display.replaceChildren(
      h('option', { value: '' }, 'Primary display'),
      ...displays.map(entry => h('option', { value: String(entry.index) }, entry.label)));
    display.value = chosen !== null && chosen !== undefined && chosen < displays.length ? String(chosen) : '';
  }

  async function load() {
    const settings = state.settings;
    for (const element of form.elements) {
      const { name, type } = element;
      if (!name || !(name in settings) || name === 'displayIndex') {
        continue;
      }
      if (type === 'checkbox') {
        element.checked = settings[name];
      } else if (type === 'radio') {
        continue;
      } else if (LINE_FIELDS.includes(name)) {
        element.value = settings[name].join('\n');
      } else {
        element.value = settings[name];
      }
    }
    const known = Object.keys(GLOW_COLORS).find(name => name.toLowerCase() === String(settings.accentColor).toLowerCase());
    for (const radio of form.querySelectorAll('input[name="accentColor"]')) {
      radio.checked = radio.value === (known ?? NO_ACCENT);
    }
    intensity.value = Math.min(MAX_GLOW, Math.max(MIN_GLOW, settings.glowIntensity));
    status.textContent = '';
    syncDependents();

    const [, enabled, problem] = await Promise.all([
      listDisplays(settings.displayIndex),
      invoke('get_autostart'),
      invoke('hotkey_status'),
    ]);
    autostart.checked = Boolean(enabled);
    showHotkeyProblem(problem);
  }

  /**
   * Reads the form into settings. Returns `{ settings }`, or `{ message, focus }` for the first
   * thing that cannot be read, in the order the C# window checked them.
   */
  async function read() {
    const value = name => field(name).value;
    const terminalPresets = value('terminalPresets').split(/[\r\n]+/).map(line => line.trim()).filter(Boolean);
    const timers = readTimerPresets(value('timerPresets'));
    const hotkeyText = value('openHotkey').trim();

    const candidate = { ...state.settings, terminalPresets, openHotkey: hotkeyText };
    const verdict = await invoke('validate_settings', { settings: candidate });
    const problem = verdict?.problem;
    if (problem?.field === 'terminalPresets') {
      return { message: problem.message, focus: field('terminalPresets') };
    }
    if (timers.bad !== undefined) {
      return {
        message: `This timer preset could not be read:\n\n${timers.bad}\n\nUse a length such as 5m, 1:30 or 1h20m, with a name in front if you like, for example "Tea 3m".`,
        focus: field('timerPresets'),
      };
    }
    if (problem) {
      return { message: problem.message, focus: hotkey };
    }

    const checked = name => field(name).checked;
    const previous = state.settings;
    const accent = form.querySelector('input[name="accentColor"]:checked')?.value ?? previous.accentColor;
    return {
      settings: {
        ...previous,
        terminalPresets: terminalPresets.slice(0, MAX_TERMINAL_PRESETS),
        timerPresets: timers.lines,
        openHotkey: verdict?.openHotkey ?? hotkeyText,
        expandOnHover: checked('expandOnHover'),
        shelfOpensOnDrag: checked('shelfOpensOnDrag'),
        theme: value('theme'),
        position: value('position'),
        style: value('style'),
        accentColor: accent,
        hideInFullscreen: checked('hideInFullscreen'),
        autoUpdate: checked('autoUpdate'),
        glowEffects: checked('glowEffects'),
        glowIntensity: Math.round(Number(value('glowIntensity'))),
        ...Object.fromEntries(MINUTE_FIELDS.map(name => [name, readMinutes(value(name), previous[name])])),
        ...Object.fromEntries(PILL_CHECKS.map(([name]) => [name, checked(name)])),
        displayIndex: display.value === '' ? null : Number(display.value),
        calendarFeeds: distinctLines(value('calendarFeeds')),
      },
    };
  }

  let saving = false;
  form.addEventListener('input', syncDependents);
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (saving) {
      return;
    }
    saving = true;
    try {
      const result = await read();
      if (!result.settings) {
        await notice(result.message);
        result.focus.focus();
        return;
      }
      applySettings(await invoke('save_settings', { settings: result.settings }));

      try {
        await invoke('set_autostart', { enabled: autostart.checked });
      } catch (error) {
        await notice(String(error));
      }

      // Only now is it known whether the system lets Notch have the hotkey.
      const problem = await invoke('hotkey_status');
      showHotkeyProblem(problem);
      if (problem) {
        await notice(`${problem} Choose another one in Settings.`);
        hotkey.focus();
        return;
      }
      closePanel();
    } catch (error) {
      status.textContent = `Could not save: ${error}`;
    } finally {
      saving = false;
    }
  });

  listen('hotkey-status', ({ problem }) => showHotkeyProblem(problem));

  // The theme can also change from the tray menu while the panel is open; the rest is left as typed.
  on('settings', () => {
    if (state.settingsOpen) {
      field('theme').value = state.settings.theme;
    } else {
      load();
    }
  });
  let wasOpen = false;
  on('ui', () => {
    if (state.settingsOpen && !wasOpen) {
      load();
    }
    wasOpen = state.settingsOpen;
  });
  load();
}
