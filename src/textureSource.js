// Texture input (Source = texture): the pure parts, kept free of DOM and MediaPipe imports so that
// `node --test test/` can load this module directly.
//
// Wire format, one binary WebSocket message per frame, little-endian:
//   offset  0  magic "TDPF"
//           4  u8  version (1)
//           5  u8  pixel format (1 = RGBA8, 2 = BGRA8)
//           6  u16 width
//           8  u16 height
//          10  u16 reserved (0)
//          12  u32 frame id
//          16  f64 timestamp in ms (sender's clock, echoed back as sourceTimestamp)
//          24  width * height * 4 pixel bytes, rows top-down

export const FRAME_MAGIC = 'TDPF';
export const FRAME_VERSION = 1;
export const FRAME_HEADER_SIZE = 24;
export const PixelFormat = Object.freeze({ RGBA8: 1, BGRA8: 2 });

// Parses one frame packet. Returns { frameId, timestampMs, width, height, format, pixels } where pixels is
// an RGBA Uint8ClampedArray view into the packet (no copy; BGRA8 packets are swapped to RGBA in place).
// Throws on anything that does not match the format exactly.
export function parseFramePacket(buffer) {
  if (!(buffer instanceof ArrayBuffer)) {
    throw new TypeError('frame packet must be an ArrayBuffer');
  }
  if (buffer.byteLength < FRAME_HEADER_SIZE) {
    throw new RangeError(`frame packet has ${buffer.byteLength} bytes, less than the ${FRAME_HEADER_SIZE}-byte header`);
  }
  const view = new DataView(buffer);
  const magic = String.fromCharCode(view.getUint8(0), view.getUint8(1), view.getUint8(2), view.getUint8(3));
  if (magic !== FRAME_MAGIC) {
    throw new Error(`frame packet magic is "${magic}", expected "${FRAME_MAGIC}"`);
  }
  const version = view.getUint8(4);
  if (version !== FRAME_VERSION) {
    throw new Error(`frame packet version ${version} is not supported (expected ${FRAME_VERSION})`);
  }
  const format = view.getUint8(5);
  if (format !== PixelFormat.RGBA8 && format !== PixelFormat.BGRA8) {
    throw new Error(`frame packet pixel format ${format} is not supported (1 = RGBA8, 2 = BGRA8)`);
  }
  const width = view.getUint16(6, true);
  const height = view.getUint16(8, true);
  if (width === 0 || height === 0) {
    throw new RangeError(`frame packet size ${width}x${height} is empty`);
  }
  const frameId = view.getUint32(12, true);
  const timestampMs = view.getFloat64(16, true);
  const pixelBytes = width * height * 4;
  if (buffer.byteLength !== FRAME_HEADER_SIZE + pixelBytes) {
    throw new RangeError(`frame packet has ${buffer.byteLength} bytes, a ${width}x${height} frame needs ${FRAME_HEADER_SIZE + pixelBytes}`);
  }
  const pixels = new Uint8ClampedArray(buffer, FRAME_HEADER_SIZE, pixelBytes);
  if (format === PixelFormat.BGRA8) {
    swapRedBlue(pixels);
  }
  return { frameId, timestampMs, width, height, format, pixels };
}

// Builds a frame packet (the page never sends frames; tests and dev tools do).
export function encodeFramePacket({ frameId, timestampMs, width, height, format = PixelFormat.RGBA8, pixels }) {
  const pixelBytes = width * height * 4;
  if (!pixels || pixels.length !== pixelBytes) {
    throw new RangeError(`pixels must hold ${pixelBytes} bytes for a ${width}x${height} frame`);
  }
  const buffer = new ArrayBuffer(FRAME_HEADER_SIZE + pixelBytes);
  const view = new DataView(buffer);
  for (let i = 0; i < 4; i++) view.setUint8(i, FRAME_MAGIC.charCodeAt(i));
  view.setUint8(4, FRAME_VERSION);
  view.setUint8(5, format);
  view.setUint16(6, width, true);
  view.setUint16(8, height, true);
  view.setUint16(10, 0, true);
  view.setUint32(12, frameId >>> 0, true);
  view.setFloat64(16, timestampMs, true);
  new Uint8Array(buffer, FRAME_HEADER_SIZE, pixelBytes).set(pixels);
  return buffer;
}

export function swapRedBlue(pixels) {
  for (let i = 0; i < pixels.length; i += 4) {
    const b = pixels[i];
    pixels[i] = pixels[i + 2];
    pixels[i + 2] = b;
  }
}

// Holds at most one frame: a newer frame replaces an older one that was not taken yet (and counts as dropped).
// Frames are never queued, so detection always runs on the newest frame that arrived.
export class LatestFrameSlot {
  constructor() {
    this.frame = null;
    this.received = 0;
    this.dropped = 0;
  }

  put(frame) {
    if (this.frame !== null) this.dropped++;
    this.frame = frame;
    this.received++;
  }

  take() {
    const frame = this.frame;
    this.frame = null;
    return frame;
  }

  get pending() {
    return this.frame !== null;
  }
}

// detectForVideo and friends require strictly increasing timestamps per task graph.
export function nextTimestamp(lastTimestampMs, nowMs) {
  return nowMs > lastTimestampMs ? nowMs : lastTimestampMs + 1;
}

// Resolves after the event loop has run already-queued tasks (such as WebSocket messages that arrived during a
// long synchronous detection). A MessageChannel task, unlike setTimeout, is not throttled in hidden pages.
export function yieldToEventLoop() {
  return new Promise((resolve) => {
    const channel = new MessageChannel();
    channel.port1.onmessage = () => {
      channel.port1.close();
      resolve();
    };
    channel.port2.postMessage(null);
  });
}

// Arrival-driven detection loop: offer(frame) stores the frame in a LatestFrameSlot and starts the loop unless it
// is already running. The loop yields to the event loop before taking a frame, so frames that arrived meanwhile
// collapse to the newest one, and detect(frame) calls never overlap.
export function createFramePump(detect, { isActive = () => true, onError = () => {}, yieldFn = yieldToEventLoop } = {}) {
  const slot = new LatestFrameSlot();
  const stats = { detected: 0, failed: 0 };
  let busy = false;
  let running = Promise.resolve();

  async function run() {
    busy = true;
    try {
      for (;;) {
        await yieldFn();
        const frame = slot.take();
        if (frame === null) break;
        if (!isActive()) {
          slot.dropped++;
          continue;
        }
        try {
          await detect(frame);
          stats.detected++;
        } catch (error) {
          stats.failed++;
          onError(error, frame);
        }
      }
    } finally {
      busy = false;
    }
  }

  return {
    slot,
    stats,
    offer(frame) {
      slot.put(frame);
      if (!busy) running = run();
    },
    get busy() {
      return busy;
    },
    // Settles once the loop has no frame left (for tests and shutdown).
    get idle() {
      return running;
    },
  };
}

// Frames per second from arrival times (exponential moving average of the interval), rounded to an integer
// because the VL side reads timers.sourceFrameRate as an int.
export class ArrivalRate {
  constructor(smoothing = 0.1) {
    this.smoothing = smoothing;
    this.lastMs = undefined;
    this.intervalMs = undefined;
  }

  tick(nowMs) {
    if (this.lastMs !== undefined) {
      const interval = nowMs - this.lastMs;
      if (interval > 0) {
        this.intervalMs = this.intervalMs === undefined ? interval : this.intervalMs + this.smoothing * (interval - this.intervalMs);
      }
    }
    this.lastMs = nowMs;
  }

  get fps() {
    return this.intervalMs ? Math.round(1000 / this.intervalMs) : 0;
  }
}
