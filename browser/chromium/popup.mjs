const $ = id => document.getElementById(id);
let tab;
let shown = '';
let disposed = false;
let operationError = '';
const send = async message => {
  const result = await chrome.runtime.sendMessage({ ...message, target: 'worker' });
  if (result?.error) throw new Error(result.error);
  return result?.value;
};
const act = operation => {
  operationError = '';
  void operation().catch(error => {
    operationError = error.message;
    $('error').textContent = operationError;
  });
};

async function refresh() {
  const snapshot = await send({ type: 'snapshot' });
  const state = snapshot?.state;
  const selected = state?.tabs.find(item => item.id === tab?.id);
  $('error').textContent = operationError || snapshot?.error || selected?.error || '';
  $('status').textContent = !state ? 'OpenXLR is not connected.'
    : !selected ? 'This tab is not available for routing.'
    : selected.capturing ? 'Tab audio is routed through OpenXLR.' : 'Choose a channel, then activate this tab.';
  $('enable').hidden = !state || state.preserveBrowserOutputs;
  const starting = snapshot?.startingTabs?.includes(tab?.id);
  const assigning = snapshot?.pendingTabs?.includes(tab?.id);
  $('start').disabled = !state?.preserveBrowserOutputs || !selected?.channelId || selected.capturing || starting || assigning;
  $('stop').disabled = !selected?.capturing && !starting;
  $('channel').disabled = !selected;
  $('remember').disabled = !selected;
  $('forget').disabled = !selected;
  const encoded = JSON.stringify([state?.channels, selected?.channelId, selected?.remember]);
  if (encoded !== shown) {
    shown = encoded;
    const choices = [new Option('Browser default', ''), ...(state?.channels ?? []).map(channel => new Option(channel.name, channel.id))];
    $('channel').replaceChildren(...choices);
    $('channel').value = selected?.channelId ?? '';
    $('remember').checked = selected?.remember !== false;
  }
}

const assign = () => send({ type: 'assign', id: tab.id, channel: $('channel').value || null, remember: $('remember').checked });
$('channel').addEventListener('change', () => act(assign));
$('remember').addEventListener('change', () => act(assign));
$('enable').addEventListener('click', () => act(() => send({ type: 'enable', value: true })));
$('start').addEventListener('click', () => act(() => send({ type: 'start', id: tab.id })));
$('stop').addEventListener('click', () => act(() => send({ type: 'stop', id: tab.id })));
$('retry').addEventListener('click', () => act(() => send({ type: 'retry' })));
$('forget').addEventListener('click', () => act(() => send({ type: 'assign', id: tab.id, channel: null, remember: true })));
$('devices').addEventListener('click', () => { void chrome.tabs.create({ url: chrome.runtime.getURL('devices.html') }); });

window.addEventListener('pagehide', () => { disposed = true; });
void (async () => {
  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  $('title').textContent = tab?.title || 'Current tab';
  while (!disposed) {
    try { await refresh(); } catch (error) { $('error').textContent = error.message; }
    await new Promise(resolve => setTimeout(resolve, 350));
  }
})();
