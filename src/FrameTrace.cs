namespace MediaPipe;

/// <summary>
/// Receives, for texture input, every frame packet VL.MediaPipe builds and every results or timers message of the page
/// it deserializes (empty results included), with the frame id and frame tag. For measurement tools; see
/// <see cref="FrameTrace"/>.
/// </summary>
public interface IFrameTraceListener
{
    /// <summary>FramePacket.Pack built packet frameId from a texture tagged frameTag (-1: untagged) at timestampMs (FramePacket.NowMs).</summary>
    void OnPacket(long frameId, int frameTag, double timestampMs);

    /// <summary>
    /// A message of the page was deserialized at receivedMs (FramePacket.NowMs, on the thread that receives the WebSocket
    /// messages). kind is its key ("handResults", "timers", ...); frameId the packet it was computed from and frameTag
    /// that packet's tag (-1 when unknown); detections the number of hands, poses, faces, objects, classifications or
    /// embeddings it holds (-1 for timers).
    /// </summary>
    void OnMessage(string kind, long frameId, int frameTag, double receivedMs, int detections);
}

/// <summary>
/// The process-wide <see cref="IFrameTraceListener"/>. Without one (the default) tracing costs a null check. A listener
/// must be fast and thread-safe; its exceptions are caught and counted in <see cref="ListenerFailures"/>, so a broken
/// listener never breaks the texture input.
/// </summary>
public static class FrameTrace
{
    private static volatile IFrameTraceListener? s_listener;
    private static long s_failures;

    /// <summary>The listener that receives packets and messages; null (the default) turns tracing off.</summary>
    public static IFrameTraceListener? Listener
    {
        get => s_listener;
        set => s_listener = value;
    }

    /// <summary>Exceptions thrown by listeners so far.</summary>
    public static long ListenerFailures => Interlocked.Read(ref s_failures);

    internal static void Packet(long frameId, int frameTag, double timestampMs)
    {
        var listener = s_listener;
        if (listener is null)
            return;
        try
        {
            listener.OnPacket(frameId, frameTag, timestampMs);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref s_failures);
        }
    }

    internal static void Message(string kind, long frameId, int frameTag, double receivedMs, int detections)
    {
        var listener = s_listener;
        if (listener is null)
            return;
        try
        {
            listener.OnMessage(kind, frameId, frameTag, receivedMs, detections);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref s_failures);
        }
    }
}
