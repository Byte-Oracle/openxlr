// Optional real-browser acceptance test. Run inside a device-isolated container
// with tools/test-monitor-volume.py --command node browser/tests/chromium-integration.mjs.
// Chrome for Testing 151+ and Playwright are required; this is not a *.test.mjs suite.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { access, lstat, mkdir, mkdtemp, readFile, readdir, realpath, rm, stat, writeFile } from 'node:fs/promises';
import { constants } from 'node:fs';
import { createServer } from 'node:http';
import { createServer as createNetServer } from 'node:net';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const endpoint = 'http://127.0.0.1:37890/api/v1/';
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const exists = path => access(path).then(() => true, () => false);
const quote = value => `'${value.replaceAll("'", "'\\''")}'`;

async function until(operation, description, timeout = 15000) {
  let last;
  const deadline = performance.now() + timeout;
  while (performance.now() < deadline) {
    try { const result = await operation(); if (result) return result; }
    catch (error) { last = error; }
    await delay(100);
  }
  throw new Error(`Timed out: ${description}`, { cause: last });
}

async function unusedPort() {
  const probe = createNetServer();
  try {
    await new Promise((resolve, reject) => {
      probe.once('error', reject);
      probe.listen(37890, '127.0.0.1', resolve);
    });
  } catch (error) {
    throw new Error('127.0.0.1:37890 must be unused. Use a private container or network namespace.', { cause: error });
  } finally {
    if (probe.listening) await new Promise(resolve => probe.close(resolve));
  }
}

async function privateEnvironment() {
  assert.equal(process.env.OPENXLR_TEST_MONITOR_VOLUME, '1', 'Use the private audio test runner.');
  assert.ok(process.env.XDG_RUNTIME_DIR, 'A private XDG_RUNTIME_DIR is required.');
  const runtime = await realpath(process.env.XDG_RUNTIME_DIR);
  assert.ok(basename(runtime).startsWith('openxlr-monitor-test-'), 'Use a runtime created by tools/test-monitor-volume.py.');
  const directory = await stat(runtime);
  assert.equal(directory.uid, process.getuid(), 'The private runtime must belong to this user.');
  assert.equal(directory.mode & 0o077, 0, 'The private runtime must not be accessible by other users.');
  assert.equal(await realpath(process.env.PIPEWIRE_RUNTIME_DIR ?? ''), runtime, 'PipeWire must use the private runtime.');
  assert.equal(process.env.PULSE_SERVER, `unix:${runtime}/pulse/native`, 'PulseAudio must use the private socket.');
  assert.equal(process.env.PIPEWIRE_REMOTE, 'pipewire-0', 'Use the private PipeWire server.');
  for (const socket of ['pipewire-0', 'pulse/native']) {
    const path = join(runtime, socket);
    assert.ok((await lstat(path)).isSocket(), `${socket} must be a private socket, not a link.`);
    assert.equal(await realpath(path), path, 'The private audio sockets must not traverse links.');
  }
  assert.ok(!await exists('/dev/snd') && !await exists('/dev/bus/usb')
    && !(await readdir('/dev')).some(name => name.startsWith('hidraw')),
  'Hardware devices must not be exposed. Use a container without sound, USB or hidraw device mounts.');
  await unusedPort();
  return runtime;
}

const audioRuntime = await privateEnvironment();
assert.ok(process.env.OPENXLR_TEST_CHROMIUM, 'Set OPENXLR_TEST_CHROMIUM to Chrome for Testing 151+ executable.');
await access(process.env.OPENXLR_TEST_CHROMIUM, constants.X_OK);
const { chromium } = await import(process.env.OPENXLR_PLAYWRIGHT_MODULE || 'playwright');
const daemonDll = join(root, 'src/OpenXLR.Daemon/bin/Release/net10.0/OpenXLR.Daemon.dll');
const uiDll = join(root, 'src/OpenXLR.UI/bin/Release/net10.0/OpenXLR.UI.dll');
await Promise.all([access(daemonDll), access(uiDll)]);

const directory = await mkdtemp(join(audioRuntime, 'chromium-integration-'));
const home = join(directory, 'home');
const runtime = join(directory, 'runtime');
const config = join(directory, 'config');
const profile = join(directory, 'profile');
// Browser, native host and daemon receive the same private paths. No installed
// browser manifest, user configuration, audio socket or profile is changed.
const environment = {
  PATH: process.env.PATH,
  HOME: home,
  LANG: process.env.LANG || 'C.UTF-8',
  XDG_RUNTIME_DIR: runtime,
  XDG_CONFIG_HOME: config,
  XDG_STATE_HOME: join(directory, 'state'),
  XDG_DATA_HOME: join(directory, 'data'),
  XDG_CACHE_HOME: join(directory, 'cache'),
  PIPEWIRE_RUNTIME_DIR: audioRuntime,
  PIPEWIRE_REMOTE: 'pipewire-0',
  PULSE_SERVER: `unix:${audioRuntime}/pulse/native`,
  OPENXLR_TEST_MONITOR_VOLUME: '1',
  OPENXLR_BUILD_MIXER: '1',
};
for (const key of ['DISPLAY', 'XAUTHORITY', 'DBUS_SESSION_BUS_ADDRESS', 'DOTNET_ROOT', 'LD_LIBRARY_PATH', 'LV2_PATH'])
  if (process.env[key]) environment[key] = process.env[key];

let daemon;
let context;
let token;
let daemonLog = '';
const servers = [];
const readers = new Set();

async function api(method, path, body) {
  const response = await fetch(endpoint + path, {
    method,
    signal: AbortSignal.timeout(5000),
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const value = await response.json();
  assert.ok(response.ok, `Daemon request failed: ${method} ${path}: ${JSON.stringify(value)}`);
  return value;
}

async function startDaemon() {
  await unusedPort();
  daemon = spawn('dotnet', [daemonDll], { cwd: root, env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  const output = chunk => { daemonLog = (daemonLog + chunk).slice(-65536); };
  daemon.stdout.on('data', output);
  daemon.stderr.on('data', output);
  daemon.on('error', error => { daemonLog += error.message; });
  return until(async () => {
    assert.equal(daemon.exitCode, null, 'The private daemon exited before startup.');
    token = (await readFile(join(runtime, 'openxlr/token'), 'utf8')).trim();
    const state = await api('GET', 'state');
    return state.mixer && state;
  }, 'private daemon and mixer startup', 30000);
}

async function stopDaemon() {
  const process = daemon;
  daemon = null;
  await stopOwned(process, 'SIGINT', 10000);
}

async function stopOwned(process, signal = 'SIGTERM', timeout = 5000) {
  if (!process || process.pid === undefined || process.exitCode !== null || process.signalCode !== null) return;
  const stopped = new Promise(resolve => { process.once('exit', resolve); process.once('error', resolve); });
  let timer;
  process.kill(signal);
  try {
    const done = await Promise.race([stopped.then(() => true), new Promise(resolve => { timer = setTimeout(() => resolve(false), timeout); })]);
    if (!done) { process.kill('SIGKILL'); await stopped; }
  } finally { clearTimeout(timer); }
}

async function sample(sink) {
  const reader = spawn('parec', [`--device=${sink}.monitor`, '--format=float32le', '--rate=48000', '--channels=2', '--latency-msec=50'],
    { env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  readers.add(reader);
  const chunks = [];
  let size = 0;
  let failure;
  reader.on('error', error => { failure = error; });
  reader.stdout.on('data', chunk => { if (size < 2 * 1024 * 1024) { chunks.push(chunk); size += chunk.length; } });
  reader.stderr.resume();
  try { await delay(700); }
  finally {
    await stopOwned(reader);
    readers.delete(reader);
  }
  if (failure) throw failure;
  const data = Buffer.concat(chunks);
  assert.ok(data.length > 4800, 'The private monitor must return audio samples.');
  const amplitude = frequency => {
    let sine = 0, cosine = 0, count = 0;
    for (let offset = 0; offset + 8 <= data.length; offset += 8) {
      const value = data.readFloatLE(offset);
      sine += value * Math.sin(2 * Math.PI * frequency * count / 48000);
      cosine += value * Math.cos(2 * Math.PI * frequency * count / 48000);
      count++;
    }
    return 2 * Math.hypot(sine, cosine) / count;
  };
  return { low: amplitude(440), high: amplitude(880) };
}

async function fixture(title, frequency) {
  const server = createServer((_request, response) => {
    response.setHeader('Content-Type', 'text/html');
    response.end(`<title>${title}</title><button id="play">Play</button><script>
      document.querySelector('button').onclick = () => {
        const audio = new AudioContext();
        const oscillator = audio.createOscillator();
        oscillator.frequency.value = ${frequency};
        const gain = audio.createGain(); gain.gain.value = .1;
        oscillator.connect(gain).connect(audio.destination); oscillator.start();
        window.audio = audio;
      };
    </script>`);
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  servers.push(server);
  const site = `http://127.0.0.1:${server.address().port}`;
  const page = await context.newPage();
  await page.goto(site);
  await page.click('#play');
  return { site, page };
}

try {
  for (const path of [home, runtime, config, profile]) await mkdir(path, { mode: 0o700 });
  await startDaemon();
  const executable = join(directory, 'native-host');
  await writeFile(executable, `#!/bin/sh\nexec dotnet ${quote(uiDll)} "$@"\n`, { mode: 0o700 });
  const native = JSON.parse(await readFile(join(root, 'browser/native-host.json'), 'utf8'));
  native.path = executable;
  for (const location of [join(profile, 'NativeMessagingHosts'), ...['google-chrome-for-testing', 'google-chrome', 'chromium']
    .flatMap(browser => [join(config, browser, 'NativeMessagingHosts'), join(home, '.config', browser, 'NativeMessagingHosts')])]) {
    await mkdir(location, { recursive: true, mode: 0o700 });
    await writeFile(join(location, `${native.name}.json`), JSON.stringify(native), { mode: 0o600 });
  }
  context = await chromium.launchPersistentContext(profile, {
    executablePath: process.env.OPENXLR_TEST_CHROMIUM,
    headless: !process.env.DISPLAY,
    env: environment,
    ignoreDefaultArgs: ['--mute-audio'],
    args: [
      ...(process.getuid() === 0 ? ['--no-sandbox'] : []),
      '--disable-dev-shm-usage', '--autoplay-policy=no-user-gesture-required', '--enable-unsafe-extension-debugging',
      `--disable-extensions-except=${join(root, 'browser/chromium')}`, `--load-extension=${join(root, 'browser/chromium')}`,
    ],
  });
  const major = Number(context.browser().version().split('.')[0]);
  assert.ok(major >= 151, 'Extensions.triggerAction requires Chrome for Testing 151+.');
  const worker = context.serviceWorkers()[0] ?? await context.waitForEvent('serviceworker');
  const extensionId = new URL(worker.url()).host;
  assert.ok(native.allowed_origins.includes(`chrome-extension://${extensionId}/`), 'The test extension must match its native manifest.');
  await context.grantPermissions(['microphone']);
  const cdp = await context.browser().newBrowserCDPSession();
  let nextId = 1;
  async function targetCall(targetId, method, params) {
    const { sessionId } = await cdp.send('Target.attachToTarget', { targetId, flatten: false });
    let handler;
    let timer;
    try {
      const id = nextId++;
      const response = new Promise((resolve, reject) => {
        handler = event => {
          if (event.sessionId !== sessionId) return;
          const message = JSON.parse(event.message);
          if (message.id !== id) return;
          if (message.error) reject(new Error(message.error.message));
          else resolve(message.result);
        };
        cdp.on('Target.receivedMessageFromTarget', handler);
        timer = setTimeout(() => reject(new Error('Extension action target timed out.')), 5000);
      });
      await cdp.send('Target.sendMessageToTarget', { sessionId, message: JSON.stringify({ id, method, params }) });
      return await response;
    } finally {
      clearTimeout(timer);
      cdp.off('Target.receivedMessageFromTarget', handler);
      await cdp.send('Target.detachFromTarget', { sessionId }).catch(() => {});
    }
  }
  const evaluate = async (targetId, expression) => {
    const result = await targetCall(targetId, 'Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, userGesture: true });
    assert.ok(!result.exceptionDetails, 'The extension action must evaluate successfully.');
    return result.result?.value;
  };

  const control = await context.newPage();
  await control.goto(`chrome-extension://${extensionId}/popup.html`);
  await until(() => control.evaluate(() => chrome.runtime.sendMessage({ target: 'worker', type: 'snapshot' }))
    .then(reply => reply.value?.state), 'native host connection');
  await control.close();
  const musicTab = await fixture('YouTube test', 440);
  const chatTab = await fixture('Teams test', 880);
  const tabs = await until(async () => {
    const tabs = (await api('GET', 'state')).mixer.browserTabs;
    return tabs.length === 2 && tabs;
  }, 'two individually registered browser tabs');
  const musicId = tabs.find(tab => tab.site === musicTab.site).id;
  const chatId = tabs.find(tab => tab.site === chatTab.site).id;
  const session = tabs[0].session;
  await api('POST', 'commands', { cmd: 'setPreserveBrowserOutputs', value: true });

  const devices = await context.newPage();
  await devices.goto(`chrome-extension://${extensionId}/devices.html`);
  await devices.click('#allow');
  await until(async () => (await devices.locator('#status').textContent()).startsWith('Found '), 'OpenXLR output access');
  await devices.close();
  for (const { page, site } of [musicTab, chatTab]) {
    await page.bringToFront();
    const target = (await cdp.send('Target.getTargets', { filter: [{}] })).targetInfos.find(target => target.type === 'tab' && target.url === page.url());
    assert.ok(target, 'The active fixture must expose a browser tab target.');
    await cdp.send('Extensions.triggerAction', { id: extensionId, targetId: target.targetId });
    const popup = await until(async () => (await cdp.send('Target.getTargets')).targetInfos
      .find(target => target.url === `chrome-extension://${extensionId}/popup.html`), 'extension action popup');
    await until(() => evaluate(popup.targetId, "document.getElementById('channel').disabled === false"), 'tab channel picker is available');
    const channel = site === musicTab.site ? 'music' : 'voicechat';
    await evaluate(popup.targetId, `
      document.getElementById('remember').checked = true;
      const picker = document.getElementById('channel');
      picker.value = ${JSON.stringify(channel)};
      picker.dispatchEvent(new Event('change', { bubbles: true }));
    `);
    await until(async () => (await api('GET', 'state')).mixer.browserTabs.some(tab => tab.site === site
      && tab.channelId === channel && tab.remember), 'popup assignment is remembered by the daemon');
    await until(async () => await evaluate(popup.targetId, "document.getElementById('start').disabled === false"), 'tab activation is available');
    await evaluate(popup.targetId, "document.getElementById('start').click()");
    await until(async () => (await api('GET', 'state')).mixer.browserTabs.find(tab => tab.site === site)?.capturing, 'user-activated tab capture');
    if (process.env.OPENXLR_TEST_BROWSER_SCREENSHOT && site === chatTab.site) {
      await until(() => evaluate(popup.targetId, "document.getElementById('status').textContent.includes('routed through OpenXLR')"), 'captured popup status');
      const screenshot = await targetCall(popup.targetId, 'Page.captureScreenshot', { format: 'png' });
      await writeFile(resolve(process.env.OPENXLR_TEST_BROWSER_SCREENSHOT), Buffer.from(screenshot.data, 'base64'), { flag: 'wx' });
    }
    await cdp.send('Target.closeTarget', { targetId: popup.targetId });
  }
  const music = await sample('OpenXLR_ch_music');
  const chat = await sample('OpenXLR_ch_voicechat');
  assert.ok(music.low > .025 && music.high < music.low / 8, 'Only the 440 Hz tab reaches Music.');
  assert.ok(chat.high > .025 && chat.low < chat.high / 8, 'Only the 880 Hz tab reaches Voice Chat.');
  console.log('PASS native bridge, two user-activated tabs and isolated 440/880 Hz channel audio');

  await api('POST', 'commands', { cmd: 'assignBrowserTab', session, tabId: musicId, site: musicTab.site, channel: 'game', remember: false });
  await delay(1000);
  const game = await sample('OpenXLR_ch_game');
  const oldMusic = await sample('OpenXLR_ch_music');
  assert.ok(game.low > .025 && game.high < game.low / 8, 'A live tab channel change reaches Game.');
  assert.ok(oldMusic.low < .003, 'The old Music route is silent.');
  await musicTab.page.goto(chatTab.site);
  await until(async () => {
    const tabs = (await api('GET', 'state')).mixer.browserTabs;
    const navigated = tabs.find(tab => tab.id === musicId);
    return navigated?.site === chatTab.site && !navigated.capturing && navigated.channelId === 'voicechat'
      && tabs.find(tab => tab.id === chatId)?.capturing;
  }, 'cross-origin navigation ends only its own capture and takes the new website preference');
  const stale = await fetch(endpoint + 'commands', {
    method: 'POST', signal: AbortSignal.timeout(5000),
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ cmd: 'assignBrowserTab', session, tabId: musicId, site: musicTab.site, channel: 'game', remember: true }),
  });
  assert.equal(stale.ok, false, 'An assignment from the previous page must be refused.');
  assert.equal((await api('GET', 'state')).mixer.browserTabs.find(tab => tab.id === musicId).channelId, 'voicechat');
  console.log('PASS live channel change, cross-origin navigation and independent capture lifetime');

  await stopDaemon();
  await until(() => worker.evaluate(async () => !await chrome.offscreen.hasDocument()), 'daemon disconnect closes the audio stages');
  await startDaemon();
  const restored = await until(async () => {
    const mixer = (await api('GET', 'state')).mixer;
    return mixer.preserveBrowserOutputs && mixer.browserTabs.length === 2
      && mixer.browserTabs.every(tab => tab.session !== session && tab.channelId === 'voicechat' && !tab.capturing && tab.remember) && mixer;
  }, 'daemon restart creates a new session, restores website choices and keeps captures stopped', 30000);
  assert.equal(new Set(restored.browserTabs.map(tab => tab.session)).size, 1);
  await musicTab.page.goto(musicTab.site);
  await until(async () => {
    const tab = (await api('GET', 'state')).mixer.browserTabs.find(tab => tab.id === musicId);
    return tab?.site === musicTab.site && tab.channelId === 'music' && tab.remember && !tab.capturing;
  }, 'the original website preference survives a tab-only route and daemon restart');
  console.log('PASS real daemon restart, fresh connection session, persisted origins and stopped captures');
} catch (error) {
  console.error(daemonLog);
  throw error;
} finally {
  await context?.close().catch(() => {});
  for (const reader of readers) await stopOwned(reader);
  for (const server of servers) await new Promise(resolve => server.close(resolve));
  await stopDaemon();
  await rm(directory, { recursive: true, force: true });
}
