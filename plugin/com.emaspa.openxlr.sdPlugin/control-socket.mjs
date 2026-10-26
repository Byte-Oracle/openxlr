import {spawn} from "node:child_process";
import {createConnection} from "node:net";

// The loopback port of the releases before the control socket.
const LEGACY_PORT = 37890;
const OLDER_OPENXLR = "This OpenDeck plugin needs a newer OpenXLR: the installed one is from before "
  + "the control socket. Update OpenXLR to the same release as the plugin.";

// Whether a daemon answers on the old loopback port. A bare connect, nothing sent.
export function legacyPortAnswers(port = LEGACY_PORT) {
  return new Promise(resolve => {
    const socket = createConnection({host: "127.0.0.1", port});
    const done = answered => { socket.destroy(); resolve(answered); };
    socket.setTimeout(500, () => done(false));
    socket.once("connect", () => done(true));
    socket.once("error", () => done(false));
  });
}

// Node's browser-compatible WebSocket cannot dial a Unix socket. The
// dedicated launcher supplies a client-only, bounded JSON-line adapter.
export class ControlSocket {
  // Replaced in tests, which must not reach a real daemon.
  static legacyPortAnswers = legacyPortAnswers;
  readyState = 0;
  // Why the adapter could not connect, from its controlError line:
  // {reason, message}, or null. Set before onclose runs. When no launcher
  // started at all and the old port answers, the reason is olderOpenXLR.
  error = null;
  #heard = false;
  #child;
  #chunks = [];
  #bytes = 0;
  #openingDeadline;

  constructor() {
    this.#openingDeadline = setTimeout(() => this.close(), 15000);
    this.#openingDeadline.unref?.();
    this.#start("openxlr-control", [], true);
  }

  #start(command, args, fallback = false) {
    const child = spawn(command, args, {stdio: ["pipe", "pipe", "ignore"]});
    this.#child = child;
    child.on("error", error => {
      if (this.#child !== child || this.readyState === 3) return;
      if (fallback && error.code === "ENOENT")
        this.#start("flatpak", ["run", "--command=/app/bin/openxlr-control", "io.github.emaspa.openxlr"]);
      else if (error.code === "ENOENT") this.#noLauncher();
      else this.close();
    });
    child.on("close", () => {
      if (this.#child !== child) return;
      // Every adapter writes a ready or error line first. Silence means the
      // launcher itself failed, as flatpak run does for an absent app.
      if (this.readyState === 0 && !this.#heard) this.#noLauncher();
      else this.close();
    });
    child.stdin.on("error", () => { if (this.#child === child) this.close(); });
    child.stdout.on("data", chunk => { if (this.#child === child) this.#receive(chunk); });
  }

  // No adapter ran. An installation from before the control socket has no
  // launcher but its daemon still answers on the old port: say that OpenXLR
  // needs updating rather than showing the daemon as offline.
  async #noLauncher() {
    if (this.readyState === 3) return;
    let older = false;
    try { older = await ControlSocket.legacyPortAnswers(); } catch { /* treat as offline */ }
    if (this.readyState === 3) return;
    if (older) this.error = {reason: "olderOpenXLR", message: OLDER_OPENXLR};
    this.close();
  }

  #receive(chunk) {
    if (this.readyState === 3) return;
    this.#heard = true;
    let start = 0;
    while (start < chunk.length) {
      const end = chunk.indexOf(10, start);
      const part = chunk.subarray(start, end < 0 ? chunk.length : end);
      this.#bytes += part.length;
      if (this.#bytes > 8 * 1024 * 1024) { this.close(); return; }
      if (this.#chunks.length >= 8192) { this.close(); return; }
      this.#chunks.push(part);
      if (end < 0) return;
      const text = Buffer.concat(this.#chunks, this.#bytes).toString("utf8");
      this.#chunks = []; this.#bytes = 0;
      if (this.readyState === 0) {
        let first;
        try { first = JSON.parse(text); } catch { this.close(); return; }
        if (first?.type === "controlError") {
          this.error = {reason: String(first.reason ?? ""), message: String(first.message ?? "")};
          this.close(); return;
        }
        if (first?.type !== "controlReady") { this.close(); return; }
        clearTimeout(this.#openingDeadline);
        this.readyState = 1;
        this.onopen?.();
      } else this.onmessage?.({data: text});
      if (this.readyState === 3) return;
      start = end + 1;
    }
  }

  send(text) {
    if (this.readyState !== 1) return false;
    if (typeof text !== "string" || Buffer.byteLength(text) > 64 * 1024 ||
        this.#child.stdin.writableLength + Buffer.byteLength(text) + 1 > 128 * 1024) {
      this.close(); return false;
    }
    this.#child.stdin.write(text + "\n");
    return true;
  }

  close() {
    if (this.readyState === 3) return;
    this.readyState = 3;
    clearTimeout(this.#openingDeadline);
    this.#chunks = []; this.#bytes = 0;
    // Under flatpak run a signal may stop at the launcher and never reach the
    // sandboxed adapter. End of input ends the adapter on its own.
    try { this.#child.stdin.end(); } catch { /* already closed */ }
    this.#child.kill();
    this.onclose?.();
  }
}
