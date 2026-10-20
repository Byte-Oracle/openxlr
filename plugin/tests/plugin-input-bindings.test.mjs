import assert from "node:assert/strict";
import test from "node:test";

// XLR 1 bound to another microphone. The interface stays driven, so its
// hardware keys and dials keep acting on it; their faces name the channel
// that carries the interface's own input, which the daemon reports in
// unitInput, and a gain dial meters that channel.
test("hardware keys stay unit controls and are named after the carrying channel", async () => {
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
    const unit = "0fd9:00b4@0123456789abcdef";
    const state = {type:"state", connected:true, profiles:[],
      capabilities:{gain:true, mute:true, phantom:true, xlrInputs:2, xlrJacks:true},
      state:{gainDb:52, mute:false, gain2Db:30, mute2:true, phantom:true},
      devices:[{kind:1,name:"alsa_input.usb-Yeti",description:"Yeti Nano"}],
      mixer:{
        channels:[
          {id:"xlr1",name:"Mic 1",hardware:true,levels:{monitor:1,stream:.5},mutedIn:["monitor"],
            binding:{kind:"source",source:"alsa_input.usb-Yeti",pair:0,monoChannel:0},bindingConnected:true},
          {id:"xlr2",name:"XLR 2",hardware:true,levels:{monitor:1},mutedIn:[],
            binding:{kind:"unit",pair:1},unitInput:{unit,input:2,primary:true}},
          {id:"cap1",name:"Pro mic",levels:{monitor:1,stream:1},mutedIn:[],volume:1,
            captureSource:"alsa_input.usb-Elgato_Wave_XLR_Pro",
            binding:{kind:"source",source:"alsa_input.usb-Elgato_Wave_XLR_Pro",pair:0,monoChannel:0},
            unitInput:{unit,input:1,primary:true}},
        ],
        mixes:[{id:"monitor",name:"Monitor A",kind:"monitor",volume:1},{id:"stream",name:"Stream",kind:"virtualMic",volume:1}],
        monitorOutputs:[], monitorFeeds:{}, inserts:{}, lowCutHz:0, auxPortEnabled:true,
      }};
    daemon.receive(state);
    const keyImage = (context) =>
      svg(host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image);
    const feedback = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.title).at(-1).payload;
    const meter = (context) =>
      host.messages.filter(m => m.event === "setFeedback" && m.context === context && m.payload.meter).at(-1)?.payload.meter;
    const alerted = (context) => host.messages.filter(m => m.event === "showAlert" && m.context === context).length;
    const key = (context, target) => {
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
      host.receive({event:"titleParametersDidChange",context,payload:{title:""}});
    };

    // The interface's keys name the channel its microphone plays on, and act on the interface.
    key("hw-mute", "mute");
    key("hw-phantom", "phantom");
    key("hw-mute2", "mute2");
    assert.ok(keyImage("hw-mute").includes(">Pro mic</text>"));
    assert.ok(keyImage("hw-mute").includes(">Mute</text>"));
    assert.ok(keyImage("hw-phantom").includes(">Pro mic</text>"));
    assert.ok(keyImage("hw-mute2").includes(">XLR 2</text>"));
    for (const face of ["hw-mute", "hw-phantom", "hw-mute2"]) {
      assert.ok(!keyImage(face).includes("XLR 1"), `${face} does not name XLR 1`);
    }
    host.receive({event:"keyDown",context:"hw-mute"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"mute",value:true});
    host.receive({event:"keyDown",context:"hw-phantom"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"phantom",value:false});

    // The bound strip's sends are ordinary channel keys.
    key("send-mute", "sendmute:xlr1:stream");
    assert.ok(keyImage("send-mute").includes(">Mic 1</text>"));
    host.receive({event:"keyDown",context:"send-mute"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setChannelMuted",channel:"xlr1",mix:"stream",value:true});

    // The gain dial turns the interface's gain, says whose, and meters that channel.
    host.receive({event:"willAppear",context:"gain-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"gain"}}});
    const gain = feedback("gain-dial");
    assert.equal(gain.value, "52");
    assert.ok(svg(gain.title).includes("Pro mic gain"));
    host.receive({event:"dialRotate",context:"gain-dial",payload:{ticks:2}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"gain",value:54});
    host.receive({event:"dialDown",context:"gain-dial"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"mute",value:true});
    daemon.receive({type:"meters", levels:{"ch:xlr1":[0,0], "ch:cap1":[0.8,0.8]}});
    assert.match(svg(meter("gain-dial")), /<rect width="104" height="6"/);

    // With the interface's microphone out of the mixer the faces say so by
    // number and the gain dial's bar empties.
    delete state.mixer.channels[2].unitInput;
    daemon.receive(state);
    assert.ok(keyImage("hw-mute").includes(">Input 1</text>"));
    assert.ok(svg(feedback("gain-dial").title).includes("Input 1 gain"));
    daemon.receive({type:"meters", levels:{"ch:xlr1":[0.9,0.9], "ch:cap1":[0.8,0.8]}});
    assert.ok(!svg(meter("gain-dial")).includes('fill="url(#level)"'));

    // Back on the jack, XLR 1 is the strip and its own name returns.
    Object.assign(state.mixer.channels[0], {name:"XLR 1", binding:{kind:"unit",pair:0}, unitInput:{unit,input:1,primary:true}});
    daemon.receive(state);
    assert.ok(keyImage("hw-mute").includes(">XLR 1</text>"));
    assert.ok(svg(feedback("gain-dial").title).includes("XLR 1 gain"));
    daemon.receive({type:"meters", levels:{"ch:xlr1":[0.5,0.5], "ch:cap1":[0,0]}});
    assert.match(svg(meter("gain-dial")), /<rect width="65" height="6"/);

    // No interface connected: no capabilities, so the hardware keys and dials
    // are unavailable and a press or turn only flashes an alert.
    state.connected = false;
    delete state.capabilities;
    delete state.state;
    daemon.receive(state);
    const sent = daemon.messages.length;
    host.receive({event:"keyDown",context:"hw-mute"});
    host.receive({event:"dialRotate",context:"gain-dial",payload:{ticks:1}});
    host.receive({event:"dialDown",context:"gain-dial"});
    assert.equal(daemon.messages.length, sent);
    assert.equal(alerted("hw-mute"), 1);
    assert.equal(alerted("gain-dial"), 2);
    assert.equal(feedback("gain-dial").value, "set up");
  }
  finally {
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
    intervals.forEach(clearInterval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
  }
});
