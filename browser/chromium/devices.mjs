import { t, translatePage } from './text.mjs';

translatePage(document);
document.getElementById('allow').addEventListener('click', async () => {
  let stream;
  const button = document.getElementById('allow');
  const status = document.getElementById('status');
  button.disabled = true;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    stream.getTracks().forEach(track => track.stop());
    stream = null;
    const devices = await navigator.mediaDevices.enumerateDevices();
    const outputs = devices.filter(device => device.kind === 'audiooutput' && device.label.startsWith('OpenXLR '));
    status.textContent = outputs.length ? t('devicesFound', outputs.length) : t('devicesNone');
    await chrome.runtime.sendMessage({ target: 'worker', type: 'devicesChanged' });
  } catch {
    status.textContent = t('devicesDenied');
  } finally {
    stream?.getTracks().forEach(track => track.stop());
    button.disabled = false;
  }
});
