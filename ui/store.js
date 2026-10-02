// The single shared state object plus a tiny topic-based change notifier.
// Topics: 'settings' (settings or profiles), 'activities', 'sessions', 'ui' (expanded, tab, settings panel).

/**
 * @typedef {{ id: string, profileId: string, displayName: string, glyph: string, folder: string,
 *   folderName: string, agent: string, state: string, exited: boolean }} Session
 */

export const state = {
  /** @type {any} */ settings: null,
  /** @type {any[]} */ profiles: [],
  folder: '',
  /** @type {any[]} */ activities: [],
  version: '',
  /** @type {Session[]} */ sessions: [],
  /** @type {string | null} */ activeSessionId: null,
  /** @type {'home' | 'terminal'} */ tab: 'home',
  expanded: false,
  settingsOpen: false,
};

/** @type {Map<string, Set<() => void>>} */
const listeners = new Map();

/** Subscribe to a topic; returns nothing, subscriptions live for the page's lifetime. */
export function on(topic, listener) {
  if (!listeners.has(topic)) {
    listeners.set(topic, new Set());
  }
  listeners.get(topic).add(listener);
}

export function emit(topic) {
  for (const listener of listeners.get(topic) ?? []) {
    listener();
  }
}

/** Applies a `{ settings, profiles }` payload (from save_settings or the `settings` event). */
export function applySettings({ settings, profiles }) {
  state.settings = settings;
  state.profiles = profiles;
  emit('settings');
}
