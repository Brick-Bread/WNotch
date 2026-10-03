// Port of Glow.cs: colours, pattern intensities and how a level is put on screen.

/** The accent and glow presets, in the order the colour picker shows them. */
export const GLOW_COLORS = {
  Blue: [0x3d, 0x8b, 0xff],
  Cyan: [0x2e, 0xc4, 0xff],
  Green: [0x3f, 0xd8, 0x6b],
  Yellow: [0xff, 0xd8, 0x4a],
  Amber: [0xff, 0xb0, 0x1f],
  Orange: [0xff, 0x7a, 0x2e],
  Red: [0xff, 0x3b, 0x3b],
  Violet: [0xa8, 0x6b, 0xff],
};
const WHITE = [0xf2, 0xf2, 0xf2];

/** Resolves a preset name (any case) or a `{ r, g, b }` object to `[r, g, b]`. */
export function rgbOf(color) {
  if (color && typeof color === 'object') {
    return [color.r, color.g, color.b];
  }
  const name = Object.keys(GLOW_COLORS).find(key => key.toLowerCase() === String(color).toLowerCase());
  return name ? GLOW_COLORS[name] : WHITE;
}

/** The colour deepened enough to read as text or a thin line on a light background. */
export function onLight([r, g, b]) {
  return [r, g, b].map(channel => Math.round(channel * 0.7));
}

const BREATHE_PERIOD = 3.2;
const PULSE_PERIOD = 1.1;
const FLASH_DECAY = 1.2;

// 0 at the start of each period, 1 half way through, smooth in between.
function wave(seconds, period) {
  const phase = Math.sin((Math.PI * (seconds % period)) / period);
  return phase * phase;
}

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

// The smoothed output loudness (0..1) the backend reports while something plays; null on a
// system without a meter, where the audio glow stays steady.
let audioLevel = null;

/** Records the latest output loudness for the `audio` glow pattern. */
export function setAudioLevel(level) {
  audioLevel = level;
}

/** Brightness (0..1) of `glow` this many seconds after it started; audio follows the output loudness. */
export function intensityAt(glow, seconds) {
  seconds = Math.max(0, seconds);
  let intensity;
  switch (glow.pattern) {
    case 'breathe': intensity = 0.45 + 0.55 * wave(seconds, BREATHE_PERIOD); break;
    case 'pulse': intensity = 0.25 + 0.75 * wave(seconds, PULSE_PERIOD); break;
    case 'flash': intensity = 0.35 + 0.65 * Math.max(0, 1 - seconds / FLASH_DECAY); break;
    case 'audio': intensity = audioLevel === null ? 0.8 : 0.2 + 0.8 * clamp(audioLevel, 0, 1); break;
    default: intensity = 0.8;
  }
  return clamp(intensity * (glow.strength ?? 1), 0, 1);
}

/** True when the brightness changes over time and needs a per-frame update. */
export const isAnimated = glow => glow.pattern !== 'steady';

/** The user's brightness percentage as a multiplier. */
export const gain = percent => clamp(percent, 25, 200) / 100;

/** How strongly to draw a glow of the given intensity; above 1 there is only size left to add. */
export const level = (intensity, gainValue) => Math.pow(clamp(intensity, 0, 1), 0.6) * Math.max(0, gainValue);
