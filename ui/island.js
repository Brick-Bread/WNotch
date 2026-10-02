// The island itself: shape (collapsed / compact / expanded), the compact activity row,
// the glow, and the hover / click / Escape behaviour.

import { state, on, emit } from './store.js';
import { invoke } from './backend.js';
import { h, $ } from './dom.js';
import { rgbOf, intensityAt, isAnimated, gain, level } from './glow.js';

const HOVER_DELAY = 80;
const CLOSE_DELAY = 450;
/** Matches the width/height transition in style.css, so the window shrinks only after the island has. */
const SHRINK_MS = 420;
/** A click this soon after a hover opened the island is the same gesture, not a request to close. */
const CLICK_GUARD_MS = 500;

const island = $('#island');
const compact = $('#compact');
const glowElement = $('#glow');

/** @type {Array<() => boolean>} */
const keepOpenReasons = [() => state.settingsOpen];
let pointerInside = false;
let hoverTimer = 0;
let closeTimer = 0;
let openedAt = 0;
let generation = 0;

/** Registers a predicate that keeps the island open while it returns true. */
export function keepOpenWhile(predicate) {
  keepOpenReasons.push(predicate);
}

const shouldStayOpen = () => keepOpenReasons.some(reason => reason());

export async function open() {
  clearTimeout(closeTimer);
  if (state.expanded) {
    return;
  }
  const mine = ++generation;
  // Grow the window first so the island never clips while it animates.
  await invoke('set_expanded', { expanded: true });
  if (mine !== generation) {
    return;
  }
  state.expanded = true;
  openedAt = performance.now();
  render();
  emit('ui');
}

export function close() {
  clearTimeout(closeTimer);
  clearTimeout(hoverTimer);
  if (!state.expanded) {
    return;
  }
  const mine = ++generation;
  document.activeElement?.blur();
  state.expanded = false;
  state.settingsOpen = false;
  render();
  emit('ui');
  // Shrink the window only once the island has finished shrinking.
  setTimeout(() => {
    if (mine === generation) {
      invoke('set_expanded', { expanded: false });
    }
  }, SHRINK_MS);
}

function scheduleClose() {
  clearTimeout(closeTimer);
  closeTimer = setTimeout(() => {
    if (!pointerInside && !shouldStayOpen()) {
      close();
    }
  }, CLOSE_DELAY);
}

// Shape --------------------------------------------------------------------

function render() {
  const activity = state.activities[0];
  island.dataset.state = state.expanded ? 'expanded' : activity ? 'compact' : 'collapsed';
  island.dataset.style = state.settings.style;
  renderCompact(activity);
  updateGlow();
}

function renderCompact(activity) {
  if (!activity) {
    compact.replaceChildren();
    return;
  }
  const icon = activity.image
    ? h('img', { class: 'activity-image', src: activity.image, alt: '' })
    : h('span', { class: 'activity-glyph' }, activity.glyph ?? '');
  const trailing = typeof activity.progress === 'number'
    ? h('span', { class: 'progress' }, h('span', { class: 'progress-fill', style: `width:${Math.round(activity.progress * 100)}%` }))
    : activity.detail && h('span', { class: 'activity-detail' }, activity.detail);
  compact.replaceChildren(icon, h('span', { class: 'activity-title' }, activity.title), trailing);
}

// Glow ---------------------------------------------------------------------

let glowKey = '';
let glowStart = 0;
let frame = 0;

function paintGlow(now) {
  const glow = state.activities[0].glow;
  const intensity = intensityAt(glow, (now - glowStart) / 1000);
  const lit = level(intensity, gain(state.settings.glowIntensity));
  const rgb = rgbOf(glow.color).join(' ');
  const alpha = Math.min(1, lit);
  const color = a => `rgb(${rgb} / ${a.toFixed(3)})`;
  glowElement.style.boxShadow = [
    `inset 0 0 0 1.5px ${color(alpha)}`,
    `inset 0 0 12px ${color(alpha * 0.3)}`,
    `0 0 ${(8 + 16 * lit).toFixed(1)}px ${(lit * 1.5).toFixed(1)}px ${color(alpha * 0.55)}`,
  ].join(', ');
  if (isAnimated(glow)) {
    frame = requestAnimationFrame(paintGlow);
  }
}

function updateGlow() {
  cancelAnimationFrame(frame);
  const glow = state.activities[0]?.glow;
  const visible = Boolean(glow) && state.settings.glowEffects && !state.expanded;
  glowElement.classList.toggle('on', visible);
  if (!glow) {
    return;
  }
  const key = `${state.activities[0].id}|${glow.pattern}|${JSON.stringify(glow.color)}`;
  if (key !== glowKey) {
    glowKey = key;
    glowStart = performance.now();
  }
  if (visible) {
    frame = requestAnimationFrame(paintGlow);
  }
}

// Input --------------------------------------------------------------------

export function initIsland() {
  island.addEventListener('pointerenter', () => {
    pointerInside = true;
    clearTimeout(closeTimer);
    if (state.settings.expandOnHover && !state.expanded) {
      clearTimeout(hoverTimer);
      hoverTimer = setTimeout(open, HOVER_DELAY);
    }
  });

  island.addEventListener('pointerleave', () => {
    pointerInside = false;
    clearTimeout(hoverTimer);
    if (state.expanded) {
      scheduleClose();
    }
  });

  // Once the keyboard leaves the island (a terminal lost focus) a waiting close can proceed.
  island.addEventListener('focusout', () => {
    if (state.expanded && !pointerInside) {
      scheduleClose();
    }
  });

  island.addEventListener('click', event => {
    if (!state.expanded) {
      open();
    } else if (event.target.closest('.strip') && !event.target.closest('button') && performance.now() - openedAt > CLICK_GUARD_MS) {
      close();
    }
  });

  // Escape closes, except where it belongs to a terminal program (vim, an agent, ...).
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') {
      return;
    }
    if (state.settingsOpen) {
      state.settingsOpen = false;
      emit('ui');
    } else if (state.expanded && !shouldStayOpen()) {
      close();
    }
  });

  // The window lost focus (the user went to another app).
  window.addEventListener('blur', () => {
    if (state.expanded && !shouldStayOpen()) {
      close();
    }
  });

  on('activities', render);
  on('settings', render);
  render();
}
