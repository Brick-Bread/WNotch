// One xterm.js terminal per session (port of the old Assets/terminal/terminal.js).
// xterm.js and its fit addon are loaded as classic scripts and expose `Terminal` and `FitAddon`.

// Backgrounds match the island colour in style.css. The light palette darkens the 16 ANSI
// colours, which are otherwise picked to read on black.
const THEMES = {
  dark: { background: '#000000', foreground: '#e6e6e6', cursor: '#e6e6e6' },
  light: {
    background: '#f3f3f5', foreground: '#1b1b1f', cursor: '#1b1b1f',
    selectionBackground: '#b6d3ff',
    black: '#1b1b1f', red: '#c4251c', green: '#1a7f37', yellow: '#8a6100',
    blue: '#0a5fd6', magenta: '#8250df', cyan: '#0f7f8f', white: '#6e7781',
    brightBlack: '#57606a', brightRed: '#d1242f', brightGreen: '#1f8f45', brightYellow: '#9a6700',
    brightBlue: '#218bff', brightMagenta: '#a475f9', brightCyan: '#1b9aaa', brightWhite: '#8c959f',
  },
};

export function decodeBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

export class TerminalView {
  /**
   * @param {HTMLElement} parent where the terminal's element is appended
   * @param {boolean} light
   * @param {{ onInput(data: string): void, onResize(cols: number, rows: number): void, onBell(): void }} callbacks
   */
  constructor(parent, light, callbacks) {
    this.callbacks = callbacks;
    this.sentSize = '';
    this.element = document.createElement('div');
    this.element.className = 'xterm-session';
    parent.appendChild(this.element);

    this.terminal = new Terminal({
      fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "DejaVu Sans Mono", monospace',
      fontSize: 13,
      cursorBlink: true,
      scrollback: 5000,
      theme: light ? THEMES.light : THEMES.dark,
    });
    this.fitAddon = new FitAddon.FitAddon();
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.open(this.element);

    this.terminal.onData(data => callbacks.onInput(data));
    this.terminal.onBell(() => callbacks.onBell());

    // Ctrl+C copies when there is a selection (otherwise it interrupts); Ctrl+V pastes.
    const terminal = this.terminal;
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
  }

  setLight(light) {
    this.terminal.options.theme = light ? THEMES.light : THEMES.dark;
  }

  /** Shows or hides the terminal; a shown one is refitted. */
  setActive(active) {
    this.element.classList.toggle('active', active);
    if (active) {
      this.fit();
    }
  }

  /** Fits to the container and tells the backend when the size changed. Needs the terminal to be displayed. */
  fit() {
    if (!this.element.offsetParent || this.element.clientWidth === 0 || this.element.clientHeight === 0) {
      return;
    }
    this.fitAddon.fit();
    const { cols, rows } = this.terminal;
    const size = `${cols}x${rows}`;
    if (size !== this.sentSize) {
      this.sentSize = size;
      this.callbacks.onResize(cols, rows);
    }
  }

  focus() {
    this.terminal.focus();
  }

  /** Whether the keyboard is in this terminal. */
  hasFocus() {
    return this.element.contains(document.activeElement);
  }

  /** @param {string} base64 raw bytes from the process */
  write(base64) {
    this.terminal.write(decodeBase64(base64));
  }

  /** Writes dim text on its own line. */
  writeNotice(text) {
    this.terminal.write(`\r\n\x1b[2m${text}\x1b[0m\r\n`);
  }

  dispose() {
    this.terminal.dispose();
    this.element.remove();
  }
}
