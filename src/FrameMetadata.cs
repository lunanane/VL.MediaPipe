using System.Text.Json.Serialization;

// Texture input (Source = Texture) only: the page adds frameId, sourceTimestamp and ingestTime to every results
// object and to timers. In camera mode the fields are absent and stay null, so upstream JSON binds unchanged.
//   FrameId          the frame id of the packet the result was computed from (FramePacket.FrameId)
//   SourceTimestamp  the packet's timestamp, echoed back (FramePacket.NowMs clock, i.e. vvvv's clock)
//   IngestTime       when the page received the packet, on the page's performance.now() clock

namespace MediaPipe.HandResults
{
    public partial class Handresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace MediaPipe.GestureResults
{
    public partial class Gestureresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace VL.MediaPipe.PoseResults
{
    public partial class Poseresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace MediaPipe.FaceLandmarkResults
{
    public partial class Facelandmarkresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace MediaPipe.FaceDetectorResults
{
    public partial class Facedetectorresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace VL.MediaPipe.ObjectResults
{
    public partial class Objectresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace VL.MediaPipe.ImageResults
{
    public partial class Imageresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace VL.MediaPipe.ImageEmbedderResults
{
    public partial class Imageembedderresults
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }
    }
}

namespace VL.MediaPipe.TimerResult
{
    public partial class Timers
    {
        private long? _frameId;

        /// <summary>The page sends timers after it has handled a frame, so setting FrameId (JSON deserialization) acknowledges that frame to FramePacket.Pack.</summary>
        public long? FrameId
        {
            get => _frameId;
            set
            {
                _frameId = value;
                if (value is long id)
                    global::MediaPipe.FramePacket.Acknowledge(id);
            }
        }

        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        /// <summary>FramePacket.NowMs when this timers message was deserialized, i.e. when vvvv received it.</summary>
        [JsonIgnore]
        public double ReceivedTime { get; } = global::MediaPipe.FramePacket.NowMs;

        /// <summary>
        /// Frame Id: the texture frame these timers belong to (-1 for camera input).
        /// Frame Latency: milliseconds from building the frame packet to receiving these timers, both on vvvv's clock
        /// (-1 for camera input). It includes the page's detection (DetectTime) and drawing (DrawTime).
        /// </summary>
        public void GetFrameInfo(out int frameId, out float frameLatency)
        {
            frameId = FrameId is long id ? unchecked((int)id) : -1;
            frameLatency = SourceTimestamp is double sent ? (float)(ReceivedTime - sent) : -1f;
        }
    }
}
