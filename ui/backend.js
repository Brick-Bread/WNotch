// The Tauri bridge. Without `window.__TAURI__` (a plain browser) a mock backend stands in,
// so the page can be developed and screenshotted outside the app.

const tauri = window.__TAURI__;

/** True when running against the mock backend. */
export const isMock = !tauri;

const backend = tauri
  ? { invoke: tauri.core.invoke, listen: tauri.event.listen }
  : (await import('./mock.js')).createMock(new URLSearchParams(location.search).get('mock'));

/** Calls a Tauri command. */
export const invoke = (command, args) => backend.invoke(command, args);

/** Subscribes to a Tauri event; the callback receives the event's payload. */
export const listen = (name, callback) => backend.listen(name, event => callback(event.payload));
