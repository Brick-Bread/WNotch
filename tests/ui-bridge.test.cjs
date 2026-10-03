const assert = require('node:assert/strict');
const { test, before, after } = require('node:test');
const { createServer } = require('node:http');
const { readFile } = require('node:fs/promises');
const path = require('node:path');
const { chromium } = require('playwright');

let server;
let browser;
let baseUrl;
const ui = path.resolve(__dirname, '../ui');

before(async () => {
  server = createServer(async (request, response) => {
    const pathname = new URL(request.url, 'http://localhost').pathname;
    const file = path.resolve(ui, '.' + (pathname === '/' ? '/index.html' : pathname));
    if (!file.startsWith(ui + path.sep)) {
      response.writeHead(403).end();
      return;
    }
    try {
      const body = await readFile(file);
      const type = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' }[path.extname(file)];
      response.writeHead(200, { 'Content-Type': type || 'application/octet-stream' }).end(body);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  baseUrl = `http://127.0.0.1:${server.address().port}`;
  browser = await chromium.launch({ headless: true, channel: process.env.PLAYWRIGHT_CHANNEL });
});

after(async () => {
  await browser?.close();
  if (server) await new Promise(resolve => server.close(resolve));
});

async function openPage() {
  const page = await browser.newPage({ viewport: { width: 980, height: 500 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    const handlers = new Map();
    let mock;
    window.bridgeCalls = [];
    window.sendBackendEvent = (name, payload) => {
      for (const callback of handlers.get(name) || []) callback({ payload });
    };
    window.__TAURI__ = {
      core: {
        invoke: async (command, args) => {
          window.bridgeCalls.push({ command, args });
          mock ||= (await import('/mock.js')).createMock('idle');
          return mock.invoke(command, args);
        },
      },
      event: {
        listen: async (name, callback) => {
          // Tauri installs listeners asynchronously; output can arrive right after opening.
          await new Promise(resolve => setTimeout(resolve, 10));
          if (!handlers.has(name)) handlers.set(name, new Set());
          handlers.get(name).add(callback);
          mock ||= (await import('/mock.js')).createMock('idle');
          await mock.listen(name, callback);
          return () => handlers.get(name).delete(callback);
        },
      },
    };
  });
  await page.goto(baseUrl);
  await page.waitForFunction(() => document.querySelector('.launcher'));
  // initIsland is last; its awaited registration signals that all subscriptions are ready.
  await page.waitForFunction(() => {
    window.sendBackendEvent('expanded', { expanded: true });
    return document.querySelector('#island').dataset.state === 'expanded';
  });
  assert.deepEqual(errors, []);
  return { page, errors };
}

test('backend hotkey events expand and collapse the UI', async () => {
  const { page, errors } = await openPage();
  try {
    assert.equal(await page.locator('#island').getAttribute('data-state'), 'expanded');
    await page.evaluate(() => window.sendBackendEvent('expanded', { expanded: false }));
    await page.waitForFunction(() => document.querySelector('#island').dataset.state === 'collapsed');
    await page.waitForFunction(() => window.bridgeCalls.some(call => call.command === 'set_expanded' && !call.args.expanded));
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
});

test('switching idle sessions reports the active session to the backend', async () => {
  const { page, errors } = await openPage();
  try {
    await page.locator('[data-tab="terminal"]').click();
    await page.getByRole('button', { name: '+ Shell', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('.session-tab').length === 1);
    await page.getByRole('button', { name: '+ Shell', exact: true }).click();
    await page.waitForFunction(() => document.querySelectorAll('.session-tab').length === 2);
    await page.evaluate(() => { window.bridgeCalls.length = 0; });
    await page.locator('.session-tab').first().click();
    const active = await page.evaluate(async () => (await import('/store.js')).state.activeSessionId);
    await page.waitForFunction(id => window.bridgeCalls.some(call => call.command === 'session_viewed' && call.args.id === id), active);
    assert.equal(active, 's1');
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
});

test('tray settings opens the settings view and session output reaches xterm', async () => {
  const { page, errors } = await openPage();
  try {
    await page.evaluate(() => window.sendBackendEvent('open-settings', null));
    assert.equal(await page.locator('#expanded').getAttribute('data-view'), 'settings');
    await page.locator('[data-tab="terminal"]').click();
    await page.getByRole('button', { name: '+ Shell', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.xterm-screen')?.textContent.includes('Notch mock terminal'));
    assert.deepEqual(errors, []);
  } finally {
    await page.close();
  }
});
