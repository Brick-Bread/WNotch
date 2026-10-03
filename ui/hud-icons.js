// Icons for the system HUDs. The backend sends each HUD's glyph as a plain Unicode string
// (see `GLYPH_*` in crates/notch-core/src/hud.rs); this maps those to inline SVG that takes its
// colour from the text, so it reads in the dark and the light theme alike. Other glyphs
// (agents, timers, plugins) stay text.

const SVG_NS = 'http://www.w3.org/2000/svg';

/** Speaker body shared by the volume icons. */
const SPEAKER = '<path d="M2 6h2.6L8 3v10L4.6 10H2z" fill="currentColor" stroke="none"/>';

/** Inner markup of each icon in a 16x16 box; strokes use `currentColor`. */
const ICONS = {
  // Volume: muted, low, medium, high
  '🔇': `${SPEAKER}<path d="M10.5 6l3.5 4M14 6l-3.5 4"/>`,
  '🔈': SPEAKER,
  '🔉': `${SPEAKER}<path d="M10.5 6.2a2.6 2.6 0 0 1 0 3.6"/>`,
  '🔊': `${SPEAKER}<path d="M10.5 6.2a2.6 2.6 0 0 1 0 3.6M12.3 4.4a5.2 5.2 0 0 1 0 7.2"/>`,
  // Brightness
  '☀': '<circle cx="8" cy="8" r="2.6" fill="currentColor" stroke="none"/><path d="M8 1.8v1.7M8 12.5v1.7M1.8 8h1.7M12.5 8h1.7M3.6 3.6l1.2 1.2M11.2 11.2l1.2 1.2M12.4 3.6l-1.2 1.2M4.8 11.2l-1.2 1.2"/>',
  // Power: charging, on battery, low battery
  '⚡': '<path d="M9 1.5L3.8 9h3.6l-.9 5.5L12.2 7H8.6z" fill="currentColor" stroke="none"/>',
  '🔋': '<rect x="1.8" y="4.5" width="10.4" height="7" rx="1.6"/><path d="M14 7v2"/><rect x="3.6" y="6.3" width="5" height="3.4" rx=".5" fill="currentColor" stroke="none"/>',
  '🪫': '<rect x="1.8" y="4.5" width="10.4" height="7" rx="1.6"/><path d="M14 7v2"/><rect x="3.6" y="6.3" width="1.6" height="3.4" rx=".4" fill="currentColor" stroke="none"/>',
  // Bluetooth
  'ᛒ': '<path d="M4.4 5l7 6-3.4 3V2l3.4 3-7 6"/>',
  // Caps Lock
  '⇪': '<path d="M8 2.2L2.6 8h3v3h4.8V8h3z"/><path d="M6 13.8h4"/>',
  // Audio output
  '🎧': '<path d="M3 10V8a5 5 0 0 1 10 0v2"/><rect x="2.2" y="9.5" width="2.6" height="4" rx="1" fill="currentColor"/><rect x="11.2" y="9.5" width="2.6" height="4" rx="1" fill="currentColor"/>',
};

/**
 * The node for an activity's glyph: an SVG icon for a known HUD glyph, otherwise the text itself.
 * The span keeps the `activity-glyph` class so both look the same in the pill.
 */
export function glyphNode(glyph) {
  const span = document.createElement('span');
  span.className = 'activity-glyph';
  const markup = glyph && ICONS[glyph];
  if (!markup) {
    span.textContent = glyph ?? '';
    return span;
  }
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('width', '14');
  svg.setAttribute('height', '14');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.4');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.style.verticalAlign = 'middle';
  svg.innerHTML = markup;
  span.append(svg);
  return span;
}
