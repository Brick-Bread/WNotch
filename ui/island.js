// The island itself: shape (collapsed / compact / expanded), the compact activity row,
// the glow, and the hover / click / Escape behaviour.

import { state, on, emit } from './store.js';
import { invoke, listen } from './backend.js';
import { h, $ } from './dom.js';
import { glyphNode } from './hud-icons.js';
import { rgbOf, intensityAt, isAnimated, gain, level } from './glow.js';

/** How long the pointer rests on the island before hover opens it. */
const OPEN_DELAY = 120;
/** How long the pointer is off the island before it closes. */
const CLOSE_DELAY = 350;
/** Matches the width/height transition in style.css, so the window shrinks only after the island has. */
const SHRINK_MS = 420;
/** How often an open island checks where the pointer really is. */
const WATCH_MS = 100;
/** How far outside the island, in px, the pointer still counts as on it. */
const HOVER_MARGIN = 2;

const island = $('#island');
const compact = $('#compact');
const glowElement = $('#glow');

/** @type {Array<() => boolean>} */
const keepOpenReasons = [() => state.settingsOpen];
let pointerInside = false;
/** The single pending open or close, as in the C# hover timer. */
let hoverTimer = 0;
let hoverWantsExpanded = false;
let pointerWatch = 0;
/** Set when the hotkey opened the island, which then stays open with the pointer elsewhere. */
let hotkeyHold = false;
let generation = 0;

/** Registers a predicate that keeps the island open while it returns true. */
export function keepOpenWhile(predicate) {
  keepOpenReasons.push(predicate);
}

/** `--pin-open`: expanded whatever the pointer does. */
const pinned = () => Boolean(state.startupOptions?.pinOpen);

const shouldStayOpen = () => keepOpenReasons.some(reason => reason());

/** Something keeps the island open although the pointer is not on it: it has the keyboard, a menu is showing, or the hotkey opened it. */
const holdOpen = () => hotkeyHold || shouldStayOpen();

/**
 * Whether the pointer is on the island or in the gap between a floating island and its screen
 * edge, judged by where both are on screen. Pointer events alone are not reliable here: resizing
 * the window under a still pointer makes the page report a leave.
 */
async function pointerOverIsland() {
  const position = await invoke('pointer_position');
  if (!Array.isArray(position)) {
    return pointerInside;
  }
  const [x, y] = position;
  const box = island.getBoundingClientRect();
  const atBottom = state.placement?.anchor === 'bottomLeft';
  const top = atBottom ? box.top - HOVER_MARGIN : (state.placement?.floating ? 0 : box.top - HOVER_MARGIN);
  const bottom = atBottom && state.placement?.floating ? innerHeight : box.bottom + HOVER_MARGIN;
  return x >= box.left - HOVER_MARGIN && x < box.right + HOVER_MARGIN && y >= top && y < bottom;
}

/** Opens or closes after `delay`, replacing any earlier request. A close is dropped while something holds the island open. */
function schedule(expanded, delay) {
  if (!expanded && holdOpen()) {
    return;
  }
  hoverWantsExpanded = expanded;
  clearTimeout(hoverTimer);
  hoverTimer = setTimeout(async () => {
    hoverTimer = 0;
    if (!hoverWantsExpanded && state.expanded && await pointerOverIsland()) {
      return;
    }
    setExpanded(hoverWantsExpanded);
  }, delay);
}

export function setExpanded(expanded) {
  if (expanded || pinned()) {
    open();
  } else {
    close();
  }
}

/** Runs while the island is open: closes it once the pointer has really left, and calls off a close when the pointer is back. */
async function watchPointer() {
  const closePending = hoverTimer !== 0 && !hoverWantsExpanded;
  if (await pointerOverIsland()) {
    // From here on the pointer is in charge again: leaving closes the island as usual.
    hotkeyHold = false;
    if (closePending) {
      clearTimeout(hoverTimer);
      hoverTimer = 0;
    }
  } else if (!closePending && !pinned() && !holdOpen()) {
    schedule(false, CLOSE_DELAY);
  }
}

export async function open() {
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
  clearInterval(pointerWatch);
  pointerWatch = setInterval(watchPointer, WATCH_MS);
  render();
  emit('ui');
}

export function close() {
  clearTimeout(hoverTimer);
  hoverTimer = 0;
  if (!state.expanded) {
    return;
  }
  const mine = ++generation;
  clearInterval(pointerWatch);
  hotkeyHold = false;
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

// Shape --------------------------------------------------------------------

function render() {
  const activity = state.activities[0];
  island.dataset.state = state.expanded ? 'expanded' : activity ? 'compact' : 'collapsed';
  const placement = state.placement;
  island.dataset.style = placement ? (placement.floating ? 'island' : 'notch') : state.settings.style;
  island.dataset.anchor = placement?.anchor ?? 'topCenter';
  island.style.setProperty('--edge-gap', `${placement?.edgeGap ?? 0}px`);
  island.style.setProperty('--side-inset', `${placement?.sideInset ?? 0}px`);
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
    : glyphNode(activity.glyph);
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

export async function initIsland() {
  await listen('expanded', ({ expanded, hotkey }) => {
    if (expanded) {
      hotkeyHold = Boolean(hotkey);
      open();
    } else {
      setExpanded(false);
    }
  });
  await listen('placement', placement => {
    state.placement = placement;
    render();
  });

  island.addEventListener('pointerenter', () => {
    pointerInside = true;
    // Entering also cancels a pending close, which matters even when hover-to-open is off.
    if (state.settings.expandOnHover || state.expanded) {
      schedule(true, OPEN_DELAY);
    }
  });

  island.addEventListener('pointerleave', async () => {
    pointerInside = false;
    // The page also reports a leave when the window is resized under a pointer that has not
    // moved, so the pointer's real position decides.
    if (!await pointerOverIsland()) {
      schedule(false, CLOSE_DELAY);
    }
  });

  island.addEventListener('click', () => {
    clearTimeout(hoverTimer);
    hoverTimer = 0;
    open();
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
      setExpanded(false);
    }
  });

  // The window lost focus (the user clicked another app): that is how a focused terminal is left.
  window.addEventListener('blur', async () => {
    if (state.expanded && !pinned() && !shouldStayOpen() && !await pointerOverIsland()) {
      setExpanded(false);
    }
  });

  on('activities', render);
  on('settings', render);
  render();
  if (pinned()) {
    open();
  }
}
