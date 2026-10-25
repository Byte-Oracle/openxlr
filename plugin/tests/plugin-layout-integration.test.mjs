import {controlFixture} from "./control-fixture.mjs";
import assert from "node:assert/strict";
import test from "node:test";

test("plugin publishes layout updates and keeps monitor feed commands intact", async () => {
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
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const daemon = control.children[0];
    const host = sockets[0];
    daemon.onopen();
    const state = {type:"state", profiles:[], devices:[{kind:0,name:"qa-output",description:"QA speakers"}], mixer:{
      channels:[{id:"system",name:"Desktop",levels:{monitor:1,monitor2:1}}],
      mixes:[{id:"monitor",name:"Monitor A"},{id:"monitor2",name:"Monitor B"}],
      monitorOutputs:["qa-output"], monitorFeeds:{}, inserts:{}
    }};
    daemon.receive(state);
    const keyImage = (context) => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      return Buffer.from(image.split(",")[1], "base64").toString();
    };
    host.receive({event:"willAppear",context:"appearance-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"sendmute:system:monitor"}}});
    host.receive({event:"willAppear",context:"own-glyph-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"sendmute:system:monitor",icon:"headphones"}}});
    state.mixer.channels[0].mutedIn = [];
    state.mixer.channels[0].appearance = {icon:"♫",colour:"#1234AB",hidden:true};
    daemon.receive(state);
    assert.ok(keyImage("appearance-key").includes(">♫</text>"));
    assert.ok(keyImage("appearance-key").includes("#1234AB"));
    assert.ok(!keyImage("own-glyph-key").includes("♫"));
    // A built-in icon by name is drawn as a path in the colour; a key's own glyph still wins.
    state.mixer.channels[0].appearance = {icon:"music",defaultIcon:"cog",colour:"#1234AB"};
    daemon.receive(state);
    assert.match(keyImage("appearance-key"), /<path d="M[^"]+" fill="#1234AB"\/>/);
    assert.ok(!keyImage("own-glyph-key").includes('fill="#1234AB"/>'));
    state.mixer.channels[0].appearance = {icon:"none",defaultIcon:"cog"};
    daemon.receive(state);
    assert.ok(!/<path d="M[^"]+" fill="[^"]+"\/>/.test(keyImage("appearance-key")));
    delete state.mixer.channels[0].appearance;
    daemon.receive(state);
    assert.ok(!keyImage("appearance-key").includes("#1234AB"));
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.ok(host.messages.at(-1).payload.levelGroups.flatMap(g => g.items)
      .some(item => item.target === "send:system:monitor2"));
    host.receive({event:"willAppear",context:"focus-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"focus:system"}}});
    host.receive({event:"keyDown",context:"focus-key"});
    const focus = daemon.messages.at(-1);
    assert.deepEqual({...focus, requestId:undefined}, {cmd:"routeFocusedApp",channel:"system",requestId:undefined});
    assert.equal(typeof focus.requestId, "string");
    daemon.receive({type:"commandResult",requestId:focus.requestId,error:"ambiguous application"});
    assert.ok(host.messages.some(m => m.event === "showAlert" && m.context === "focus-key"));
    host.receive({event:"willAppear",context:"feed-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"feed:qa-output"}}});
    host.receive({event:"keyDown",context:"feed-key"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:"monitor2"});
    state.mixer.monitorFeeds["qa-output"] = "monitor2";
    state.mixer.channels[0].name = "Renamed Desktop";
    daemon.receive(state);
    const update = host.messages.filter(message => message.event === "sendToPropertyInspector" && message.context === "qa").at(-1);
    assert.ok(update.payload.levelGroups.flatMap(g => g.items).some(item => item.label === "Renamed Desktop in Monitor B"));
    host.receive({event:"keyDown",context:"feed-key"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:"monitor+monitor2"});
    state.mixer.mixes.push({id:"stream",name:"Stream",kind:"virtualMic"}, {id:"auxout",name:"Aux",kind:"auxPort"});
    for (const [current, next] of [["monitor+monitor2", "stream"], ["stream", "auxout"], ["auxout", "monitor"], ["deleted", "monitor2"], ["", "monitor2"]]) {
      state.mixer.monitorFeeds["qa-output"] = current;
      daemon.receive(state);
      host.receive({event:"keyDown",context:"feed-key"});
      assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:next});
    }
    state.mixer.mixes.push({id:"headphones",name:"Headphones",kind:"monitor"});
    for (const [current, next] of [["monitor2", "monitor+monitor2"], ["auxout", "headphones"], ["headphones", "monitor"]]) {
      state.mixer.monitorFeeds["qa-output"] = current;
      daemon.receive(state);
      host.receive({event:"keyDown",context:"feed-key"});
      assert.deepEqual(daemon.messages.at(-1), {cmd:"setMonitorFeed",device:"qa-output",mix:next});
    }
    host.receive({event:"propertyInspectorDidDisappear",context:"qa"});
    const count = host.messages.filter(m => m.event === "sendToPropertyInspector").length;
    state.mixer.channels[0].name = "Another name";
    daemon.receive(state);
    assert.equal(host.messages.filter(m => m.event === "sendToPropertyInspector").length, count);

    for (const [target, expected] of [
      ["outputup:", {cmd:"adjustOutputVolume",device:null,value:.05}],
      ["outputdown:qa-output", {cmd:"adjustOutputVolume",device:"qa-output",value:-.05}],
      ["outputmute:qa-output", {cmd:"toggleOutputMute",device:"qa-output"}],
      ["mainoutput:qa-output", {cmd:"setMainOutput",device:"qa-output"}],
      ["mainoutput:@monitor", {cmd:"setMainOutput",device:"@monitor"}],
    ]) {
      host.receive({event:"willAppear",context:"output-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
      host.receive({event:"keyDown",context:"output-key"});
      const {requestId, ...payload} = daemon.messages.at(-1);
      assert.deepEqual(payload, expected);
      daemon.receive({type:"commandResult",requestId});
      assert.ok(host.messages.some(m => m.event === "showOk" && m.context === "output-key"));
    }
    host.receive({event:"willAppear",context:"rapid-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"outputdown:qa-output"}}});
    const rapidStart = daemon.messages.length;
    for (let i = 0; i < 3; i++) host.receive({event:"keyDown",context:"rapid-key"});
    assert.equal(daemon.messages.length, rapidStart + 1);
    host.receive({event:"willAppear",context:"select-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"mainoutput:qa-output"}}});
    host.receive({event:"keyDown",context:"select-key"});
    const select = daemon.messages.at(-1);
    assert.equal(select.cmd, "setMainOutput", "output selection must not wait behind queued volume steps");
    daemon.receive({type:"commandResult",requestId:select.requestId});
    assert.ok(host.messages.some(m => m.event === "showOk" && m.context === "select-key"));
    host.receive({event:"willDisappear",context:"select-key"});
    for (let i = 0; i < 3; i++) {
      assert.equal(daemon.messages.length, rapidStart + i + 2, "each acknowledged volume step must send the next queued press");
      const step = daemon.messages.filter(m => m.cmd === "adjustOutputVolume").at(-1);
      assert.deepEqual({...step, requestId:undefined}, {cmd:"adjustOutputVolume",device:"qa-output",value:-.05,requestId:undefined});
      daemon.receive({type:"commandResult",requestId:step.requestId});
    }
    host.receive({event:"keyDown",context:"rapid-key"});
    host.receive({event:"keyDown",context:"rapid-key"});
    const oldStep = daemon.messages.at(-1);
    host.receive({event:"didReceiveSettings",context:"rapid-key",payload:{settings:{target:"outputup:qa-output"}}});
    const reboundStart = daemon.messages.length;
    daemon.receive({type:"commandResult",requestId:oldStep.requestId});
    assert.equal(daemon.messages.length, reboundStart, "a changed target must discard queued actions");
    host.receive({event:"keyDown",context:"rapid-key"});
    assert.equal(daemon.messages.at(-1).value, .05);
    host.receive({event:"willDisappear",context:"rapid-key"});
    host.receive({event:"willAppear",context:"missing-output",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"outputmute:gone"}}});
    const beforeMissing = daemon.messages.length;
    host.receive({event:"keyDown",context:"missing-output"});
    assert.equal(daemon.messages.length, beforeMissing);
    assert.ok(host.messages.some(m => m.event === "showAlert" && m.context === "missing-output"));

    // An exclusive group key names the member that is heard and asks the
    // daemon for the next one; a deleted group disables it.
    state.mixer.channels.push({id:"headset",name:"Headset",levels:{monitor:1,monitor2:1},mutedIn:["monitor"]});
    state.mixer.channels[0].mutedIn = ["monitor", "monitor2"];
    state.mixer.exclusiveGroups = [{id:"mics",name:"Microphones",channels:["system","headset"]}];
    daemon.receive(state);
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.ok(host.messages.at(-1).payload.toggleGroups.flatMap(g => g.items)
      .some(item => item.target === "group:mics" && item.label === "Microphones: next member"));
    host.receive({event:"willAppear",context:"group-key",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"group:mics"}}});
    host.receive({event:"titleParametersDidChange",context:"group-key",payload:{title:""}});
    const face = host.messages.filter(m => m.event === "setImage" && m.context === "group-key").at(-1);
    assert.match(Buffer.from(face.payload.image.split(",")[1], "base64").toString(), />Headset</);
    host.receive({event:"keyDown",context:"group-key"});
    const {requestId: cycleId, ...cycle} = daemon.messages.at(-1);
    assert.deepEqual(cycle, {cmd:"cycleExclusiveGroup",group:"mics"});
    daemon.receive({type:"commandResult",requestId:cycleId});
    assert.ok(host.messages.some(m => m.event === "showOk" && m.context === "group-key"));
    state.mixer.exclusiveGroups = [];
    daemon.receive(state);
    const beforeDeleted = daemon.messages.length;
    host.receive({event:"keyDown",context:"group-key"});
    assert.equal(daemon.messages.length, beforeDeleted);
    assert.ok(host.messages.some(m => m.event === "showAlert" && m.context === "group-key"));
    state.mixer.channels.pop();
    delete state.mixer.exclusiveGroups;
    delete state.mixer.channels[0].mutedIn;
    daemon.receive(state);

    // A desktop boost must not jump back to 100% on the first dial tick.
    state.mixer.mixes = [
      {id:"monitor",name:"Monitor A",kind:"monitor",volume:1.5},
      {id:"monitor2",name:"Monitor B",kind:"monitor",volume:1.2},
      {id:"chat",name:"Chat",kind:"virtualMic",volume:1}
    ];
    state.mixer.outputVolume = 1.5;
    daemon.receive(state);
    for (const [target, volume, maximum] of [
      ["mixvol:monitor", 0, 1.5],
      ["mixvol:monitor", 1, 1.5],
      ["mixvol:monitor", 1.2, 1.5],
      ["mixvol:monitor", 1.5, 1.5],
      ["mixvol:monitor2", 1.2, 1.5],
      ["outputVolume", 1, 1.5],
      ["outputVolume", 1.2, 1.5],
      ["outputVolume", 1.5, 1.5],
      ["mixvol:chat", 0.5, 1],
      ["mixvol:chat", 1, 1],
      ["send:system:monitor", 1, 1],
    ]) {
      if (target.startsWith("mixvol:")) state.mixer.mixes.find(m => m.id === target.slice(7)).volume = volume;
      else if (target === "outputVolume") state.mixer.outputVolume = volume;
      daemon.receive(state);
      host.receive({event:"willAppear",context:"needle-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target}}});
      const feedback = host.messages.filter(m => m.event === "setFeedback" && m.context === "needle-dial").at(-1).payload;
      assert.equal(feedback.value, `${Math.round(volume * 100)}%`);
      const svg = Buffer.from(feedback.needle.split(",")[1], "base64").toString();
      const angle = Number(svg.match(/rotate\(([^,]+)/)[1]);
      assert.ok(Math.abs(angle - (volume / maximum * 100 - 50)) < 0.0001, target);
    }
    for (const [target, ticks, expected] of [
      ["mixvol:monitor", -1, {cmd:"setMixVolume",mix:"monitor",value:1.49}],
      ["mixvol:monitor", 1, {cmd:"setMixVolume",mix:"monitor",value:1.5}],
      ["mixvol:monitor2", -1, {cmd:"setMixVolume",mix:"monitor2",value:1.19}],
      ["mixvol:chat", 1, {cmd:"setMixVolume",mix:"chat",value:1}],
      ["outputVolume", -1, {cmd:"setOutputVolume",value:1.49}],
      ["outputVolume", 1, {cmd:"setOutputVolume",value:1.5}],
      ["outputVolume", -200, {cmd:"setOutputVolume",value:0}],
    ]) {
      host.receive({event:"willAppear",context:"volume-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target}}});
      host.receive({event:"dialRotate",context:"volume-dial",payload:{ticks}});
      await new Promise(resolve => setTimeout(resolve, 120));   // past the send window
      assert.deepEqual(daemon.messages.at(-1), expected);
    }

    // Every tick of a burst has to land. The dial steps from its own last
    // value, so ticks arriving before the daemon's echo are not lost, and the
    // echo cannot pull the strip back to where the turn started.
    await new Promise(resolve => setTimeout(resolve, 900));   // let the turns above cool
    state.mixer.outputVolume = 0.5;
    daemon.receive(state);
    host.receive({event:"willAppear",context:"burst-dial",action:"com.emaspa.openxlr.dial",payload:{settings:{target:"outputVolume"}}});
    for (let tick = 0; tick < 5; tick++)
      host.receive({event:"dialRotate",context:"burst-dial",payload:{ticks:1}});
    const shown = () => host.messages
      .filter(m => m.event === "setFeedback" && m.context === "burst-dial").at(-1).payload.value;
    assert.equal(shown(), "55%");
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setOutputVolume",value:0.55});
    daemon.receive(state);
    assert.equal(shown(), "55%");

    // Output mute keys read the sink's state: a mix sink is its mix, a monitor
    // output is the mixes feeding it or the sink itself, any other sink is the
    // server's flag, and the system default is the enforced sink when named.
    state.devices = [
      {kind:0,name:"qa-output",description:"QA speakers",volume:0.8,muted:false},
      {kind:0,name:"desk",description:"Desk speakers",volume:1.2,muted:true},
      {kind:0,name:"OpenXLR_mix_monitor",description:"Monitor A",isOwn:true,volume:1,muted:false},
      {kind:0,name:"legacy",description:"Old sink"},
    ];
    state.mixer.monitorFeeds = {"qa-output":"monitor+monitor2"};
    state.mixer.mixes[0].muted = true;
    state.mixer.mixes[1].muted = false;
    state.mixer.enforcedDefaultSink = "desk";
    const keyFace = (context) => {
      const image = host.messages.filter(m => m.event === "setImage" && m.context === context).at(-1).payload.image;
      const svg = Buffer.from(image.split(",")[1], "base64").toString();
      return svg.toLowerCase().includes("#ff3c4e") ? "muted" : svg.includes("#4a4f5c") ? "unknown" : "off";
    };
    const keyTarget = (context, target) =>
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.toggle",payload:{settings:{target}}});
    for (const [context, target] of [["mix-key","outputmute:OpenXLR_mix_monitor"], ["monitor-key","outputmute:qa-output"],
      ["desk-key","outputmute:desk"], ["legacy-key","outputmute:legacy"], ["default-key","outputmute:"]]) keyTarget(context, target);
    daemon.receive(state);
    assert.equal(keyFace("mix-key"), "muted");
    assert.equal(keyFace("monitor-key"), "off");
    assert.equal(keyFace("desk-key"), "muted");
    assert.equal(keyFace("legacy-key"), "unknown");
    assert.equal(keyFace("default-key"), "muted");
    state.mixer.mixes[1].muted = true;
    daemon.receive(state);
    assert.equal(keyFace("monitor-key"), "muted");
    state.mixer.mixes[0].muted = state.mixer.mixes[1].muted = false;
    state.devices[0].muted = true;
    daemon.receive(state);
    assert.equal(keyFace("monitor-key"), "muted");
    state.devices[0].muted = false;
    state.mixer.enforcedDefaultSink = "@monitor";
    daemon.receive(state);
    assert.equal(keyFace("monitor-key"), "off");
    assert.equal(keyFace("default-key"), "off");   // momentary while the default is not a named sink
    const beforeLegacy = daemon.messages.length;
    host.receive({event:"keyDown",context:"legacy-key"});
    assert.equal(daemon.messages.length, beforeLegacy);
    host.receive({event:"keyDown",context:"default-key"});
    assert.deepEqual({...daemon.messages.at(-1), requestId:undefined}, {cmd:"toggleOutputMute",device:null,requestId:undefined});
    daemon.receive({type:"commandResult",requestId:daemon.messages.at(-1).requestId});
    state.mixer.enforcedDefaultSink = "desk";
    daemon.receive(state);

    // Output dials: the level and mute of a sink, the system default resolved
    // through the enforced sink, sent with the target's own device name.
    const dialTarget = (context, target) =>
      host.receive({event:"willAppear",context,action:"com.emaspa.openxlr.dial",payload:{settings:{target}}});
    const feedbackOf = (context) => host.messages.filter(m => m.event === "setFeedback" && m.context === context).at(-1).payload;
    for (const [target, value, rotate, expected] of [
      ["output:", "MUTED", 1, {cmd:"setOutputDeviceVolume",device:null,value:1.21}],
      ["output:qa-output", "80%", -1, {cmd:"setOutputDeviceVolume",device:"qa-output",value:0.79}],
      ["output:OpenXLR_mix_monitor", "150%", 1, {cmd:"setOutputDeviceVolume",device:"OpenXLR_mix_monitor",value:1.5}],
    ]) {
      dialTarget("output-dial", target);
      assert.equal(feedbackOf("output-dial").value, value, target);
      assert.equal(feedbackOf("output-dial").muteOverlay.enabled, value === "MUTED", target);
      host.receive({event:"dialRotate",context:"output-dial",payload:{ticks:rotate}});
      assert.deepEqual(daemon.messages.at(-1), expected);
      host.receive({event:"dialDown",context:"output-dial"});
      const {requestId, ...press} = daemon.messages.at(-1);
      assert.deepEqual(press, {cmd:"toggleOutputMute",device:expected.device});
      assert.equal(typeof requestId, "string");
      daemon.receive({type:"commandResult",requestId});
    }
    dialTarget("output-dial", "output:legacy");
    assert.equal(feedbackOf("output-dial").value, "set up");
    const beforeLegacyDial = daemon.messages.length;
    host.receive({event:"dialRotate",context:"output-dial",payload:{ticks:1}});
    assert.equal(daemon.messages.length, beforeLegacyDial);

    // Volume-only headphone jacks have no output selector to use as mute.
    for (const routing of [false, undefined, true]) {
      state.connected = true;
      state.capabilities = {hpVolume:true, hpOutputs:2, outputRouting:routing};
      state.state = {hpVolumeDb:-12, hp2VolumeDb:-30, outHp1:false, outHp2:false};
      daemon.receive(state);
      for (const [target, value, control] of [["hp", "80%", "outHp1"], ["hp2", "50%", "outHp2"]]) {
        dialTarget("headphone-dial", target);
        assert.equal(feedbackOf("headphone-dial").value, routing === true ? "MUTED" : value);
        assert.equal(feedbackOf("headphone-dial").muteOverlay.enabled, routing === true);
        const sent = daemon.messages.length;
        host.receive({event:"dialDown",context:"headphone-dial"});
        if (routing === true) assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control,value:true});
        else assert.equal(daemon.messages.length, sent, "unsupported headphone mute must send nothing");
      }
    }
    state.capabilities = {hpVolume:true, hpOutputs:1, outputRouting:true};
    daemon.receive(state);
    dialTarget("missing-headphone-dial", "hp2");
    const beforeMissingHeadphone = daemon.messages.length;
    host.receive({event:"dialDown",context:"missing-headphone-dial"});
    assert.equal(daemon.messages.length, beforeMissingHeadphone, "a missing second jack cannot be toggled");

    // The gain dial stops at the device's highest gain, 40 dB on the Wave:3,
    // and draws its needle against that range.
    state.capabilities = {gain:true, mute:true, gainMaxDb:40};
    state.state = {gainDb:38, mute:false};
    daemon.receive(state);
    dialTarget("gain-dial", "gain");
    assert.equal(feedbackOf("gain-dial").value, "38");
    host.receive({event:"dialRotate",context:"gain-dial",payload:{ticks:5}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"set",control:"gain",value:40});
    delete state.capabilities;
    delete state.state;
    daemon.receive(state);

    // A global monitor pause must preserve a mix that was already muted.
    state.mixer.mixes[0].muted = false;
    state.mixer.mixes[1].muted = true;
    state.mixer.monitorFeeds = {"qa-output":"monitor+monitor2"};
    delete state.mixer.monitorMuted; // An older state means not globally paused.
    daemon.receive(state);
    dialTarget("monitor-dial", "outputVolume");
    dialTarget("physical-monitor-dial", "output:qa-output");
    dialTarget("mix-monitor-dial", "output:OpenXLR_mix_monitor");
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, false);
    assert.equal(feedbackOf("physical-monitor-dial").muteOverlay.enabled, false);
    const beforeMonitorPause = daemon.messages.length;
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.equal(daemon.messages.length, beforeMonitorPause + 1);
    const {requestId: pauseId, ...pause} = daemon.messages.at(-1);
    assert.deepEqual(pause, {cmd:"toggleMonitorMuted"});
    assert.equal(typeof pauseId, "string");
    state.mixer.monitorMuted = true;
    daemon.receive(state);
    daemon.receive({type:"commandResult",requestId:pauseId});
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, true);
    assert.equal(feedbackOf("physical-monitor-dial").muteOverlay.enabled, true);
    assert.equal(feedbackOf("mix-monitor-dial").muteOverlay.enabled, true, "the explicit mix sink shows the effective monitor pause");
    assert.equal(keyFace("monitor-key"), "muted");
    assert.equal(keyFace("mix-key"), "muted");
    assert.equal(keyFace("desk-key"), "muted");
    assert.equal(keyFace("default-key"), "muted");
    const beforeMonitorResume = daemon.messages.length;
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.equal(daemon.messages.length, beforeMonitorResume + 1);
    const {requestId: resumeId, ...resume} = daemon.messages.at(-1);
    assert.deepEqual(resume, {cmd:"toggleMonitorMuted"});
    state.mixer.monitorMuted = false;
    daemon.receive(state);
    daemon.receive({type:"commandResult",requestId:resumeId});
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, false);
    assert.equal(feedbackOf("physical-monitor-dial").muteOverlay.enabled, false);
    assert.equal(state.mixer.mixes[0].muted, false);
    assert.equal(state.mixer.mixes[1].muted, true);
    assert.equal(keyFace("monitor-key"), "off");
    assert.equal(keyFace("desk-key"), "muted", "an unrelated sink keeps its server mute flag");

    // Feed-local mutes still show silence; a press only changes master pause.
    state.mixer.monitorFeeds = {"qa-output":"monitor2"};
    daemon.receive(state);
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, true);
    assert.equal(feedbackOf("physical-monitor-dial").muteOverlay.enabled, true);
    const beforeMutedFeed = daemon.messages.length;
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.equal(daemon.messages.length, beforeMutedFeed + 1);
    const {requestId: mutedFeedId, ...mutedFeed} = daemon.messages.at(-1);
    assert.deepEqual(mutedFeed, {cmd:"toggleMonitorMuted"});
    daemon.receive({type:"commandResult",requestId:mutedFeedId});
    assert.equal(state.mixer.mixes[1].muted, true);

    // A system-default key resolves the selected physical output the same way.
    state.mixer.enforcedDefaultSink = "qa-output";
    state.mixer.monitorMuted = true;
    state.mixer.mixes[1].muted = false;
    daemon.receive(state);
    assert.equal(keyFace("default-key"), "muted");
    state.mixer.monitorMuted = false;
    daemon.receive(state);
    assert.equal(keyFace("default-key"), "off");
    state.mixer.enforcedDefaultSink = "desk";

    // The daemon toggles atomically, so queued presses retain parity even
    // if only acknowledgements arrive and the displayed state stays stale.
    for (const count of [2, 3]) {
      state.mixer.monitorMuted = false;
      state.mixer.mixes[1].muted = true;
      daemon.receive(state);
      const beforeBurst = daemon.messages.length;
      for (let i = 0; i < count; i++) host.receive({event:"dialDown",context:"monitor-dial"});
      assert.equal(daemon.messages.length, beforeBurst + 1);
      let paused = false;
      const requestIds = new Set();
      for (let i = 0; i < count; i++) {
        assert.equal(daemon.messages.length, beforeBurst + i + 1);
        const {requestId, ...command} = daemon.messages.at(-1);
        assert.deepEqual(command, {cmd:"toggleMonitorMuted"});
        requestIds.add(requestId);
        paused = !paused;
        daemon.receive({type:"commandResult",requestId});
      }
      assert.equal(requestIds.size, count);
      assert.equal(paused, count % 2 === 1);
      assert.equal(state.mixer.mixes[1].muted, true);
      state.mixer.monitorMuted = paused;
      daemon.receive(state);
    }

    // Raw Pro controls follow selected jack feeds, not an unsuffixed default.
    state.devices.push({kind:0,name:"pro",description:"Pro",volume:1,muted:false});
    state.mixer.monitorOutputs = ["pro#hp1"];
    state.mixer.monitorFeeds = {"pro#hp1":"monitor2"};
    state.mixer.monitorMuted = false;
    state.mixer.mixes[0].muted = false;
    state.mixer.mixes[1].muted = true;
    daemon.receive(state);
    dialTarget("pro-dial", "output:pro");
    keyTarget("pro-key", "outputmute:pro");
    assert.equal(feedbackOf("pro-dial").value, "MUTED");
    assert.equal(keyFace("pro-key"), "muted");
    state.mixer.mixes[1].muted = false;
    state.mixer.monitorMuted = true;
    daemon.receive(state);
    assert.equal(feedbackOf("pro-dial").muteOverlay.enabled, true);
    assert.equal(keyFace("pro-key"), "muted");
    assert.equal(feedbackOf("physical-monitor-dial").muteOverlay.enabled, false);
    assert.equal(keyFace("monitor-key"), "off", "an unselected physical output keeps its native mute");
    assert.equal(feedbackOf("mix-monitor-dial").muteOverlay.enabled, true);
    dialTarget("paused-mix-master", "mixvol:monitor");
    host.receive({event:"dialDown",context:"paused-mix-master"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMixMuted",mix:"monitor",value:false});
    keyTarget("paused-mix-key", "mixmute:monitor");
    host.receive({event:"keyDown",context:"paused-mix-key"});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setMixMuted",mix:"monitor",value:false});
    assert.equal(keyFace("desk-key"), "muted");
    state.mixer.monitorMuted = false;
    state.mixer.monitorOutputs.push("pro#hp2");
    state.mixer.monitorFeeds["pro#hp2"] = "monitor";
    state.mixer.mixes[1].muted = true;
    daemon.receive(state);
    assert.equal(feedbackOf("pro-dial").muteOverlay.enabled, false, "one audible selected jack keeps the raw output active");
    state.mixer.mixes[0].muted = true;
    daemon.receive(state);
    assert.equal(feedbackOf("pro-dial").muteOverlay.enabled, true);
    assert.equal(keyFace("pro-key"), "muted");
    state.mixer.monitorOutputs = ["qa-output"];
    state.mixer.monitorFeeds = {"qa-output":"monitor2"};
    state.mixer.mixes[0].muted = state.mixer.mixes[1].muted = false;
    daemon.receive(state);

    // With no mixer state, a dial press cannot guess a master mute value.
    daemon.receive({...state,mixer:null});
    const beforeMissingMonitor = daemon.messages.length;
    host.receive({event:"dialDown",context:"monitor-dial"});
    assert.equal(daemon.messages.length, beforeMissingMonitor);
    daemon.receive(state);

    // A channel volume dial is offered for a channel the daemon reports a
    // volume for, turns that volume, and its press mutes the channel in every
    // mix the way an all-mixes send dial does.
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.ok(!host.messages.at(-1).payload.levelGroups.some(g => g.id === "layout-channel-volumes"));
    state.mixer.channels[0].volume = 0.6;
    state.mixer.channels[0].mutedIn = [];
    daemon.receive(state);
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.deepEqual(host.messages.at(-1).payload.levelGroups.find(g => g.id === "layout-channel-volumes").items,
      [{target:"chvol:system", label:`${state.mixer.channels[0].name} volume`}]);
    dialTarget("chvol-dial", "chvol:system");
    assert.equal(feedbackOf("chvol-dial").value, "60%");
    host.receive({event:"dialRotate",context:"chvol-dial",payload:{ticks:-10}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setChannelVolume",channel:"system",value:0.5});
    assert.equal(feedbackOf("chvol-dial").value, "50%");
    host.receive({event:"dialDown",context:"chvol-dial"});
    assert.deepEqual(daemon.messages.slice(-2), [
      {cmd:"setChannelMuted",channel:"system",mix:"monitor",value:true},
      {cmd:"setChannelMuted",channel:"system",mix:"monitor2",value:true}]);
    state.mixer.channels[0].mutedIn = ["monitor", "monitor2"];
    daemon.receive(state);
    assert.equal(feedbackOf("chvol-dial").value, "MUTED");
    // XLR 1 has a volume too, labelled with the strip's name: "Mic 1" while
    // it is bound to another source. Its dial turns that volume.
    state.mixer.channels[0].mutedIn = [];
    state.mixer.channels.unshift({id:"xlr1",name:"Mic 1",hardware:true,volume:0.8,levels:{monitor:1,monitor2:1},mutedIn:[],
      binding:{kind:"source",source:"alsa_input.usb-webcam"}});
    daemon.receive(state);
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.deepEqual(host.messages.at(-1).payload.levelGroups.find(g => g.id === "layout-channel-volumes").items,
      [{target:"chvol:xlr1", label:"Mic 1 volume"}, {target:"chvol:system", label:`${state.mixer.channels[1].name} volume`}]);
    dialTarget("xlr1-volume-dial", "chvol:xlr1");
    assert.equal(feedbackOf("xlr1-volume-dial").value, "80%");
    host.receive({event:"dialRotate",context:"xlr1-volume-dial",payload:{ticks:-5}});
    assert.deepEqual(daemon.messages.at(-1), {cmd:"setChannelVolume",channel:"xlr1",value:0.75});
    state.mixer.channels[0].name = "XLR 1";
    delete state.mixer.channels[0].binding;
    daemon.receive(state);
    host.receive({event:"sendToPlugin",context:"qa",payload:{request:"layout"}});
    assert.equal(host.messages.at(-1).payload.levelGroups.find(g => g.id === "layout-channel-volumes").items[0].label, "XLR 1 volume");
    state.mixer.channels.shift();
    daemon.receive(state);
    // A daemon that reports no volume for the channel leaves the dial unset.
    delete state.mixer.channels[0].volume;
    state.mixer.channels[0].mutedIn = [];
    daemon.receive(state);
    const before = daemon.messages.length;
    await new Promise(resolve => setTimeout(resolve, 900));   // let the turn above cool
    host.receive({event:"dialRotate",context:"chvol-dial",payload:{ticks:1}});
    assert.equal(daemon.messages.length, before);
    // Persisted settings can carry old or malformed icon values.
    for (const icon of ["constructor", "__proto__", "toString", "<svg/>", "", 12, true, {}, [], {toString:null,valueOf:null}, null]) {
      assert.doesNotThrow(() => host.receive({event:"willAppear",context:"icon-settings",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"mixmute:monitor",icon}}}));
      const image = host.messages.filter(m => m.event === "setImage" && m.context === "icon-settings").at(-1).payload.image;
      assert.ok(Buffer.from(image.split(",")[1], "base64").toString().includes('M42 58'), "invalid icons retain the target's speaker glyph");
    }
    host.receive({event:"willAppear",context:"icon-settings",action:"com.emaspa.openxlr.toggle",payload:{settings:{target:"mixmute:monitor",icon:"mic"}}});
    const chosen = host.messages.filter(m => m.event === "setImage" && m.context === "icon-settings").at(-1).payload.image;
    assert.ok(Buffer.from(chosen.split(",")[1], "base64").toString().includes('x="58" y="30"'));

    // Deleted built-ins leave the daemon's default and renamed monitor in control.
    state.mixer.mixes = state.mixer.mixes.filter(m => m.id !== "monitor");
    const remainingMonitor = state.mixer.mixes.find(m => m.id === "monitor2");
    remainingMonitor.name = "Studio";
    remainingMonitor.muted = true;
    state.mixer.defaultMonitorMix = "monitor2";
    state.mixer.monitorFeeds = {};
    state.mixer.monitorOutputs = ["qa-output"];
    state.mixer.monitorMuted = false;
    state.mixer.channels[0].levels.monitor2 = .42;
    daemon.receive(state);
    assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, true);
    host.receive({event:"titleParametersDidChange",context:"feed-key",payload:{title:""}});
    assert.ok(keyImage("feed-key").includes("Studio"));
    assert.equal(keyFace("feed-key"), "off");
    dialTarget("all-sends-dial", "send:system:all");
    assert.equal(feedbackOf("all-sends-dial").value, "42%");
    for (const feed of ["gone", "  monitor2 + removed  ", ""]) {
      state.mixer.monitorFeeds["qa-output"] = feed;
      daemon.receive(state);
      assert.equal(feedbackOf("monitor-dial").muteOverlay.enabled, true);
      host.receive({event:"titleParametersDidChange",context:"feed-key",payload:{title:""}});
      assert.ok(keyImage("feed-key").includes("Studio"));
      assert.equal(keyFace("feed-key"), "off");
    }
  }
  finally {
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
    intervals.forEach(clearInterval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
  }
});
