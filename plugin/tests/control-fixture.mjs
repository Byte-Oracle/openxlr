import childProcess from "node:child_process";
import {syncBuiltinESMExports} from "node:module";
import {EventEmitter} from "node:events";
import {PassThrough, Writable} from "node:stream";

export function controlFixture() {
  const previous = childProcess.spawn;
  const children = [];
  childProcess.spawn = (file, args) => {
    if (!(file === "openxlr-daemon" && args.join(" ") === "--control-stdio") &&
        !(file === "flatpak" && args.join(" ") === "run --command=/app/lib/openxlr/daemon/OpenXLR.Daemon io.github.emaspa.openxlr --control-stdio"))
      throw new Error("Unexpected helper");
    const child = new EventEmitter();
    child.command = file; child.args = args;
    child.stdout = new PassThrough();
    child.messages = [];
    child.stdin = new Writable({write(chunk, encoding, done) {
      for (const line of chunk.toString().trim().split("\n")) child.messages.push(JSON.parse(line));
      done();
    }});
    child.kill = () => { child.killed = true; };
    child.onopen = () => child.receive({type: "controlReady"});
    child.receive = message => child.stdout.write(JSON.stringify(message) + "\n");
    children.push(child);
    return child;
  };
  syncBuiltinESMExports();
  return {children, restore() {
    childProcess.spawn = previous;
    syncBuiltinESMExports();
    for (const child of children) { child.stdout.destroy(); child.stdin.destroy(); }
  }};
}
