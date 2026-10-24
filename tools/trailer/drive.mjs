// Drives the HyperHarbor client through "make a profile, then create a VM from it" with the real mouse,
// records the client's content area with ffmpeg, and turns the recording into an animated WebP.
//
// The client must be running with WebView2's debug port; make-trailer.ps1 starts it that way and runs this.
// Usage: node drive.mjs [--profile "Dev Workstation"] [--vm dev-01] [--window 40,10,1720,1040] [--out <dir>]
//                       [--port 9333] [--dry]
//   --window  the client window's outer frame in physical pixels: x,y,width,height. Keep it clear of anything
//             on the desktop that should not be recorded.
//   --dry     stops before Save, cancels the editor, and does not record.
// demo.js is loaded for the run: the recording shows made-up hosts and VMs and nothing is created on the host.
import { spawn, execFileSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { connect } from './cdp.mjs';

const here = import.meta.dirname;
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const DRY = process.argv.includes('--dry');
const PROFILE = arg('profile', 'Dev Workstation');
const VM = arg('vm', 'dev-01');
const PORT = Number(arg('port', '9333'));
const WINDOW = arg('window', '40,10,1720,1040').split(',').map(Number);
const OUT = arg('out', join(here, 'out'));
if (WINDOW.length !== 4 || WINDOW.some((n) => !Number.isInteger(n))) throw new Error('--window takes x,y,width,height.');
mkdirSync(OUT, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- status bar (status.ps1), just below the client window and outside the recording ----
const statusFile = join(OUT, 'status.txt');
function status(state, text) {
  writeFileSync(statusFile, `${state}|${text}`);
  console.log(text);
}
status('ready', 'Trailer: getting ready. Do not touch the mouse.');
// Closes the bar however the script ends, including a failure before the scene starts.
process.on('exit', () => {
  try { writeFileSync(statusFile, 'exit|'); } catch { /* the bar times out on its own */ }
});
spawn('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', join(here, 'status.ps1'), '-File', statusFile,
  '-X', `${WINDOW[0]}`, '-Y', `${WINDOW[1] + WINDOW[3] + 8}`, '-Width', `${WINDOW[2]}`], { stdio: 'ignore' });

// ---- native mouse helper ----
const mouse = spawn('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', join(here, 'mouse.ps1')], {
  stdio: ['pipe', 'pipe', 'inherit'],
});
const replies = createInterface({ input: mouse.stdout });
const queue = [];
replies.on('line', (line) => queue.shift()?.(line));
const native = (cmd) => new Promise((ok, fail) => {
  queue.push((line) => (line.startsWith('error') ? fail(new Error(line)) : ok(line)));
  mouse.stdin.write(cmd + '\n');
});

// ---- page helpers ----
const c = await connect(PORT);
// The demo layer is loaded with a page reload and removed again by a reload at the end.
await c.send('Page.enable');
const demo = await c.send('Page.addScriptToEvaluateOnNewDocument', {
  source: readFileSync(join(here, 'demo.js'), 'utf8'),
});
await c.send('Page.reload', {});
await sleep(4000);
if (!(await c.evaluate('!!window.__demo'))) throw new Error('The demo layer did not load.');
await c.evaluate(`
window.__hh = {
  visible: (el) => !!el && el.getClientRects().length > 0,
  scope() {
    const dialogs = [...document.querySelectorAll('dialog[open], [role=dialog], [aria-modal=true]')].filter(this.visible);
    return dialogs.at(-1) ?? document;
  },
  btn(text, starts = false) {
    const all = [...this.scope().querySelectorAll('button, [role=tab]')].filter(this.visible);
    return all.find((b) => { const t = b.textContent.trim().replace(/\\s+/g, ' '); return starts ? t.startsWith(text) : t === text; });
  },
  field(label) {
    const l = [...this.scope().querySelectorAll('label')].filter(this.visible)
      .find((x) => x.textContent.trim().startsWith(label));
    return l && (l.control ?? l.querySelector('input, select, textarea'));
  },
  placeholder(p) { return [...this.scope().querySelectorAll('input')].filter(this.visible).find((i) => (i.placeholder ?? '').startsWith(p)); },
  checkbox(label) {
    const l = [...this.scope().querySelectorAll('label')].filter(this.visible).find((x) => x.textContent.trim().startsWith(label));
    return l && l.querySelector('input[type=checkbox]');
  },
  rowButton(rowText, text) {
    const rows = [...document.querySelectorAll('tr, li, [role=row]')].filter(this.visible).filter((r) => r.textContent.includes(rowText));
    for (const r of rows) {
      const b = [...r.querySelectorAll('button')].find((b) => b.textContent.trim() === text);
      if (b) return b;
    }
  },
};
1`);

let origin = { x: 0, y: 0 };
async function locate(expr) {
  const found = await c.evaluate(`(() => { const el = ${expr}; if (!el) return false;
    el.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'smooth' }); return true; })()`);
  if (!found) throw new Error(`Not found: ${expr}`);
  await sleep(400);
  const r = await c.evaluate(`(() => { const r = (${expr}).getBoundingClientRect();
    return { x: r.x + r.width / 2, y: r.y + r.height / 2, dpr: devicePixelRatio }; })()`);
  return { x: Math.round(origin.x + r.x * r.dpr), y: Math.round(origin.y + r.y * r.dpr) };
}
async function moveTo(expr, ms = 650) {
  const p = await locate(expr);
  await native(`move ${p.x} ${p.y} ${ms}`);
  return p;
}
async function click(expr, { ms = 650, after = 500 } = {}) {
  await moveTo(expr, ms);
  await sleep(120);
  await native('click');
  await sleep(after);
}
async function type(text, perChar = 55) {
  for (const ch of text) {
    await c.send('Input.insertText', { text: ch });
    await sleep(perChar + Math.random() * 40);
  }
}
async function waitFor(expr, timeoutMs, what) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    if (await c.evaluate(`!!(${expr})`)) return;
    await sleep(250);
  }
  throw new Error(`Timed out waiting for ${what ?? expr}`);
}

// ---- recording ----
let recStart = 0;
let ffmpeg = null;
const now = () => (Date.now() - recStart) / 1000;

// FFMPEG, then ffmpeg on PATH, then the winget package (winget install Gyan.FFmpeg).
function ffmpegPath() {
  if (process.env.FFMPEG) return process.env.FFMPEG;
  try {
    return execFileSync('where.exe', ['ffmpeg'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).split(/\r?\n/)[0];
  } catch { /* not on PATH */ }
  const root = join(process.env.LOCALAPPDATA, 'Microsoft', 'WinGet', 'Packages');
  const pkg = existsSync(root) && readdirSync(root).find((d) => d.startsWith('Gyan.FFmpeg'));
  const build = pkg && readdirSync(join(root, pkg)).find((d) => d.startsWith('ffmpeg'));
  if (!build) throw new Error('ffmpeg not found: install it (winget install Gyan.FFmpeg) or set FFMPEG.');
  return join(root, pkg, build, 'bin', 'ffmpeg.exe');
}
async function startRecording() {
  const size = await c.evaluate('[innerWidth * devicePixelRatio, innerHeight * devicePixelRatio]');
  const w = Math.floor(size[0] / 2) * 2;
  const h = Math.floor(size[1] / 2) * 2;
  ffmpeg = spawn(ffmpegPath(), ['-hide_banner', '-loglevel', 'warning', '-y', '-f', 'gdigrab', '-framerate', '30',
    '-draw_mouse', '1', '-offset_x', `${origin.x}`, '-offset_y', `${origin.y}`, '-video_size', `${w}x${h}`, '-i', 'desktop',
    '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '0', '-pix_fmt', 'yuv444p', join(OUT, 'raw.mp4')],
    { stdio: ['pipe', 'inherit', 'inherit'] });
  recStart = Date.now() + 400; // gdigrab's first frame arrives a little after the spawn
  await sleep(1200);
}
async function stopRecording() {
  ffmpeg.stdin.write('q');
  await new Promise((ok) => ffmpeg.on('exit', ok));
}

// ---- the scene ----
try {
  await native(`place ${WINDOW.join(' ')}`);
  await native('front');
  await sleep(600);
  origin = Object.fromEntries((await native('origin')).split(' ').map((v, i) => [i ? 'y' : 'x', Number(v)]));

  await c.evaluate("(() => { __hh.btn('Virtual machines').click(); return 1; })()");
  await sleep(800);
  const center = await c.evaluate('[innerWidth / 2, innerHeight / 2, devicePixelRatio]');
  await native(`move ${Math.round(origin.x + center[0] * center[2])} ${Math.round(origin.y + center[1] * center[2])} 200`);

  if (!DRY) await startRecording();
  status(DRY ? 'ready' : 'rec', DRY ? 'Dry run (not recording): making the profile' : 'Making the profile');
  await sleep(1200);

  // Profiles tab, New profile.
  await click("__hh.btn('Profiles')", { after: 900 });
  await click("__hh.btn('New profile')", { after: 1200 });

  // Name and description.
  await click("__hh.field('Name')", { after: 200 });
  await type(PROFILE);
  await click("__hh.field('Description')", { after: 200 });
  await type('Windows 11 for coding, ready to go');
  await sleep(500);

  // Account: unattended install.
  await click("__hh.checkbox('Install Windows unattended')", { after: 1600 });

  // Compute: 4 processors, 128 GB disk.
  await click("__hh.btn('Compute', true)", { after: 1300 });
  await click("__hh.field('Virtual processors')", { after: 200 });
  await type('4');
  await click("__hh.field('Disk size')", { after: 200 });
  await type('128');
  await sleep(900);

  // Software: two catalog picks and one search.
  await click("__hh.btn('Install', true)", { after: 1300 });
  await click("__hh.rowButton('Google.Chrome', 'Add')", { after: 700 });
  await click("__hh.rowButton('Git.Git', 'Add')", { after: 700 });
  await click("__hh.placeholder('Search the catalog')", { after: 200 });
  await type('vscode', 90);
  await waitFor("__hh.rowButton('Microsoft.VisualStudioCode', 'Add')", 15_000, 'the search results');
  await sleep(700);
  await click("__hh.rowButton('Microsoft.VisualStudioCode', 'Add')", { after: 1500 });

  if (DRY) {
    await click("__hh.btn('Cancel')", { after: 500 });
    status('done', 'Dry run finished before Save.');
  } else {
    // Save, then the profile appears in the list.
    await click("__hh.btn('Save')", { after: 0 });
    await waitFor(`__hh.rowButton(${JSON.stringify(PROFILE)}, 'Create VM…')`, 30_000, 'the new profile row');
    await sleep(1400);

    // Create a VM from it.
    status('rec', 'Creating a VM from the profile');
    await click(`__hh.rowButton(${JSON.stringify(PROFILE)}, 'Create VM…')`, { after: 1200 });
    await click("__hh.field('Name')", { after: 200 });
    await type(VM);
    await sleep(700);
    await click("__hh.btn('Create')", { after: 0 });

    // The job, fast-forwarded in the trailer.
    const jobStart = now() + 1.5;
    await waitFor("__hh.btn('Close')", 5 * 60_000, 'the create job');
    const jobEnd = now();
    status('rec', 'Showing the new VM (recording stops in a few seconds)');
    await sleep(2200);
    await click("__hh.btn('Close')", { after: 900 });
    await click("__hh.btn('Virtual machines')", { after: 1000 });
    await moveTo(`[...document.querySelectorAll('tr')].find((r) => r.textContent.includes(${JSON.stringify(VM)}))`, 700).catch(() => {});
    await sleep(3500);
    await stopRecording();
    status('work', 'Recording stopped. Encoding the WebP, about a minute. The mouse is yours.');

    // Keep everything; play the job in about 4 seconds.
    const speed = Math.max(1, (jobEnd - jobStart) / 4);
    const cuts = [`0-${jobStart.toFixed(2)}`, `${jobStart.toFixed(2)}-${jobEnd.toFixed(2)}@${speed.toFixed(2)}`,
      `${jobEnd.toFixed(2)}-${now().toFixed(2)}`];
    writeFileSync(join(OUT, 'cuts.json'), JSON.stringify({ cuts }, null, 2));
    console.log('Cuts:', cuts.join(' '));
    execFileSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', join(here, 'make-gif.ps1'),
      '-In', join(OUT, 'raw.mp4'), '-Out', join(OUT, 'hyperharbor-trailer.webp'),
      '-Cuts', cuts.join(','), '-Width', '1200', '-Fps', '15', '-Speed', '1.5'], { stdio: 'inherit' });
    const mb = (statSync(join(OUT, 'hyperharbor-trailer.webp')).size / 1048576).toFixed(1);
    status('done', `Done: ${join(OUT, 'hyperharbor-trailer.webp')} (${mb} MB)`);
  }
} catch (e) {
  status('error', `Stopped: ${e.message}`);
  if (ffmpeg) await stopRecording();
  process.exitCode = 1;
} finally {
  // Back to the real view.
  await c.send('Page.removeScriptToEvaluateOnNewDocument', { identifier: demo.identifier }).catch(() => {});
  await c.send('Page.reload', {}).catch(() => {});
  mouse.stdin.end();
  mouse.kill();
  c.close();
  await sleep(8000); // leave the last status readable, then close the bar
  writeFileSync(statusFile, 'exit|');
}
