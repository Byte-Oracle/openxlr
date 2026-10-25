export function eqMixer() {
  const band = {enabled: true, type: "peak", frequency: 1000, gain: 0, q: Math.pow(10, -.152)};
  return {
    eqLimits: {bands: 4, types: ["lowCut", "lowShelf", "peak", "notch", "highShelf", "highCut"],
      frequency: {min: 20, max: 20000, step: 1}, gain: {min: -12, max: 12, step: .25},
      q: {min: .1, max: 10, log10Step: .016}},
    mixes: [{id: "monitor", name: "Monitor A", kind: "monitor"}],
    channels: [{id: "xlr1", name: "Main mic", microphone: true, hardware: true,
      appearance: {icon: "mic", colour: "#12ab34"},
      eq: {enabled: false, available: true, bands: Array.from({length: 4}, () => ({...band}))}}],
  };
}
