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
    status.textContent = outputs.length ? `Found ${outputs.length} OpenXLR outputs. Return to your audio tab and activate routing.`
      : 'Access was granted, but no OpenXLR outputs were found. Check that the daemon is running.';
    await chrome.runtime.sendMessage({ target: 'worker', type: 'devicesChanged' });
  } catch {
    status.textContent = 'Output access was not granted. Tab audio has not been changed. You can retry when ready.';
  } finally {
    stream?.getTracks().forEach(track => track.stop());
    button.disabled = false;
  }
});
