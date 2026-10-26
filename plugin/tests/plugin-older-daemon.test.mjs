import assert from "node:assert/strict";
import test from "node:test";
import {controlFixture} from "./control-fixture.mjs";

// After an upgrade the daemon from the previous release keeps running and
// serves no control socket. The adapter says so on its first line; keys and
// dials then read "restart" instead of "offline" until a connection opens.
test("a daemon from before the update shows restart on keys and dials until the adapter connects", async t => {
  t.mock.timers.enable({apis: ["setTimeout"]});
  const oldSkin = process.env.OPENXLR_SKIN;
  process.env.OPENXLR_SKIN = "default";
  const previous = globalThis.WebSocket;
  const previousInterval = globalThis.setInterval;
  const previousError = console.error;
  const intervals = [];
  const logged = [];
  globalThis.setInterval = (...args) => {
    const interval = previousInterval(...args);
    intervals.push(interval);
    return interval;
  };
  console.error = (...args) => logged.push(args.join(" "));
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
  const control = controlFixture();
  try {
    await import("../com.emaspa.openxlr.sdPlugin/plugin.mjs");
    const host = sockets[0];
    const message = "An OpenXLR daemon from before the update is still running and cannot accept this client. "
      + "Restart it: systemctl --user restart openxlr-daemon";
    const first = control.children[0];
    first.receive({type: "controlError", reason: "olderDaemon", message});
    assert.ok(first.killed);
    assert.ok(first.stdin.writableEnded);
    assert.deepEqual(logged.filter(line => line.includes("before the update")), ["OpenXLR: " + message]);

    host.receive({event: "willAppear", context: "dial", action: "com.emaspa.openxlr.dial", payload: {settings: {target: "gain"}}});
    const face = () => host.messages.filter(m => m.event === "setFeedback" && m.context === "dial" && m.payload.title).at(-1).payload;
    assert.equal(face().value, "restart");
    host.receive({event: "willAppear", context: "key", action: "com.emaspa.openxlr.toggle", payload: {settings: {target: "mute"}}});
    host.receive({event: "titleParametersDidChange", context: "key", payload: {title: ""}});
    const key = () => svg(host.messages.filter(m => m.event === "setImage" && m.context === "key").at(-1).payload.image);
    assert.ok(key().includes(">restart</text>"));

    // The next attempt reports the same thing once more without a second log line.
    t.mock.timers.tick(500);
    const second = control.children[1];
    second.receive({type: "controlError", reason: "olderDaemon", message});
    assert.equal(logged.filter(line => line.includes("before the update")).length, 1);

    // Once restarted, the adapter connects and the notice is gone.
    t.mock.timers.tick(1000);
    const third = control.children[2];
    third.onopen();
    third.receive({type: "state", connected: true, profiles: [], capabilities: {gain: true, mute: true, xlrInputs: 1, gainMaxDb: 80},
      state: {gainDb: 40, mute: false}, devices: [],
      mixer: {channels: [], mixes: [], monitorOutputs: [], monitorFeeds: {}, inserts: {}, lowCutHz: 0}});
    assert.equal(face().value, "40");
    assert.ok(!key().includes(">restart</text>"));

    // A daemon that is simply not running keeps the usual offline word.
    third.emit("close");
    t.mock.timers.tick(500);
    control.children[3].receive({type: "controlError", reason: "notRunning", message: "The OpenXLR daemon is not running."});
    assert.equal(face().value, "offline");
  } finally {
    control.restore();
    for (const interval of intervals) clearInterval(interval);
    globalThis.setInterval = previousInterval;
    globalThis.WebSocket = previous;
    console.error = previousError;
    if (oldSkin === undefined) delete process.env.OPENXLR_SKIN;
    else process.env.OPENXLR_SKIN = oldSkin;
  }
});
