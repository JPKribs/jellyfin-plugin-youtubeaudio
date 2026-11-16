// Captures the docs screenshots at 2x: the three tabs at an 860 px window, and the two user pages' status cards.
// Run python3 -I server.py 8857 in the background, then node shoot.mjs [file name ...] to take all or only the named ones.
// Every PNG goes to docs/screenshots. PLAYWRIGHT_CORE points at playwright-core's index.mjs and CHROMIUM at the Chromium binary.
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
const CHROMIUM = process.env.CHROMIUM || join(homedir(), 'Library/Caches/ms-playwright/chromium-1148/chrome-mac/Chromium.app/Contents/MacOS/Chromium');
if (!existsSync(CHROMIUM)) throw new Error(`No Chromium at ${CHROMIUM}. Set CHROMIUM to a Chromium binary.`);
let chromium;
try {
  ({ chromium } = await import(process.env.PLAYWRIGHT_CORE || 'playwright-core'));
} catch (e) {
  throw new Error(`Could not load playwright-core (${e.message}). Set PLAYWRIGHT_CORE to playwright-core's index.mjs.`);
}
const OUT = fileURLToPath(new URL('../../docs/screenshots/', import.meta.url));
const ORIGIN = 'http://127.0.0.1:8857';
const PAD = 24;
const wait = (p, ms) => p.waitForTimeout(ms);

const shots = [
  {
    file: 'Download', page: 'youtubeaudio_download', width: 860, source: 'spotify',
    prep: async () => {}
  },
  {
    file: 'Import', page: 'youtubeaudio_import', width: 860,
    prep: async () => {
      // Selects the first two files, so Import and Delete act on them.
      const boxes = [...document.querySelectorAll('.yta-import-row .yta-import-head input[type=checkbox]')];
      boxes.slice(0, 2).forEach((b) => { b.click(); });
    }
  },
  {
    file: 'Settings', page: 'youtubeaudio_settings', width: 860,
    prep: async () => {
      document.querySelectorAll('.base-collapsible-header.collapsed').forEach((h) => h.click());
      await new Promise((r) => setTimeout(r, 400));
    }
  },
  {
    file: 'Queue', url: '/YouTube/Queue', width: 640, card: true,
    prep: async () => {}
  },
  {
    file: 'User-Download', url: '/YouTube/Download', width: 960, card: true,
    prep: async () => {
      const set = (id, v) => { const i = document.getElementById(id); i.value = v; i.dispatchEvent(new Event('input', { bubbles: true })); };
      set('url', 'https://www.youtube.com/watch?v=aB3dE5fG7hJ');
      set('artist', 'Johann Sebastian Bach');
      set('album', 'Cello Suite No. 1');
      set('year', '1720');
      set('title', 'Prelude');
    }
  }
];

const only = process.argv.slice(2);
const b = await chromium.launch({ executablePath: CHROMIUM });
for (const s of shots) {
  if (only.length && !only.includes(s.file)) continue;
  const ctx = await b.newContext({ viewport: { width: s.width, height: 900 }, deviceScaleFactor: 2 });
  // A sample signed in client entry for the local harness, so the user pages take their signed in path.
  await ctx.addInitScript((source) => {
    localStorage.setItem('jellyfin_credentials', JSON.stringify({ Servers: [{ Id: 's1', AccessToken: 'sample-token', ManualAddress: location.origin }] }));
    if (source) localStorage.setItem('yta-download-source', source);
  }, s.source || null);
  const p = await ctx.newPage();
  const errs = [];
  p.on('pageerror', (e) => errs.push(e.message));
  p.on('console', (m) => { if (m.type() === 'error' && !/404/.test(m.text())) errs.push(m.text()); });
  await p.goto(ORIGIN + (s.url || '/harness.html?page=' + s.page));
  if (!s.url) await p.waitForFunction(() => window.__ready);
  await wait(p, 1500);
  await p.evaluate(`(${s.prep.toString()})()`);
  await p.evaluate(() => document.activeElement && document.activeElement.blur());
  await wait(p, 600);
  let clip;
  if (s.card) {
    const r = await p.evaluate(() => { const c = document.querySelector('.base-status-card').getBoundingClientRect(); return { x: c.left, y: c.top + scrollY, w: c.width, h: c.height }; });
    clip = { x: Math.round(r.x), y: Math.round(r.y), width: Math.round(r.w), height: Math.round(r.h) };
  } else {
    const bottom = await p.evaluate(() => {
      let max = 0;
      document.querySelectorAll('#host *').forEach((e) => { const r = e.getBoundingClientRect(); if (r.height && r.width) max = Math.max(max, r.bottom + scrollY); });
      return max;
    });
    clip = { x: 0, y: 0, width: s.width, height: Math.round(bottom + PAD) };
  }
  await p.screenshot({ path: `${OUT}${s.file}.png`, clip, fullPage: true });
  console.log(s.file, clip.width * 2 + 'x' + clip.height * 2, JSON.stringify(errs), JSON.stringify(await p.evaluate(() => window.__errors || [])));
  await ctx.close();
}
await b.close();
