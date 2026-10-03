// The Terminal tab: folder picker, launcher buttons, session tabs and the xterm views.

import { state, on, emit } from './store.js';
import { invoke, listen } from './backend.js';
import { h, icon } from './dom.js';
import { TerminalView } from './terminal.js';
import { isLight } from './theme.js';

const STATE_LABELS = { working: 'Working', needsInput: 'Needs input', done: 'Done' };

/** @type {Map<string, TerminalView>} */
const views = new Map();
let currentFolder = '';
let noticeTimer = 0;

const folderName = path => path.split(/[\\/]/).filter(Boolean).pop() ?? path;
const viewing = () => state.expanded && !state.settingsOpen && state.tab === 'terminal';

/** Whether the keyboard is in a terminal, so the island must stay open. */
export function terminalHasFocus() {
  return viewing() && [...views.values()].some(view => view.hasFocus());
}

/** Switches to the Terminal tab and shows a session. Set up by initTerminalTab. */
export let showSession = () => {};

export async function initTerminalTab(pane) {
  currentFolder = state.folder;

  const folderLabel = h('span', { class: 'folder-label' });
  const folderMenu = h('div', { class: 'menu', hidden: true });
  const folderButton = h('button', { class: 'btn folder', title: 'Folder new terminals start in' },
    icon('folder'), folderLabel, h('span', { class: 'caret' }, '▾'));
  const launchers = h('div', { class: 'launchers' });
  const notice = h('div', { class: 'notice', hidden: true });
  const sessionTabs = h('div', { class: 'session-tabs', hidden: true });
  const host = h('div', { class: 'term-host' });
  const empty = h('div', { class: 'empty' }, 'No terminals open.', h('br'), 'Pick a folder, then start a shell or an agent above.');
  host.append(empty);

  pane.append(
    h('div', { class: 'term-bar' }, h('div', { class: 'folder-wrap' }, folderButton, folderMenu), launchers),
    notice, sessionTabs, host);

  function showNotice(text) {
    notice.textContent = text;
    notice.hidden = false;
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(() => { notice.hidden = true; }, 5000);
  }

  // Folder -----------------------------------------------------------------

  const setFolder = path => {
    currentFolder = path;
    folderLabel.textContent = folderName(path) || 'Folder';
    folderButton.title = path;
  };

  function renderFolderMenu() {
    const recent = state.settings.recentFolders.filter(path => path !== currentFolder);
    folderMenu.replaceChildren(
      h('button', { class: 'menu-item', onclick: browse }, 'Browse…'),
      recent.length > 0 && h('div', { class: 'menu-sep' }),
      recent.map(path => h('button', {
        class: 'menu-item', title: path,
        onclick: () => { setFolder(path); folderMenu.hidden = true; },
      }, folderName(path), h('span', { class: 'menu-path' }, path))));
  }

  async function browse() {
    folderMenu.hidden = true;
    const picked = await invoke('pick_folder');
    if (picked) {
      setFolder(picked);
    }
  }

  folderButton.addEventListener('click', () => {
    renderFolderMenu();
    folderMenu.hidden = !folderMenu.hidden;
  });
  document.addEventListener('pointerdown', event => {
    if (!folderMenu.hidden && !folderMenu.parentElement.contains(event.target)) {
      folderMenu.hidden = true;
    }
  });

  // Launchers --------------------------------------------------------------

  function renderLaunchers() {
    launchers.replaceChildren(...state.profiles.map(profile => h('button', {
      class: 'btn launcher',
      title: [profile.command, ...profile.arguments].join(' '),
      onclick: () => invoke('open_session', { profileId: profile.id, folder: currentFolder })
        .catch(error => showNotice(String(error))),
    }, `+ ${profile.displayName}`)));
  }

  // Sessions ---------------------------------------------------------------

  function renderSessionTabs() {
    sessionTabs.hidden = state.sessions.length === 0;
    empty.hidden = state.sessions.length > 0;
    sessionTabs.replaceChildren(...state.sessions.map(session => {
      const label = STATE_LABELS[session.state];
      return h('div', {
        class: `session-tab${session.id === state.activeSessionId ? ' active' : ''}`,
        title: session.folder,
        onclick: () => activate(session.id),
      },
      h('span', { class: `dot ${session.exited ? 'exited' : session.state}`, title: label ?? false }),
      h('span', { class: 'session-name' }, session.displayName),
      h('span', { class: 'session-folder' }, session.folderName),
      h('button', {
        class: 'close', 'aria-label': 'Close terminal',
        onclick: event => { event.stopPropagation(); close(session.id); },
      }, '×'));
    }));
  }

  function activate(id) {
    state.activeSessionId = id;
    for (const [viewId, view] of views) {
      view.setActive(viewId === id);
    }
    if (viewing()) {
      views.get(id)?.focus();
      markViewed();
    }
    renderSessionTabs();
    emit('sessions');
  }

  showSession = id => {
    state.tab = 'terminal';
    emit('ui');
    activate(id);
  };

  /** The user is looking at the active session: clears its Done badge. */
  function markViewed() {
    const session = state.sessions.find(item => item.id === state.activeSessionId);
    if (!session) {
      return;
    }
    invoke('session_viewed', { id: session.id });
    if (session.state === 'done') {
      session.state = 'idle';
      renderSessionTabs();
      emit('sessions');
    }
  }

  function close(id) {
    invoke('close_session', { id });
    views.get(id)?.dispose();
    views.delete(id);
    const index = state.sessions.findIndex(session => session.id === id);
    state.sessions.splice(index, 1);
    if (state.activeSessionId === id) {
      activate(state.sessions[Math.min(index, state.sessions.length - 1)]?.id ?? null);
    } else {
      renderSessionTabs();
      emit('sessions');
    }
  }

  await listen('session-opened', info => {
    const id = info.id;
    state.sessions.push({ ...info, state: 'idle', exited: false });
    views.set(id, new TerminalView(host, isLight(), {
      onInput: data => invoke('session_input', { id, data }),
      onResize: (cols, rows) => invoke('session_resize', { id, cols, rows }),
      onBell: () => invoke('session_bell', { id }),
    }));
    activate(id);
  });

  await listen('session-output', ({ id, data }) => views.get(id)?.write(data));

  await listen('session-state', ({ id, state: agentState }) => {
    const session = state.sessions.find(item => item.id === id);
    if (!session) {
      return;
    }
    session.state = agentState;
    renderSessionTabs();
    emit('sessions');
    if (id === state.activeSessionId && viewing()) {
      markViewed();
    }
  });

  await listen('session-exited', ({ id, code }) => {
    const session = state.sessions.find(item => item.id === id);
    if (session) {
      session.exited = true;
      renderSessionTabs();
    }
    views.get(id)?.writeNotice(`Process exited with code ${code}.`);
  });

  // Plumbing ---------------------------------------------------------------

  let fitFrame = 0;
  const scheduleFit = () => {
    cancelAnimationFrame(fitFrame);
    fitFrame = requestAnimationFrame(() => views.get(state.activeSessionId)?.fit());
  };
  new ResizeObserver(scheduleFit).observe(host);

  let wasViewing = false;
  on('ui', () => {
    const nowViewing = viewing();
    if (nowViewing !== wasViewing) {
      wasViewing = nowViewing;
      invoke('set_viewing_terminal', { viewing: nowViewing });
      if (nowViewing) {
        // The pane only becomes visible once every 'ui' listener has run.
        requestAnimationFrame(() => {
          views.get(state.activeSessionId)?.fit();
          views.get(state.activeSessionId)?.focus();
          markViewed();
        });
      }
    }
  });

  on('settings', renderLaunchers);
  on('theme', () => {
    for (const view of views.values()) {
      view.setLight(isLight());
    }
  });

  setFolder(currentFolder);
  renderLaunchers();
  renderSessionTabs();
}
