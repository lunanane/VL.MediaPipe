using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using VL.Lib.Basics.Imaging;
using StridePixelFormat = Stride.Graphics.PixelFormat;
using VLPixelFormat = VL.Lib.Basics.Imaging.PixelFormat;

[assembly: InternalsVisibleTo("FramePacketCheck")]

namespace MediaPipe;

/// <summary>When the texture input's frame is read back and sent (MediaPipe's Readback Mode pin).</summary>
public enum ReadbackMode
{
    /// <summary>The draw of frame f copies the texture; update f + 1 reads the copy and the patch packs and sends it:
    /// one render frame (~16.7 ms at 60 Hz) between the texture and the send, no wait for the GPU in the draw.</summary>
    NextUpdate,
    /// <summary>The draw of frame f copies the texture, waits for the GPU to finish the copy (a blocking map) and sends
    /// the packet right there: the frame leaves in the vvvv frame it was rendered in, at the cost of that wait.</summary>
    SameFrame,
}

/// <summary>
/// VL.Stride's TextureToImage with a tag per frame, read back as early as the GPU allows: the draw scales the texture into
/// an R8G8B8A8_UNorm render target and copies it into a ring of staging textures, and the next update reads the newest
/// copy back. The tag given with the texture comes back with the image that was copied from it, so a camera frame id
/// (MediaPipe's Input Frame Tag) reaches the frame packet. VL.MediaPipe.vl's texture path uses it.
/// </summary>
/// <remarks>
/// <para>Use it as TextureToImage is used: call <see cref="Readback"/> in the update and schedule this renderer in the same
/// frame (RendererScheduler). Main thread only (vvvv updates and draws on one thread; the read in the update uses the
/// command list of the last draw).</para>
/// <para>A copy made in the draw of frame f is usually still queued on the GPU when update f + 1 runs. Frame Delay says how
/// many frames a copy may lag before the update waits for it: with 1, update f + 1 waits for the GPU to finish frame f's
/// copy (the image is one frame old); with more, the update only takes copies that are already finished and waits only for
/// one that is Frame Delay frames old. Older unread copies are dropped when a newer one is read, so no copy is returned
/// twice. A tagged texture is copied once per tag: while the tag does not change the draw copies nothing.</para>
/// <para>With <see cref="ReadbackMode.SameFrame"/> (<see cref="Configure"/>, before Readback) the draw does the rest too:
/// when FramePacket.Pack would send a frame now (a client, the previous packet acknowledged or timed out, a new tag) it
/// copies the texture, waits for the GPU (blocking map), builds the packet and hands it to the WebSocketServer's
/// SendAsync. Readback then returns no image, so the patch's Pack and SendData stay idle. This relies on the renderers
/// that produce the texture being drawn before this one in the same frame, as they are when the texture's producer was
/// updated (and so scheduled) before RendererScheduler scheduled this renderer. The environment variable
/// MP_READBACK_MODE (NextUpdate / SameFrame) overrides the mode, for A/B measurements with unchanged patches;
/// MP_READBACK_TRACE names a file that gets one JSON line per same-frame send (tag, wait, mean red of the pixels).</para>
/// </remarks>
public sealed class TaggedTextureReadback : IGraphicsRendererBase, IDisposable
{
    /// <summary>The Frame Delay VL.MediaPipe.vl uses: frame f's image is read in update f + 1, waiting for the GPU if needed.</summary>
    public const int DefaultFrameDelay = 1;

    private const StridePixelFormat TargetFormat = StridePixelFormat.R8G8B8A8_UNorm;

    private readonly List<Texture> _staging = new();
    private readonly Func<int, bool, bool> _readSlot;
    private readonly int _frameDelay;
    private ImageScaler? _scaler;
    private Texture? _target;
    private TaggedReadbackRing? _ring;
    private CommandList? _commandList;
    private byte[] _result = Array.Empty<byte>();

    private Texture? _input;
    private Int2 _size;
    private int _tag = -1;
    private int _stagedTag = -1;
    private long _updates;

    private bool _available;
    private int _resultTag = -1;
    private int _resultWidth;
    private int _resultHeight;

    // Same-frame mode (Configure)
    private static readonly ReadbackMode? s_modeOverride =
        Enum.TryParse<ReadbackMode>(Environment.GetEnvironmentVariable("MP_READBACK_MODE"), ignoreCase: true, out var m) ? m : null;
    private static readonly object s_traceLock = new();
    private static StreamWriter? s_trace;
    private static bool s_traceOpened;
    private ReadbackMode _mode;
    private object? _sender;
    private string? _client;
    private int _sendQueueLength;
    private float _ackTimeout = 1000;
    private Texture? _sameFrameStaging;
    private Task<bool>? _sendTask;

    /// <summary>Creates the readback; GPU resources are created on the first draw.</summary>
    /// <param name="frameDelay">Frames a copy may lag before the update waits for the GPU (clamped to 1..16), also the number
    /// of staging textures. Create a new readback to change it (VL.MediaPipe.vl creates it in a Cache region with Frame
    /// Delay as its input).</param>
    public TaggedTextureReadback(int frameDelay = DefaultFrameDelay)
    {
        _frameDelay = Math.Clamp(frameDelay, 1, 16);
        _readSlot = ReadSlot;
    }

    /// <summary>
    /// Hands the texture of this frame to the next draw and returns the newest image read back so far.
    /// </summary>
    /// <param name="texture">The texture to read back; nothing is drawn while it is null.</param>
    /// <param name="size">Read-back size; a component of 0 keeps the texture's own width or height.</param>
    /// <param name="frameTag">Identifies what the texture shows (e.g. a camera frame id); -1 for none.</param>
    /// <param name="image">The read-back pixels (R8G8B8A8, volatile: valid during this update), or null.</param>
    /// <param name="imageTag">The frameTag that was given with the texture the image was copied from; -1 without a result.</param>
    /// <returns>True once an image has been read back (TextureToImage's Result Available). The image stays the same until a
    /// newer copy is read: a tag that repeats is a frame already seen.</returns>
    public bool Readback(Texture? texture, Int2 size, int frameTag, out IImage? image, out int imageTag)
    {
        _updates++;
        if (_mode == ReadbackMode.SameFrame)
        {
            // the draw sends; nothing for the patch's Pack
            _input = texture;
            _size = size;
            _tag = frameTag < 0 ? -1 : frameTag;
            image = null;
            imageTag = -1;
            return false;
        }
        if (_ring is not null && _commandList is not null && _ring.Read(_readSlot, _updates, _frameDelay, out var tag, out var blocking))
        {
            _available = true;
            _resultTag = tag;
            IsBlocking = blocking;
        }
        _input = texture;
        _size = size;
        _tag = frameTag < 0 ? -1 : frameTag;
        if (!_available)
        {
            image = null;
            imageTag = -1;
            return false;
        }
        image = _result.ToImage(_resultWidth, _resultHeight, VLPixelFormat.R8G8B8A8, nameof(StridePixelFormat.R8G8B8A8_UNorm), isVolatile: true);
        imageTag = _resultTag;
        return true;
    }

    /// <summary>True when the last read had to wait for the GPU to finish the copy.</summary>
    public bool IsBlocking { get; private set; }

    /// <summary>
    /// Chooses when frames are read back and sent; call it in the update before <see cref="Readback"/>. With
    /// <see cref="ReadbackMode.SameFrame"/> the draw packs (FramePacket.Pack's rules, acknowledgement included) and sends
    /// through sender itself, and Readback returns no image.
    /// </summary>
    /// <param name="mode">NextUpdate (the patch packs and sends in the next update) or SameFrame. MP_READBACK_MODE overrides it.</param>
    /// <param name="sender">The WebSocketServer (Pack's owner): its SendAsync (Advanced) sends the packets.</param>
    /// <param name="client">The client the packets go to (as Pack's Client).</param>
    /// <param name="sendQueueLength">The WebSocketServer's Send Queue Length (as Pack's).</param>
    /// <param name="ackTimeout">As Pack's Ack Timeout, in milliseconds.</param>
    /// <param name="activeMode">The mode in effect (after the environment override).</param>
    /// <param name="waitMs">Milliseconds the last same-frame send waited for the GPU in the draw (the blocking map).</param>
    /// <param name="problem">Why the last same-frame send failed, or empty.</param>
    public void Configure(ReadbackMode mode, object? sender, string? client, int sendQueueLength, float ackTimeout,
        out ReadbackMode activeMode, out float waitMs, out string problem)
    {
        _mode = s_modeOverride ?? mode;
        _sender = sender;
        _client = client;
        _sendQueueLength = sendQueueLength;
        _ackTimeout = ackTimeout;
        activeMode = _mode;
        waitMs = LastWaitMs;
        problem = Problem;
    }

    /// <summary>Milliseconds the last same-frame send waited for the GPU in the draw.</summary>
    public float LastWaitMs { get; private set; }

    /// <summary>Why the last same-frame send failed, or empty.</summary>
    public string Problem { get; private set; } = "";

    void IGraphicsRendererBase.Draw(RenderDrawContext context)
    {
        var input = _input;
        if (input is null)
            return;
        int width = _size.X > 0 ? _size.X : input.ViewWidth;
        int height = _size.Y > 0 ? _size.Y : input.ViewHeight;
        EnsureTarget(context.GraphicsDevice, width, height);
        _commandList = context.CommandList;
        if (_tag >= 0 && _tag == _stagedTag)
            return; // this frame is already copied (same-frame mode: already sent)
        if (_mode == ReadbackMode.SameFrame)
        {
            DrawSameFrame(context, input);
            return;
        }

        _scaler ??= new ImageScaler(SamplingPattern.Linear);
        _scaler.SetInput(0, input);
        _scaler.SetOutput(_target!);
        _scaler.Draw(context);
        context.CommandList.Copy(_target!, _staging[_ring!.Stage(_tag, _updates)]);
        _stagedTag = _tag;
    }

    private void DrawSameFrame(RenderDrawContext context, Texture input)
    {
        var sender = _sender;
        if (sender is null || string.IsNullOrEmpty(_client))
            return;
        // A send still leaving the socket counts as a queued send, like the patch's Send Queue Length.
        int queue = _sendQueueLength + (_sendTask is { IsCompleted: false } ? 1 : 0);
        if (!FramePacket.CanSend(sender, _tag, _client, queue, _ackTimeout))
            return; // nothing would be sent: no copy, no wait

        _scaler ??= new ImageScaler(SamplingPattern.Linear);
        _scaler.SetInput(0, input);
        _scaler.SetOutput(_target!);
        _scaler.Draw(context);
        _sameFrameStaging ??= _target!.ToStaging();
        context.CommandList.Copy(_target!, _sameFrameStaging);
        long start = Stopwatch.GetTimestamp();
        _sameFrameStaging.GetData(context.CommandList, _result, 0, 0, doNotWait: false);
        LastWaitMs = (float)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        _stagedTag = _tag;

        var image = _result.ToImage(_resultWidth, _resultHeight, VLPixelFormat.R8G8B8A8, nameof(StridePixelFormat.R8G8B8A8_UNorm), isVolatile: true);
        if (!FramePacket.BuildChecked(sender, image, _tag, out var packet, out var frameId))
            return;
        _sendTask = WebSocketSend.Send(sender, _client!, (byte[])packet);
        Problem = _sendTask is null ? WebSocketSend.Problem : "";
        Trace(frameId);
    }

    private void Trace(int frameId)
    {
        lock (s_traceLock)
        {
            if (!s_traceOpened)
            {
                s_traceOpened = true;
                var path = Environment.GetEnvironmentVariable("MP_READBACK_TRACE");
                if (!string.IsNullOrEmpty(path))
                {
                    try { s_trace = new StreamWriter(path, append: true) { AutoFlush = true }; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s_trace = null; }
                }
            }
            if (s_trace is null)
                return;
            long sum = 0;
            for (int i = 0; i < _result.Length; i += 4)
                sum += _result[i];
            double mean = _result.Length > 0 ? sum * 4.0 / _result.Length : 0;
            s_trace.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{{\"kind\": \"same_frame_send\", \"tag\": {_tag}, \"frame_id\": {frameId}, \"t_ms\": {FramePacket.NowMs:F3}, \"wait_ms\": {LastWaitMs:F3}, \"mean_r\": {mean:F3}, \"sent\": {(_sendTask is not null ? "true" : "false")}}}"));
        }
    }

    private bool ReadSlot(int slot, bool doNotWait) =>
        _staging[slot].GetData(_commandList!, _result, 0, 0, doNotWait);

    private void EnsureTarget(GraphicsDevice device, int width, int height)
    {
        if (_target is null || _target.Width != width || _target.Height != height)
        {
            DisposeStaging();
            _target?.Dispose();
            _target = Texture.New2D(device, width, height, TargetFormat, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
        }
        if (_ring is null) // first draw, or the target was recreated
        {
            DisposeStaging();
            for (int i = 0; i < _frameDelay; i++)
                _staging.Add(_target.ToStaging());
            _ring = new TaggedReadbackRing(_frameDelay);
            _result = new byte[_target.CalculatePixelDataCount<byte>()];
            _resultWidth = width;
            _resultHeight = height;
        }
    }

    private void DisposeStaging()
    {
        foreach (var texture in _staging)
            texture.Dispose();
        _staging.Clear();
        _sameFrameStaging?.Dispose();
        _sameFrameStaging = null;
        _ring = null;
        _available = false;
        _resultTag = -1;
        _stagedTag = -1;
    }

    public void Dispose()
    {
        DisposeStaging();
        _target?.Dispose();
        _target = null;
        _scaler?.Dispose();
        _scaler = null;
        _input = null;
        _commandList = null;
    }
}

/// <summary>
/// The staging-slot bookkeeping of <see cref="TaggedTextureReadback"/>: which slots hold an unread copy, the tag and the
/// update each copy was made after. Separate from the GPU calls so the slot choice can be checked without a device.
/// </summary>
internal sealed class TaggedReadbackRing
{
    private readonly bool[] _pending;
    private readonly int[] _tags;
    private readonly long[] _stagedAt;
    private int _next;

    public TaggedReadbackRing(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Expecting a value > 0");
        _pending = new bool[count];
        _tags = new int[count];
        _stagedAt = new long[count];
        Array.Fill(_tags, -1);
    }

    public int Count => _pending.Length;

    /// <summary>Takes the next slot for a copy of the frame tagged tag, made in the draw after update: copy into it.</summary>
    public int Stage(int tag, long update)
    {
        int slot = _next;
        _pending[slot] = true;
        _tags[slot] = tag;
        _stagedAt[slot] = update;
        _next = (slot + 1) % Count;
        return slot;
    }

    /// <summary>
    /// Goes through the unread copies from the newest to the oldest and reads the first one that is finished
    /// (readSlot(slot, doNotWait: true)) or, when it is not, was made frameDelay or more updates ago: that one is waited for
    /// (readSlot(slot, false)). A read drops every older unread copy, so at most frameDelay copies are ever unread.
    /// </summary>
    /// <returns>True when a slot was read; tag is its tag (-1 otherwise), blocking tells whether it waited.</returns>
    public bool Read(Func<int, bool, bool> readSlot, long update, int frameDelay, out int tag, out bool blocking)
    {
        tag = -1;
        blocking = false;
        for (int age = 1; age <= Count; age++)
        {
            int slot = (_next - age + Count) % Count;
            if (!_pending[slot])
                break; // copies older than a read one were dropped
            if (readSlot(slot, true))
                return Take(age, out tag);
            if (update - _stagedAt[slot] >= frameDelay)
            {
                readSlot(slot, false);
                blocking = true;
                return Take(age, out tag);
            }
        }
        return false;
    }

    /// <summary>The copy <paramref name="age"/> stagings old was read: it and every older copy are no longer unread.</summary>
    private bool Take(int age, out int tag)
    {
        tag = _tags[(_next - age + Count) % Count];
        for (int older = age; older <= Count; older++)
            _pending[(_next - older + Count) % Count] = false;
        return true;
    }
}
