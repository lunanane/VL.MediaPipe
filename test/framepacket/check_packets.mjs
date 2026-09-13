// Parses the packets FramePacketCheck (C#) wrote with the page's own parser and compares every field and pixel.
// usage: node test/framepacket/check_packets.mjs <dir with packets.json>
import fs from "node:fs";
import path from "node:path";
import { parseFramePacket } from "../../mediapipe-touchdesigner/src/textureSource.js";

const dir = path.resolve(process.argv[2] ?? "results/framepacket-check");
const { cases } = JSON.parse(fs.readFileSync(path.join(dir, "packets.json"), "utf8"));
let failures = 0;
const check = (ok, what) => { console.log(`${ok ? "ok  " : "FAIL"}  ${what}`); if (!ok) failures++; };

for (const c of cases) {
  const bytes = fs.readFileSync(path.join(dir, c.file));
  const buffer = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  let frame;
  try {
    frame = parseFramePacket(buffer);
  } catch (error) {
    check(false, `${c.name}: parse threw ${error.message}`);
    continue;
  }
  const expected = Buffer.from(c.expectedRgbaBase64, "base64");
  check(frame.width === c.width && frame.height === c.height, `${c.name}: size ${frame.width}x${frame.height}`);
  check(frame.frameId === c.frameId, `${c.name}: frame id ${frame.frameId}`);
  check(frame.timestampMs === c.timestampMs, `${c.name}: timestamp ${frame.timestampMs}`);
  check(Buffer.from(frame.pixels.buffer, frame.pixels.byteOffset, frame.pixels.length).equals(expected), `${c.name}: ${frame.pixels.length} RGBA bytes equal (${c.format} in)`);
}
console.log(failures === 0 ? `ALL ${cases.length} PACKETS MATCH` : `${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
