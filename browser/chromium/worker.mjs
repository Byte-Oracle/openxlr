import { HOST, MAX_TABS, deviceFor, errorText, siteOf, tabRecords } from './routing.mjs';

const ASSIGNMENT_PENDING = "Waiting for OpenXLR to confirm this tab's channel assignment.";
const ASSIGNMENT_TIMEOUT = 'OpenXLR did not confirm the tab assignment in time. Choose the channel again.';

let native = null;
let state = null;
let bridgeError = null;
let creating = null;
let refreshing = null;
let refreshAgain = false;
let lastTabs = '';
let refreshTimer;
let bindingOperation = Promise.resolve();
let recovering = true;
let stopping = null;
const captures = new Map();
const sites = new Map();
const operations = new Map();
const starts = new Map();
const stops = new Map();
const assignments = new Map();

function clearAssignment(id) {
  const pending = assignments.get(id);
  if (pending) clearTimeout(pending.timer);
  assignments.delete(id);
}

function clearAssignments() {
  for (const id of assignments.keys()) clearAssignment(id);
}

function connect() {
  if (recovering || native) return;
  try {
    const port = chrome.runtime.connectNative(HOST);
    native = port;
    bridgeError = null;
    lastTabs = '';
    port.onMessage.addListener(message => {
      if (native !== port) return;
      if (message?.type === 'error') { clearAssignments(); bridgeError = errorText(message.error); return; }
      if (message?.type !== 'state' || !Array.isArray(message.channels) || !Array.isArray(message.tabs)) return;
      state = message;
      for (const [id, pending] of assignments) {
        const tab = state.tabs.find(candidate => candidate.id === id);
        if (tab?.site === pending.site && tab.channelId === pending.channel && (tab.remember === true) === pending.remember)
          clearAssignment(id);
      }
      bridgeError = null;
      void reconcile().catch(error => { bridgeError = errorText(error); });
    });
    port.onDisconnect.addListener(() => {
      if (native !== port) return;
      // Read lastError while its callback is active, but expose no native paths.
      const disconnected = chrome.runtime.lastError;
      bridgeError = disconnected ? 'OpenXLR browser integration is unavailable. Check the native host setup.' : 'OpenXLR browser integration disconnected.';
      native = null;
      state = null;
      clearAssignments();
      void stopAll(bridgeError);
    });
    void refreshTabs();
  } catch {
    native = null;
    clearAssignments();
    bridgeError = 'OpenXLR browser integration is unavailable. Check the native host setup.';
  }
}

function post(message) {
  if (!native) throw new Error(bridgeError || 'OpenXLR is not connected.');
  native.postMessage(message);
}

async function ensureOffscreen() {
  creating ??= (async () => {
    if (await chrome.offscreen.hasDocument()) return;
    await chrome.offscreen.createDocument({ url: 'offscreen.html', reasons: ['USER_MEDIA'],
      justification: 'Route user-activated tab audio to its selected OpenXLR output without recording it.' });
  })();
  const task = creating;
  try { await task; } finally { if (creating === task) creating = null; }
}

async function audio(message, create = false, validate = () => true) {
  if (create) await ensureOffscreen();
  else if (!await chrome.offscreen.hasDocument()) return null;
  if (validate() !== true) return null;
  const reply = await chrome.runtime.sendMessage({ ...message, target: 'offscreen' });
  if (reply?.error) throw new Error(reply.error);
  return reply?.value;
}

function serial(id, operation) {
  const previous = operations.get(id) ?? Promise.resolve();
  const next = previous.catch(() => {}).then(operation);
  operations.set(id, next);
  void next.finally(() => { if (operations.get(id) === next) operations.delete(id); }).catch(() => {});
  return next;
}

function cancelStart(id) {
  const pending = starts.get(id);
  if (pending) pending.cancelled = true;
  starts.delete(id);
}

function stopTab(id, error = null) {
  cancelStart(id);
  if (captures.has(id)) captures.set(id, { capturing: false, error });
  const task = (stops.get(id) ?? Promise.resolve()).catch(() => {}).then(() => audio({ type: 'stop', id, error }));
  stops.set(id, task);
  void task.finally(() => { if (stops.get(id) === task) stops.delete(id); }).catch(() => {});
  scheduleRefresh();
  return task;
}

function stopAll(error = null) {
  for (const pending of starts.values()) pending.cancelled = true;
  starts.clear();
  const task = (stopping ?? Promise.resolve()).then(async () => {
    try {
      // Include the initial document check so a late creation cannot outlive shutdown.
      await creating;
      await audio({ type: 'stopAll', error });
    }
    catch (failure) { bridgeError = errorText(failure); }
    finally {
      for (const [id] of captures) captures.set(id, { capturing: false, error });
      try { if (await chrome.offscreen.hasDocument()) await chrome.offscreen.closeDocument(); }
      catch (failure) { bridgeError = errorText(failure); }
      scheduleRefresh();
    }
  });
  stopping = task;
  void task.finally(() => { if (stopping === task) stopping = null; }).catch(() => {});
  return task;
}

function scheduleRefresh() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => { void refreshTabs().catch(error => { bridgeError = errorText(error); }); }, 100);
}

async function refreshTabs() {
  if (refreshing) { refreshAgain = true; return refreshing; }
  refreshing = (async () => {
    do {
      refreshAgain = false;
      const tabs = await chrome.tabs.query({});
      const alive = new Set(tabs.map(tab => tab.id));
      for (const id of captures.keys()) if (!alive.has(id)) captures.delete(id);
      for (const [id, pending] of assignments)
        if (!alive.has(id) || siteOf(tabs.find(tab => tab.id === id)?.url) !== pending.site) clearAssignment(id);
      for (const [id, previous] of sites) {
        const tab = tabs.find(candidate => candidate.id === id);
        if (!alive.has(id) || siteOf(tab?.url) !== previous) {
          await stopTab(id);
          captures.delete(id);
          sites.delete(id);
        }
      }
      const records = tabRecords(tabs, captures);
      for (const tab of records) sites.set(tab.id, tab.site);
      const encoded = JSON.stringify(records);
      if (native && encoded !== lastTabs) { post({ type: 'tabs', tabs: records }); lastTabs = encoded; }
    } while (refreshAgain);
  })();
  try { await refreshing; } finally { refreshing = null; }
}

async function outputFor(channel) {
  const devices = await audio({ type: 'devices' }, true);
  const task = bindingOperation.catch(() => {}).then(async () => {
    const { deviceBindings = {} } = await chrome.storage.local.get('deviceBindings');
    const id = deviceFor(channel, devices ?? [], deviceBindings);
    const alive = new Set(state?.channels.map(item => item.id) ?? []);
    const next = Object.fromEntries(Object.entries(deviceBindings).filter(([key]) => alive.has(key)));
    next[channel.id] = id;
    if (JSON.stringify(next) !== JSON.stringify(deviceBindings)) await chrome.storage.local.set({ deviceBindings: next });
    return id;
  });
  bindingOperation = task;
  return task;
}

async function reconcile() {
  if (!state?.preserveBrowserOutputs) { await stopAll(); return; }
  for (const [id, pending] of starts) {
    const tab = state.tabs.find(candidate => candidate.id === id);
    if (tab?.site !== pending.site || tab?.channelId !== pending.channel) {
      await stopTab(id);
    }
  }
  for (const [id, current] of captures) {
    if (!current.capturing || assignments.has(id)) continue;
    await serial(id, async () => {
      if (captures.get(id) !== current || !current.capturing || !state?.preserveBrowserOutputs || assignments.has(id)) return;
      const tab = state?.tabs.find(candidate => candidate.id === id);
      const channel = state?.channels.find(candidate => candidate.id === tab?.channelId);
      if (!channel) { await stopTab(id); return; }
      if (current.channelId === channel.id) return;
      const unchanged = () => captures.get(id) === current && state?.preserveBrowserOutputs === true
        && !assignments.has(id) && state.tabs.find(candidate => candidate.id === id)?.channelId === channel.id;
      try {
        const deviceId = await outputFor(channel);
        await audio({ type: 'route', id, deviceId }, false, unchanged);
        if (unchanged()) current.channelId = channel.id;
      }
      catch (error) { if (unchanged()) await stopTab(id, errorText(error)); }
    });
  }
}

async function handle(message) {
  switch (message.type) {
    case 'snapshot': return { state, error: bridgeError, startingTabs: [...starts.keys()], pendingTabs: [...assignments.keys()] };
    case 'retry': connect(); await refreshTabs(); return { state, error: bridgeError };
    case 'enable': post({ type: 'enable', value: message.value === true }); return null;
    case 'assign': {
      const tab = state?.tabs.find(candidate => candidate.id === message.id);
      if (!Number.isSafeInteger(message.id) || !tab) throw new Error('This tab is no longer registered with OpenXLR.');
      if (message.channel !== null && !state.channels.some(channel => channel.id === message.channel)) throw new Error('This OpenXLR channel is unavailable.');
      if (!assignments.has(message.id) && assignments.size >= MAX_TABS)
        throw new Error('Too many browser assignments are waiting for OpenXLR. Try again after a pending assignment finishes.');
      clearAssignment(message.id);
      const pending = { site: tab.site, channel: message.channel, remember: message.channel !== null && message.remember === true };
      assignments.set(message.id, pending);
      bridgeError = null;
      pending.timer = setTimeout(() => {
        if (assignments.get(message.id) !== pending) return;
        clearAssignment(message.id);
        bridgeError = ASSIGNMENT_TIMEOUT;
      }, 5000);
      if (starts.has(message.id)) void stopTab(message.id).catch(error => { bridgeError = errorText(error); });
      try { post({ type: 'assign', tabId: message.id, channel: message.channel, remember: message.remember === true }); }
      catch (error) { if (assignments.get(message.id) === pending) clearAssignment(message.id); throw error; }
      return null;
    }
    case 'start': {
      if (assignments.has(message.id)) throw new Error(ASSIGNMENT_PENDING);
      const [active] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!active || active.id !== message.id || active.incognito || !siteOf(active.url)) throw new Error('Activate routing from the tab you want to listen to.');
      if (assignments.has(active.id)) throw new Error(ASSIGNMENT_PENDING);
      const selected = state?.tabs.find(tab => tab.id === active.id && tab.site === siteOf(active.url));
      const channel = state?.channels.find(channel => channel.id === selected?.channelId);
      if (!state?.preserveBrowserOutputs) throw new Error('Enable browser output selection in OpenXLR first.');
      if (!channel) throw new Error('Choose an OpenXLR channel and wait for the assignment to appear first.');
      if (starts.has(active.id)) throw new Error('Routing is already starting for this tab.');
      const pending = { site: selected.site, channel: channel.id, cancelled: false };
      starts.set(active.id, pending);
      const check = () => {
        const latest = state?.tabs.find(candidate => candidate.id === active.id);
        if (assignments.has(active.id)) throw new Error(ASSIGNMENT_PENDING);
        if (pending.cancelled || !state?.preserveBrowserOutputs || latest?.site !== pending.site || latest?.channelId !== pending.channel)
          throw new Error('The tab or channel changed while routing was starting. Activate the tab again.');
        return true;
      };
      try {
        await stopping;
        await stops.get(active.id);
        check();
        const deviceId = await outputFor(channel);
        check();
        // The browser grants this only for a user-activated tab. Never reuse ids.
        const streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: active.id });
        check();
        await audio({ type: 'start', id: active.id, streamId, deviceId }, true, check);
        if (!pending.cancelled && captures.get(active.id)?.capturing) captures.get(active.id).channelId = channel.id;
        return null;
      } finally { if (starts.get(active.id) === pending) starts.delete(active.id); }
    }
    case 'stop': {
      return stopTab(message.id);
    }
    case 'audioStatus':
      if (!Number.isSafeInteger(message.id)) return null;
      captures.set(message.id, { capturing: message.capturing === true, error: message.error ? errorText(message.error) : null });
      scheduleRefresh();
      return null;
    case 'devicesChanged': await reconcile(); return null;
    default: throw new Error('Unknown browser routing action.');
  }
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (sender.id !== chrome.runtime.id || message?.target !== 'worker') return;
  if (message.type === 'audioStatus' && sender.url !== chrome.runtime.getURL('offscreen.html')) return;
  void handle(message).then(value => reply({ value }), error => reply({ error: errorText(error) }));
  return true;
});

chrome.tabs.onCreated.addListener(scheduleRefresh);
chrome.tabs.onUpdated.addListener((id, changes) => {
  const previous = assignments.get(id)?.site ?? starts.get(id)?.site ?? sites.get(id);
  if (changes.url && previous && siteOf(changes.url) !== previous) {
    clearAssignment(id);
    void stopTab(id).catch(error => { bridgeError = errorText(error); });
  }
  scheduleRefresh();
});
chrome.tabs.onRemoved.addListener(id => {
  clearAssignment(id);
  void stopTab(id).catch(error => { bridgeError = errorText(error); });
  scheduleRefresh();
});
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);

// A service worker can sleep while its offscreen audio continues. Recover the
// active sessions before publishing a replacement provider to the daemon.
void (async () => {
  try {
    const existing = await audio({ type: 'snapshot' });
    for (const tab of existing ?? []) captures.set(tab.id, { capturing: tab.capturing, error: null });
  } catch (error) {
    await stopAll(errorText(error));
  } finally {
    recovering = false;
    connect();
  }
})().catch(error => { bridgeError = errorText(error); });
