using System.Runtime.CompilerServices;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using VL.Lib.Basics.Imaging;
using StridePixelFormat = Stride.Graphics.PixelFormat;
using VLPixelFormat = VL.Lib.Basics.Imaging.PixelFormat;

[assembly: InternalsVisibleTo("FramePacketCheck")]

namespace MediaPipe;

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
            return; // this frame is already copied

        _scaler ??= new ImageScaler(SamplingPattern.Linear);
        _scaler.SetInput(0, input);
        _scaler.SetOutput(_target!);
        _scaler.Draw(context);
        context.CommandList.Copy(_target!, _staging[_ring!.Stage(_tag, _updates)]);
        _stagedTag = _tag;
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
