// node --test test/   (Node 18+; developed with portable Node 24.21.0)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  FRAME_HEADER_SIZE,
  PixelFormat,
  parseFramePacket,
  encodeFramePacket,
  LatestFrameSlot,
  nextTimestamp,
  createFramePump,
  ArrivalRate,
} from '../src/textureSource.js';

// Writes a header byte by byte (independently of encodeFramePacket) so the offsets themselves are under test.
function handMadePacket({ width, height, format, frameId, timestampMs, pixels, magic = 'TDPF', version = 1, extraBytes = 0 }) {
  const buffer = new ArrayBuffer(24 + width * height * 4 + extraBytes);
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < 4; i++) bytes[i] = magic.charCodeAt(i);
  bytes[4] = version;
  bytes[5] = format;
  bytes[6] = width & 0xff;
  bytes[7] = (width >> 8) & 0xff;
  bytes[8] = height & 0xff;
  bytes[9] = (height >> 8) & 0xff;
  bytes[10] = 0;
  bytes[11] = 0;
  bytes[12] = frameId & 0xff;
  bytes[13] = (frameId >>> 8) & 0xff;
  bytes[14] = (frameId >>> 16) & 0xff;
  bytes[15] = (frameId >>> 24) & 0xff;
  new DataView(buffer).setFloat64(16, timestampMs, true);
  if (pixels) bytes.set(pixels.slice(0, Math.min(pixels.length, buffer.byteLength - 24)), 24);
  return buffer;
}

test('header: every field at its offset, little-endian', () => {
  const width = 0x0103; // 259: low and high byte differ, so byte order matters
  const height = 2;
  const pixels = new Uint8Array(width * height * 4).map((_, i) => i % 251);
  const buffer = handMadePacket({ width, height, format: PixelFormat.RGBA8, frameId: 0xdeadbeef, timestampMs: 123456.789, pixels });
  assert.equal(FRAME_HEADER_SIZE, 24);
  const frame = parseFramePacket(buffer);
  assert.equal(frame.width, 259);
  assert.equal(frame.height, 2);
  assert.equal(frame.format, PixelFormat.RGBA8);
  assert.equal(frame.frameId, 0xdeadbeef);
  assert.equal(frame.timestampMs, 123456.789);
  assert.equal(frame.pixels.length, width * height * 4);
  assert.equal(frame.pixels.byteOffset, 24);
  assert.equal(frame.pixels.buffer, buffer, 'pixels are a view into the packet, not a copy');
  assert.deepEqual(Array.from(frame.pixels.subarray(0, 8)), [0, 1, 2, 3, 4, 5, 6, 7]);
});

test('header: rows stay top-down (first bytes are the first row)', () => {
  const width = 2, height = 2;
  const pixels = new Uint8Array([
    255, 0, 0, 255, 255, 0, 0, 255, // top row red
    0, 0, 255, 255, 0, 0, 255, 255, // bottom row blue
  ]);
  const frame = parseFramePacket(encodeFramePacket({ frameId: 1, timestampMs: 0, width, height, pixels }));
  assert.deepEqual(Array.from(frame.pixels.subarray(0, 4)), [255, 0, 0, 255]);
  assert.deepEqual(Array.from(frame.pixels.subarray(12, 16)), [0, 0, 255, 255]);
});

test('BGRA8 packets are swapped to RGBA in place', () => {
  const bgra = new Uint8Array([10, 20, 30, 40, 1, 2, 3, 4]);
  const buffer = handMadePacket({ width: 2, height: 1, format: PixelFormat.BGRA8, frameId: 7, timestampMs: 1, pixels: bgra });
  const frame = parseFramePacket(buffer);
  assert.equal(frame.format, PixelFormat.BGRA8);
  assert.deepEqual(Array.from(frame.pixels), [30, 20, 10, 40, 3, 2, 1, 4]);
});

test('encode then parse round-trips every field', () => {
  const pixels = new Uint8Array(3 * 5 * 4).map((_, i) => (i * 7) % 256);
  const frame = parseFramePacket(encodeFramePacket({ frameId: 42, timestampMs: 98765.4321, width: 3, height: 5, format: PixelFormat.RGBA8, pixels }));
  assert.equal(frame.frameId, 42);
  assert.equal(frame.timestampMs, 98765.4321);
  assert.equal(frame.width, 3);
  assert.equal(frame.height, 5);
  assert.deepEqual(Array.from(frame.pixels), Array.from(pixels));
});

test('size mismatches are rejected', () => {
  const base = { width: 4, height: 3, format: PixelFormat.RGBA8, frameId: 1, timestampMs: 0 };
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, extraBytes: 1 })), RangeError);
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, extraBytes: -1 })), RangeError);
  assert.throws(() => parseFramePacket(new ArrayBuffer(23)), RangeError);
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, width: 0 })), RangeError);
});

test('bad magic, version, pixel format and input type are rejected', () => {
  const base = { width: 1, height: 1, format: PixelFormat.RGBA8, frameId: 1, timestampMs: 0 };
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, magic: 'TDPX' })), /magic/);
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, version: 2 })), /version/);
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, format: 0 })), /pixel format/);
  assert.throws(() => parseFramePacket(handMadePacket({ ...base, format: 3 })), /pixel format/);
  assert.throws(() => parseFramePacket('TDPF'), TypeError);
  assert.throws(() => parseFramePacket(new Uint8Array(28)), TypeError);
});

test('LatestFrameSlot keeps only the newest frame and counts drops', () => {
  const slot = new LatestFrameSlot();
  assert.equal(slot.take(), null);
  slot.put({ frameId: 1 });
  slot.put({ frameId: 2 });
  slot.put({ frameId: 3 });
  assert.equal(slot.pending, true);
  assert.equal(slot.take().frameId, 3);
  assert.equal(slot.take(), null);
  assert.equal(slot.pending, false);
  slot.put({ frameId: 4 });
  assert.equal(slot.take().frameId, 4);
  assert.equal(slot.received, 4);
  assert.equal(slot.dropped, 2);
});

test('nextTimestamp is strictly increasing for equal, decreasing and increasing clocks', () => {
  assert.equal(nextTimestamp(-Infinity, 5), 5);
  assert.equal(nextTimestamp(5, 6.5), 6.5);
  assert.equal(nextTimestamp(6.5, 6.5), 7.5);
  assert.equal(nextTimestamp(7.5, 3), 8.5);
  const clock = [0, 0, 0, 16.6, 16.6, 10, 33.3, 33.3, 50, 49.999, 1e9, 5];
  let last = -Infinity;
  for (const now of clock) {
    const t = nextTimestamp(last, now);
    assert.ok(t > last, `timestamp ${t} must exceed ${last}`);
    last = t;
  }
});

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

test('frame pump: detection never overlaps and frames arriving meanwhile collapse to the newest', async () => {
  const detected = [];
  let active = 0;
  let maxActive = 0;
  const pump = createFramePump(async (frame) => {
    active++;
    maxActive = Math.max(maxActive, active);
    detected.push(frame.frameId);
    await sleep(30);
    active--;
  });

  pump.offer({ frameId: 1 });
  assert.equal(pump.busy, true);
  pump.offer({ frameId: 2 }); // before the pump took frame 1: 1 is dropped
  await sleep(10); // pump is now inside detect(2)
  pump.offer({ frameId: 3 });
  pump.offer({ frameId: 4 });
  pump.offer({ frameId: 5 }); // 3 and 4 are dropped while 2 is being detected
  await pump.idle;
  assert.deepEqual(detected, [2, 5]);
  assert.equal(maxActive, 1);
  assert.equal(pump.slot.dropped, 3);
  assert.equal(pump.stats.detected, 2);
  assert.equal(pump.busy, false);

  pump.offer({ frameId: 6 }); // an idle pump starts again on the next arrival
  await pump.idle;
  assert.deepEqual(detected, [2, 5, 6]);
});

test('frame pump: a failing detection is counted and the loop keeps going', async () => {
  const errors = [];
  const seen = [];
  const pump = createFramePump(async (frame) => {
    seen.push(frame.frameId);
    if (frame.frameId === 1) throw new Error('boom');
  }, { onError: (error, frame) => errors.push([error.message, frame.frameId]) });
  pump.offer({ frameId: 1 });
  await pump.idle;
  pump.offer({ frameId: 2 });
  await pump.idle;
  assert.deepEqual(seen, [1, 2]);
  assert.deepEqual(errors, [['boom', 1]]);
  assert.equal(pump.stats.failed, 1);
  assert.equal(pump.stats.detected, 1);
});

test('frame pump: frames are dropped, not detected, while the source is inactive', async () => {
  let active = false;
  const seen = [];
  const pump = createFramePump(async (frame) => seen.push(frame.frameId), { isActive: () => active });
  pump.offer({ frameId: 1 });
  await pump.idle;
  active = true;
  pump.offer({ frameId: 2 });
  await pump.idle;
  assert.deepEqual(seen, [2]);
  assert.equal(pump.slot.dropped, 1);
});

test('frame pump + nextTimestamp: timestamps handed to detection are strictly increasing', async () => {
  let last = -Infinity;
  const stamps = [];
  const fakeClock = [100, 100, 99, 116.7, 116.7, 150];
  let tick = 0;
  const pump = createFramePump(async () => {
    last = nextTimestamp(last, fakeClock[tick++ % fakeClock.length]);
    stamps.push(last);
    await sleep(1);
  });
  for (let i = 0; i < fakeClock.length; i++) {
    pump.offer({ frameId: i });
    await pump.idle;
  }
  assert.equal(stamps.length, fakeClock.length);
  for (let i = 1; i < stamps.length; i++) assert.ok(stamps[i] > stamps[i - 1], `${stamps[i]} > ${stamps[i - 1]}`);
});

test('ArrivalRate reports whole frames per second', () => {
  const rate = new ArrivalRate();
  assert.equal(rate.fps, 0);
  for (let i = 0; i < 120; i++) rate.tick(i * (1000 / 60));
  assert.equal(rate.fps, 60);
});
