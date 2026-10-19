import assert from "node:assert/strict";
import test from "node:test";

// Generic microphone mode: the daemon drives no Elgato unit, so "connected"
// is false and "capabilities" is absent, while mixer.primaryInput names the
// PipeWire source behind XLR 1. Mixer keys keep working; hardware keys say
// "Generic mic" and send nothing the daemon would refuse.
test("generic microphone mode keeps mixer keys and labels hardware keys", async () => {
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
    const state = {type:"state", connected:false, profiles:[],
      devices:[{kind:1,name:"alsa_input.usb-Yeti",description:"Yeti Nano"}],
      mixer:{
        primaryInput:{source:"alsa_input.usb-Yeti",channel:0}, primaryInputConnected:true,
        channels:[{id:"xlr1",name:"XLR 1",hardware:true,levels:{monitor:1,stream:.5},mutedIn:["monitor"]}],
        mixes:[{id:"monitor",name:"Monitor A",kind:"monitor",volume:1},{id:"stream",name:"Stream",kind:"virtualMic",volume:1}],
        monitorOutputs:[], monitorFeeds:{}, inserts:{}, lowCutHz:0, auxPortEnabled:true,
      }};
    daemon.receive(state);
    const keyImage = (context) =>
      svg(host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image);
    const feedback = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.title).at(-1).payload;
    const alerted = (context) => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const key = (context, target) => {
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
      host.receive({event:"titleParametersDidChange",context,payload:{title:""}});
    };

    // Hardware keys: a reason on the face, an alert on press, nothing sent.
    for (const [target, name] of [["mute", "XLR 1 Mute"], ["phantom", "XLR 1 48V"], ["voiceTune", "Voice Tune"],
                                  ["gainLocked", "Gain Lock"], ["outHp1", "HP 1 Out"], ["auxPort", "Aux Port"]]) {
      const context = `hw-${target}`;
      key(context, target);
      const face = keyImage(context);
      assert.ok(face.includes(">Generic mic</text>"), `${target} says Generic mic`);
      assert.ok(face.includes(`>${name}</text>`), `${target} keeps its name`);
      const sent = daemon.messages.length;
      host.receive({event:"keyDown",context});
      assert.equal(daemon.messages.length, sent, `${target} sends nothing in generic mode`);
      assert.equal(alerted(context), 1);
    }

    // Mixer keys still work on XLR 1.
    key("send-mute", "sendmute:xlr1:stream");
    assert.ok(!keyImage("send-mute").includes("Generic mic"));
    host.receive({event:"keyDown",context:"send-mute"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setChannelMuted",channel:"xlr1",mix:"stream",value:true});
    key("low-cut", "softLowCut");
    host.receive({event:"keyDown",context:"low-cut"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setLowCutHz",value:80});

    // A hardware dial names itself and the reason, and neither turn nor press sends.
    host.receive({event:"willAppear",context:"gain-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"gain"}}});
    const gain = feedback("gain-dial");
    assert.equal(gain.value, "n/a");
    assert.ok(svg(gain.title).includes("Generic mic"));
    assert.ok(svg(gain.title).includes("XLR 1 gain"));
    const beforeDial = daemon.messages.length;
    host.receive({event:"dialRotate",context:"gain-dial",payload:{ticks:2}});
    host.receive({event:"dialDown",context:"gain-dial"});
    assert.equal(daemon.messages.length, beforeDial);
    assert.equal(alerted("gain-dial"), 2);

    host.receive({event:"willAppear",context:"send-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"send:xlr1:stream"}}});
    assert.equal(feedback("send-dial").value, "50%");
    host.receive({event:"dialRotate",context:"send-dial",payload:{ticks:1}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setLevel",channel:"xlr1",mix:"stream",value:.51});

    // Back on the Elgato interface the hardware keys are ordinary again.
    delete state.mixer.primaryInput;
    state.connected = true;
    state.capabilities = {mute:true, gain:true};
    state.state = {mute:false, gainDb:30};
    daemon.receive(state);
    assert.ok(!keyImage("hw-mute").includes("Generic mic"));
    assert.ok(keyImage("hw-mute").includes(">XLR 1</text>"));
    host.receive({event:"keyDown",context:"hw-mute"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"mute",value:true});
    assert.equal(feedback("gain-dial").value, "30");
  }
  finally {
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
    intervals.forEach(clearInterval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
  }
});
