// A fake backend for developing the UI in a plain browser (`?mock`, `?mock=idle`, `?mock=progress`).
// Terminals are an echo shell; the agent buttons also walk through working -> needsInput -> done.

import { createMockMedia } from './mock-media.js';
import { createShelfMock } from './mock-shelf.js';

const DEFAULT_PRESETS =['Shell = shell', 'Claude = claude', 'Codex = codex'];
const FOLDER = 'C:\\Users\\Brick\\notch';

const toBase64 = text => btoa(String.fromCharCode(...new TextEncoder().encode(text)));

function profilesFrom(lines) {
  return lines.filter(line => line.trim()).slice(0, 6).map(line => {
    const [name, ...rest] = line.split('=');
    const command = rest.join('=').trim();
    const agent = /claude/i.test(command) ? 'claude' : /codex/i.test(command) ? 'codex' : 'none';
    return {
      id: name.trim().toLowerCase().replace(/\W+/g, '-'),
      displayName: name.trim(),
      command,
      glyph: { claude: '✳', codex: '◈', none: '▸' }[agent],
      agent,
      installHint: '',
      arguments: [],
      environment: {},
    };
  });
}

/** The system HUDs as the backend builds them (crates/notch-core/src/hud.rs), selectable as `?mock=volume` etc. */
function hudVariants() {
  const glow = (r, g, b, pattern, strength) => ({ color: { r, g, b }, pattern, strength });
  const white = (strength) => glow(0xf2, 0xf2, 0xf2, 'flash', strength);
  const hud = (id, title, glyph, extra) => [{ id: `hud.${id}`, tier: 'transient', title, glyph, ...extra }];
  return {
    volume: hud('volume', 'Volume', '🔊', { progress: 0.64, glow: white(0.78) }),
    muted: hud('volume', 'Muted', '🔇', { progress: 0, glow: white(0.35) }),
    brightness: hud('brightness', 'Brightness', '☀', { progress: 0.4, glow: glow(0xff, 0xd8, 0x4a, 'flash', 0.64) }),
    charging: hud('power', 'Charging', '⚡', { detail: '82%', glow: glow(0x3f, 0xd8, 0x6b, 'flash', 1) }),
    battery: hud('power', 'On battery', '🔋', { detail: '81%', glow: white(0.6) }),
    lowbattery: hud('power', 'Low battery', '🪫', { detail: '9%', glow: glow(0xff, 0x3b, 0x3b, 'pulse', 1) }),
    bluetooth: hud('bluetooth', 'Headphones', 'ᛒ', { detail: 'Connected', glow: glow(0x3d, 0x8b, 0xff, 'flash', 1) }),
    capslock: hud('caps-lock', 'Caps Lock', '⇪', { detail: 'On', glow: white(0.8) }),
    output: hud('audio-output', 'Speakers', '🎧', { detail: 'Output', glow: white(0.7) }),
  };
}

/** @param {string | null} variant `idle` (no activity), `progress` (a progress bar), otherwise a Claude activity */
export function createMock(variant) {
  const handlers = new Map();
  const emit = (name, payload) => handlers.get(name)?.forEach(callback => callback({ payload }));

  let settings = {
    displayIndex: null,
    style: 'notch',
    position: 'topCenter',
    expandOnHover: true,
    shelfOpensOnDrag: true,
    openHotkey: 'Alt+Shift+N',
    hideInFullscreen: true,
    theme: 'dark',
    accentColor: 'Blue',
    glowEffects: true,
    glowIntensity: 100,
    autoUpdate: true,
    terminalPresets: DEFAULT_PRESETS,
    recentFolders: [FOLDER, 'C:\\Users\\Brick\\projects\\site', 'D:\\work\\api'],
  };

  const mockMedia = variant === 'media' ? createMockMedia(emit) : null;
  mockMedia?.start();

  const activities = {
    idle: [],
    ...(mockMedia && { media: mockMedia.activities() }),
    progress: [{
      id: 'download', tier: 'ongoing', title: 'Downloading', glyph: '⬇', progress: 0.62,
      glow: { color: { r: 0x2e, g: 0xc4, b: 0xff }, pattern: 'steady', strength: 1 },
    }],
    ...hudVariants(),
  }[variant] ?? [{
    id: 'agent.mock', tier: 'ongoing', title: 'Claude', detail: 'Working in notch', glyph: '✳',
    glow: { color: { r: 0x3d, g: 0x8b, b: 0xff }, pattern: 'breathe', strength: 1 },
  }];

  const sessions = new Map();
  let nextId = 1;

  const setAgentState = (id, agentState) => emit('session-state', { id, state: agentState });

  function output(id, text) {
    emit('session-output', { id, data: toBase64(text) });
  }

  function openSession({ profileId, folder }) {
    const profile = profilesFrom(settings.terminalPresets).find(p => p.id === profileId);
    const id = `s${nextId++}`;
    const prompt = `\x1b[32mPS ${folder}>\x1b[0m `;
    sessions.set(id, { prompt, agent: profile.agent });
    setTimeout(() => {
      emit('session-opened', {
        id, profileId, displayName: profile.displayName, glyph: profile.glyph, folder,
        folderName: folder.split('\\').pop(), agent: profile.agent,
      });
      output(id, `Notch mock terminal (${profile.command})\r\n${prompt}`);
    }, 40);
    return { id };
  }

  function sessionInput({ id, data }) {
    const session = sessions.get(id);
    if (!session) return;
    if (data === '\r') {
      output(id, `\r\n${session.prompt}`);
      if (session.agent !== 'none') {
        setAgentState(id, 'working');
        setTimeout(() => setAgentState(id, 'needsInput'), 2500);
        setTimeout(() => setAgentState(id, 'done'), 5000);
      }
    } else if (data === '\x7f') {
      output(id, '\b \b');
    } else {
      output(id, data);
    }
  }

  const commands = {
    ...createShelfMock(emit),
    get_state: () => ({ settings, profiles: profilesFrom(settings.terminalPresets), folder: FOLDER, activities, version: '0.9.0' }),
    save_settings: ({ settings: next }) => {
      settings = next;
      const result = { settings, profiles: profilesFrom(settings.terminalPresets) };
      emit('settings', result);
      return result;
    },
    open_session: openSession,
    session_input: sessionInput,
    close_session: ({ id }) => sessions.delete(id),
    pick_folder: () => 'C:\\Users\\Brick\\projects\\demo',
    ...mockMedia?.commands,
    update_status: () => ({ text: '', busy: false, version: '0.9.0' }),
    check_updates_now: () => new Promise(resolve => setTimeout(() => resolve('Notch 0.9.0 is the latest version.'), 800)),
  };

  return {
    invoke: async (command, args) => commands[command]?.(args) ?? null,
    listen: async (name, callback) => {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(callback);
    },
  };
}
