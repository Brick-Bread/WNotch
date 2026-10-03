// The media card on the Home tab: artwork, title and artist, a seekable timeline and the
// previous / play-pause / next buttons. Port of NotchWindow.Media.cs.

import { state, on } from './store.js';
import { invoke, listen } from './backend.js';
import { h } from './dom.js';
import { setAudioLevel } from './glow.js';

/** The timeline only needs to tick while it is on screen and moving. */
const TICK_MS = 500;

const ICONS = {
  previous: '<path d="M6 5v14M19 5.5v13a.5.5 0 0 1-.8.4l-9-6.5a.5.5 0 0 1 0-.8l9-6.5a.5.5 0 0 1 .8.4z"/>',
  next: '<path d="M18 5v14M5 5.5v13a.5.5 0 0 0 .8.4l9-6.5a.5.5 0 0 0 0-.8l-9-6.5a.5.5 0 0 0-.8.4z"/>',
  play: '<path d="M7 4.8v14.4a.6.6 0 0 0 .9.5l11.6-7.2a.6.6 0 0 0 0-1L7.9 4.3a.6.6 0 0 0-.9.5z"/>',
  pause: '<path d="M8 5v14M16 5v14"/>',
  note: '<path d="M9 18V6l10-2v12"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="16.5" cy="16" r="2.5"/>',
};

function svg(name, className = 'media-icon') {
  const element = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  element.setAttribute('viewBox', '0 0 24 24');
  element.setAttribute('class', className);
  element.setAttribute('aria-hidden', 'true');
  element.innerHTML = ICONS[name];
  return element;
}

/** m:ss, or h:mm:ss from an hour on. */
export function formatTime(milliseconds) {
  const total = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = String(total % 60).padStart(2, '0');
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${seconds}` : `${minutes}:${seconds}`;
}

/** The live position: sources report it only now and then, so it is extrapolated. */
export function positionAt(media, now) {
  let position = media.positionMs;
  if (media.isPlaying && now > media.positionUpdatedAtMs) {
    position += (now - media.positionUpdatedAtMs) * media.playbackRate;
  }
  position = Math.max(0, position);
  return media.hasTimeline ? Math.min(position, media.durationMs) : position;
}

/** Builds the card and keeps it up to date; returns the element to put on the Home tab. */
export function initMedia() {
  /** @type {any} */
  let media = null;
  let timer = 0;

  const art = h('div', { class: 'media-art' });
  const title = h('div', { class: 'media-title' });
  const artist = h('div', { class: 'media-artist' });
  const elapsed = h('span', { class: 'media-time' });
  const remaining = h('span', { class: 'media-time media-remaining' });
  const fill = h('div', { class: 'media-fill' });
  const seek = h('div', { class: 'media-seek', role: 'slider', 'aria-label': 'Position' }, h('div', { class: 'media-track' }, fill));
  const timeline = h('div', { class: 'media-timeline' }, elapsed, seek, remaining);
  const previous = h('button', { class: 'media-button', 'aria-label': 'Previous', title: 'Previous', onclick: () => control('previous') }, svg('previous'));
  const playPause = h('button', { class: 'media-button media-play', onclick: () => control('playPause') });
  const next = h('button', { class: 'media-button', 'aria-label': 'Next', title: 'Next', onclick: () => control('next') }, svg('next'));

  const card = h('div', { class: 'card media-card', hidden: true },
    h('div', { class: 'media-art-frame' }, svg('note', 'media-note'), art),
    h('div', { class: 'media-body' },
      title, artist, timeline,
      h('div', { class: 'media-controls' }, previous, playPause, next)));

  const control = action => invoke('media_control', { action });

  seek.addEventListener('pointerdown', event => {
    const rect = seek.getBoundingClientRect();
    if (event.button !== 0 || !media?.canSeek || !media.hasTimeline || rect.width <= 0) {
      return;
    }
    const ratio = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
    const target = media.durationMs * ratio;
    invoke('media_seek', { seconds: target / 1000 });
    // Show the jump at once; the player's own update follows.
    media = { ...media, positionMs: target, positionUpdatedAtMs: Date.now() };
    renderTimeline();
    event.stopPropagation();
  });

  function renderTimeline() {
    if (!media?.hasTimeline) {
      return;
    }
    const position = positionAt(media, Date.now());
    fill.style.width = `${(100 * position) / media.durationMs}%`;
    elapsed.textContent = formatTime(position);
    remaining.textContent = `-${formatTime(media.durationMs - position)}`;
    seek.setAttribute('aria-valuenow', String(Math.round(position / 1000)));
    seek.setAttribute('aria-valuemax', String(Math.round(media.durationMs / 1000)));
  }

  function updateTimer() {
    const tick = state.expanded && state.tab === 'home' && !state.settingsOpen && media?.isPlaying && media.hasTimeline;
    if (tick && !timer) {
      renderTimeline();
      timer = setInterval(renderTimeline, TICK_MS);
    } else if (!tick && timer) {
      clearInterval(timer);
      timer = 0;
    }
  }

  function render() {
    card.hidden = !media;
    updateTimer();
    if (!media) {
      return;
    }
    const name = media.title.trim() ? media.title : 'Unknown title';
    title.textContent = name;
    title.title = name;
    artist.textContent = media.artist;
    artist.title = media.artist;
    art.style.backgroundImage = media.art ? `url("${media.art}")` : '';
    playPause.replaceChildren(svg(media.isPlaying ? 'pause' : 'play'));
    playPause.setAttribute('aria-label', media.isPlaying ? 'Pause' : 'Play');
    playPause.title = media.isPlaying ? 'Pause' : 'Play';
    playPause.disabled = !media.canTogglePlayPause;
    previous.disabled = !media.canGoPrevious;
    next.disabled = !media.canGoNext;
    timeline.style.visibility = media.hasTimeline ? 'visible' : 'hidden';
    seek.classList.toggle('seekable', media.canSeek);
    renderTimeline();
  }

  listen('media', payload => {
    media = payload;
    render();
  });
  listen('media-level', ({ level }) => setAudioLevel(level));
  on('ui', updateTimer);
  invoke('get_media').then(payload => {
    media = payload;
    render();
  });
  return card;
}
