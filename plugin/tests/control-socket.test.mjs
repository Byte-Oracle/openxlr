import test from "node:test";
import assert from "node:assert/strict";
import {controlFixture} from "./control-fixture.mjs";
import {ControlSocket} from "../com.emaspa.openxlr.sdPlugin/control-socket.mjs";

test("a stalled adapter closes once and ignores a late ready line", t => {
  t.mock.timers.enable({apis: ["setTimeout"]});
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    let opens = 0, closes = 0;
    socket.onopen = () => opens++;
    socket.onclose = () => closes++;
    t.mock.timers.tick(15000);
    const child = fixture.children[0];
    child.onopen(); child.emit("close");
    assert.equal(socket.readyState, 3);
    assert.equal(closes, 1);
    assert.equal(opens, 0);
    assert.ok(child.killed);
    assert.equal(socket.send('{}'), false);
  } finally { fixture.restore(); }
});

test("a ready adapter cancels its startup deadline", t => {
  t.mock.timers.enable({apis: ["setTimeout"]});
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    fixture.children[0].onopen();
    t.mock.timers.tick(30000);
    assert.equal(socket.readyState, 1);
    assert.ok(!fixture.children[0].killed);
    socket.close();
  } finally { fixture.restore(); }
});

test("the adapter opens only on its ready line and preserves fragmented Unicode", () => {
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    const child = fixture.children[0];
    let opened = 0;
    const messages = [];
    socket.onopen = () => opened++;
    socket.onmessage = message => messages.push(JSON.parse(message.data));
    socket.send('{"cmd":"getState"}');
    assert.deepEqual(child.messages, []);
    child.stdout.write('{"type":"control');
    assert.equal(opened, 0);
    child.stdout.write('Ready"}\n');
    assert.equal(opened, 1);
    const frame = Buffer.from('{"type":"error","message":"Café"}\n');
    const split = frame.indexOf(0xc3) + 1;
    child.stdout.write(frame.subarray(0, split));
    child.stdout.write(frame.subarray(split));
    assert.deepEqual(messages, [{type: "error", message: "Café"}]);
    socket.send('{"cmd":"getState"}');
    assert.deepEqual(child.messages, [{cmd: "getState"}]);
    socket.close();
    assert.ok(child.killed);
  } finally { fixture.restore(); }
});

for (const invalid of ['{}\n', 'bad\n', 'x'.repeat(8 * 1024 * 1024 + 1)])
  test("invalid or oversized adapter output closes without publishing state: " + invalid.slice(0, 8), () => {
    const fixture = controlFixture();
    try {
      const socket = new ControlSocket();
      let closed = 0, messages = 0;
      socket.onclose = () => closed++;
      socket.onmessage = () => messages++;
      const child = fixture.children[0];
      child.stdout.write(invalid);
      child.emit("close");
      assert.equal(socket.readyState, 3);
      assert.equal(closed, 1);
      assert.equal(messages, 0);
      assert.ok(child.killed);
    } finally { fixture.restore(); }
  });

for (const overload of ["command", "backpressure"])
  test("the adapter bounds " + overload, () => {
    const fixture = controlFixture();
    try {
      const socket = new ControlSocket();
      const child = fixture.children[0];
      child.onopen();
      if (overload === "backpressure") Object.defineProperty(child.stdin, "writableLength", {value: 128 * 1024});
      assert.equal(socket.send(overload === "command" ? 'é'.repeat(32 * 1024 + 1) : '{}'), false);
      assert.equal(socket.readyState, 3);
      assert.deepEqual(child.messages, []);
    } finally { fixture.restore(); }
  });

test("many tiny output chunks cannot grow an unbounded fragment list", () => {
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    const child = fixture.children[0];
    child.onopen();
    for (let i = 0; i < 8193; i++) child.stdout.write('x');
    assert.equal(socket.readyState, 3);
    assert.ok(child.killed);
  } finally { fixture.restore(); }
});

test("a missing native adapter falls back to Flatpak without accepting late signals from its predecessor", () => {
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    let opens = 0, closes = 0;
    socket.onopen = () => opens++;
    socket.onclose = () => closes++;
    const first = fixture.children[0];
    first.emit("error", {code: "ENOENT"});
    const second = fixture.children[1];
    assert.equal(second.command, "flatpak");
    first.emit("close"); first.onopen();
    assert.equal(opens, 0); assert.equal(closes, 0);
    second.onopen();
    assert.equal(opens, 1);
    socket.close(); first.emit("error", {code: "ENOENT"});
    assert.equal(fixture.children.length, 2);
    assert.equal(closes, 1);
  } finally { fixture.restore(); }
});

test("a native adapter error other than ENOENT closes instead of launching a second installation", () => {
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    fixture.children[0].emit("error", {code: "EACCES"});
    assert.equal(socket.readyState, 3);
    assert.equal(fixture.children.length, 1);
  } finally { fixture.restore(); }
});
