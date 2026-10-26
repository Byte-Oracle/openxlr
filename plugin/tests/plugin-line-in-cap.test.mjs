import assert from "node:assert/strict";
import test from "node:test";

// The Line In dial stops at the highest gain the daemon writes (40 dB on the
// Wave XLR Pro). A unit set higher elsewhere shows its own gain, and a turn
// either way writes the cap.
test("the Line In dial stops at the daemon's Line In cap", async () => {
  const oldSkin = process.env.OPENXLR_SKIN;
  process.env.OPENXLR_SKIN = "default";
  const previous = globalThis.WebSocket;
  const previousInterval = globalThis.setInterval;
  const intervals = [];
  globalThis.setInterval = (...args) => {
    const interval = previousInterval(...args);
    intervals.push(interval);
    return interval;
  };
  const sockets = [];
  class Socket {
    static OPEN = 1;
    readyState = 1;
    messages = [];
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.messages.push(JSON.parse(text)); }
    receive(message) { this.onmessage({data: JSON.stringify(message)}); }
  }
  globalThis.WebSocket = Socket;
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = sockets.find(socket => socket.url.includes(":37890/"));
    const host = sockets.find(socket => socket !== daemon);
    daemon.onopen();
    daemon.receive({type:"state", connected:true, profiles:[],
      capabilities:{gain:true, mute:true, auxInput:true, lineInput:true, xlrInputs:2, gainMaxDb:80, lineInGainMaxDb:40},
      state:{gainDb:52, mute:false, auxLevelDb:-30, lineInGainDb:52},
      devices:[],
      mixer:{
        channels:[{id:"line",name:"Line In",hardware:true,levels:{monitor:1},mutedIn:[],volume:1}],
        mixes:[{id:"monitor",name:"Monitor A",kind:"monitor",volume:1}],
        monitorOutputs:[], monitorFeeds:{}, inserts:{}, lowCutHz:0, auxPortEnabled:false,
      }});
    const feedback = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.title).at(-1).payload;

    host.receive({event:"willAppear",context:"line-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"lineIn"}}});
    assert.equal(feedback("line-dial").value, "52 dB");

    host.receive({event:"dialRotate",context:"line-dial",payload:{ticks:1}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"lineInGain",value:40});
    assert.equal(feedback("line-dial").value, "40 dB");
  } finally {
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
  }
});
