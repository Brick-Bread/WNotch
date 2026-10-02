// A fake backend for developing the UI in a plain browser (`?mock`, `?mock=idle`, `?mock=progress`).
// Terminals are an echo shell; the agent buttons also walk through working -> needsInput -> done.

const DEFAULT_PRESETS = ['Shell = shell', 'Claude = claude', 'Codex = codex'];
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

/** @param {string | null} variant `idle` (no activity), `progress` (a progress bar), otherwise a Claude activity */
export function createMock(variant) {
  const handlers = new Map();
  const emit = (name, payload) => handlers.get(name)?.forEach(callback => callback({ payload }));

  let settings = {
    displayIndex: null,
    style: 'notch',
    position: 'topCenter',
    expandOnHover: true,
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

  const activities = {
    idle: [],
    progress: [{
      id: 'download', tier: 'ongoing', title: 'Downloading', glyph: '⬇', progress: 0.62,
      glow: { color: { r: 0x2e, g: 0xc4, b: 0xff }, pattern: 'steady', strength: 1 },
    }],
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
  };

  return {
    invoke: async (command, args) => commands[command]?.(args) ?? null,
    listen: async (name, callback) => {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(callback);
    },
  };
}
