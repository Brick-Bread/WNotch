// Picture taking for `notch --demo --screenshots=<folder>`: the backend sets a state up and
// asks for a picture; the page draws itself into a PNG (an SVG <foreignObject> holding a copy
// of the document, rasterised on a canvas) and hands it back. Transparent where the page is.
// Nothing happens unless the backend asks, so this is inert in normal runs.

import { invoke, listen } from './backend.js';

/** Room around the island so its glow is part of the picture. */
const MARGIN = 56;

const SVG_NAMESPACE = 'http://www.w3.org/2000/svg';
const XHTML_NAMESPACE = 'http://www.w3.org/1999/xhtml';

function pageCss() {
  return [...document.styleSheets].map(sheet => {
    try {
      return [...sheet.cssRules].map(rule => rule.cssText).join('\n');
    } catch {
      return '';
    }
  }).join('\n');
}

/** The document as a standalone XHTML string with its styles inlined and no scripts. */
function snapshotMarkup() {
  const root = document.documentElement.cloneNode(true);
  root.setAttribute('xmlns', XHTML_NAMESPACE);
  root.querySelectorAll('script, link, style').forEach(node => node.remove());
  const style = document.createElement('style');
  style.textContent = pageCss();
  root.querySelector('head').append(style);
  return new XMLSerializer().serializeToString(root);
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('the page could not be drawn'));
    image.src = url;
  });
}

/** The island and the room around it as a PNG, base64 encoded. */
export async function capturePicture() {
  const { innerWidth: width, innerHeight: height } = window;
  const rect = document.getElementById('island').getBoundingClientRect();
  const left = Math.max(0, Math.floor(rect.left - MARGIN));
  const top = Math.max(0, Math.floor(rect.top - MARGIN));
  const cropWidth = Math.min(width - left, Math.ceil(rect.width + MARGIN * 2));
  const cropHeight = Math.min(height - top, Math.ceil(rect.height + MARGIN * 2));

  const svg = `<svg xmlns="${SVG_NAMESPACE}" width="${width}" height="${height}">`
    + `<foreignObject width="100%" height="100%">${snapshotMarkup()}</foreignObject></svg>`;
  const image = await loadImage(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`);

  const scale = Math.max(1, window.devicePixelRatio || 1);
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(cropWidth * scale);
  canvas.height = Math.round(cropHeight * scale);
  const context = canvas.getContext('2d');
  context.scale(scale, scale);
  context.drawImage(image, -left, -top);
  return canvas.toDataURL('image/png').split(',')[1];
}

/** Listens for the backend's picture requests and view changes. */
export function initDemoCapture() {
  listen('demo-view', ({ tab }) => {
    document.querySelector(`.tab[data-tab="${tab}"]`)?.click();
  });

  listen('demo-capture', async ({ name }) => {
    try {
      await invoke('demo_save_picture', { name, data: await capturePicture() });
    } catch (error) {
      await invoke('demo_save_picture', { name, data: '', error: String(error) });
    }
  });
}
