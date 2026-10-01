// Bridge between the WPF host and xterm.js. One page holds every terminal session; only
// the active one is displayed. Messages in both directions are JSON objects with a `type`.

const sessions = new Map();
let activeId = null;

const post = message => window.chrome.webview.postMessage(message);

function decodeBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

function fitSession(session) {
  // fit() measures the element, so it only works while the session is displayed.
  if (session.element.classList.contains('active')) {
    session.fit.fit();
  }
}

function createSession(id) {
  const element = document.createElement('div');
  element.className = 'session';
  document.body.appendChild(element);

  const terminal = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
    fontSize: 13,
    cursorBlink: true,
    scrollback: 5000,
    theme: { background: '#000000', foreground: '#e6e6e6', cursor: '#e6e6e6' },
  });
  const fit = new FitAddon.FitAddon();
  terminal.loadAddon(fit);
  terminal.open(element);

  terminal.onData(data => post({ type: 'input', id, data }));
  terminal.onResize(({ cols, rows }) => post({ type: 'resize', id, cols, rows }));
  terminal.onBell(() => post({ type: 'bell', id }));
  terminal.onTitleChange(title => post({ type: 'title', id, title }));

  // Ctrl+C copies when there is a selection (otherwise it interrupts); Ctrl+V pastes.
  terminal.attachCustomKeyEventHandler(event => {
    if (event.type !== 'keydown' || !event.ctrlKey || event.altKey) {
      return true;
    }
    if (event.key === 'c' && terminal.hasSelection()) {
      navigator.clipboard.writeText(terminal.getSelection());
      terminal.clearSelection();
      return false;
    }
    if (event.key === 'v') {
      navigator.clipboard.readText().then(text => terminal.paste(text));
      return false;
    }
    return true;
  });

  const session = { terminal, fit, element };
  sessions.set(id, session);
  return session;
}

function activateSession(id) {
  activeId = id;
  for (const [sessionId, session] of sessions) {
    session.element.classList.toggle('active', sessionId === id);
  }
  const session = sessions.get(id);
  if (session) {
    fitSession(session);
    session.terminal.focus();
  }
}

window.chrome.webview.addEventListener('message', event => {
  const message = event.data;
  const session = sessions.get(message.id);

  switch (message.type) {
    case 'create': {
      const created = createSession(message.id);
      activateSession(message.id);
      // The host starts the process once it knows how big the terminal is.
      post({ type: 'created', id: message.id, cols: created.terminal.cols, rows: created.terminal.rows });
      break;
    }
    case 'activate':
      activateSession(message.id);
      break;
    case 'output':
      session?.terminal.write(decodeBase64(message.data));
      break;
    case 'close':
      if (session) {
        session.terminal.dispose();
        session.element.remove();
        sessions.delete(message.id);
      }
      break;
    case 'focus':
      sessions.get(activeId)?.terminal.focus();
      break;
  }
});

new ResizeObserver(() => {
  const session = sessions.get(activeId);
  if (session) {
    fitSession(session);
  }
}).observe(document.body);

post({ type: 'loaded' });
