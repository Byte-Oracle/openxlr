import {spawn} from "node:child_process";

// Node's browser-compatible WebSocket cannot dial a Unix socket. The
// installed daemon binary supplies a client-only, bounded JSON-line adapter.
export class ControlSocket {
  readyState = 0;
  #child;
  #chunks = [];
  #bytes = 0;
  #openingDeadline;

  constructor() {
    this.#openingDeadline = setTimeout(() => this.close(), 15000);
    this.#openingDeadline.unref?.();
    this.#start("openxlr-daemon", ["--control-stdio"], true);
  }

  #start(command, args, fallback = false) {
    const child = spawn(command, args, {stdio: ["pipe", "pipe", "ignore"]});
    this.#child = child;
    child.on("error", error => {
      if (this.#child !== child || this.readyState === 3) return;
      if (fallback && error.code === "ENOENT")
        this.#start("flatpak", ["run", "--command=/app/lib/openxlr/daemon/OpenXLR.Daemon", "io.github.emaspa.openxlr", "--control-stdio"]);
      else this.close();
    });
    child.on("close", () => { if (this.#child === child) this.close(); });
    child.stdin.on("error", () => { if (this.#child === child) this.close(); });
    child.stdout.on("data", chunk => { if (this.#child === child) this.#receive(chunk); });
  }

  #receive(chunk) {
    if (this.readyState === 3) return;
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
        try { if (JSON.parse(text).type !== "controlReady") { this.close(); return; } }
        catch { this.close(); return; }
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
    this.#child.kill();
    this.onclose?.();
  }
}
