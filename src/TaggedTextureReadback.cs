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
/// VL.Stride's TextureToImage with a tag per frame: the texture is scaled into an R8G8B8A8_UNorm render target and read
/// back through a ring of staging textures exactly like Stride's ImageReadback (the newest older copy that maps without
/// waiting, the current copy blocking only when no older one is ready), and the tag given with the texture comes back
/// with the image that was copied from it. VL.MediaPipe.vl's texture path uses it so a camera frame id (Input Frame Tag)
/// reaches the frame packet, although the readback returns a copy from 1 to 4 frames earlier.
/// </summary>
/// <remarks>
/// Use it as TextureToImage is used: call <see cref="Readback"/> in the update and schedule this renderer in the same
/// frame (RendererScheduler). Readback returns what the previous frame's draw read back. Main thread only (vvvv updates
/// and draws on one thread).
/// </remarks>
public sealed class TaggedTextureReadback : IGraphicsRendererBase, IDisposable
{
    /// <summary>TextureToImage's Frame Delay (VL.Stride ReadBackTexture's default).</summary>
    public const int DefaultFrameDelay = 5;

    private const StridePixelFormat TargetFormat = StridePixelFormat.R8G8B8A8_UNorm;

    private readonly List<Texture> _staging = new();
    private readonly Func<int, bool, bool> _readSlot;
    private ImageScaler? _scaler;
    private Texture? _target;
    private TaggedReadbackRing? _ring;
    private CommandList? _commandList;
    private byte[] _result = Array.Empty<byte>();

    private readonly int _frameDelay;

    private Texture? _input;
    private Int2 _size;
    private int _tag = -1;

    private bool _available;
    private int _resultTag = -1;
    private int _resultWidth;
    private int _resultHeight;

    /// <summary>Creates the readback; GPU resources are created on the first draw.</summary>
    /// <param name="frameDelay">Staging textures in the ring (clamped to 1..16), as ReadBackTexture's Frame Delay. Create a
    /// new readback to change it (VL.MediaPipe.vl creates it in a Cache region with Frame Delay as its input).</param>
    public TaggedTextureReadback(int frameDelay = DefaultFrameDelay)
    {
        _frameDelay = Math.Clamp(frameDelay, 1, 16);
        _readSlot = ReadSlot;
    }

    /// <summary>
    /// Hands the texture of this frame to the next draw and returns the image the previous draw read back.
    /// </summary>
    /// <param name="texture">The texture to read back; nothing is drawn while it is null.</param>
    /// <param name="size">Read-back size; a component of 0 keeps the texture's own width or height.</param>
    /// <param name="frameTag">Identifies what the texture shows (e.g. a camera frame id); -1 for none.</param>
    /// <param name="image">The read-back pixels (R8G8B8A8, volatile: valid during this update), or null.</param>
    /// <param name="imageTag">The frameTag that was given with the texture the image was copied from; -1 without a result.</param>
    /// <returns>True when the previous draw read back an image (TextureToImage's Result Available).</returns>
    public bool Readback(Texture? texture, Int2 size, int frameTag, out IImage? image, out int imageTag)
    {
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

    /// <summary>True when the last draw had to wait for the GPU (no older copy was ready): increase the frame delay.</summary>
    public bool IsBlocking { get; private set; }

    void IGraphicsRendererBase.Draw(RenderDrawContext context)
    {
        var input = _input;
        if (input is null)
            return;
        int width = _size.X > 0 ? _size.X : input.ViewWidth;
        int height = _size.Y > 0 ? _size.Y : input.ViewHeight;
        EnsureTarget(context.GraphicsDevice, width, height);

        _scaler ??= new ImageScaler(SamplingPattern.Linear);
        _scaler.SetInput(0, input);
        _scaler.SetOutput(_target!);
        _scaler.Draw(context);

        _commandList = context.CommandList;
        context.CommandList.Copy(_target!, _staging[_ring!.Stage(_tag)]);
        _available = _ring.Resolve(_readSlot, out _resultTag, out var blocking);
        IsBlocking = blocking;
        _commandList = null;
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
    }

    public void Dispose()
    {
        DisposeStaging();
        _target?.Dispose();
        _target = null;
        _scaler?.Dispose();
        _scaler = null;
        _input = null;
    }
}

/// <summary>
/// The staging-slot bookkeeping of Stride's ImageReadback&lt;T&gt;.DrawCore (Stride.Rendering 4.2), with the tag of the
/// frame each slot was copied from. Separate from the GPU calls so the slot choice can be checked without a device.
/// </summary>
internal sealed class TaggedReadbackRing
{
    private readonly bool[] _used;
    private readonly int[] _tags;
    private int _current;

    public TaggedReadbackRing(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Expecting a value > 0");
        _used = new bool[count];
        _tags = new int[count];
        Array.Fill(_tags, -1);
    }

    public int Count => _used.Length;

    /// <summary>Marks the current slot as holding the frame tagged tag and returns it: copy the frame into that slot.</summary>
    public int Stage(int tag)
    {
        _used[_current] = true;
        _tags[_current] = tag;
        return _current;
    }

    /// <summary>
    /// Reads one slot back like ImageReadback: from the newest older copy to the oldest, the first readSlot(slot, doNotWait:
    /// true) that succeeds; the current slot (just staged) only when no older one is ready, blocking (readSlot(slot,
    /// false), taken as read). Then advances to the next slot.
    /// </summary>
    /// <returns>True when a slot was read; tag is that slot's tag (-1 otherwise), blocking tells whether it waited.</returns>
    public bool Resolve(Func<int, bool, bool> readSlot, out int tag, out bool blocking)
    {
        tag = -1;
        blocking = false;
        var available = false;
        for (int age = Count - 1; !available && age >= 0; age--)
        {
            int slot = (_current + age) % Count;
            if (!_used[slot])
                continue;
            if (age == 0)
            {
                readSlot(slot, false);
                blocking = true;
                available = true;
            }
            else
            {
                available = readSlot(slot, true);
            }
            if (available)
                tag = _tags[slot];
        }
        _current = (_current + 1) % Count;
        return available;
    }
}
