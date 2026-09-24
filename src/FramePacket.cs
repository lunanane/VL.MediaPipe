using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using VL.Lib.Basics.Imaging;

namespace MediaPipe;

/// <summary>
/// Where the MediaPipe node takes its frames from.
/// </summary>
public enum InputSource
{
    /// <summary>A webcam opened by the browser page (the upstream behaviour).</summary>
    Camera,
    /// <summary>The Input Texture pin, read back and sent to the page as binary frame packets.</summary>
    Texture,
}

/// <summary>
/// Texture input (Source = Texture): builds the binary frame packets the MediaPipe page decodes.
/// </summary>
/// <remarks>
/// Wire format, little-endian: "TDPF" | u8 version (1) | u8 pixel format (1 = RGBA8, 2 = BGRA8) | u16 width |
/// u16 height | u16 reserved | u32 frame id | f64 timestamp in ms (NowMs clock) | width * height * 4 pixel bytes,
/// rows top-down.
/// This assembly is imported without ImportAsIs, so there is no [ProcessNode] here: the per-sender state behind
/// Pack lives in a weak table keyed by the owner object the patch passes in (its WebSocketServer).
/// A frame tag (e.g. a camera frame id, MediaPipe's Input Frame Tag) does not travel in the packet: Pack records it per
/// frame id, and the results and timers the page echoes that frame id with look it up (<see cref="TagOf"/>).
/// </remarks>
public static class FramePacket
{
    /// <summary>Bytes before the pixel data.</summary>
    public const int HeaderSize = 24;

    /// <summary>Wire format version written into byte 4.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Pixel format code for R8G8B8A8 pixels.</summary>
    public const byte Rgba8 = 1;

    /// <summary>Pixel format code for B8G8R8A8 pixels.</summary>
    public const byte Bgra8 = 2;

    // VL.IO.WebSocket's SendAsync (Advanced) passes a byte[] straight to the socket but copies any other
    // IReadOnlyList<byte> element by element, so packets are exact-size arrays. At most one packet is in flight
    // (see Pack); cycling a few buffers adds a margin so a buffer is never rewritten while a send could read it.
    private const int PoolSize = 3;

    // Frame tags of the most recent packets, indexed by frame id modulo the length (ids are consecutive process-wide):
    // results arrive long before 256 newer packets have been built.
    private const int TagHistory = 256;

    private sealed class Sender
    {
        public readonly byte[]?[] Pool = new byte[]?[PoolSize];
        public int PoolIndex;
        public long LastFrameId = -1;
        public long InFlightFrameId = -1;
        public double InFlightSince;
        public int DroppedFrames;
        public int AckTimeouts;
        public int LastTag = -1;
    }

    private static readonly ConditionalWeakTable<object, Sender> s_senders = new();

    // Frame ids are unique across all senders, so an id the page echoes back identifies exactly one packet. The
    // page's timers message is the acknowledgement: Timers.FrameId records it here while VL.MediaPipe.vl
    // deserializes the message (the patch cannot link its timers channel into the operation that sends frames).
    private static int s_frameIdCounter = -1;
    private static readonly long[] s_echoedFrameIds = Enumerable.Repeat(-1L, 64).ToArray();
    private static int s_echoIndex;
    private static readonly object s_echoLock = new();

    private static readonly long[] s_taggedFrameIds = Enumerable.Repeat(-1L, TagHistory).ToArray();
    private static readonly int[] s_frameTags = new int[TagHistory];
    private static readonly object s_tagLock = new();

    internal static void Acknowledge(long frameId)
    {
        lock (s_echoLock)
        {
            s_echoedFrameIds[s_echoIndex] = frameId;
            s_echoIndex = (s_echoIndex + 1) % s_echoedFrameIds.Length;
        }
    }

    private static bool IsAcknowledged(long frameId)
    {
        lock (s_echoLock)
        {
            return Array.IndexOf(s_echoedFrameIds, frameId) >= 0;
        }
    }

    private static void RecordTag(long frameId, int frameTag)
    {
        int index = (int)(frameId % TagHistory);
        lock (s_tagLock)
        {
            s_taggedFrameIds[index] = frameId;
            s_frameTags[index] = frameTag;
        }
    }

    /// <summary>
    /// The frame tag Pack was given for the packet with this frame id; -1 when the packet had none, is unknown or is more
    /// than 256 packets old.
    /// </summary>
    public static int TagOf(long frameId)
    {
        if (frameId < 0)
            return -1;
        int index = (int)(frameId % TagHistory);
        lock (s_tagLock)
        {
            return s_taggedFrameIds[index] == frameId ? s_frameTags[index] : -1;
        }
    }

    /// <summary>Milliseconds on a monotonic clock (Stopwatch). Packet timestamps use it, and so does latency on receipt.</summary>
    public static double NowMs => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency);

    /// <summary>
    /// Builds the next frame packet from image when apply is true, a client is connected, the previous packet's send
    /// has left the queue and the page has acknowledged it (its timers echoed the frame id), or ackTimeout ms passed.
    /// A tagged image whose frame tag is the one of this sender's last packet is not sent again.
    /// Returns true on the frames a packet was built: connect to SendData's Apply, and packet to its Data.
    /// </summary>
    /// <param name="owner">Identifies the sender whose state (buffers, in-flight packet) is used: the WebSocketServer.</param>
    /// <param name="image">The read-back image: R8G8B8A8 or B8G8R8A8 (R8G8B8X8 / B8G8R8X8 are sent with alpha 255).</param>
    /// <param name="frameTag">What image shows, e.g. a camera frame id (TaggedTextureReadback's Image Tag); -1 for none.
    /// Results and timers of this packet report it as their FrameTag.</param>
    /// <param name="apply">Build a packet from image on this frame (the readback's Result).</param>
    /// <param name="client">The WebSocket client the packet goes to; nothing is built while it is empty.</param>
    /// <param name="sendQueueLength">The WebSocketServer's Send Queue Length; nothing is built while a send is queued.</param>
    /// <param name="ackTimeout">Milliseconds after which an unacknowledged packet stops blocking the next one. A finished
    /// socket send only means the bytes reached the OS, so without the acknowledgement frames pile up in socket buffers
    /// while the page is still detecting.</param>
    /// <param name="packet">The packet, a byte[] of exactly HeaderSize + width * height * 4 bytes (empty when none was built).</param>
    /// <param name="frameId">Frame id of this sender's latest packet, -1 before the first. The page echoes it in every result.</param>
    /// <param name="droppedFrames">Frames skipped so far because the previous packet was still in flight.</param>
    public static bool Pack(object? owner, IImage? image, int frameTag, bool apply, string? client, int sendQueueLength, float ackTimeout,
        out IReadOnlyList<byte> packet, out int frameId, out int droppedFrames)
    {
        packet = Array.Empty<byte>();
        frameId = -1;
        droppedFrames = 0;
        if (owner is null)
            return false;
        var sender = s_senders.GetValue(owner, _ => new Sender());
        frameId = unchecked((int)sender.LastFrameId);
        droppedFrames = sender.DroppedFrames;
        if (!apply || image is null)
            return false;
        if (!Gate(sender, frameTag, client, sendQueueLength, ackTimeout))
        {
            droppedFrames = sender.DroppedFrames;
            return false;
        }
        return Build(sender, image, frameTag, out packet, out frameId);
    }

    /// <summary>
    /// Pack's checks without an image: true when Pack would build a packet for a frame tagged frameTag now. A frame
    /// refused because the previous packet is still in flight counts as dropped, as in Pack. The same-frame readback asks
    /// this before it copies and maps a texture, so it never stalls on a frame that would not be sent.
    /// </summary>
    internal static bool CanSend(object owner, int frameTag, string? client, int sendQueueLength, float ackTimeout) =>
        Gate(s_senders.GetValue(owner, _ => new Sender()), frameTag, client, sendQueueLength, ackTimeout);

    /// <summary>Builds the packet Pack builds once <see cref="CanSend"/> said yes (same sender state, frame ids and tags).</summary>
    internal static bool BuildChecked(object owner, IImage image, int frameTag, out IReadOnlyList<byte> packet, out int frameId) =>
        Build(s_senders.GetValue(owner, _ => new Sender()), image, frameTag, out packet, out frameId);

    /// <summary>Frames this sender skipped so far because the previous packet was still in flight.</summary>
    internal static int DroppedFramesOf(object owner) => s_senders.GetValue(owner, _ => new Sender()).DroppedFrames;

    private static bool Gate(Sender sender, int frameTag, string? client, int sendQueueLength, float ackTimeout)
    {
        if (string.IsNullOrEmpty(client))
            return false;
        if (frameTag >= 0 && frameTag == sender.LastTag)
            return false; // the readback repeats its newest image until a newer copy is read: this frame was sent

        var now = NowMs;
        if (sender.InFlightFrameId >= 0)
        {
            if (IsAcknowledged(sender.InFlightFrameId))
            {
                sender.InFlightFrameId = -1;
            }
            else if (now - sender.InFlightSince < ackTimeout)
            {
                ++sender.DroppedFrames;
                return false;
            }
            else
            {
                sender.AckTimeouts++;
                sender.InFlightFrameId = -1;
            }
        }
        if (sendQueueLength > 0)
        {
            ++sender.DroppedFrames;
            return false;
        }
        return true;
    }

    private static bool Build(Sender sender, IImage image, int frameTag, out IReadOnlyList<byte> packet, out int frameId)
    {
        packet = Array.Empty<byte>();
        frameId = unchecked((int)sender.LastFrameId);
        var now = NowMs;
        byte[] buffer;
        var id = unchecked((uint)Interlocked.Increment(ref s_frameIdCounter));
        try
        {
            buffer = Write(image, id, now, sender.Pool[sender.PoolIndex]);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            return false;
        }
        var tag = frameTag < 0 ? -1 : frameTag;
        RecordTag(id, tag);
        sender.Pool[sender.PoolIndex] = buffer;
        sender.PoolIndex = (sender.PoolIndex + 1) % PoolSize;
        sender.InFlightFrameId = id;
        sender.InFlightSince = now;
        sender.LastFrameId = id;
        sender.LastTag = tag;
        frameId = unchecked((int)id);
        packet = buffer;
        FrameTrace.Packet(id, tag, now);
        return true;
    }

    /// <summary>
    /// Writes one frame packet for image into buffer (reused when it already has the exact size, otherwise a new array).
    /// </summary>
    public static byte[] Write(IImage image, uint frameId, double timestampMs, byte[]? buffer = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var info = image.Info;
        var (format, opaque) = info.Format switch
        {
            PixelFormat.R8G8B8A8 => (Rgba8, false),
            PixelFormat.B8G8R8A8 => (Bgra8, false),
            PixelFormat.R8G8B8X8 => (Rgba8, true),
            PixelFormat.B8G8R8X8 => (Bgra8, true),
            _ => throw new NotSupportedException(
                $"FramePacket needs an 8-bit RGBA or BGRA image, got {info.Format}. Read the texture back with New Format R8G8B8A8_UNorm."),
        };
        int width = info.Width;
        int height = info.Height;
        if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
            throw new ArgumentException($"FramePacket image size {width}x{height} is outside 1..65535.");

        int rowBytes = width * 4;
        int size = HeaderSize + rowBytes * height;
        if (buffer is null || buffer.Length != size)
            buffer = new byte[size];

        var packet = buffer.AsSpan();
        packet[0] = (byte)'T';
        packet[1] = (byte)'D';
        packet[2] = (byte)'P';
        packet[3] = (byte)'F';
        packet[4] = FormatVersion;
        packet[5] = format;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(8), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12), frameId);
        BinaryPrimitives.WriteDoubleLittleEndian(packet.Slice(16), timestampMs);

        using var data = image.GetData();
        var source = data.Bytes.Span;
        int scan = data.ScanSize > 0 ? data.ScanSize : rowBytes;
        if (scan < rowBytes || source.Length < (long)scan * (height - 1) + rowBytes)
            throw new ArgumentException($"FramePacket image data ({source.Length} bytes, {scan} per row) is too small for {width}x{height}.");

        var pixels = packet.Slice(HeaderSize);
        if (scan == rowBytes)
        {
            source.Slice(0, pixels.Length).CopyTo(pixels);
        }
        else
        {
            for (int y = 0; y < height; y++)
                source.Slice(y * scan, rowBytes).CopyTo(pixels.Slice(y * rowBytes, rowBytes));
        }
        if (opaque)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }
        return buffer;
    }

    /// <summary>
    /// The readback Size for a texture of textureSize: zero (native size) when maxInputSize is 0 or the texture
    /// already fits, otherwise scaled down so its longer side is maxInputSize, keeping the aspect ratio.
    /// </summary>
    /// <remarks>An out parameter on purpose: vvvv's legacy import names the output of an Int2 -> Int2 method differently
    /// ("ReadbackSize doesn't have a pin called Result").</remarks>
    public static void ReadbackSize(int maxInputSize, Int2 textureSize, out Int2 size)
    {
        int longer = Math.Max(textureSize.X, textureSize.Y);
        if (maxInputSize <= 0 || longer <= maxInputSize || textureSize.X <= 0 || textureSize.Y <= 0)
        {
            size = Int2.Zero;
            return;
        }
        double scale = (double)maxInputSize / longer;
        size = new Int2(Math.Max(1, (int)Math.Round(textureSize.X * scale)), Math.Max(1, (int)Math.Round(textureSize.Y * scale)));
    }

    /// <summary>True when source is Texture and a texture is connected.</summary>
    public static bool IsTextureInput(InputSource source, object? texture) => source == InputSource.Texture && texture is not null;

    /// <summary>The value of the page's Source config key ("camera" or "texture").</summary>
    public static string PageSource(InputSource source) => source == InputSource.Texture ? "texture" : "camera";
}
