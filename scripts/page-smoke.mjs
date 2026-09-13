// Headless browser smoke test of the MediaPipe page in Source = texture mode, without vvvv.
//
// Serves a web folder over HTTP, plays the vvvv side of the WebSocket (sends Source/Detecthands config, then
// TDPF frame packets of a raw RGBA image as fast as the socket drains, like FramePacket + SendData do), drives a
// headless Chromium over the DevTools protocol, and records what comes back: handResults with frameId,
// timers with sourceTimestamp (latency on this process's performance.now() clock, the role vvvv's clock plays in
// the VL harness), console errors, and the page's textureInputStats() (received / dropped / detected).
//
// usage: node scripts/page-smoke.mjs --web <dir> --image <raw RGBA file> --width <w> --height <h>
//          [--frames 150] [--fps 60] [--seconds 90] [--browser <msedge.exe>] [--profile <dir>]
//          [--http-port 18480] [--cdp-port 19480] --out <result.json>
//   --fps 0 sends a new frame whenever the socket has drained (stress test of the latest-frame slot).
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { spawn, execFileSync } from "node:child_process";
import { performance } from "node:perf_hooks";
import { encodeFramePacket, PixelFormat } from "../mediapipe-touchdesigner/src/textureSource.js";

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, a, i, all) => {
  if (a.startsWith("--")) pairs.push([a.slice(2), all[i + 1]]);
  return pairs;
}, []));
const webDir = path.resolve(args.web);
const width = Number(args.width), height = Number(args.height);
const targetFrames = Number(args.frames ?? 150);
const fps = Number(args.fps ?? 60);
const seconds = Number(args.seconds ?? 90);
const browserExe = args.browser ?? "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const profileDir = path.resolve(args.profile ?? path.join(path.dirname(args.out), "browser-profile"));
const httpPort = Number(args["http-port"] ?? 18480), cdpPort = Number(args["cdp-port"] ?? 19480);
const pixels = new Uint8Array(fs.readFileSync(args.image));
if (pixels.length !== width * height * 4) throw new Error(`${args.image}: ${pixels.length} bytes, expected ${width * height * 4}`);

const types = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".cjs": "text/javascript", ".css": "text/css",
  ".wasm": "application/wasm", ".json": "application/json", ".svg": "image/svg+xml", ".map": "application/json" };
const t0 = performance.now();
const stamp = () => Math.round(performance.now() - t0);
const log = { config: { webDir, width, height, targetFrames, fps, seconds }, served: 0, missing: [], console: [], exceptions: [],
  sent: 0, skippedBusySocket: 0, textTypes: {}, handResults: [], timers: [], firstHand: null, events: [] };

// ---- HTTP + minimal RFC 6455 server -------------------------------------------------------------------------
function wsFrame(opcode, payload) {
  const len = payload.length;
  const head = len < 126 ? Buffer.from([0x80 | opcode, len])
    : len < 65536 ? Buffer.from([0x80 | opcode, 126, len >> 8, len & 255])
    : (() => { const b = Buffer.alloc(10); b[0] = 0x80 | opcode; b[1] = 127; b.writeBigUInt64BE(BigInt(len), 2); return b; })();
  return Buffer.concat([head, payload]);
}

let client = null;
const server = http.createServer((req, res) => {
  const u = new URL(req.url, "http://x");
  const p = path.join(webDir, decodeURIComponent(u.pathname));
  if (!p.startsWith(webDir) || !fs.existsSync(p) || fs.statSync(p).isDirectory()) { log.missing.push(u.pathname); res.writeHead(404); res.end(); return; }
  log.served++;
  res.writeHead(200, { "Content-Type": types[path.extname(p)] || "application/octet-stream" });
  fs.createReadStream(p).pipe(res);
});
server.on("upgrade", (req, sock) => {
  const accept = crypto.createHash("sha1").update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
  sock.write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
  sock.setNoDelay(true);
  client = sock;
  log.events.push({ t: stamp(), event: "ws open" });
  const sendText = (s) => sock.write(wsFrame(1, Buffer.from(s, "utf8")));
  // What VL.MediaPipe's senders put on a new connection in texture mode with only hands enabled.
  sendText('{ "Source": "texture" }');
  for (const [key, on] of [["Detectfacelandmarks", 0], ["Detectfaces", 0], ["Detectgestures", 0], ["Detecthands", 1], ["Detectposes", 0],
    ["Detectobjects", 0], ["Detectimages", 0], ["Detectsegments", 0], ["Detectimageembeddings", 0], ["Showoverlays", 1]]) {
    sendText(`{"${key}": ${on}}`);
  }
  let buf = Buffer.alloc(0);
  sock.on("data", (d) => {
    buf = Buffer.concat([buf, d]);
    while (buf.length >= 2) {
      const op = buf[0] & 15; let len = buf[1] & 127; let off = 2;
      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      const masked = (buf[1] & 128) !== 0; const mask = masked ? buf.subarray(off, off + 4) : null; if (masked) off += 4;
      if (buf.length < off + len) return;
      const payload = Buffer.from(buf.subarray(off, off + len)); buf = buf.subarray(off + len);
      if (mask) for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i % 4];
      if (op === 1) onText(payload.toString("utf8"));
      if (op === 8) { log.events.push({ t: stamp(), event: "ws close" }); sock.end(); client = null; return; }
    }
  });
  sock.on("error", () => {});
  sock.on("close", () => { client = null; });
});

function onText(text) {
  const receivedAt = performance.now();
  if (text === "pong" || text === "ping") { log.textTypes[text] = (log.textTypes[text] ?? 0) + 1; return; }
  let msg;
  try { msg = JSON.parse(text); } catch { log.textTypes["<not json>"] = (log.textTypes["<not json>"] ?? 0) + 1; return; }
  const key = Object.keys(msg)[0];
  log.textTypes[key] = (log.textTypes[key] ?? 0) + 1;
  if (key === "type") log.events.push({ t: stamp(), event: "webcamDevices", text: text.slice(0, 200) });
  if (key === "handResults") {
    const r = msg.handResults;
    const hands = r.landmarks?.length ?? 0;
    const entry = { frameId: msg.frameId, innerFrameId: r.frameId, hands, landmarks0: hands ? r.landmarks[0].length : 0,
      latencyMs: receivedAt - msg.sourceTimestamp, resolution: msg.resolution };
    log.handResults.push(entry);
    if (hands && !log.firstHand) log.firstHand = { frameId: msg.frameId, landmarks: r.landmarks[0], handedness: r.handedness?.[0] ?? r.handednesses?.[0] };
  }
  if (key === "timers") {
    const t = msg.timers;
    if (typeof t.frameId === "number" && t.frameId > lastEchoed) lastEchoed = t.frameId;
    log.timers.push({ frameId: t.frameId, detectTime: t.detectTime, drawTime: t.drawTime, sourceFrameRate: t.sourceFrameRate,
      latencyMs: receivedAt - t.sourceTimestamp, hasIngestTime: typeof t.ingestTime === "number" });
  }
}
await new Promise((r) => server.listen(httpPort, "127.0.0.1", r));

// ---- frame sender: like FramePacket, one packet when the socket has drained and (with --ack 1) the page has
// echoed the previous packet's frame id in timers, or --ack-timeout ms have passed ----------------------------
const ack = (args.ack ?? "1") !== "0";
const ackTimeoutMs = Number(args["ack-timeout"] ?? 1000);
log.config.ack = ack;
log.config.ackTimeoutMs = ackTimeoutMs;
log.skippedAwaitingAck = 0;
log.ackTimeouts = 0;
let frameId = 0;
let lastSend = 0;
let inFlight = -1;
let inFlightSince = 0;
let lastEchoed = -1;
const sender = setInterval(() => {
  if (!client) return;
  const now = performance.now();
  if (fps > 0 && now - lastSend < 1000 / fps - 0.5) return;
  if (ack && inFlight >= 0) {
    if (lastEchoed >= inFlight) inFlight = -1;
    else if (now - inFlightSince < ackTimeoutMs) { log.skippedAwaitingAck++; return; }
    else { log.ackTimeouts++; inFlight = -1; }
  }
  if (client.writableLength > 0) { log.skippedBusySocket++; return; }
  const packet = encodeFramePacket({ frameId, timestampMs: now, width, height, format: PixelFormat.RGBA8, pixels });
  client.write(wsFrame(2, Buffer.from(packet)));
  inFlight = frameId;
  inFlightSince = now;
  frameId++;
  log.sent++;
  lastSend = now;
}, 1);

// ---- browser ------------------------------------------------------------------------------------------------
fs.rmSync(profileDir, { recursive: true, force: true });
const browser = spawn(browserExe, ["--headless=new", `--remote-debugging-port=${cdpPort}`, `--user-data-dir=${profileDir}`,
  "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--autoplay-policy=no-user-gesture-required", "about:blank"], { stdio: "ignore" });
log.browserPid = browser.pid;
let target = null;
for (let i = 0; i < 60 && !target; i++) {
  try { target = (await (await fetch(`http://127.0.0.1:${cdpPort}/json/list`)).json()).find((t) => t.type === "page"); } catch { }
  if (!target) await new Promise((r) => setTimeout(r, 500));
}
if (!target) throw new Error("no CDP page target");
const cdp = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => cdp.addEventListener("open", r, { once: true }));
let nextId = 1; const pending = new Map();
const send = (method, params = {}) => new Promise((resolve) => { const id = nextId++; pending.set(id, resolve); cdp.send(JSON.stringify({ id, method, params })); });
cdp.addEventListener("message", (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
  if (m.method === "Runtime.consoleAPICalled" && (m.params.type === "error" || m.params.type === "warning" || log.console.length < 80))
    log.console.push({ t: stamp(), type: m.params.type, text: m.params.args.map((a) => a.value ?? a.description ?? a.type).join(" ").slice(0, 300) });
  if (m.method === "Runtime.exceptionThrown")
    log.exceptions.push({ t: stamp(), text: (m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text).slice(0, 400) });
});
await send("Runtime.enable");
await send("Page.enable");
const url = `http://127.0.0.1:${httpPort}/index.html?Wsaddress=${encodeURIComponent("ws://127.0.0.1")}&Wsport=${httpPort}`;
log.url = url;
await send("Page.navigate", { url });

const deadline = Date.now() + seconds * 1000;
const detected = () => log.handResults.filter((h) => h.hands > 0).length;
while (Date.now() < deadline && detected() < targetFrames) await new Promise((r) => setTimeout(r, 200));
clearInterval(sender);
await new Promise((r) => setTimeout(r, 500));
const stats = await send("Runtime.evaluate", { returnByValue: true, expression: "window.textureInputStats ? window.textureInputStats() : null" });
log.pageStats = stats?.result?.value ?? null;
const gl = await send("Runtime.evaluate", { returnByValue: true, expression: `(() => { const c = document.createElement('canvas'); const g = c.getContext('webgl2'); const d = g && g.getExtension('WEBGL_debug_renderer_info'); return g ? g.getParameter(d ? d.UNMASKED_RENDERER_WEBGL : g.RENDERER) : null; })()` });
log.webgl = gl?.result?.value ?? null;
cdp.close();
try { execFileSync("taskkill", ["/PID", String(browser.pid), "/T", "/F"], { stdio: "ignore" }); log.browserKilled = true; } catch { log.browserKilled = false; }
server.close();

// ---- summary --------------------------------------------------------------------------------------------------
const quantile = (values, q) => { if (!values.length) return null; const s = [...values].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(q * (s.length - 1) + 0.5))]; };
const withHands = log.handResults.filter((h) => h.hands > 0);
const ids = log.timers.map((t) => t.frameId).filter((id) => typeof id === "number");
const latencies = withHands.map((h) => h.latencyMs);
const detectTimes = log.timers.filter((t) => t.detectTime > 0).map((t) => t.detectTime);
const summary = {
  url, served: log.served, missing: log.missing, ack, sent: log.sent, skippedAwaitingAck: log.skippedAwaitingAck, ackTimeouts: log.ackTimeouts,
  skippedBusySocket: log.skippedBusySocket, textTypes: log.textTypes,
  handResultsWithHand: withHands.length, landmarkCounts: [...new Set(withHands.map((h) => h.landmarks0))],
  frameIdsEqualInnerAndRoot: withHands.every((h) => h.frameId === h.innerFrameId),
  timersFrameIdsStrictlyIncreasing: ids.every((id, i) => i === 0 || id > ids[i - 1]),
  timersAllHaveIngestTime: log.timers.every((t) => t.hasIngestTime),
  latencyMs: { n: latencies.length, median: quantile(latencies, 0.5), p90: quantile(latencies, 0.9), max: latencies.length ? Math.max(...latencies) : null },
  detectTimeMs: { median: quantile(detectTimes, 0.5), p90: quantile(detectTimes, 0.9) },
  pageStats: log.pageStats, webgl: log.webgl, firstHandFrameId: log.firstHand?.frameId,
  firstHandWrist: log.firstHand?.landmarks?.[0], firstHandIndexTip: log.firstHand?.landmarks?.[8], handedness: log.firstHand?.handedness,
  exceptions: log.exceptions, consoleErrors: log.console.filter((c) => c.type === "error"), browserKilled: log.browserKilled, finishedAfterMs: stamp(),
};
log.summary = summary;
fs.mkdirSync(path.dirname(path.resolve(args.out)), { recursive: true });
fs.writeFileSync(args.out, JSON.stringify(log, null, 1));
console.log(JSON.stringify(summary, null, 2));
const ok = withHands.length >= Math.min(targetFrames, 1) && summary.landmarkCounts.every((n) => n === 21) && summary.frameIdsEqualInnerAndRoot
  && summary.timersFrameIdsStrictlyIncreasing && log.exceptions.length === 0;
process.exit(ok ? 0 : 1);
