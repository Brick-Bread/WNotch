// The Shelf commands for the mock backend: an in-memory shelf that behaves like the real one.

const SAMPLE = [
  'C:\\Users\\Brick\\Documents\\Report Q3.pdf',
  'C:\\Users\\Brick\\Pictures\\holiday.png',
  'C:\\Users\\Brick\\Projects',
  'D:\\work\\notes.txt',
  'C:\\Users\\Brick\\Downloads\\installer-with-a-very-long-name.exe',
];
const CAPACITY = 24;

const nameOf = path => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path;
const item = path => ({ path, name: nameOf(path), isFolder: !/\.[^.\\/]+$/.test(nameOf(path)) });

const PICTURE = (label, color) => `data:image/svg+xml;utf8,${encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" width="88" height="88"><rect width="88" height="88" rx="10" fill="${color}"/>`
  + `<text x="44" y="52" font-size="22" font-family="sans-serif" text-anchor="middle" fill="#fff">${label}</text></svg>`)}`;

/** @param {(name: string, payload: unknown) => void} emit */
export function createShelfMock(emit) {
  let paths = [...SAMPLE];
  const changed = () => emit('shelf', paths.map(item));

  return {
    shelf_items: () => paths.map(item),
    shelf_add: ({ paths: added, announce }) => {
      const tidy = [...new Set(added.map(path => path.trim()).filter(Boolean))];
      const same = (a, b) => a.toLowerCase() === b.toLowerCase();
      paths = [...tidy, ...paths.filter(path => !tidy.some(fresh => same(fresh, path)))].slice(0, CAPACITY);
      changed();
      if (announce) console.info(`mock: "On the shelf, ${tidy.length} item(s)" would show in the pill`);
    },
    shelf_remove: ({ path }) => {
      paths = paths.filter(held => held.toLowerCase() !== path.toLowerCase());
      changed();
    },
    shelf_clear: () => {
      paths = [];
      changed();
    },
    shelf_open: ({ path }) => console.info('mock: open', path),
    shelf_reveal: ({ path }) => console.info('mock: show in folder', path),
    shelf_thumbnail: ({ path }) => (/\.png$/i.test(path) ? PICTURE('PNG', '#3d8bff') : /\.pdf$/i.test(path) ? PICTURE('PDF', '#e5484d') : null),
    shelf_start_drag: ({ path }) => {
      console.info('mock: drag out', path);
      return new Promise(resolve => setTimeout(() => resolve('copy'), 1200));
    },
  };
}
