// Texture input (Source = texture): vvvv sends frames as binary WebSocket messages (format in textureSource.js)
// and the enabled tasks run on those frames instead of on the webcam. Camera mode never runs this code, except
// that receiveFramePacket ignores binary messages that arrive while Source is camera.
import { webcamState, socketState, overlayState, sourceState } from "./state.js";
import { segmenterState } from "./imageSegmentation.js";
import { objectState } from "./objectDetection.js";
import { faceDetectorState } from "./faceDetector.js";
import { parseFramePacket, createFramePump, nextTimestamp, ArrivalRate } from "./textureSource.js";

let page; // elements and model lists handed over by main.js

const texture = {
  canvas: undefined, // shows the received frame under the overlays
  context: undefined,
  width: 0,
  height: 0,
  lastTimestamp: -Infinity,
  rejected: 0,
  ignored: 0,
  arrival: new ArrivalRate(),
};

const pump = createFramePump(detectFrame, {
  isActive: () => sourceState.mode === 'texture',
  onError: (error, frame) => console.error(`Texture input: detection failed on frame ${frame.frameId}`, error),
});

export function setupTextureInput(elements) {
  page = elements;
  sourceState.onChange = onSourceChange;
  // For the DevTools console and headless tests.
  window.textureInputStats = () => ({
    source: sourceState.mode,
    received: pump.slot.received,
    dropped: pump.slot.dropped,
    detected: pump.stats.detected,
    failed: pump.stats.failed,
    rejected: texture.rejected,
    ignored: texture.ignored,
  });
}

export function receiveFramePacket(data) {
  if (sourceState.mode !== 'texture') {
    texture.ignored++;
    return;
  }
  let frame;
  try {
    frame = parseFramePacket(data);
  } catch (error) {
    if (texture.rejected++ % 300 === 0) console.error('Texture input: rejected frame packet: ' + error.message);
    return;
  }
  frame.ingestTime = performance.now();
  texture.arrival.tick(frame.ingestTime);
  pump.offer(frame);
}

function onSourceChange(mode) {
  const textureMode = mode === 'texture';
  page.video.style.display = textureMode ? 'none' : '';
  if (textureMode) {
    ensureCanvas();
    texture.canvas.style.display = '';
    texture.width = 0; // everything is sized to the first frame
    texture.height = 0;
  } else if (texture.canvas) {
    texture.canvas.style.display = 'none';
  }
  for (const landmarker of page.allModelState) {
    landmarker.results = undefined; // never draw one source's results over the other
  }
  clearOverlay();
}

function ensureCanvas() {
  if (texture.canvas) return;
  texture.canvas = document.createElement('canvas');
  texture.canvas.id = 'texture_input';
  texture.canvas.style.position = 'absolute';
  texture.canvas.style.left = '0px';
  texture.canvas.style.top = '0px';
  page.video.after(texture.canvas); // where the video sits: under the overlay elements
  texture.context = texture.canvas.getContext('2d');
}

function resize(width, height) {
  texture.width = width;
  texture.height = height;
  for (const element of [texture.canvas, page.canvasElement, page.objectsDiv, page.facesDiv, page.segmentationCanvas]) {
    element.style.width = width + 'px';
    element.style.height = height + 'px';
    element.width = width;
    element.height = height;
  }
}

function clearOverlay() {
  const canvas = page.canvasElement;
  canvas.getContext('2d').clearRect(0, 0, canvas.width, canvas.height);
}

function send(data) {
  const ws = socketState.ws;
  if (ws && ws.readyState === ws.OPEN) {
    ws.send(data);
  }
}

// Mirrors the detection part of predictWebcam in main.js, on one received frame.
async function detectFrame(frame) {
  const { width, height } = frame;
  if (width !== texture.width || height !== texture.height) {
    ensureCanvas();
    resize(width, height);
  }
  const image = new ImageData(frame.pixels, width, height);
  texture.context.putImageData(image, 0, 0);

  // Texture mode only: every message says which frame it was computed from. sourceTimestamp echoes the packet
  // header (the sender's clock, so vvvv can compute latency); ingestTime is this page's performance.now().
  const meta = { frameId: frame.frameId, sourceTimestamp: frame.timestampMs, ingestTime: frame.ingestTime };
  const resolution = { width, height };
  texture.lastTimestamp = nextTimestamp(texture.lastTimestamp, performance.now());
  const timestamp = texture.lastTimestamp;

  let timeToDetect = 0;
  const startDetect = performance.now();
  for (const landmarker of page.allModelState) {
    if (landmarker.detect && landmarker.landmarker) {
      const marker = landmarker.landmarker;
      if (landmarker.resultsName === 'segmenterResults') {
        await marker.segmentForVideo(image, timestamp, segmenterState.toImageBitmap);
      }
      else if (landmarker.resultsName === 'gestureResults') {
        landmarker.results = await marker.recognizeForVideo(image, timestamp);
      }
      else if (landmarker.resultsName === 'imageResults') {
        landmarker.results = await marker.classifyForVideo(image, timestamp);
      }
      else if (landmarker.resultsName === 'imageEmbedderResults') {
        landmarker.results = await marker.embedForVideo(image, timestamp);
      }
      else {
        landmarker.results = await marker.detectForVideo(image, timestamp);
      }
      // The results key must stay first: VL.MediaPipe routes messages by their first 8 bytes.
      send(JSON.stringify({
        [landmarker.resultsName]: { ...landmarker.results, ...meta },
        'resolution': resolution,
        ...meta,
      }));
    }
    timeToDetect = Math.round(performance.now() - startDetect);
  }

  const startDraw = performance.now();
  clearOverlay();
  if (overlayState.show) {
    for (const landmarker of page.landmarkerModelState) {
      if (landmarker.detect && landmarker.results) {
        landmarker.draw(landmarker.results, webcamState.drawingUtils);
      }
    }
    if (objectState.detect && objectState.results) {
      objectState.draw(texture.canvas);
    }
    if (faceDetectorState.detect && faceDetectorState.results) {
      faceDetectorState.draw(texture.canvas);
    }
  }
  const timeToDraw = Math.round(performance.now() - startDraw);

  send(JSON.stringify({ 'timers': { 'detectTime': timeToDetect, 'drawTime': timeToDraw, 'sourceFrameRate': texture.arrival.fps, ...meta } }));
}
