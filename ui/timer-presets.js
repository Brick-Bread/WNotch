// Reads timer lengths and timer preset lines the way people type them: a port of
// DurationParser.cs and TimerPreset.cs, so the settings panel can refuse a line before saving.

const SHORTEST = 1;
const LONGEST = 24 * 3600;
const MAX_NAME_LENGTH = 10;
export const MAX_PRESETS = 6;

const UNIT = /(\d+(?:\.\d+)?)\s*(hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)/gi;
const UNITS_ONLY = /^(?:\s*\d+(?:\.\d+)?\s*(?:hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s))+\s*$/i;

function clockSeconds(text) {
  const parts = text.split(':');
  if (parts.length !== 2 && parts.length !== 3) {
    return null;
  }
  let seconds = 0;
  for (const part of parts) {
    if (!/^\d+$/.test(part)) {
      return null;
    }
    seconds = seconds * 60 + Number(part);
  }
  return seconds;
}

function unitSeconds(text) {
  if (!UNITS_ONLY.test(text)) {
    return null;
  }
  let seconds = 0;
  const factor = { h: 3600, m: 60, s: 1 };
  for (const [, number, unit] of text.matchAll(UNIT)) {
    seconds += Number(number) * factor[unit[0].toLowerCase()];
  }
  return seconds;
}

/**
 * A bare number is minutes ("12", "1.5"); colons read as a clock ("1:30" is a minute and a half);
 * otherwise numbers carry units ("90s", "45 min", "1h 20m"). Returns whole seconds, or null for
 * anything else and for lengths outside one second to 24 hours.
 */
export function parseDuration(input) {
  const text = (input ?? '').trim();
  if (!text) {
    return null;
  }
  let seconds;
  if (/^(?:\d+\.?\d*|\.\d+)$/.test(text)) {
    seconds = Number(text) * 60;
  } else if (text.includes(':')) {
    seconds = clockSeconds(text);
  } else {
    seconds = unitSeconds(text);
  }
  if (seconds === null || Number.isNaN(seconds) || seconds < SHORTEST || seconds > LONGEST) {
    return null;
  }
  return Math.round(seconds);
}

/**
 * Reads a line such as "15m", "Tea 3m" or "Long walk 1h 20m": a length, optionally with a name in
 * front of it. Returns `{ name, seconds }` or null.
 */
export function parseTimerPreset(input) {
  const line = (input ?? '').trim();
  const whole = parseDuration(line);
  if (whole !== null) {
    return { name: '', seconds: whole };
  }
  // The name ends at the first space after which the rest reads as a length.
  for (let space = line.indexOf(' '); space >= 0; space = line.indexOf(' ', space + 1)) {
    const seconds = parseDuration(line.slice(space));
    if (seconds !== null) {
      const name = line.slice(0, space).trim();
      return { name: name.length > MAX_NAME_LENGTH ? name.slice(0, MAX_NAME_LENGTH).trimEnd() : name, seconds };
    }
  }
  return null;
}

/** The shortest spelling parseDuration reads back: "5m", "1h30m", "1m30s", "45s". */
export function describeDuration(totalSeconds) {
  const total = Math.max(0, Math.round(totalSeconds));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor(total / 60) % 60;
  const seconds = total % 60;
  let text = '';
  if (hours > 0) text += `${hours}h`;
  if (minutes > 0) text += `${minutes}m`;
  if (seconds > 0 || text === '') text += `${seconds}s`;
  return text;
}

/** The preset as one line parseTimerPreset reads back: "Tea 3m" or "15m". */
export function timerPresetLine({ name, seconds }) {
  return name ? `${name} ${describeDuration(seconds)}` : describeDuration(seconds);
}

/**
 * Reads the timer presets box: at most six lines. Returns `{ lines }` (as typed, trimmed) or
 * `{ bad }` with the first line that cannot be read.
 */
export function readTimerPresets(text) {
  const lines = text.split(/[\r\n]+/).map(line => line.trim()).filter(Boolean).slice(0, MAX_PRESETS);
  const bad = lines.find(line => parseTimerPreset(line) === null);
  return bad === undefined ? { lines } : { bad };
}
