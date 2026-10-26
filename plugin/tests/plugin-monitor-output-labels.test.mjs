import assert from "node:assert/strict";
import test from "node:test";

// A monitor output key names its output by the device list's description.
// While the output is away the device list no longer has it, and the key
// takes the description the daemon remembers for the selected output.
test("an output key names an absent selected output by its remembered description", async () => {
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
  const svg = (uri) => Buffer.from(uri.split(",")[1], "base64").toString();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = sockets.find(socket => socket.url.includes(":37890/"));
    const host = sockets.find(socket => socket !== daemon);
    daemon.onopen();
    const headset = "alsa_output.usb-SteelSeries_Arctis_Nova_Elite-00.analog-stereo";
    const state = {type:"state", connected:true, profiles:[], capabilities:{}, state:{},
      devices:[{kind:0, name:headset, description:"Arctis Nova Elite"}],
      mixer:{
        channels:[{id:"system",name:"System",levels:{monitor:1},mutedIn:[]}],
        mixes:[{id:"monitor",name:"Monitor A",kind:"monitor",volume:1}],
        monitorOutputs:[headset], monitorFeeds:{}, monitorOutputLabels:{[headset]:"Arctis Nova Elite"},
        inserts:{}, lowCutHz:0, auxPortEnabled:false,
      }};
    daemon.receive(state);
    const keyImage = (context) =>
      svg(host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image);
    for (const [context, target] of [["monitor-key", `monitor:${headset}`], ["feed-key", `feed:${headset}`]]) {
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
      host.receive({event:"titleParametersDidChange",context,payload:{title:""}});
    }
    assert.ok(keyImage("monitor-key").includes("Arctis Nova Elite"));

    // Unplugged: the device list drops it, the daemon still names it.
    state.devices = [];
    daemon.receive(state);
    assert.ok(keyImage("monitor-key").includes("Arctis Nova Elite"));
    assert.ok(keyImage("feed-key").includes("Arctis Nova Elite"));

    // An output the daemon never saw keeps the last part of its node name.
    state.mixer.monitorOutputLabels = {};
    daemon.receive(state);
    assert.ok(keyImage("monitor-key").includes("analog-stereo"));
  }
  finally {
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
    intervals.forEach(clearInterval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
  }
});
