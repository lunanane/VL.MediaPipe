using System.Text.Json;
using MediaPipe;
using MediaPipe.HandResults;
using Stride.Core.Mathematics;
using VL.Lib.Basics.Imaging;
using VL.MediaPipe.TimerResult;

// Checks FramePacket (wire format, gating, frame tags), the texture-input fields of the result POCOs, FrameTrace and
// the staging-slot choice of TaggedTextureReadback.
// usage: FramePacketCheck <output dir>   -> <dir>\*.bin + packets.json for test\framepacket\check_packets.mjs
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "results/framepacket-check");
Directory.CreateDirectory(outDir);
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    if (!ok) failures++;
}

byte[] Pattern(int width, int height, int seed) => Enumerable.Range(0, width * height * 4).Select(i => (byte)((i * 7 + seed) % 256)).ToArray();
IImage Image(byte[] data, int width, int height, PixelFormat format) => ImageExtensions.ToImage(data, width, height, format, format.ToString(), false);

// ---- wire format: packets for the Node parser ----------------------------------------------------------------
var cases = new List<Dictionary<string, object>>();
void WriteCase(string name, IImage image, uint frameId, double timestampMs, byte[] expectedRgba)
{
    var packet = FramePacket.Write(image, frameId, timestampMs);
    File.WriteAllBytes(Path.Combine(outDir, name + ".bin"), packet);
    cases.Add(new()
    {
        ["name"] = name, ["file"] = name + ".bin", ["format"] = image.Info.Format.ToString(),
        ["width"] = image.Info.Width, ["height"] = image.Info.Height, ["frameId"] = frameId, ["timestampMs"] = timestampMs,
        ["expectedRgbaBase64"] = Convert.ToBase64String(expectedRgba),
    });
    Check(packet.Length == FramePacket.HeaderSize + image.Info.Width * image.Info.Height * 4, $"{name}: packet is header + w*h*4 bytes ({packet.Length})");
}

var rgba = Pattern(3, 2, 1);
WriteCase("rgba8-3x2", Image(rgba, 3, 2, PixelFormat.R8G8B8A8), 0, 1234.5678, rgba);

var bgra = Pattern(259, 2, 5); // width 259: both bytes of the u16 are non-zero
var bgraAsRgba = (byte[])bgra.Clone();
for (int i = 0; i < bgraAsRgba.Length; i += 4) (bgraAsRgba[i], bgraAsRgba[i + 2]) = (bgraAsRgba[i + 2], bgraAsRgba[i]);
WriteCase("bgra8-259x2", Image(bgra, 259, 2, PixelFormat.B8G8R8A8), 0xDEADBEEF, 98765.4321, bgraAsRgba);

var rgbx = Pattern(2, 2, 9);
var rgbxOpaque = (byte[])rgbx.Clone();
for (int i = 3; i < rgbxOpaque.Length; i += 4) rgbxOpaque[i] = 255;
WriteCase("rgbx8-2x2", Image(rgbx, 2, 2, PixelFormat.R8G8B8X8), 7, 0, rgbxOpaque);

var padded = new PaddedImage(4, 3, extraPerRow: 5, seed: 3);
WriteCase("rgba8-4x3-padded-rows", padded, 42, 16.666, padded.ExpectedRgba());

File.WriteAllText(Path.Combine(outDir, "packets.json"), JsonSerializer.Serialize(new { cases }, new JsonSerializerOptions { WriteIndented = true }));

var header = FramePacket.Write(Image(rgba, 3, 2, PixelFormat.R8G8B8A8), 0x01020304, 1.5);
Check(header[0] == 'T' && header[1] == 'D' && header[2] == 'P' && header[3] == 'F' && header[4] == 1 && header[5] == 1, "header: magic TDPF, version 1, format 1");
Check(header[6] == 3 && header[7] == 0 && header[8] == 2 && header[9] == 0 && header[10] == 0 && header[11] == 0, "header: u16 width/height little-endian, reserved 0");
Check(header[12] == 4 && header[13] == 3 && header[14] == 2 && header[15] == 1 && BitConverter.ToDouble(header, 16) == 1.5, "header: u32 frame id and f64 timestamp little-endian");
var reuse = new byte[header.Length];
Check(ReferenceEquals(FramePacket.Write(Image(rgba, 3, 2, PixelFormat.R8G8B8A8), 1, 0, reuse), reuse), "Write reuses a buffer of the exact size");
try { FramePacket.Write(Image(new byte[4], 2, 2, PixelFormat.R8), 0, 0); Check(false, "R8 image is rejected"); }
catch (NotSupportedException) { Check(true, "R8 image is rejected with NotSupportedException"); }

// ---- Pack gating ---------------------------------------------------------------------------------------------
var owner = new object();
var frame = Image(Pattern(8, 4, 0), 8, 4, PixelFormat.R8G8B8A8);
bool Pack(object o, bool apply, string client, int queue, float ackTimeout, out IReadOnlyList<byte> packet, out int id, out int dropped, int tag = -1)
    => FramePacket.Pack(o, frame, tag, apply, client, queue, ackTimeout, out packet, out id, out dropped);
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
// The page's timers message acknowledges a frame: deserializing it sets Timers.FrameId.
Timers Echo(long? frameId) => JsonSerializer.Deserialize<TimerRoot>(
    frameId is long id ? $"{{\"timers\":{{\"detectTime\":1,\"frameId\":{id}}}}}" : "{\"timers\":{\"detectTime\":1}}", options)!.Timers;

Check(!Pack(owner, false, "127.0.0.1:1", 0, 1000, out _, out var idBefore, out _) && idBefore == -1, "Pack: frame id is -1 before the first packet");
var built = Pack(owner, true, "127.0.0.1:1", 0, 1000, out var p0, out var first, out _);
Check(built && first >= 0 && p0 is byte[] b0 && b0.Length == 24 + 8 * 4 * 4, $"Pack: first packet is built (frame id {first}, exact-size byte[])");
Check(!Pack(owner, true, "127.0.0.1:1", 0, 1000, out var pEmpty, out var idHeld, out var dropped1) && dropped1 >= 1 && idHeld == first && pEmpty.Count == 0,
    "Pack: nothing while the packet is unacknowledged (dropped counts, frame id stays)");
Echo(null);
Check(!Pack(owner, true, "127.0.0.1:1", 0, 1000, out _, out _, out _), "Pack: camera-mode timers (no frame id) do not acknowledge");
Echo(first + 1000);
Check(!Pack(owner, true, "127.0.0.1:1", 0, 1000, out _, out _, out _), "Pack: an echo of some other frame id does not acknowledge");
Echo(first);
Check(Pack(owner, true, "127.0.0.1:1", 0, 1000, out var p1, out var id1, out _) && id1 > first && !ReferenceEquals(p0, p1),
    "Pack: the echoed frame id releases the next packet (new id, different buffer)");
Echo(id1);
Check(!Pack(owner, true, "127.0.0.1:1", 3, 1000, out _, out _, out _), "Pack: nothing while the socket send queue is not empty");
Check(!Pack(owner, true, "", 0, 1000, out _, out _, out _), "Pack: nothing without a client");
Check(!Pack(owner, false, "127.0.0.1:1", 0, 1000, out _, out _, out _), "Pack: nothing without Apply");
Check(Pack(owner, true, "127.0.0.1:1", 0, 1000, out _, out var id2, out _) && id2 > id1, "Pack: next packet once the queue drained");
Check(Pack(owner, true, "127.0.0.1:1", 0, 0, out var p3, out var id3, out _) && id3 > id2, "Pack: an unacknowledged packet stops blocking after Ack Timeout");
Check(ReferenceEquals(p3, p0), "Pack: buffers cycle through a pool of 3");
Check(Pack(new object(), true, "127.0.0.1:1", 0, 1000, out _, out var otherId, out _) && otherId > id3, "Pack: frame ids are unique across owners");
Check(!FramePacket.Pack(null, frame, 5, true, "c", 0, 1000, out _, out var nullOwnerId, out _) && nullOwnerId == -1, "Pack: no owner, no packet");

// ---- frame tags and FrameTrace ----------------------------------------------------------------------------------
var trace = new RecordingListener();
FrameTrace.Listener = trace;
var tagOwner = new object();
Check(Pack(tagOwner, true, "c", 0, 0, out _, out var tagged, out _, tag: 1234), "Pack: a tagged packet is built");
Check(FramePacket.TagOf(tagged) == 1234, "TagOf: the packet id maps to the tag Pack was given");
Check(trace.Packets.Count == 1 && trace.Packets[0] == (tagged, 1234), "FrameTrace: OnPacket reports the packet id and tag");
Check(Pack(tagOwner, true, "c", 0, 0, out _, out var untagged, out _, tag: -7) && FramePacket.TagOf(untagged) == -1, "Pack: a negative tag is stored as -1 (untagged)");
Check(FramePacket.TagOf(tagged + 100_000) == -1 && FramePacket.TagOf(-1) == -1, "TagOf: unknown or negative frame ids give -1");
var taggedTimers = Echo(tagged);
taggedTimers.GetFrameInfo(out var tiId, out _, out var tiTag);
Check(taggedTimers.FrameTag == 1234 && tiId == tagged && tiTag == 1234, "timers JSON: FrameTag and GetFrameInfo's Frame Tag come from the echoed frame id");
var echoedHands = JsonSerializer.Deserialize<HandRoot>(
    $"{{\"handResults\":{{\"landmarks\":[[{{\"x\":0.1,\"y\":0.2,\"z\":0}}],[{{\"x\":0.3,\"y\":0.4,\"z\":0}}]],\"frameId\":{tagged}}},\"resolution\":{{\"width\":8,\"height\":4}},\"frameId\":{tagged}}}", options)!;
Check(echoedHands.HandResults.FrameTag == 1234, "handResults JSON: FrameTag resolved on deserialization");
var emptyHands = JsonSerializer.Deserialize<HandRoot>($"{{\"handResults\":{{\"landmarks\":[],\"frameId\":{untagged}}}}}", options)!;
Check(emptyHands.HandResults.FrameTag == -1, "handResults JSON: an untagged packet's result has FrameTag -1");
Check(trace.Messages.Any(m => m == ("timers", tagged, 1234, -1)), "FrameTrace: OnMessage reports timers with id and tag");
Check(trace.Messages.Any(m => m == ("handResults", tagged, 1234, 2)), "FrameTrace: OnMessage reports handResults with its hand count");
Check(trace.Messages.Any(m => m == ("handResults", untagged, -1, 0)), "FrameTrace: empty handResults are reported too (0 hands)");
var cameraHands = JsonSerializer.Deserialize<HandRoot>("{\"handResults\":{\"landmarks\":[]}}", options)!;
Check(cameraHands.HandResults.FrameTag == -1 && trace.Messages.Count == 3, "camera-mode results (no frame id): FrameTag -1, not traced");
FrameTrace.Listener = new ThrowingListener();
Check(Pack(tagOwner, true, "c", 0, 0, out _, out var afterThrow, out _, tag: 9) && FramePacket.TagOf(afterThrow) == 9 && FrameTrace.ListenerFailures == 1,
    "FrameTrace: a throwing listener is counted, the packet is still built");
FrameTrace.Listener = null;

// ---- helpers -------------------------------------------------------------------------------------------------
Int2 Readback(Int2 texture, int max) { FramePacket.ReadbackSize(max, texture, out var size); return size; }
Check(Readback(new Int2(1280, 720), 640) == new Int2(640, 360), "ReadbackSize 1280x720 max 640 -> 640x360");
Check(Readback(new Int2(576, 640), 320) == new Int2(288, 320), "ReadbackSize 576x640 max 320 -> 288x320");
Check(Readback(new Int2(640, 576), 0) == Int2.Zero, "ReadbackSize max 0 -> native (0,0)");
Check(Readback(new Int2(350, 345), 640) == Int2.Zero, "ReadbackSize already fits -> native (0,0)");
Check(FramePacket.IsTextureInput(InputSource.Texture, new object()) && !FramePacket.IsTextureInput(InputSource.Texture, null) && !FramePacket.IsTextureInput(InputSource.Camera, new object()),
    "IsTextureInput needs Source = Texture and a texture");
Check(FramePacket.PageSource(InputSource.Camera) == "camera" && FramePacket.PageSource(InputSource.Texture) == "texture", "PageSource values");

// ---- JSON binding of the texture-input fields (options as in VL.MediaPipe.vl: case-insensitive) ------------------
var cameraTimers = JsonSerializer.Deserialize<TimerRoot>("{\"timers\":{\"detectTime\":9,\"drawTime\":1,\"sourceFrameRate\":30}}", options)!.Timers;
cameraTimers.GetFrameInfo(out var camId, out var camLatency, out var camTag);
Check(cameraTimers.DetectTime == 9 && cameraTimers.FrameId is null && camId == -1 && camLatency == -1 && camTag == -1, "camera-mode timers JSON binds unchanged; Frame Id / Latency / Tag -1");
var sent = FramePacket.NowMs - 12.5;
var textureJson = $"{{\"timers\":{{\"detectTime\":9,\"drawTime\":1,\"sourceFrameRate\":58,\"frameId\":4000000000,\"sourceTimestamp\":{sent.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"ingestTime\":1234.56}}}}";
var textureTimers = JsonSerializer.Deserialize<TimerRoot>(textureJson, options)!.Timers;
textureTimers.GetFrameInfo(out var texId, out var texLatency, out _);
Check(textureTimers.FrameId == 4000000000L && textureTimers.IngestTime == 1234.56, "texture-mode timers JSON binds frameId (u32 range) and ingestTime");
Check(texLatency >= 12.5f && texLatency < 1000f, $"GetFrameInfo latency is receive time - sourceTimestamp ({texLatency:F2} ms)");
var hands = JsonSerializer.Deserialize<HandRoot>("{\"handResults\":{\"landmarks\":[],\"frameId\":17,\"sourceTimestamp\":5.5,\"ingestTime\":6.5},\"resolution\":{\"width\":350,\"height\":345},\"frameId\":17}", options)!;
Check(hands.HandResults.FrameId == 17 && hands.HandResults.SourceTimestamp == 5.5 && hands.Resolution.Width == 350, "handResults JSON binds the inner frameId");
Check(JsonSerializer.Serialize(new TimerRoot { Timers = textureTimers }).Contains("FrameTag") == false, "FrameTag is not part of the JSON contract");

// ---- TaggedTextureReadback staging ring vs a GPU model ----------------------------------------------------------
// Each frame stages a copy (tag = frame number) that the simulated GPU finishes 0..6 frames later. The ring must return
// what Stride's ImageReadback returns: the newest finished older copy without waiting, else the current one blocking.
{
    var ring = new TaggedReadbackRing(5);
    var rng = new Random(11);
    var slotFrame = new int[ring.Count];
    var readyAt = new int[ring.Count];
    Array.Fill(slotFrame, -1);
    int wrongTag = 0, notNewest = 0, delayOutOfRange = 0, blockingFrames = 0, results = 0, nonMonotonic = 0, lastNonBlockingTag = -1;
    var delays = new SortedDictionary<int, int>();
    for (int f = 0; f < 20_000; f++)
    {
        int slot = ring.Stage(f);
        slotFrame[slot] = f;
        readyAt[slot] = f + (rng.Next(10) < 8 ? rng.Next(0, 3) : rng.Next(3, 7));
        int readSlot = -1;
        bool available = ring.Resolve((s, doNotWait) =>
        {
            bool ok = !doNotWait || readyAt[s] <= f;
            if (ok) readSlot = s;
            return ok;
        }, out var tag, out var blocking);
        if (!available) continue;
        results++;
        if (blocking) blockingFrames++;
        if (tag != slotFrame[readSlot]) wrongTag++;
        int newestReady = Enumerable.Range(0, ring.Count).Where(s => s != slot && slotFrame[s] >= 0 && readyAt[s] <= f)
            .Select(s => slotFrame[s]).DefaultIfEmpty(-1).Max();
        if (!blocking && tag != newestReady) notNewest++;
        if (blocking && (tag != f || newestReady >= 0)) notNewest++;
        int delay = f - tag;
        delays[delay] = delays.GetValueOrDefault(delay) + 1;
        if (!blocking)
        {
            if (delay < 1 || delay > ring.Count - 1) delayOutOfRange++;
            // A blocking read returns the current frame; the next frame may return an older copy that finished late, as
            // Stride's ImageReadback does. Non-blocking reads never go back.
            if (tag < lastNonBlockingTag) nonMonotonic++;
            lastNonBlockingTag = tag;
        }
    }
    Check(results > 19_000 && wrongTag == 0, $"ring: every result carries the tag of the frame its slot was copied from ({results} results)");
    Check(notNewest == 0, "ring: the newest finished older copy is returned, the current copy only when none is ready (blocking)");
    Check(delayOutOfRange == 0 && nonMonotonic == 0, $"ring: non-blocking delays are 1..4 frames and never go back (delays {string.Join(" ", delays.Select(d => $"{d.Key}:{d.Value}"))}, blocking {blockingFrames})");
    var fresh = new TaggedReadbackRing(3);
    Check(fresh.Stage(7) == 0 && fresh.Resolve((_, _) => false, out var firstTag, out var firstBlocking) && firstBlocking && firstTag == 7,
        "ring: the first frame has no older copy, so it is read back blocking with its own tag");
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// An RGBA8 image whose rows carry extra bytes (ScanSize > width * 4), like many camera/readback buffers.
sealed class PaddedImage : IImage
{
    private readonly byte[] _data;
    private readonly int _scan;

    public PaddedImage(int width, int height, int extraPerRow, int seed)
    {
        Info = new ImageInfo(width, height, PixelFormat.R8G8B8A8, "padded");
        _scan = width * 4 + extraPerRow;
        _data = new byte[_scan * height];
        for (int i = 0; i < _data.Length; i++) _data[i] = (byte)((i % _scan) >= width * 4 ? 0xEE : (i * 13 + seed) % 256);
    }

    public ImageInfo Info { get; }
    public bool IsVolatile => false;
    public IImageData GetData() => new Data(_data, _scan);

    public byte[] ExpectedRgba()
    {
        var result = new byte[Info.Width * Info.Height * 4];
        for (int y = 0; y < Info.Height; y++) Array.Copy(_data, y * _scan, result, y * Info.Width * 4, Info.Width * 4);
        return result;
    }

    private sealed class Data : IImageData
    {
        public Data(byte[] bytes, int scan) { Bytes = bytes; ScanSize = scan; }
        public ReadOnlyMemory<byte> Bytes { get; }
        public int ScanSize { get; }
        public void Dispose() { }
    }
}

sealed class RecordingListener : IFrameTraceListener
{
    public readonly List<(long Id, int Tag)> Packets = new();
    public readonly List<(string Kind, long Id, int Tag, int Detections)> Messages = new();
    public void OnPacket(long frameId, int frameTag, double timestampMs) => Packets.Add((frameId, frameTag));
    public void OnMessage(string kind, long frameId, int frameTag, double receivedMs, int detections) => Messages.Add((kind, frameId, frameTag, detections));
}

sealed class ThrowingListener : IFrameTraceListener
{
    public void OnPacket(long frameId, int frameTag, double timestampMs) => throw new InvalidOperationException("listener failure");
    public void OnMessage(string kind, long frameId, int frameTag, double receivedMs, int detections) => throw new InvalidOperationException("listener failure");
}
