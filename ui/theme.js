import { state, on, emit } from './store.js';
import { rgbOf, onLight } from './glow.js';

const prefersLight = matchMedia('(prefers-color-scheme: light)');

/** Whether the light theme is in effect (settings.theme, with `system` following the OS). */
export function isLight() {
  const theme = state.settings?.theme;
  return theme === 'light' || (theme === 'system' && prefersLight.matches);
}

function apply() {
  const root = document.documentElement;
  const light = isLight();
  root.dataset.theme = light ? 'light' : 'dark';
  const rgb = rgbOf(state.settings?.accentColor ?? 'Blue');
  root.style.setProperty('--accent-rgb', (light ? onLight(rgb) : rgb).join(' '));
  emit('theme');
}

export function initTheme() {
  on('settings', apply);
  prefersLight.addEventListener('change', apply);
  apply();
}
