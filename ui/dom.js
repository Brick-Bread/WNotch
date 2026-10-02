/**
 * Creates an element. `class` sets the class name, `onclick`-style keys add listeners,
 * `true` makes a boolean attribute, `false`/null skips the attribute. Children may be nested arrays.
 */
export function h(tag, attrs = {}, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (key === 'class') {
      element.className = value;
    } else if (key.startsWith('on')) {
      element.addEventListener(key.slice(2), value);
    } else if (value === true) {
      element.setAttribute(key, '');
    } else if (value !== false && value != null) {
      element.setAttribute(key, value);
    }
  }
  element.append(...children.flat().filter(child => child != null && child !== false));
  return element;
}

export const $ = selector => document.querySelector(selector);

const ICONS = {
  folder: '<path d="M1.5 4.5a1 1 0 0 1 1-1h3.2l1.3 1.5h6.5a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1h-11a1 1 0 0 1-1-1z"/>',
};

/** A 14px stroke icon that takes its colour from the text. */
export function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = ICONS[name];
  return svg;
}
