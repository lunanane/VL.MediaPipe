using System.Text.Json.Serialization;

// Texture input (Source = Texture) only: the page adds frameId, sourceTimestamp and ingestTime to every results
// object and to timers. In camera mode the fields are absent and stay null, so upstream JSON binds unchanged.
//   FrameId          the frame id of the packet the result was computed from (FramePacket.FrameId)
//   SourceTimestamp  the packet's timestamp, echoed back (FramePacket.NowMs clock, i.e. vvvv's clock)
//   IngestTime       when the page received the packet, on the page's performance.now() clock
//   FrameTag         not sent by the page: the Frame Tag Pack was given for that packet (MediaPipe's Input Frame Tag,
//                    e.g. a camera frame id), looked up when the message is deserialized; -1 without one

namespace MediaPipe
{
    internal static class FrameMetadata
    {
        /// <summary>A message of the page was deserialized: its frame tag, reported to FrameTrace.</summary>
        internal static int Received(string kind, long? frameId, int detections)
        {
            if (frameId is not long id)
                return -1;
            var tag = FramePacket.TagOf(id);
            FrameTrace.Message(kind, id, tag, FramePacket.NowMs, detections);
            return tag;
        }
    }
}

namespace MediaPipe.HandResults
{
    public partial class Handresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("handResults", FrameId, Landmarks?.Length ?? 0);
    }
}

namespace MediaPipe.GestureResults
{
    public partial class Gestureresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("gestureResults", FrameId, Gestures?.Length ?? 0);
    }
}

namespace VL.MediaPipe.PoseResults
{
    public partial class Poseresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("poseResults", FrameId, Landmarks?.Length ?? 0);
    }
}

namespace MediaPipe.FaceLandmarkResults
{
    public partial class Facelandmarkresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("faceLandmarkResults", FrameId, FaceLandmarks?.Length ?? 0);
    }
}

namespace MediaPipe.FaceDetectorResults
{
    public partial class Facedetectorresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("faceDetectorResults", FrameId, Faces?.Length ?? 0);
    }
}

namespace VL.MediaPipe.ObjectResults
{
    public partial class Objectresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("objectResults", FrameId, Objects?.Length ?? 0);
    }
}

namespace VL.MediaPipe.ImageResults
{
    public partial class Imageresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("imageResults", FrameId, Classifications?.Length ?? 0);
    }
}

namespace VL.MediaPipe.ImageEmbedderResults
{
    public partial class Imageembedderresults : IJsonOnDeserialized
    {
        public long? FrameId { get; set; }
        public double? SourceTimestamp { get; set; }
        public double? IngestTime { get; set; }

        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("imageEmbedderResults", FrameId, Embeddings?.Length ?? 0);
    }
}

namespace VL.MediaPipe.TimerResult
{
    public partial class Timers : IJsonOnDeserialized
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

        /// <summary>The Frame Tag of the frame these timers belong to (-1 for camera input or an untagged frame).</summary>
        [JsonIgnore]
        public int FrameTag { get; private set; } = -1;

        /// <summary>FramePacket.NowMs when this timers message was deserialized, i.e. when vvvv received it.</summary>
        [JsonIgnore]
        public double ReceivedTime { get; } = global::MediaPipe.FramePacket.NowMs;

        void IJsonOnDeserialized.OnDeserialized() =>
            FrameTag = global::MediaPipe.FrameMetadata.Received("timers", FrameId, -1);

        /// <summary>
        /// Frame Id: the texture frame these timers belong to (-1 for camera input).
        /// Frame Latency: milliseconds from building the frame packet to receiving these timers, both on vvvv's clock
        /// (-1 for camera input). It includes the page's detection (DetectTime) and drawing (DrawTime).
        /// Frame Tag: the Input Frame Tag given with that frame's texture, e.g. its camera frame id (-1 without one).
        /// </summary>
        public void GetFrameInfo(out int frameId, out float frameLatency, out int frameTag)
        {
            frameId = FrameId is long id ? unchecked((int)id) : -1;
            frameLatency = SourceTimestamp is double sent ? (float)(ReceivedTime - sent) : -1f;
            frameTag = FrameTag;
        }
    }
}
