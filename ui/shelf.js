// The Shelf tab: files dragged onto the notch, kept as paths, to drag out again.
//
// Backend: `shelf_items`, `shelf_add`, `shelf_remove`, `shelf_clear`, `shelf_open`, `shelf_reveal`,
// `shelf_thumbnail`, `shelf_start_drag`, and the `shelf` event (the whole list, newest first).
// Files dropped on the window arrive through Tauri's drag-drop event with their paths.

import { state, on, emit } from './store.js';
import { invoke, listen, isMock } from './backend.js';
import { h, $ } from './dom.js';
import { open, close, keepOpenWhile } from './island.js';

/** The picture is drawn at 44px; ask for twice that so it stays sharp on a scaled display. */
const PICTURE_SIZE = 88;
/** How far the pointer must move with the button down before a tile becomes a drag. */
const DRAG_DISTANCE = 5;
/** Matches the notch's own delay before it closes after the pointer leaves. */
const CLOSE_DELAY = 450;

const FILE_ICON = '<path d="M4 1.5h5.5L13 5v9a.5.5 0 0 1-.5.5h-8.5a.5.5 0 0 1-.5-.5v-12a.5.5 0 0 1 .5-.5z"/><path d="M9.5 1.5V5H13"/>';
const FOLDER_ICON = '<path d="M1.5 4.5a1 1 0 0 1 1-1h3.2l1.3 1.5h6.5a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1z"/>';

/** @type {{ path: string, name: string, isFolder: boolean }[]} */
let items = [];
/** Pictures already fetched, by path; null for a file the system has no picture of. @type {Map<string, string | null>} */
const pictures = new Map();
const pending = [];
let loading = false;

/** Set while a tile is being dragged out; the pointer is then elsewhere and the notch must wait for it. */
let dragOut = false;
let menuOpen = false;
/** Whether a file drag opened the notch, which then closes again if the files are taken away. */
let openedByDrag = false;
let closeTimer = 0;

const glyph = isFolder => {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = isFolder ? FOLDER_ICON : FILE_ICON;
  return svg;
};

export function initShelf(pane) {
  const tabButton = $('[data-tab="shelf"]');
  const summary = h('div', { class: 'shelf-summary' });
  const clear = h('button', {
    class: 'btn',
    title: 'Take everything off the shelf. The files themselves stay where they are.',
    onclick: () => invoke('shelf_clear'),
  }, 'Clear');
  const grid = h('div', { class: 'shelf-grid', role: 'list' });
  const empty = h('div', { class: 'shelf-empty' }, 'Drag files onto the notch to keep them here');
  const menu = h('div', { class: 'shelf-menu', role: 'menu', hidden: true });
  pane.append(h('div', { class: 'shelf-head' }, summary, clear), h('div', { class: 'shelf-body' }, grid, empty), menu);

  keepOpenWhile(() => dragOut || menuOpen);

  // Tiles --------------------------------------------------------------------

  function render() {
    const count = items.length;
    tabButton.textContent = count > 0 ? `Shelf ${count}` : 'Shelf';
    summary.textContent = count === 0 ? '' : count === 1 ? '1 item · drag it out to use it' : `${count} items · drag one out to use it`;
    empty.hidden = count > 0;
    clear.hidden = count === 0;
    grid.replaceChildren(...items.map(tile));
    for (const item of items) {
      if (!pictures.has(item.path)) {
        queuePicture(item.path);
      }
    }
  }

  function tile(item) {
    const picture = h('span', { class: 'tile-picture' }, glyph(item.isFolder));
    const remove = h('button', {
      class: 'tile-remove',
      title: 'Take off the shelf',
      'aria-label': `Take ${item.name} off the shelf`,
      onpointerdown: event => event.stopPropagation(),
      onclick: event => {
        event.stopPropagation();
        invoke('shelf_remove', { path: item.path });
      },
    }, '×');
    const element = h('div', {
      class: 'tile',
      role: 'listitem',
      tabindex: '0',
      title: item.path,
      'data-path': item.path,
      ondblclick: () => invoke('shelf_open', { path: item.path }),
      onkeydown: event => {
        if (event.key === 'Enter') {
          invoke('shelf_open', { path: item.path });
        } else if (event.key === 'Delete') {
          invoke('shelf_remove', { path: item.path });
        }
      },
      oncontextmenu: event => {
        event.preventDefault();
        showMenu(item, event);
      },
      onpointerdown: event => pressed(item, element, event),
    }, picture, h('span', { class: 'tile-name' }, item.name), remove);
    if (pictures.get(item.path)) {
      showPicture(element, pictures.get(item.path));
    }
    return element;
  }

  const tileOf = path => [...grid.children].find(element => element.dataset.path === path);

  function showPicture(element, source) {
    const slot = element.querySelector('.tile-picture');
    slot.replaceChildren(h('img', { src: source, alt: '', draggable: 'false' }));
  }

  // One picture at a time and after the tiles are drawn: a thumbnail the system has not cached yet takes a moment.
  function queuePicture(path) {
    if (!pending.includes(path)) {
      pending.push(path);
    }
    if (!loading) {
      loading = true;
      setTimeout(loadPictures, 0);
    }
  }

  async function loadPictures() {
    while (pending.length > 0) {
      const path = pending.shift();
      if (pictures.has(path) || !items.some(item => item.path === path)) {
        continue;
      }
      let source = null;
      try {
        source = (await invoke('shelf_thumbnail', { path, size: PICTURE_SIZE })) ?? null;
      } catch {
        // The tile keeps its glyph.
      }
      pictures.set(path, source);
      const element = tileOf(path);
      if (source && element) {
        showPicture(element, source);
      }
    }
    loading = false;
  }

  function setItems(next) {
    items = next;
    for (const path of [...pictures.keys()]) {
      if (!items.some(item => item.path === path)) {
        pictures.delete(path);
      }
    }
    render();
  }

  // Dragging a tile out -------------------------------------------------------

  /** @type {{ item: any, element: HTMLElement, x: number, y: number, id: number } | null} */
  let press = null;

  function pressed(item, element, event) {
    if (event.button !== 0 || event.target.closest('.tile-remove')) {
      return;
    }
    press = { item, element, x: event.clientX, y: event.clientY, id: event.pointerId };
  }

  document.addEventListener('pointermove', event => {
    if (!press) {
      return;
    }
    if (event.buttons !== 1) {
      press = null;
      return;
    }
    if (Math.hypot(event.clientX - press.x, event.clientY - press.y) >= DRAG_DISTANCE) {
      const { item, element } = press;
      press = null;
      startDragOut(item, element);
    }
  });
  document.addEventListener('pointerup', () => { press = null; });

  async function startDragOut(item, element) {
    hideMenu();
    dragOut = true;
    element.classList.add('dragging');
    try {
      // Returns when the file has been dropped somewhere or the drag was called off.
      await invoke('shelf_start_drag', { path: item.path });
    } catch (error) {
      console.warn('could not drag the file out', error);
    } finally {
      dragOut = false;
      element.classList.remove('dragging');
    }
    if (state.expanded && !$('#island').matches(':hover')) {
      clearTimeout(closeTimer);
      closeTimer = setTimeout(() => {
        if (!$('#island').matches(':hover')) {
          close();
        }
      }, CLOSE_DELAY);
    }
  }

  // Context menu --------------------------------------------------------------

  function showMenu(item, event) {
    const entry = (label, action) => h('button', {
      class: 'menu-item',
      role: 'menuitem',
      onclick: () => {
        hideMenu();
        action();
      },
    }, label);
    menu.replaceChildren(
      entry('Open', () => invoke('shelf_open', { path: item.path })),
      entry('Show in folder', () => invoke('shelf_reveal', { path: item.path })),
      entry('Copy path', () => copyText(item.path)),
      h('hr'),
      entry('Take off the shelf', () => invoke('shelf_remove', { path: item.path })));
    menu.hidden = false;
    menuOpen = true;
    const bounds = pane.getBoundingClientRect();
    const x = Math.min(event.clientX - bounds.left, bounds.width - menu.offsetWidth - 4);
    const y = Math.min(event.clientY - bounds.top, bounds.height - menu.offsetHeight - 4);
    menu.style.left = `${Math.max(4, x)}px`;
    menu.style.top = `${Math.max(4, y)}px`;
    menu.firstElementChild.focus();
  }

  function hideMenu() {
    if (!menuOpen) {
      return;
    }
    menu.hidden = true;
    menuOpen = false;
    if (state.expanded && !$('#island').matches(':hover')) {
      clearTimeout(closeTimer);
      closeTimer = setTimeout(() => {
        if (!$('#island').matches(':hover') && !menuOpen) {
          close();
        }
      }, CLOSE_DELAY);
    }
  }

  document.addEventListener('pointerdown', event => {
    if (menuOpen && !menu.contains(event.target)) {
      hideMenu();
    }
  }, true);
  document.addEventListener('keydown', event => {
    if (menuOpen && event.key === 'Escape') {
      event.stopPropagation();
      hideMenu();
    }
  }, true);

  // Files dropped on the notch ------------------------------------------------

  function hover(active) {
    pane.classList.toggle('drop-target', active);
  }

  /** Files are over the window: open the notch on this tab, as the setting asks. */
  async function dragEntered() {
    clearTimeout(closeTimer);
    hover(true);
    if (!state.settings.shelfOpensOnDrag) {
      return;
    }
    state.tab = 'shelf';
    state.settingsOpen = false;
    emit('ui');
    if (!state.expanded) {
      openedByDrag = true;
      await open();
    }
  }

  function dragLeft() {
    hover(false);
    if (openedByDrag) {
      openedByDrag = false;
      clearTimeout(closeTimer);
      closeTimer = setTimeout(() => {
        if (!$('#island').matches(':hover')) {
          close();
        }
      }, CLOSE_DELAY);
    }
  }

  function dropped(paths) {
    hover(false);
    openedByDrag = false;
    if (paths.length > 0) {
      // A drop on the closed pill is confirmed in the pill.
      invoke('shelf_add', { paths, announce: !state.expanded });
    }
  }

  const webview = window.__TAURI__?.webview?.getCurrentWebview?.();
  if (webview) {
    webview.onDragDropEvent(({ payload }) => {
      // Our own tile coming back over the window is not a drop.
      if (dragOut) {
        return;
      }
      if (payload.type === 'enter') {
        dragEntered();
      } else if (payload.type === 'drop') {
        dropped(payload.paths ?? []);
      } else if (payload.type === 'leave') {
        dragLeft();
      }
    });
  } else if (isMock) {
    // A browser hides the paths of dropped files; the mock shelves their names instead.
    const island = $('#island');
    island.addEventListener('dragenter', event => { event.preventDefault(); dragEntered(); });
    island.addEventListener('dragover', event => event.preventDefault());
    island.addEventListener('dragleave', event => { if (!island.contains(event.relatedTarget)) dragLeft(); });
    island.addEventListener('drop', event => {
      event.preventDefault();
      dropped([...event.dataTransfer.files].map(file => `C:\\Users\\Brick\\Dropped\\${file.name}`));
    });
  }

  // Backend ------------------------------------------------------------------

  listen('shelf', setItems);
  // Opening the notch is when files that have gone are noticed.
  let wasExpanded = false;
  on('ui', () => {
    if (state.expanded && !wasExpanded) {
      invoke('shelf_items').then(setItems);
    }
    wasExpanded = state.expanded;
  });
  invoke('shelf_items').then(setItems);
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const area = h('textarea', { style: 'position:fixed;opacity:0' });
    area.value = text;
    document.body.append(area);
    area.select();
    document.execCommand('copy');
    area.remove();
  }
}
