import test from "node:test";
import assert from "node:assert/strict";
import {existsSync, mkdtempSync, rmSync, writeFileSync} from "node:fs";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {controlFixture} from "./control-fixture.mjs";
import {createServer} from "node:net";
import {ControlSocket, legacyPortAnswers} from "../com.emaspa.openxlr.sdPlugin/control-socket.mjs";

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
    assert.equal(child.command, "openxlr-control");
    assert.deepEqual(child.args, []);
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
    assert.deepEqual(second.args, ["run", "--command=/app/bin/openxlr-control", "io.github.emaspa.openxlr"]);
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

test("an older native and Flatpak installation cannot launch a daemon through the adapter", {timeout: 3000}, async () => {
  const directory = mkdtempSync(join(tmpdir(), "openxlr-old-install-"));
  const previousPath = process.env.PATH;
  const previousMarker = process.env.OPENXLR_OLD_DAEMON_MARKER;
  const marker = join(directory, "daemon-started");
  let socket;
  try {
    writeFileSync(join(directory, "openxlr-daemon"), '#!/bin/sh\nprintf started > "$OPENXLR_OLD_DAEMON_MARKER"\n', {mode: 0o755});
    // An older Flatpak lacks /app/bin/openxlr-control. Never select its daemon.
    writeFileSync(join(directory, "flatpak"), '#!/bin/sh\n[ "$2" = "--command=/app/bin/openxlr-control" ] && exit 1\nprintf started > "$OPENXLR_OLD_DAEMON_MARKER"\n', {mode: 0o755});
    process.env.PATH = directory;
    process.env.OPENXLR_OLD_DAEMON_MARKER = marker;
    socket = new ControlSocket();
    await new Promise(resolve => { socket.onclose = resolve; });
    assert.equal(socket.readyState, 3);
    assert.equal(existsSync(marker), false);
  } finally {
    socket?.close();
    if (previousPath === undefined) delete process.env.PATH; else process.env.PATH = previousPath;
    if (previousMarker === undefined) delete process.env.OPENXLR_OLD_DAEMON_MARKER; else process.env.OPENXLR_OLD_DAEMON_MARKER = previousMarker;
    rmSync(directory, {recursive: true, force: true});
  }
});

test("an adapter error line closes with its reason instead of opening", () => {
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    let opens = 0, closes = 0, seen = null;
    socket.onopen = () => opens++;
    socket.onclose = () => { closes++; seen = socket.error; };
    const child = fixture.children[0];
    child.receive({type: "controlError", reason: "olderDaemon", message: "Restart it: systemctl --user restart openxlr-daemon"});
    assert.equal(socket.readyState, 3);
    assert.equal(opens, 0);
    assert.equal(closes, 1);
    assert.deepEqual(seen, {reason: "olderDaemon", message: "Restart it: systemctl --user restart openxlr-daemon"});
  } finally { fixture.restore(); }
});

test("close ends the adapter's input as well as signalling it", () => {
  const fixture = controlFixture();
  try {
    const socket = new ControlSocket();
    const child = fixture.children[0];
    child.onopen();
    socket.close();
    assert.ok(child.stdin.writableEnded);
    assert.ok(child.killed);
    assert.equal(socket.error, null);
  } finally { fixture.restore(); }
});

// A plugin newer than the installation finds no launcher. When the daemon
// still answers on the old loopback port, OpenXLR itself needs updating.
for (const [answers, reason] of [[true, "olderOpenXLR"], [false, undefined]])
  test(`no launcher with the old port ${answers ? "answering" : "silent"} reports ${reason ?? "offline"}`, async () => {
    const fixture = controlFixture();
    const probe = ControlSocket.legacyPortAnswers;
    ControlSocket.legacyPortAnswers = async () => answers;
    try {
      const socket = new ControlSocket();
      let closes = 0;
      socket.onclose = () => closes++;
      fixture.children[0].emit("error", {code: "ENOENT"});
      fixture.children[1].emit("error", {code: "ENOENT"});
      assert.equal(socket.readyState, 0);
      await new Promise(setImmediate);
      assert.equal(closes, 1);
      assert.equal(socket.error?.reason, reason);
      if (answers) assert.match(socket.error.message, /needs a newer OpenXLR/);
    } finally { ControlSocket.legacyPortAnswers = probe; fixture.restore(); }
  });

test("a flatpak launcher that exits without a line is a missing launcher, an adapter error line is not", async () => {
  const fixture = controlFixture();
  const probe = ControlSocket.legacyPortAnswers;
  let probes = 0;
  ControlSocket.legacyPortAnswers = async () => { probes++; return true; };
  try {
    const silent = new ControlSocket();
    fixture.children[0].emit("error", {code: "ENOENT"});
    fixture.children[1].emit("close");
    await new Promise(setImmediate);
    assert.equal(silent.error?.reason, "olderOpenXLR");
    const answered = new ControlSocket();
    fixture.children[2].receive({type: "controlError", reason: "notRunning", message: "The OpenXLR daemon is not running."});
    fixture.children[2].emit("close");
    await new Promise(setImmediate);
    assert.equal(answered.error.reason, "notRunning");
    assert.equal(probes, 1);
  } finally { ControlSocket.legacyPortAnswers = probe; fixture.restore(); }
});

test("the old port probe connects without sending and reports a closed port", async () => {
  const received = [];
  const server = createServer(connection => connection.on("data", data => received.push(data)));
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const {port} = server.address();
  try {
    assert.equal(await legacyPortAnswers(port), true);
  } finally { await new Promise(resolve => server.close(resolve)); }
  assert.equal(await legacyPortAnswers(port), false);
  assert.deepEqual(received, []);
});
