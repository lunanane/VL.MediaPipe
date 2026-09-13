# VL.MediaPipe texture input: M0 groundwork notes

Workstream W6 (plan `depth-handtracking/plans/vl-tracking-depth-prepass.md`, section B, milestone M0).
Written 2026-09-13 on RENDERPC01. **Nothing here is implemented yet**: this file records the baseline,
proves the page rebuild, maps every change site for M7, and gives an ordered change list.

Line references are `file:line`. They point to:
- page source: `mediapipe-touchdesigner/` at `d8a0c21`
- VL document: `VL.MediaPipe.vl` at `05ab3d2`
- installed packs: `C:\Program Files\vvvv\vvvv_gamma_7.4-0008-g793e4b395d-win-x64\packs\` (written `<packs>`)

## 1. Baseline

| Item | Value |
|---|---|
| Superproject | `https://github.com/vvvv/VL.MediaPipe`, `main` @ **`05ab3d2bdb77b981c0136c5807c9c4dd69d89a10`** (2026-07-23, "Update README.md") |
| Submodule | `mediapipe-touchdesigner` @ **`d8a0c2169b120a9b34f61446dd7e485e3bd03699`** (tag `v0.5.1`, 2025-06-14, "bump version") |
| Local branch | `feature/texture-input` created from `main`, no commits |
| LFS | `*.tflite`, `*.wasm` (superproject) and `src/mediapipe/**` (submodule) came down as real content |
| Checkout EOL | `core.autocrlf=true`: `web/` text files are LF in the index, CRLF in the working tree (`git ls-files --eol web/`) |
| Last `web/` commits | `e6a25de` "update to mediapipe-touchdesigner 0.5.1" (2025-10-10), then `8ca59f3` "works around torinmb/mediapipe-touchdesigner#133" |
| Portable Node | `D:\claude\_tools\node\node.exe` **v24.21.0** (LTS "Krypton"), npm 11.19.0. The zip's SHA-256 `158f7685…e541` matches `https://nodejs.org/dist/v24.21.0/SHASUMS256.txt` |
| Tool caches | npm `D:\claude\_tools\npm-cache`, corepack `D:\claude\_tools\corepack`, yarn `D:\claude\_tools\yarn-global` (nothing under the user profile) |

### How `web/` is produced

- `VL.MediaPipe/.github/workflows/main.yml` only packs the committed `web/` (via `deployment/VL.MediaPipe.nuspec`, `<file src="..\web\**" target="web\" />`). **It never builds the page.**
- `buildTransitive/VL.MediaPipe.targets:5-9` copies `web\**` to the output of consuming projects (exported apps).
- The submodule builds with Vite: `package.json` has `"build": "vite build"`.
  - `vite.config.js` sets `outDir: '_mpdist'`, `minify: false`, `treeshake: false`.
  - `vite-plugin-static-copy` copies `src/mediapipe/*` (models) and `node_modules/@mediapipe/tasks-vision/*` (bundle + wasm) into `mediapipe/`.
- The submodule's own CI (`.github/workflows/release-action.yaml`) uses `yarn install` + `yarn build` on Node 16.13 and zips `dist`, which does not match `outDir: _mpdist`. That CI is TouchDesigner-specific and irrelevant to vvvv.
- No script in either repo copies `_mpdist` to `web/`; the maintainer did it by hand.

## 2. Page rebuild: does it reproduce the shipped `web/`?

**Verdict: yes, a rebuild is trustworthy.** Use recipe A below. Its output matches the shipped `web/` byte for byte, except for two lines that were hand-edited into the shipped bundle. It also behaves identically in a headless browser.

### Attempts (all in scratch copies of the submodule's sources, never in `web/`)

| # | Command (portable Node) | Toolchain resolved | Result |
|---|---|---|---|
| 0 | `npm ci` | n/a | **fails**. `package-lock.json` is stale (version 0.4.2 era: tasks-vision 0.10.10, vite ^5.1.4, static-copy ^1.0.1). npm: "lock file's @mediapipe/tasks-vision@0.10.10 does not satisfy @mediapipe/tasks-vision@0.10.18" (log: `D:\claude\_tools\mp-rebuild\evidence\npm-ci-failure.log`) |
| A | `npm install --no-package-lock` then `npm run build` | vite 6.4.3, esbuild 0.25.12, rollup 4.63.2, vite-plugin-static-copy 2.3.2, tasks-vision 0.10.18 | builds in ~0.3 s; output `D:\claude\_tools\mp-rebuild\npm\_mpdist` |
| B | `corepack yarn install --immutable` then `corepack yarn build` (yarn 4.6.0 from `packageManager`, `YARN_NODE_LINKER=node-modules`) | vite 6.0.7, esbuild 0.24.2, rollup 4.29.1, static-copy 2.2.0 (exactly `yarn.lock`) | builds; output `D:\claude\_tools\mp-rebuild\yarn\_mpdist`. `YARN_NODE_LINKER=node-modules` is required: with PnP there is no `node_modules/@mediapipe/tasks-vision` for static-copy |

### Comparison with committed `web/`

Evidence and scripts are in `D:\claude\_tools\mp-rebuild\evidence\` and `...\scripts\` (`compare_web.py`, `ast_compare.mjs`, `smoke_page.mjs`). Text files were compared with CRLF normalised to LF, because of `autocrlf`.

| Check | Recipe A (npm) | Recipe B (yarn.lock) |
|---|---|---|
| File list | 34 = 34, same paths | 34 = 34 (bundle hash name differs) |
| Byte-identical files | **33 / 34**: all 18 models, both `.wasm`, both wasm `.js`, `vision_bundle.{mjs,cjs}` + maps, `vision.d.ts`, `package.json`, `README.md`, css, svg, **`index.html`** | 32 / 34 (`index.html` differs only by the asset hash name) |
| Main bundle name | **`assets/index-DS3gr1CQ.js`**, the same content hash as shipped | `assets/index-DxkbxhNO.js` |
| Main bundle size (LF) | 232 177 B vs shipped 232 280 B (**-0.044 %**) | 231 693 B (**-0.253 %**) |
| Text diff vs shipped | **2 lines**, both shown below | 1525 diff lines, all esbuild parenthesisation (`(function(){})()` vs `function(){}()`) |
| AST (rollup `parseAst`, ESTree, positions stripped) | 484 top-level statements; differs from shipped `8ca59f3` in exactly the 2 statements holding the 2 lines | **AST-identical to recipe A**; vs the pre-edit blob `e6a25de` it differs only in the `numHands` statement |
| Config keys in bundle (`Wsaddress`, `Wsport`, `Webcam`, `Wwidth`, `Wheight`, `Wtargetframerate`, `Wflip`, `Owidth`, `Oheight`, all 9 `Detect*`, `Showoverlays`, `H*`, `Gnumhands`, `Posemodeltype`, `Omodeltype`, `Smodeltype`, `Iemodeltype`) | all present, same counts | same |
| API tokens (`detectForVideo` 7, `recognizeForVideo` 2, `classifyForVideo` 2, `embedForVideo` 3, `segmentForVideo` 2, `safeSocketSend` 3, `requestAnimationFrame` 4, `getUserMedia` 1, `delegate: "GPU"` 8) | identical counts | identical counts |
| tasks-vision | `mediapipe/package.json` `"version": "0.10.18"`, byte-identical | same |

The two lines that are **in shipped `web/` but not in source `d8a0c21`**:

```
@@ createHandLandmarker (web bundle ~L4132)
-  console.log('numHands', parseInt(handState.numHands))      <- already in e6a25de; `git log --all -S` finds it in no submodule commit (local edit)
@@ setup() (web bundle ~L4882; source equivalent main.js:47-49)
-  await new Promise(r => setTimeout(r, 1000));               <- commit 8ca59f3, workaround for torinmb/mediapipe-touchdesigner#133
```

### Smoke run (headless Edge, `--headless=new`, DevTools protocol, not vvvv)

`smoke_page.mjs` serves a `web/` folder on `127.0.0.1` and accepts the page's WebSocket (minimal RFC 6455). It loads `index.html?Wsaddress=ws://127.0.0.1&Wsport=<port>` and records console output, exceptions, resources and the WebGL renderer. It kills only the browser PID it started. All three folders (shipped `web/`, recipe A, recipe B) behaved identically:
- 31 files served, 0 missing
- WebSocket `open`, then `pong`, then `{"type":"webcamDevices","devices":[{"label":""}]}`
- all 9 "Starting …" logs, reaching `changeWebcam` (so every landmarker was created)
- 0 exceptions, 0 console errors
- WebGL2 renderer `ANGLE (NVIDIA, NVIDIA GeForce RTX 4090 … Direct3D11 …)`
- wasm plus all 9 model files fetched

Run twice with the same result. Shipped took 5.3 s / 5.1 s, the rebuilds 3.9 s / 3.8 s, consistent with the 1 s delay being present only in shipped. The second run's JSON is in `evidence/smoke-{shipped,npm,yarn}.json`.

### Rebuild recipe for M7

```
cd D:\claude\VL.MediaPipe\mediapipe-touchdesigner
set PATH=D:\claude\_tools\node;%PATH%   (per shell only; or call node/npm by full path)
set npm_config_cache=D:\claude\_tools\npm-cache
npm install --no-package-lock
npm run build                             -> _mpdist\ (gitignored)
robocopy _mpdist ..\web /MIR              (removes old hashed assets; index.html references the new ones)
```

Before any feature change, the M7 rebuild gate is: port the 1 s delay into `src/main.js`, rebuild, and confirm the bundle is AST-identical to shipped `8ca59f3` except the `numHands` log (`ast_compare.mjs`). Tolerance, not lockfile determinism, protects against drift: recipe A resolves ranges at install time. To pin it, commit a regenerated `package-lock.json` in the submodule and switch to `npm ci` (open question Q2).

## 3. C# build

`dotnet build src\VL.MediaPipe.csproj -c Release` succeeded on the first attempt:
- restore 1.06 s (VL.Core 2024.6.6 already cached)
- **0 errors**, 349 warnings (CS8618 nullable etc., pre-existing), 4.15 s
- output `lib\net8.0\VL.MediaPipe.dll` (29 696 B) + `.deps.json/.pdb/.xml`, all gitignored; `git status` stays clean
- log: `D:\claude\_tools\mp-rebuild\evidence\dotnet-build.log` (German locale output)

Environment facts relevant to M7:
- 7.4 ships `VL.CoreLib`/`VL.Core`/`VL.Stride` `2025.7.4-0008-g793e4b395d`. `VL.MediaPipe.vl` declares `LanguageVersion="2025.7.1"` and `VL.CoreLib 2025.7.1` (line 2-3), so it is compatible.
- 7.4 ships `VL.IO.WebSocket 1.1.0` in `<packs>` (WatsonWebsocket 4.1.2).
- `VL.CEF` (0.7.2, 0.8.2, `.stride`, `.skia`, `.win-x64`) and `VL.IO.WebServer 1.0.1` are only in the NuGet cache, not in `<packs>`.

## 4. M7 change sites: page source (`mediapipe-touchdesigner/src`)

| Concern | Location | Notes |
|---|---|---|
| Entry / setup | `main.js:44-68` | `handleQueryParams` L45, `setupWebSocket` L46, `getWebcamDevices` L47, landmarker creation L49-65, `webcamState.startWebcam()` L66, first `requestAnimationFrame` L67 |
| **WebSocket creation** | `main.js:188` `socketState.ws = new WebSocket(socketURL)` | URL built at L46 from `socketState.adddress` (sic) + port; defaults `state.js:28-32` (`ws://localhost`, `3002`); port taken from page URL `main.js:71`, then `Wsport` query overrides |
| WS open | `main.js:190-198` | sends `'pong'` L192, then `{type:'webcamDevices', devices}` L194-197 (keep in both modes: this populates the `Camera` enum) |
| **WS message parsing** | `main.js:200-211` | `'ping'/'pong'` filter L202, `JSON.parse(event.data)` L204, `configMap` dispatch L205-210. No `binaryType` set (default `blob`); a binary message would currently throw in `JSON.parse` |
| **Config application** | `modelParams.js:13-104` (`configMap`) | Also applied from URL query `main.js:70-78`. Keys: `Wsaddress/Wsport` L14-15, `Webcam` L17, `Wheight/Wwidth` L18-19, `Wtargetframerate` L20, `Wflip` L21-24, `Oheight/Owidth` L25-26, `Detect*` L28-36, `Showoverlays` L37, hands L39-42, gestures L44-49, pose L51-56, face landmarks L58-63, face detector L65-67, objects L69-71, image classifier L73-75, segmenter L77-79, embedder L81. Helpers `modelCheck` L106-114, `detectSwitch` L116-146, `overlaySwitch` L148-167. Model/confidence keys only take effect at landmarker creation (setup), i.e. after a page reload (`Reset` pin) |
| **Device enumeration** | `main.js:222-237` `getWebcamDevices` (labels only); `state.js:43-86` `changeWebcam` (`enumerateDevices` L52-77, label → deviceId L58-65) | `webcamState.startWebcam/changeWebcam` `state.js:24-25` |
| **getUserMedia** | `state.js:88-136` `startNewWebcam`; constraints L89-105 (`exact` width/height, `aspectRatio: 1.7777…`, ideal frameRate); stop old tracks L108-114; `getUserMedia` L118; frame rate from track settings L120-124; unguarded `ws.send` L128/L131; offscreen canvas sized L134-135 | page defaults `state.js:17-18` 1280×720, `outputState` `state.js:38-41` 1280×720 |
| **predictWebcam / rAF loop** | `main.js:86-185` | early return + rAF L91-95 when no video; new-frame gate `video.currentTime` L121; rAF L183 (and L67, L93). One `startTimeMs = performance.now()` L120 shared by all tasks |
| **captureAndFlipWebcam** | `main.js:239-252` (called L123) | draws video into `webcamState.offscreenCanvas` at `outputState` size, mirrored when `flipped` |
| **Detection calls** (all `main.js:127-154`, looping `allModelState` L40) | segmenter `segmentForVideo(flippedVideo, ts, segmenterState.toImageBitmap)` **L133** (+ `video.style.opacity = 0` L132); gestures `recognizeForVideo(flippedVideo, ts)` **L136**; image classifier `classifyForVideo(flippedVideo, ts)` **L139**; image embedder `embedForVideo(video, ts)` **L142** (raw video, not flipped); face landmarks / face detector / hands / poses / objects `detectForVideo(flippedVideo, ts)` **L145** | All created with `runningMode: "VIDEO"`: `handDetection.js:21-37` (GPU L28, VIDEO L30), `handGestures.js:23-43` (L30/L32), `faceLandmarks.js:23-41` (L30/L32), `faceDetector.js:21-36` (L28/L30), `poseTracking.js:23-40` (L31/L33), `objectDetection.js:23-38` (L31/L33), `imageClassification.js:22-35` (L28/L30), `imageSegmentation.js:59-89` (L73-76, output canvas L75), `imageEmbedder.js:18-29` (no delegate = CPU, VIDEO L26) |
| **Result sending** | `safeSocketSend` `main.js:80-84`; per result `main.js:147-150` `{[resultsName]: results, resolution: {width: video.videoWidth, height: video.videoHeight}}`; timers `main.js:181` `{timers: {detectTime, drawTime, sourceFrameRate}}` | `resultsName` values: `handResults` (`handDetection.js:13`), `gestureResults`, `faceLandmarkResults`, `faceDetectorResults`, `poseResults`, `objectResults`, `imageResults`, `segmenterResults` (never sends data: `segmentForVideo` with callback returns void), `imageEmbedderResults` |
| **Timers** | `main.js:88-89`, `startDetect` L125, `timeToDetect` L152-153 (computed **inside** the per-task loop), `startDraw` L157, `timeToDraw` L176-177; `sourceFrameRate` = `webcamState.frameRate` (`state.js:122`, may be fractional) | `Date.now()` ms, rounded to int |
| **Overlay canvas sizing** | `main.js:97-118`: `output_canvas`, `#objects`, `#faces`, `#segmentation` and the offscreen canvas are all set to `outputState.width/height` **every frame** | `DrawingUtils` bound to `output_canvas` (`state.js:3-4, 23`); face/object boxes scale by `div.width / video.width` (`faceDetector.js:39-40`, `objectDetection.js:41-42`); segmenter viewport/aspect `imageSegmentation.js:195, 204-205` (uses `videoElement.videoWidth`); DOM in `index.html` (`#webcam`, `#objects`, `#faces`, `#segmentation`, `#output_canvas`) |
| Draw | `main.js:157-177` | `landmarkerModelState` L41; object/face DOM drawing L169-174 |
| `ImageData` accepted | `web/mediapipe/vision.d.ts:1935` `export declare type ImageSource = TexImageSource;` (marked `@deprecated` alias) | confirms the plan |

## 5. M7 change sites: `VL.MediaPipe.vl`

`MediaPipe` is a class definition (`Aspects="Internal"`) that is instantiated as a process node:
- `<Node Name="MediaPipe" Aspects="Internal" … Id="Q5HejFxEzF3O35hE7Mpbcl">` at **L11**
- its `<Patch Id="E8arcmZGe0TPZ0vlEpFeqO">` at L16, and `<Canvas Id="JYhXpwd4mcaLkH0seSBDPL" CanvasType="Group">` at L17

(Structure dumped with `scripts/vl_map.py`; full link list in `evidence/vl_mediapipe_map.txt`.)

### Pin declaration (what M7 extends)

- **`<Patch Id="DBkzuIyvkhMNED6D71n01n" Name="Update" ManuallySortedPins="true">` at L1863-1887** declares every public pin:
  - the nine detect toggles L1864-1872
  - `Show overlays` L1873
  - **`Camera`** L1874-1878: `<Pin Id="OG1rlKkndVuQPC5NTsO77T" Name="Camera" Kind="InputPin">` with a `<p:TypeAnnotation LastCategoryFullName="MediaPipe" LastDependency="VL.MediaPipe.dll"><Choice Kind="TypeFlag" Name="CamerasEnum" /></p:TypeAnnotation>`. **This is the template for a C#-enum `Source` pin.**
  - `Flip Camera` L1879
  - **`Image Size`** L1880 (`DefaultValue="640, 360"`)
  - `Webserver Port` L1881 (9980, Optional), `Websocket Port` L1882 (3002, Optional)
  - **`Output`** L1883
  - `Shared Texture Enabled` L1884 (True, Optional), `Reset` L1885, `Enabled` L1886
  - New `Source`, `Input Texture` and `Max Input Size` pins go here, with fresh 22-char base62 Ids.
- `<Patch Id="RAjM2C25AeyMcMHy6wfJFl" Name="Create" />` L1862.
- **`<ProcessDefinition Id="F0VFBEpCwPoL32Jm5O9xIB" HasStateOut="true">` L1776-1790** lists Fragments for Create, Update and the internal getter operations (`GetWebsocket (Internal)` L1888, `GetFaces` L1948, `GetFaceLandmarks` L1968, `GetPoses` L1983, `GetTimers` L1995, `GetHands` L2034, `GetGestures` L2052, `GetObjects` L2071, `GetImageClassifications` L2077, `GetImageSegments` L2099, `GetImageEmbeddings` L2118). Any new operation patch needs a `<Fragment>` here.

### Data flow inside the patch

| Part | Element (line, Id) | Wiring |
|---|---|---|
| **WebSocketServer** process node | L18 `Id="UKUnRI08ijmLL0vKqrLYTc"` (`ProcessAppFlag WebSocketServer`, category `IO.WebSocket`) | Pins L19-33: `Hostname or IP`, `Port` (3002; fed from `Websocket Port` via L2024), `Ssl`, `Enabled`, `Output` (`Gl65k0TSkU5NhSHTPFP2nC`), `Data`, `Connected Clients`, `Statistics`, `Is Listening`, `Send Queue Length`. **It has no Message Type pin** (see §7) |
| WebServer | L36 (`IO.HTTP`) | Content Base Directory = `AppHost.GetPackagePath("VL.MediaPipe")` + `web` (L652, L660, L670, L680, L685). In a dev package repo that is this repo's `web/` |
| **WebBrowser** (VL.CEF) | L244 `Id="VI2xrpZmxxmME1cDdVJsZC"` | `Startup Url` from `String.Format` L278 with pad **L273 `http://localhost:{0}/index.html?Wsport={1}`** (`{0}` = Webserver Port L1848, `{1}` = Websocket Port L2023; `Input 3`/`Input 4` are free). `Enabled` = WebServer Is Listening AND WebSocket Is Listening (L643). `Shared Texture Enabled` from the pin (L1903) |
| **Output** | IO `Output` L1883 ← CP L261 ← `WebBrowser.Reload` L263 `Output` (L1845, L1822) | The node's `Output` is the **WebBrowser instance**, not a texture. The help patches turn it into a texture with `ToStrideRenderer` from `VL.CEF.Stride.vl` (`help/HowTo Use MediaPipe.vl:47`, then `RenderWindow` L56; `help/HowTo Use the HandDetector.vl:46`). `Reset` → `Reload.Apply` (L1843-1844) |
| **Config JSON senders** | nested definitions `BoolSender` **L6132**, `NumberSender` **L6251**, `StringSender` **L6368** | Each is ForEach over `GetWebsocket.Connected Clients` with a `Cache` region, so it re-sends on change **and to every newly connected client**. Output: `WebSocketServer.SendString` of `{"<Name>": 0/1}` (Bool, `Switch` L6190), `{"<Name>": <ToString(value)>}` (Number L6298/L6310), `{"<Name>": "<value>" }` (String L6415). IO pins: Bool `Input/Name/Enabled` (Update L6229), Number `Input/Name/Value` (L6354), String `Input/Name/Value` (L6463) |
| Sender instances in MediaPipe | detect flags: BoolSender L55 (`Detectfacelandmarks` pad L66), L76 (`Detectfaces` L87), L97 (`Detectgestures` L108), L118 (`Detecthands` L129), L139 (`Detectposes` L150), L160 (`Detectobjects` L171), L181 (`Detectimages` L192), L202 (`Detectsegments` L213), L1730 (`Detectimageembeddings` L1741); `Showoverlays` L223/L234; `Wflip` L335/L346 | `Image Size` → `Int2 (Split)` L1764 → NumberSender `Wwidth` L305, `Owidth` L351 (X) and `Wheight` L320, `Oheight` L366 (Y) (links L2122-2127) |
| **Camera selection** | `Camera` pin → CP L381 → `DynamicEnum.Value` L453 → `+` L409 (`{ "Webcam": "` + value + `" }`) → `WebSocketServer.SendString` L395 inside `Cache` L382, forced by `DynamicEnumDefinition.OnChange` L470 → `HoldLatest` L479 | client = `FirstOrDefault(Connected Clients)` L441 |
| **Camera enum fill** | message `{"type":…` branch L1072/L1081 → `WebcamRoot.Devices` L1095 → `CamerasEnumDefinition.ClearEntries` L1146 → ForEach `AddEntry(Device.Label)` L1166/L1198 | |
| **Incoming message routing** | `Reactive.ForEach` L690 over `WebSocketServer.Data` (L1931) → `WSMessage.Data` L709 → **`Sequence.Take 8` L719 → `String.FromBytes` L729** → `!= "pong"` L1607 gates the `If` L782 → 8-char prefix compares: `{"faceDe` L792, `{"faceLa` L862, `{"poseRe` L932, `{"timers` L1002, `{"type":` L1072, `{"handRe` L1230, `{"gestur` L1300, `{"object` L1370, `{"imageR` L1440, `{"segmen` L1510 (empty branch), `{"imageE` L1535 | Payload for `JsonSerializer.Deserialize` = `ReadOnlySpan.Create(array, offset, count)` L739 over the whole message; options `JsonSerializerOptions.Create` L1212 + `SetPropertyNameCaseInsensitive(true)` L1220 |
| Result channels | faces L1618, face landmarks L1631, poses L1645, **timers L1659**, **hands L1674**, gestures L1688, objects L1702, image classes L1716, embeddings L1751 | Each stores the **inner** results object, e.g. `HandRoot.HandResults` L1289 → `SetValue` L1253 (link L2060); `TimerRoot.Timers` L1025 → L1034 (L2000) |
| Consumers | `HandDetector` L4549 (IO Update L5198: `Input`, `Max Number of Hands`, 3 confidences, `Landmarks`, `World Landmarks`, `Output`); `GetHands` L4634; landmark `Position` maps 0..1 → (-1,1)..(1,-1) (`Map` L4747, aspect ignored). `Timings` L3789 (IO Update L3904: `Detection Time`, `Draw Time`, `Source Frame Rate`); `GetTimers` L3812 inside `Try` L3821 | `Is Valid` exists only on nested `SelectJoint` helpers (L4914 hand, L4340 pose) |

### VL.IO.WebSocket 1.1.0 (`<packs>\VL.IO.WebSocket\VL.IO.WebSocket.vl`, identical to the NuGet-cache copy)

- `WebSocketServer` class L973.
  - `SendString` patch L1875 (Client, Data, Encoding, Apply): implemented as `String.ToBytes` → `SendAsync` with `Message Type = Text` (L1423-1428).
  - **`SendAsync (Advanced)` patch L1883-1898**: `Client`, `Data` typed **`IReadOnlyList<Byte>`**, **`Message Type`**, `Result`, `Cancellation Token`. Internally an `If` region calls `SendAsync` with `Message Type` defaulting to **`Binary`** (L1692-1701) plus a `TaskHandler`.
  - `SendData` patch L1943.
- The underlying call (L~1522) is `WatsonWebsocket.WatsonWsServer.SendAsync` with the **`ArraySegment<Byte>`** overload. WatsonWebsocket 4.1.2 has `(Guid, string|byte[]|ArraySegment<byte>, WebSocketMessageType, CancellationToken)` overloads (`~/.nuget/packages/watsonwebsocket/4.1.2/lib/net462/WatsonWebsocket.xml:487-507`).

### Readback nodes in 7.4 (for the texture path)

- **`TextureToImage`**, `<packs>\VL.Stride.Runtime\VL.Stride.Rendering.vl:19270` (Update patch L19525). Pins:
  - `Input`, `Frame Delay`
  - **`Size`** ("If 0, the input size is used. Otherwise the texture will be resized.")
  - **`New Format`** (default `R8G8B8A8_UNorm_SRgb`)
  - `Output` (IImage), `Is Blocking`, `Readback Time`, `Result Available`
  - The built-in `Size` resize can implement `Max Input Size`. Set `New Format` to `R8G8B8A8_UNorm` for the UNORM prepass output.
- `ReadBack`/`ReadBackFloat`/`ReadBackVec2-4`: `<packs>\VL.Stride.Runtime\VL.Stride.Graphics.vl:7-400` (buffer readback).
- `GetDataAsync<T>(this Texture)`: `VL.StandardLibs/VL.Stride.Runtime/src/Graphics/TextureExtensions.cs:189`.

## 6. M7 change sites: `src/` POCOs that gain `FrameId`

The VL channels store the **inner** results object (§5), so the field must be on the inner class, and the page must put `frameId` **inside** the results object as well as at the root.

| File | Class to extend (lines) | Root class (lines) |
|---|---|---|
| `HandResults.cs` | `Handresults` L35-41 | `HandRoot` L29-33 |
| `GestureResults.cs` | `Gestureresults` L9-16 | `GestureRoot` L3-7 |
| `PoseResults.cs` | `Poseresults` L45-49 | `PoseRoot` L39-43 |
| `FaceLandmarkResults.cs` | `Facelandmarkresults` L9-14 | `FaceLandmarkRoot` L3-7 |
| `FaceDetectorResults.cs` | `Facedetectorresults` L12-16 | `faceDetectorRoot` L6-10 |
| `ObjectResults.cs` | `Objectresults` L11-15 | `ObjectRoot` L5-9 |
| `ImageResults.cs` | `Imageresults` L9-13 (already has `TimestampMs`) | `ImageRoot` L3-7 |
| `ImageEmbedderResults.cs` | `Imageembedderresults` L9-13 (already has `TimestampMs`) | `ImageembedderRoot` L3-7 |
| `TimerResult.cs` | `Timers` L8-13 (`DetectTime`, `DrawTime`, `SourceFrameRate`, all `int`) | `TimerRoot` L3-6 |

Namespaces are inconsistent upstream (`MediaPipe.*` vs `VL.MediaPipe.*`); keep them. `CamerasEnum.cs`, `ModelEnums.cs` and `WebcamResult.cs` are unchanged apart from a new `InputSource` enum.

## 7. Where the plan and the source disagree (flags)

1. **Submodule branch.** The plan says the page is branch `image-embedding`. The pinned commit `d8a0c21` (tag `v0.5.1`) is on `origin/main` (also `feature/segmentation`, `image-upload`), **not** on `origin/image-embedding` (tip `14fd214`, 2024-01-03). `.gitmodules` `branch = image-embedding` is stale metadata.
2. **"Shipped minified in `web/`".** Not minified: `vite.config.js` sets `minify: false, treeshake: false`. The bundle is 5035 readable lines; only the embedded tasks-vision code is pre-minified by Google.
3. **"`npm ci && npm run build` reproduces the shipped bundle" (M0).** `npm ci` fails on the stale `package-lock.json`. The reproducing command is `npm install --no-package-lock && npm run build` (identical asset hash). The `yarn.lock` build is AST-identical but not byte-identical.
4. **Shipped `web/` is not a pure build of the pinned source.** It carries two hand edits: the 1 s startup delay (`8ca59f3`, issue #133) and a `numHands` console log that is in no commit. A rebuild from source drops the delay, **so M7 must port it into `src/main.js`**.
5. **"Capture is forced to exactly 1280×720."** 1280×720 is only the page default (`state.js:17-18`, `38-41`). In vvvv the `Image Size` pin (definition default **640, 360**, L1880) is sent as `Wwidth/Wheight/Owidth/Oheight` to every connecting client (NumberSenders L305-L366). `startNewWebcam` then requests `exact` width/height with a fixed `aspectRatio` 16:9 (`state.js:89-105`), and detection runs on the offscreen canvas at `Owidth×Oheight` (`main.js:117-118`, `239-252`). Message ordering against camera start is unverified without a camera.
6. **"The existing `WebSocketServer` with Message Type Binary."** `Message Type` is a pin of the `WebSocketServer.SendAsync (Advanced)` operation (VL.IO.WebSocket.vl:1883-1898, default Binary), not of the process node the MediaPipe patch instantiates (L18-33). ArraySegment support is confirmed at the Watson call, but the VL operation's `Data` pin is typed `IReadOnlyList<Byte>`. Whether an `ArraySegment<byte>` passes through without a copy must be verified in M7.
7. **"Waits for `HandDetector Is Valid`" (texture-input harness).** `HandDetector` has no `Is Valid` pin (IO L5198). It exists only on the nested `SelectJoint` helper (L4914). Use `SelectJoint`'s `Is Valid` or a non-empty `Landmarks`.
8. **"Every result message and `timers` gains `frameId`" vs "`camera` … that path is untouched".** These conflict. Recommendation: add the fields in texture mode only and make them nullable in C#, so camera-mode JSON is byte-identical to upstream.
9. **"Result POCOs gain an optional `FrameId`" + "changes stay in … one new C# file".** The channels hold the inner results object, so `FrameId` must go on the inner classes, and those classes are not `partial`. Minimal diff: add the `partial` keyword to the 9 inner classes (one word each) and put all new properties in one new file.
10. **Not in the plan, but a hard constraint:** the VL side routes messages by their **first 8 bytes** (`Take 8`, L719). Every result JSON must keep `"<resultsName>"` as its first key; `frameId`/`ingestTime` go **after** it. Any new page → VL message type needs a prefix distinct from the 10 existing ones.
11. **Latency needs a common clock.** `ingestTime` from the page is `performance.now()` in the browser and cannot be compared with vvvv's clock. Recommendation: the page also echoes the packet header's `f64 timestamp` (`sourceTimestamp`), so VL computes `Frame Latency (ms) = now − sourceTimestamp − detectTime` without a lookup table.
12. **Confirmed as stated:** tasks-vision 0.10.18, WebGL GPU delegate (`delegate: "GPU"` in 8 of 9 tasks; `imageEmbedder.js` has none → CPU), `ImageSource = TexImageSource` at `vision.d.ts:1935`, MIT licences, `Camera` enum filled from device labels, the WebServer + WebSocketServer + CEF WebBrowser composition.

Risks noticed (not plan contradictions; verify in M7):
- **Number formatting.** `NumberSender` builds JSON with `ToString(value)` (L6310). On this de-DE machine a float might become `0,5`, which is invalid JSON, and the page would silently ignore `Hdetectconf` etc.
- **`sourceFrameRate` type.** It comes from `track.getSettings().frameRate`, which may be fractional, but `Timers.SourceFrameRate` is `int`, and System.Text.Json rejects fractional values for `int`. Texture mode should send an integer, and new fields should be `double?`/`long?`.
- **Binary message size.** 640×576×4 + 24 B = 1.47 MB per message through Watson → Chromium. Measure queueing (`Send Queue Length`) and drop frames on the VL side while a send is pending.

## 8. Ordered change list for M7

Wire format (plan §B), all little-endian: `"TDPF"` | u8 version=1 | u8 format (1 RGBA8, 2 BGRA8) | u16 width | u16 height | u16 reserved | u32 frameId | f64 timestamp ms | `width·height·4` bytes, rows top-down. Header = 24 B.

### Page (submodule)

0. **Where page commits live (open question Q1).** Default: a local branch `texture-input` in the submodule off `d8a0c21`, with the superproject's submodule pointer bumped. Nothing is pushed, so that pointer only resolves in this clone.
1. **Rebuild gate.** Port the 1 s delay into `main.js` after L47 (issue #133). Rebuild with the recipe in §2 and confirm AST parity with shipped `8ca59f3` minus the `numHands` log. Commit the rebuilt `web/` as its own commit before any feature change.
2. **New module `src/textureSource.js`** (pure, DOM-free where possible):
   - `parseFramePacket(ArrayBuffer)`:
     - validates magic, version 1, format ∈ {1,2}, `byteLength === 24 + w·h·4`
     - returns `{frameId, timestampMs, width, height, pixels: new Uint8ClampedArray(buf, 24, w*h*4)}`, a view with no copy
     - swaps BGRA→RGBA in place for format 2
   - `LatestFrameSlot`: `put(frame)` overwrites and counts drops, `take()` returns and clears.
   - `nextTimestamp(lastTs, headerTs) = Math.max(headerTs, lastTs + 1)`, so timestamps are strictly increasing per landmarker graph.
3. **Node test runner suite** `test/textureSource.test.mjs` (`node --test`, portable Node 24). Cover:
   - header parse: every field, little-endian, size mismatch, bad magic/version/format
   - BGRA swap
   - the slot keeps only the latest frame
   - timestamps stay monotonic with equal or decreasing header times
4. **`Source` config key.**
   - Add `'Source': value => setSource(String(value).toLowerCase())` to `configMap` (`modelParams.js`, after `Wflip` L24). URL query applies it too (`main.js:70-78`).
   - `setSource('texture')`: stop webcam tracks (factor `state.js:108-114` into `stopWebcam()`), stop scheduling `predictWebcam`, hide `#webcam`.
   - `setSource('camera')`: `webcamState.startWebcam()` + `requestAnimationFrame(predictWebcam)`. **Default `camera`**, with setup `main.js:66-67` unchanged when no `Source` is given.
5. **Binary receive.**
   - After `main.js:188` set `socketState.ws.binaryType = 'arraybuffer'`.
   - At the top of the message handler (`main.js:200`): `if (typeof event.data !== 'string') { if (mode === 'texture') onFrame(event.data); return; }`, before the ping check and `JSON.parse`.
   - Keep the `open` handler (L190-198) in both modes.
6. **Arrival-driven, non-overlapping detect loop.** `onFrame(buf)` does `slot.put(parse(buf)); if (!busy) pump();`. `pump()` sets `busy = true`, runs `while ((f = slot.take())) await detectFrame(f)`, then sets `busy = false`.
7. **`detectFrame(f)`** mirrors `main.js:127-154` without touching `predictWebcam`:
   - `image = new ImageData(f.pixels, f.width, f.height)`; `ts = nextTimestamp(...)`
   - Same per-task branches with `image` instead of `flippedVideo`/`video`: `segmentForVideo(image, ts, cb)`, `recognizeForVideo`, `classifyForVideo`, `embedForVideo`, `detectForVideo`.
   - Send `safeSocketSend(ws, JSON.stringify({[resultsName]: {...results, frameId, ingestTime}, resolution: {width: f.width, height: f.height}, frameId, ingestTime, sourceTimestamp: f.timestampMs}))`. The results key must be **first**.
   - Then timers: `{timers: {detectTime (int), drawTime (int), sourceFrameRate (int, measured arrival rate), frameId, ingestTime, sourceTimestamp}}`.
8. **Overlay in texture mode.**
   - When the frame size changes, set `outputState.width/height = f.width/f.height` and size the canvases as in `main.js:97-118`.
   - `putImageData` the frame into a background canvas, so `ToStrideRenderer` shows image + overlay.
   - Pass `{width, height}` of that canvas to the face/object drawers (`faceDetector.js:39-40`, `objectDetection.js:41-42`).
   - Hide `#webcam`.
9. Rebuild `web/` (§2 recipe), run `node --test`, rerun `smoke_page.mjs` in camera mode (no regression: 9 landmarkers, `webcamDevices` sent).

### C#

10. **`src/FramePacket.cs`** (new; namespace `MediaPipe`):
    - `public static int HeaderSize = 24`
    - `FramePacketWriter` (IDisposable) with `bool TryWrite(IImage image, uint frameId, double timestampMs, out ArraySegment<byte> packet)`:
      - accepts `image.Info.Format` R8G8B8A8 (1) or B8G8R8A8 (2); PixelFormat member names still to verify
      - width/height ≤ 65535
      - copies rows top-down from `image.GetData().Bytes` honouring `IImageData.ScanSize` (VL.Core 2024.6.6: `IImage.Info/GetData/IsVolatile`, `IImageData.Bytes/ScanSize`, `ImageInfo.PixelSize/ImageSize/ScanSize`)
      - uses a reused/pooled buffer and returns `false` while the previous packet's send is still pending (latest-frame on the VL side)
    - Also a monotonic `Stopwatch` clock `NowMs` and `LatencyMs(double sourceTimestamp, int detectTime)`.
11. **`InputSource` enum** `{ Camera, Texture }` (in the new file or `ModelEnums.cs`).
12. **Result `FrameId`:**
    - add `partial` to the 9 inner result classes (§6) and to `TimerRoot`/`Timers`
    - new file `src/FrameMetadata.cs` adds `public long? FrameId`, `public double? IngestTime`, `public double? SourceTimestamp`
    - JSON binding is case-insensitive (L1220), so `frameId` maps
    - `dotnet build` must stay at 0 errors

### VL document

13. **`MediaPipe` Update patch (L1863).** Add:
    - `Source` (TypeAnnotation `InputSource` from `VL.MediaPipe.dll`, modelled on `Camera` L1874-1878, default Camera)
    - `Input Texture` (Stride `Texture`)
    - `Max Input Size` (Integer32, default 0)
14. **Source to the page.**
    - Extend the Startup Url pad L273 to `http://localhost:{0}/index.html?Wsport={1}&Source={2}` (`Format` L278 `Input 3`); the mode is then known before the camera would start.
    - Add a `StringSender` (def L6368) named `Source` for runtime switching.
15. **Texture path, in a new internal patch/definition.** Enabled when `Source = Texture`:
    - `Input Texture` → `TextureToImage` (`Size` = Max Input Size or 0, `New Format` = `R8G8B8A8_UNorm`)
    - on `Result Available`: `FramePacketWriter.TryWrite` → `WebSocketServer.SendAsync (Advanced)` (Message Type Binary)
    - Websocket and client come from `GetWebsocket (Internal)` L1888 (client like L441 or per client like the senders)
    - add any new operation's `<Fragment>` to `ProcessDefinition` L1776
16. **Outputs.**
    - `Timings` (L3789, IO L3904): add `Frame Id` and `Frame Latency (ms)` from `GetTimers` (L3812) using the new `Timers` fields
    - optionally `HandDetector` (L4549, IO L5198): `Frame Id` from `Handresults.FrameId`
17. **Gates.** Rules from `vvvv-node.md` §8-§12 apply to hand-edited XML (22-char base62 Ids, no `--` in comments). Then:
    - `vvvvc.exe VL.MediaPipe.vl` exits 0 (take the team lock `D:\claude\_vl-dev\vvvv.lock`)
    - headless harness: send fixture frames, wait for landmarks with frame ids
    - camera-mode regression: the `Camera` enum is still filled
    - help patch `help/HowTo Track hands on a texture.vl` + `help.xml`

## 9. Open questions for the orchestrator/owner

- **Q1.** Where do page-source commits live, given no remotes? Default: local submodule branch + superproject pointer bump. Alternative: keep page changes as a patch file in this repo and leave the submodule pinned at `d8a0c21`.
- **Q2.** Pin the page toolchain with a regenerated `package-lock.json` (then `npm ci`), or keep `npm install --no-package-lock` (reproduces upstream exactly today, but resolves ranges at install time)?
- **Q3.** Frame-id fields in camera mode: omit (recommended, flag 8) or add everywhere?

## 10. Artefacts from this run (outside the repo, not committed)

- `D:\claude\_tools\node\`: portable Node 24.21.0
- `D:\claude\_tools\mp-rebuild\npm\` (recipe A, with `_mpdist`), `...\yarn\` (recipe B), `...\npm-ci-attempt\` (failed `npm ci`)
- `D:\claude\_tools\mp-rebuild\scripts\`: `compare_web.py`, `ast_compare.mjs` (run from `...\npm\` so `rollup/parseAst` resolves), `smoke_page.mjs`, `vl_map.py`
- `D:\claude\_tools\mp-rebuild\evidence\`:
  - `compare_{npm,yarn}.out`
  - `smoke-{shipped,npm,yarn}.json` (smoke runs)
  - `vl_mediapipe_map.txt`, `vl_HandDetector.txt`, `vl_Timings.txt`
  - `dotnet-build.log`, `npm-ci-failure.log`
  - the two shipped bundle blobs (`e6a25de`, `8ca59f3`)
