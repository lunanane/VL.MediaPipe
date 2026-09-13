using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;

[assembly: ImportAsIs(Namespace = "VL.MediaPipe.TextureHarness", Category = "MediaPipe.Test")]

namespace VL.MediaPipe.TextureHarness;

/// <summary>
/// Test-only sink for test\TextureInputHarness.vl: watches the MediaPipe node's outputs while it runs on a texture
/// and writes sentinel lines into vvvv's log under the category VL.MediaPipe, which scripts\run-texture-harness.ps1 greps.
/// </summary>
[ProcessNode(Name = "TextureInputProbe")]
public sealed class TextureInputProbe : IDisposable
{
    public const string LogCategory = "VL.MediaPipe";

    private readonly ILogger _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private object? _landmarks;
    private int _frameId = -1;
    private float _frameLatency = -1;
    private int _detectionTime;
    private int _targetFrames = 150;

    private IDynamicEnum? _camera;
    private IDisposable? _cameraSubscription;
    private int _cameraEvents;
    private int _cameraEventsAtSubscribe;
    private bool _deviceListLogged;

    private int _firstFrameId = -1;
    private int _lastFrameId = -1;
    private int _uniqueFrames;
    private int _nonIncreasing;
    private long _gapFrames;
    private int _framesWithHand;
    private bool _firstHandLogged;
    private readonly List<double> _latency = new();
    private readonly List<double> _detect = new();
    private readonly List<double> _added = new();
    private bool _done;

    public TextureInputProbe(NodeContext nodeContext)
    {
        _log = nodeContext.AppHost.LoggerFactory.CreateLogger(LogCategory);
        _log.LogInformation("TEXTURE HARNESS START");
    }

    /// <summary>HandDetector's Landmarks.</summary>
    public object? Landmarks { set => _landmarks = value; }

    /// <summary>Timings' Frame Id.</summary>
    [DefaultValue(-1)]
    public int FrameId { set => _frameId = value; }

    /// <summary>Timings' Frame Latency (ms).</summary>
    [DefaultValue(-1f)]
    public float FrameLatency { set => _frameLatency = value; }

    /// <summary>Timings' Detection Time.</summary>
    public int DetectionTime { set => _detectionTime = value; }

    /// <summary>A Camera value (any CamerasEnum): its definition is filled by the page's webcamDevices message.</summary>
    public IDynamicEnum? Camera { set => _camera = value; }

    /// <summary>Latency samples (frames after the first detected hand) to collect before logging the summary.</summary>
    [DefaultValue(150)]
    public int TargetFrames { set => _targetFrames = Math.Max(1, value); }

    public void Update()
    {
        WatchDeviceList();
        if (_frameId < 0 || _frameId == _lastFrameId)
            return;

        if (_lastFrameId >= 0)
        {
            if (_frameId < _lastFrameId) _nonIncreasing++;
            else _gapFrames += _frameId - _lastFrameId - 1;
        }
        if (_firstFrameId < 0)
        {
            _firstFrameId = _frameId;
            _log.LogInformation("TEXTURE HARNESS FIRST FRAME ID {FrameId} at {Seconds:F1} s", _frameId, _clock.Elapsed.TotalSeconds);
        }
        _lastFrameId = _frameId;
        _uniqueFrames++;

        int landmarkCount = FirstHandLandmarkCount(_landmarks, out var wrist, out var indexTip);
        if (landmarkCount > 0)
            _framesWithHand++;

        if (_firstHandLogged && _frameLatency >= 0)
        {
            _latency.Add(_frameLatency);
            _detect.Add(_detectionTime);
            _added.Add(_frameLatency - _detectionTime);
        }
        if (!_firstHandLogged && landmarkCount > 0)
        {
            _firstHandLogged = true;
            _log.LogInformation(
                "TEXTURE HARNESS FIRST HAND landmarks={Count} frameId={FrameId} wrist={Wrist} indexTip={IndexTip} at {Seconds:F1} s",
                landmarkCount, _frameId, wrist, indexTip, _clock.Elapsed.TotalSeconds);
        }
        if (_uniqueFrames % 60 == 0)
        {
            _log.LogInformation("TEXTURE HARNESS PROGRESS frames={Frames} withHand={WithHand} lastFrameId={FrameId} latencyMs={Latency:F1}",
                _uniqueFrames, _framesWithHand, _frameId, _frameLatency);
        }
        if (!_done && _latency.Count >= _targetFrames)
        {
            _done = true;
            LogSummary(landmarkCount);
        }
    }

    /// <summary>True once the summary has been logged.</summary>
    public bool Done => _done;

    public void Dispose() => _cameraSubscription?.Dispose();

    private void WatchDeviceList()
    {
        var definition = _camera?.Definition;
        if (definition is null)
            return;
        if (_cameraSubscription is null)
        {
            _cameraSubscription = definition.OnChange.Subscribe(_ => Interlocked.Increment(ref _cameraEvents));
            _cameraEventsAtSubscribe = _cameraEvents;
            _log.LogInformation("TEXTURE HARNESS CAMERA ENUM watched: entries={Entries} eventsAtSubscribe={Events}",
                definition.Entries.Count, _cameraEventsAtSubscribe);
        }
        if (!_deviceListLogged && (_cameraEvents > _cameraEventsAtSubscribe || definition.Entries.Count > 0))
        {
            _deviceListLogged = true;
            _log.LogInformation("TEXTURE HARNESS DEVICE LIST received: entries={Entries} labels=[{Labels}] events={Events} at {Seconds:F1} s",
                definition.Entries.Count, string.Join("|", definition.Entries), _cameraEvents - _cameraEventsAtSubscribe, _clock.Elapsed.TotalSeconds);
        }
    }

    private void LogSummary(int lastLandmarkCount)
    {
        _log.LogInformation(
            "TEXTURE HARNESS FRAME IDS first={First} last={Last} unique={Unique} gaps={Gaps} nonIncreasing={NonIncreasing} withHand={WithHand} lastLandmarks={Landmarks}",
            _firstFrameId, _lastFrameId, _uniqueFrames, _gapFrames, _nonIncreasing, _framesWithHand, lastLandmarkCount);
        _log.LogInformation(
            "TEXTURE HARNESS LATENCY n={N} median={Median:F2} p90={P90:F2} min={Min:F2} max={Max:F2} mean={Mean:F2} ms (packet built -> timers received, vvvv clock)",
            _latency.Count, Quantile(_latency, 0.5), Quantile(_latency, 0.9), _latency.Min(), _latency.Max(), _latency.Average());
        _log.LogInformation(
            "TEXTURE HARNESS DETECT median={Median:F1} p90={P90:F1} ms; ADDED (latency - detectTime) median={AddedMedian:F2} p90={AddedP90:F2} ms",
            Quantile(_detect, 0.5), Quantile(_detect, 0.9), Quantile(_added, 0.5), Quantile(_added, 0.9));
        var definition = _camera?.Definition;
        _log.LogInformation("TEXTURE HARNESS DEVICE LIST final: received={Received} entries={Entries} events={Events}",
            _deviceListLogged, definition?.Entries.Count ?? -1, _cameraEvents - _cameraEventsAtSubscribe);
        _log.LogInformation("TEXTURE HARNESS DONE after {Seconds:F1} s", _clock.Elapsed.TotalSeconds);
    }

    private static double Quantile(List<double> values, double q)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * (sorted.Count - 1) + 0.5))];
    }

    // Landmarks is MediaPipe.HandResults.Landmark[][] (hands x 21); read through reflection so this assembly does not
    // reference VL.MediaPipe.dll. Landmark.Position is the page's normalized (x, y, z).
    private static int FirstHandLandmarkCount(object? landmarks, out string wrist, out string indexTip)
    {
        wrist = "";
        indexTip = "";
        if (landmarks is not IEnumerable hands)
            return 0;
        foreach (var hand in hands)
        {
            if (hand is not IEnumerable points)
                return 0;
            var list = points.Cast<object?>().ToList();
            if (list.Count > 0) wrist = Describe(list[0]);
            if (list.Count > 8) indexTip = Describe(list[8]);
            return list.Count;
        }
        return 0;
    }

    private static string Describe(object? landmark)
    {
        var position = landmark?.GetType().GetProperty("Position")?.GetValue(landmark);
        if (position is null) return "?";
        var type = position.GetType();
        string Component(string name) => Convert.ToDouble(type.GetField(name)?.GetValue(position) ?? double.NaN).ToString("F4", CultureInfo.InvariantCulture);
        return $"({Component("X")},{Component("Y")},{Component("Z")})";
    }
}
