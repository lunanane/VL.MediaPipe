"""Applies the texture-input changes to VL.MediaPipe.vl (plus frame tags: public GetHands, HandDetector / Timings Frame Tag).

Starts from the upstream document in git (default: bd17c52, the fork point before any texture-input edit of the
.vl), inserts XML snippets at anchors that must each match exactly once, and writes VL.MediaPipe.vl with CRLF line
endings like the committed file. Ids of new elements are derived from their names (sha256 -> 22 base62
characters), so re-running produces a byte-identical document.

    python scripts/vl/patch_mediapipe_vl.py [--base bd17c52] [--check]

--check exits 1 if the working-tree VL.MediaPipe.vl differs from what the script would write.

Hand-authored .vl rules followed here (docs/TEXTURE-INPUT-NOTES.md): 22-character base62 Ids, no XML comments,
links from outside into region nodes directly, IO pins mirrored by hidden-linked ControlPoints.
Once vvvv has re-saved VL.MediaPipe.vl, that file is the source of truth and this script is stale.
"""
import argparse
import hashlib
import re
import subprocess
import sys
import xml.dom.minidom
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DOC = REPO / "VL.MediaPipe.vl"
UPSTREAM_BASE = "bd17c52"
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"


def make_id(name: str) -> str:
    digest = int.from_bytes(hashlib.sha256(f"VL.MediaPipe.vl/texture-input/{name}".encode()).digest(), "big")
    chars = []
    for _ in range(22):
        digest, r = divmod(digest, 62)
        chars.append(ALPHABET[r])
    chars[0] = ALPHABET[10 + int.from_bytes(hashlib.md5(name.encode()).digest()[:1], "big") % 26]  # upper-case first, like vvvv
    return "".join(chars)


def expand(snippet: str) -> str:
    return re.sub(r"@ID\(([^)]+)\)", lambda m: make_id(m.group(1)), snippet)


# ---------------------------------------------------------------------------------------------------------------
# MediaPipe: pins
UPDATE_PINS = r'''
            <Pin Id="@ID(pin-source)" Name="Source" Kind="InputPin" DefaultValue="Camera" Summary="Camera: the page opens the webcam chosen by Camera (upstream behaviour). Texture: the page tracks the frames of Input Texture.">
              <p:TypeAnnotation LastCategoryFullName="MediaPipe" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="TypeFlag" Name="InputSource" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(pin-texture)" Name="Input Texture" Kind="InputPin" Summary="Source = Texture: the frames to track. They are read back as R8G8B8A8_UNorm, so pass a texture whose values are the colors to send (not an sRGB-decoded view).">
              <p:TypeAnnotation LastCategoryFullName="Stride.API.Graphics" LastDependency="VL.Stride.Runtime.TypeForwards.vl">
                <Choice Kind="TypeFlag" Name="Texture" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(pin-tag)" Name="Input Frame Tag" Kind="InputPin" DefaultValue="-1" Summary="Source = Texture: what Input Texture shows in this frame, e.g. a camera frame id. The results and timings computed from that frame report it as their Frame Tag. The readback sends a frame one frame later, so this tag, not a value read next to the results, tells which frame they belong to. -1: no tag.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(pin-maxsize)" Name="Max Input Size" Kind="InputPin" DefaultValue="0" Summary="Source = Texture: 0 sends the texture at its own size. Otherwise it is scaled down on the GPU before readback so that its longer side is at most this many pixels.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(pin-readbackmode)" Name="Readback Mode" Kind="InputPin" DefaultValue="NextUpdate" Summary="Source = Texture: NextUpdate reads each frame back in the next vvvv update and sends it from there (one render frame later, no wait in the draw). SameFrame sends it from the draw that rendered it, after waiting for the GPU to finish the copy: one render frame less latency, at the cost of that wait. The environment variable MP_READBACK_MODE overrides this pin.">
              <p:TypeAnnotation LastCategoryFullName="MediaPipe" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="TypeFlag" Name="ReadbackMode" />
              </p:TypeAnnotation>
            </Pin>'''

# MediaPipe: canvas elements (Source sender, texture path in an If region)
CANVAS = r'''
            <ControlPoint Id="@ID(cp-source)" Bounds="2010,110" />
            <ControlPoint Id="@ID(cp-texture)" Bounds="2230,110" />
            <ControlPoint Id="@ID(cp-tag)" Bounds="2340,110" />
            <ControlPoint Id="@ID(cp-maxsize)" Bounds="2450,110" />
            <ControlPoint Id="@ID(cp-readbackmode)" Bounds="2560,110" />
            <Node Bounds="2008,160,70,19" Id="@ID(n-pagesource)">
              <p:NodeReference LastCategoryFullName="MediaPipe.FramePacket" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                <Choice Kind="OperationCallFlag" Name="PageSource" />
              </p:NodeReference>
              <Pin Id="@ID(pagesource-source)" Name="Source" Kind="InputPin" />
              <Pin Id="@ID(pagesource-result)" Name="Result" Kind="OutputPin" />
            </Node>
            <Pad Id="@ID(pad-source-name)" Comment="Name" Bounds="2090,195,45,15" ShowValueBox="true" isIOBox="true" Value="Source">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="String" />
              </p:TypeAnnotation>
            </Pad>
            <Node Bounds="2008,230,84,19" Id="@ID(n-stringsender)">
              <p:NodeReference LastCategoryFullName="MediaPipe" LastDependency="VL.MediaPipe.vl">
                <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                <Choice Kind="ProcessAppFlag" Name="StringSender" />
              </p:NodeReference>
              <Pin Id="@ID(ss-ctx)" Name="Node Context" Kind="InputPin" IsHidden="true" />
              <Pin Id="@ID(ss-input)" Name="Input" Kind="InputPin" />
              <Pin Id="@ID(ss-name)" Name="Name" Kind="InputPin" />
              <Pin Id="@ID(ss-value)" Name="Value" Kind="InputPin" />
            </Node>
            <Node Bounds="2228,160,90,19" Id="@ID(n-istexture)">
              <p:NodeReference LastCategoryFullName="MediaPipe.FramePacket" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                <Choice Kind="OperationCallFlag" Name="IsTextureInput" />
              </p:NodeReference>
              <Pin Id="@ID(istexture-source)" Name="Source" Kind="InputPin" />
              <Pin Id="@ID(istexture-texture)" Name="Texture" Kind="InputPin" />
              <Pin Id="@ID(istexture-result)" Name="Result" Kind="OutputPin" />
            </Node>
            <Node Bounds="2200,210,640,420" Id="@ID(region-if)">
              <p:NodeReference LastCategoryFullName="Primitive" LastDependency="Builtin">
                <Choice Kind="StatefulRegion" Name="Region (Stateful)" Fixed="true" />
                <CategoryReference Kind="Category" Name="Primitive" />
                <Choice Kind="ApplicationStatefulRegion" Name="If" />
              </p:NodeReference>
              <Pin Id="@ID(if-condition)" Name="Condition" Kind="InputPin" />
              <Patch Id="@ID(if-patch)" ManuallySortedPins="true">
                <Patch Id="@ID(if-create)" Name="Create" ManuallySortedPins="true" />
                <Patch Id="@ID(if-then)" Name="Then" ManuallySortedPins="true" />
                <Node Bounds="2228,250,85,19" Id="@ID(n-info)">
                  <p:NodeReference LastCategoryFullName="Stride.Textures.Utils" LastDependency="VL.Stride.Graphics.vl">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="OperationCallFlag" Name="Info" />
                    <CategoryReference Kind="Category" Name="Utils" NeedsToBeDirectParent="true">
                      <p:OuterCategoryReference Kind="Category" Name="Textures" NeedsToBeDirectParent="true" />
                    </CategoryReference>
                  </p:NodeReference>
                  <Pin Id="@ID(info-input)" Name="Input" Kind="InputPin" />
                  <Pin Id="@ID(info-viewsize)" Name="View Size" Kind="OutputPin" />
                </Node>
                <Node Bounds="2228,300,90,19" Id="@ID(n-readbacksize)">
                  <p:NodeReference LastCategoryFullName="MediaPipe.FramePacket" LastDependency="VL.MediaPipe.dll">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="OperationCallFlag" Name="ReadbackSize" />
                  </p:NodeReference>
                  <Pin Id="@ID(rs-maxsize)" Name="Max Input Size" Kind="InputPin" />
                  <Pin Id="@ID(rs-texturesize)" Name="Texture Size" Kind="InputPin" />
                  <Pin Id="@ID(rs-result)" Name="Size" Kind="OutputPin" />
                </Node>
                <Pad Id="@ID(pad-framedelay)" Comment="Frame Delay" Bounds="2602,225,35,15" ShowValueBox="true" isIOBox="true" Value="1">
                  <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                    <Choice Kind="TypeFlag" Name="Integer32" />
                  </p:TypeAnnotation>
                </Pad>
                <Node Bounds="2600,250,140,60" Id="@ID(region-cache)">
                  <p:NodeReference LastCategoryFullName="Primitive" LastDependency="Builtin">
                    <Choice Kind="StatefulRegion" Name="Region (Stateful)" Fixed="true" />
                    <Choice Kind="ProcessStatefulRegion" Name="Cache" />
                    <CategoryReference Kind="Category" Name="Primitive" />
                  </p:NodeReference>
                  <ControlPoint Id="@ID(cache-cp-framedelay)" Bounds="2614,256" Alignment="Top" />
                  <ControlPoint Id="@ID(cache-cp-readback)" Bounds="2614,304" Alignment="Bottom" />
                  <Pin Id="@ID(cache-force)" Name="Force" Kind="InputPin" />
                  <Pin Id="@ID(cache-dispose)" Name="Dispose Cached Outputs" Kind="InputPin" DefaultValue="True">
                    <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                      <Choice Kind="TypeFlag" Name="Boolean" />
                    </p:TypeAnnotation>
                  </Pin>
                  <Pin Id="@ID(cache-changed)" Name="Has Changed" Kind="OutputPin" />
                  <Patch Id="@ID(cache-patch)" ManuallySortedPins="true">
                    <Patch Id="@ID(cache-create)" Name="Create" ManuallySortedPins="true" />
                    <Patch Id="@ID(cache-then)" Name="Then" ManuallySortedPins="true" />
                    <Node Bounds="2612,270,120,19" Id="@ID(n-readback-create)">
                      <p:NodeReference LastCategoryFullName="MediaPipe.TaggedTextureReadback" LastDependency="VL.MediaPipe.dll">
                        <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                        <CategoryReference Kind="AssemblyCategory" Name="TaggedTextureReadback" />
                        <Choice Kind="OperationCallFlag" Name="Create" />
                      </p:NodeReference>
                      <Pin Id="@ID(readback-create-framedelay)" Name="Frame Delay" Kind="InputPin" />
                      <Pin Id="@ID(readback-create-output)" Name="Output" Kind="StateOutputPin" />
                    </Node>
                  </Patch>
                </Node>
                <Node Bounds="2400,322,190,19" Id="@ID(n-configure)">
                  <p:NodeReference LastCategoryFullName="MediaPipe.TaggedTextureReadback" LastDependency="VL.MediaPipe.dll">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <CategoryReference Kind="AssemblyCategory" Name="TaggedTextureReadback" NeedsToBeDirectParent="true" />
                    <Choice Kind="OperationCallFlag" Name="Configure" />
                  </p:NodeReference>
                  <Pin Id="@ID(configure-input)" Name="Input" Kind="StateInputPin" />
                  <Pin Id="@ID(configure-mode)" Name="Mode" Kind="InputPin" />
                  <Pin Id="@ID(configure-sender)" Name="Sender" Kind="InputPin" />
                  <Pin Id="@ID(configure-client)" Name="Client" Kind="InputPin" />
                  <Pin Id="@ID(configure-queue)" Name="Send Queue Length" Kind="InputPin" />
                  <Pin Id="@ID(configure-acktimeout)" Name="Ack Timeout" Kind="InputPin" DefaultValue="1000" />
                  <Pin Id="@ID(configure-output)" Name="Output" Kind="StateOutputPin" />
                  <Pin Id="@ID(configure-activemode)" Name="Active Mode" Kind="OutputPin" />
                  <Pin Id="@ID(configure-waitms)" Name="Wait Ms" Kind="OutputPin" />
                  <Pin Id="@ID(configure-problem)" Name="Problem" Kind="OutputPin" />
                </Node>
                <Node Bounds="2228,350,300,19" Id="@ID(n-readback)">
                  <p:NodeReference LastCategoryFullName="MediaPipe.TaggedTextureReadback" LastDependency="VL.MediaPipe.dll">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <CategoryReference Kind="AssemblyCategory" Name="TaggedTextureReadback" NeedsToBeDirectParent="true" />
                    <Choice Kind="OperationCallFlag" Name="Readback" />
                  </p:NodeReference>
                  <Pin Id="@ID(readback-input)" Name="Input" Kind="StateInputPin" />
                  <Pin Id="@ID(readback-texture)" Name="Texture" Kind="InputPin" />
                  <Pin Id="@ID(readback-size)" Name="Size" Kind="InputPin" />
                  <Pin Id="@ID(readback-frametag)" Name="Frame Tag" Kind="InputPin" />
                  <Pin Id="@ID(readback-output)" Name="Output" Kind="StateOutputPin" />
                  <Pin Id="@ID(readback-result)" Name="Result" Kind="OutputPin" />
                  <Pin Id="@ID(readback-image)" Name="Image" Kind="OutputPin" />
                  <Pin Id="@ID(readback-imagetag)" Name="Image Tag" Kind="OutputPin" />
                </Node>
                <Node Bounds="2600,390,101,19" Id="@ID(n-scheduler)">
                  <p:NodeReference LastCategoryFullName="Stride.Utils" LastDependency="VL.Stride.Games.vl">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="ProcessAppFlag" Name="RendererScheduler" />
                  </p:NodeReference>
                  <Pin Id="@ID(scheduler-ctx)" Name="Node Context" Kind="InputPin" IsHidden="true" />
                  <Pin Id="@ID(scheduler-renderer)" Name="Renderer" Kind="InputPin" />
                  <Pin Id="@ID(scheduler-output)" Name="Output" Kind="OutputPin" />
                </Node>
                <Node Bounds="2468,420,80,19" Id="@ID(n-firstclient)">
                  <p:NodeReference LastCategoryFullName="Collections.Sequence" LastDependency="VL.CoreLib.vl">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="OperationCallFlag" Name="FirstOrDefault" />
                  </p:NodeReference>
                  <Pin Id="@ID(first-input)" Name="Input" Kind="StateInputPin" />
                  <Pin Id="@ID(first-default)" Name="Default Value" Kind="InputPin" />
                  <Pin Id="@ID(first-output)" Name="Output" Kind="StateOutputPin" />
                  <Pin Id="@ID(first-result)" Name="Result" Kind="OutputPin" />
                </Node>
                <Node Bounds="2228,460,360,19" Id="@ID(n-pack)">
                  <p:NodeReference LastCategoryFullName="MediaPipe.FramePacket" LastDependency="VL.MediaPipe.dll">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="OperationCallFlag" Name="Pack" />
                  </p:NodeReference>
                  <Pin Id="@ID(pack-owner)" Name="Owner" Kind="InputPin" />
                  <Pin Id="@ID(pack-image)" Name="Image" Kind="InputPin" />
                  <Pin Id="@ID(pack-frametag)" Name="Frame Tag" Kind="InputPin" />
                  <Pin Id="@ID(pack-apply)" Name="Apply" Kind="InputPin" />
                  <Pin Id="@ID(pack-client)" Name="Client" Kind="InputPin" />
                  <Pin Id="@ID(pack-queue)" Name="Send Queue Length" Kind="InputPin" />
                  <Pin Id="@ID(pack-acktimeout)" Name="Ack Timeout" Kind="InputPin" DefaultValue="1000" />
                  <Pin Id="@ID(pack-result)" Name="Result" Kind="OutputPin" />
                  <Pin Id="@ID(pack-packet)" Name="Packet" Kind="OutputPin" />
                  <Pin Id="@ID(pack-frameid)" Name="Frame Id" Kind="OutputPin" />
                  <Pin Id="@ID(pack-dropped)" Name="Dropped Frames" Kind="OutputPin" />
                </Node>
                <Node Bounds="2228,540,85,26" Id="@ID(n-senddata)">
                  <p:NodeReference LastCategoryFullName="IO.WebSocket.WebSocketServer" LastDependency="VL.IO.WebSocket.vl">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <CategoryReference Kind="ClassType" Name="WebSocketServer" NeedsToBeDirectParent="true" />
                    <Choice Kind="OperationCallFlag" Name="SendData" />
                  </p:NodeReference>
                  <Pin Id="@ID(send-input)" Name="Input" Kind="StateInputPin" />
                  <Pin Id="@ID(send-client)" Name="Client" Kind="InputPin" />
                  <Pin Id="@ID(send-data)" Name="Data" Kind="InputPin" />
                  <Pin Id="@ID(send-cancel)" Name="Cancellation Token" Kind="InputPin" IsHidden="true" />
                  <Pin Id="@ID(send-apply)" Name="Apply" Kind="InputPin" />
                  <Pin Id="@ID(send-output)" Name="Output" Kind="StateOutputPin" />
                </Node>
              </Patch>
            </Node>'''

# Existing ids in the MediaPipe definition this change links to (upstream 05ab3d2):
THIS_INSTANCE = "HDTqgIXrwzOMsJFk3mtzao"        # This.Instance
WS_OUTPUT = "Gl65k0TSkU5NhSHTPFP2nC"            # WebSocketServer.Output
WS_CLIENTS = "NEfUTqOZ4LgOwuSNgZL98F"           # WebSocketServer.Connected Clients
WS_QUEUE = "VmNNQHk7ureOZgb1L3S5ka"             # WebSocketServer.Send Queue Length
# The timers Channel's Value (BUCnZUXlFjwN75B0txa6tQ) is read by the GetTimers operation; linking it into Update fails
# ("This link is between two elements that are executed on different operations"). Acknowledgements reach
# FramePacket.Pack through Timers.FrameId instead.

LINKS = rf'''
          <Link Id="@ID(l-io-source)" Ids="@ID(pin-source),@ID(cp-source)" IsHidden="true" />
          <Link Id="@ID(l-io-texture)" Ids="@ID(pin-texture),@ID(cp-texture)" IsHidden="true" />
          <Link Id="@ID(l-io-tag)" Ids="@ID(pin-tag),@ID(cp-tag)" IsHidden="true" />
          <Link Id="@ID(l-io-maxsize)" Ids="@ID(pin-maxsize),@ID(cp-maxsize)" IsHidden="true" />
          <Link Id="@ID(l-io-readbackmode)" Ids="@ID(pin-readbackmode),@ID(cp-readbackmode)" IsHidden="true" />
          <Link Id="@ID(l-source-page)" Ids="@ID(cp-source),@ID(pagesource-source)" />
          <Link Id="@ID(l-page-ss)" Ids="@ID(pagesource-result),@ID(ss-value)" />
          <Link Id="@ID(l-this-ss)" Ids="{THIS_INSTANCE},@ID(ss-input)" />
          <Link Id="@ID(l-name-ss)" Ids="@ID(pad-source-name),@ID(ss-name)" />
          <Link Id="@ID(l-source-is)" Ids="@ID(cp-source),@ID(istexture-source)" />
          <Link Id="@ID(l-texture-is)" Ids="@ID(cp-texture),@ID(istexture-texture)" />
          <Link Id="@ID(l-is-if)" Ids="@ID(istexture-result),@ID(if-condition)" />
          <Link Id="@ID(l-texture-info)" Ids="@ID(cp-texture),@ID(info-input)" />
          <Link Id="@ID(l-info-size)" Ids="@ID(info-viewsize),@ID(rs-texturesize)" />
          <Link Id="@ID(l-max-size)" Ids="@ID(cp-maxsize),@ID(rs-maxsize)" />
          <Link Id="@ID(l-framedelay-cache)" Ids="@ID(pad-framedelay),@ID(cache-cp-framedelay)" />
          <Link Id="@ID(l-framedelay-create)" Ids="@ID(cache-cp-framedelay),@ID(readback-create-framedelay)" />
          <Link Id="@ID(l-readback-create)" Ids="@ID(readback-create-output),@ID(cache-cp-readback)" />
          <Link Id="@ID(l-readback-configure)" Ids="@ID(cache-cp-readback),@ID(configure-input)" />
          <Link Id="@ID(l-configure-readback)" Ids="@ID(configure-output),@ID(readback-input)" />
          <Link Id="@ID(l-mode-configure)" Ids="@ID(cp-readbackmode),@ID(configure-mode)" />
          <Link Id="@ID(l-ws-configure)" Ids="{WS_OUTPUT},@ID(configure-sender)" />
          <Link Id="@ID(l-first-configure)" Ids="@ID(first-result),@ID(configure-client)" />
          <Link Id="@ID(l-queue-configure)" Ids="{WS_QUEUE},@ID(configure-queue)" />
          <Link Id="@ID(l-texture-readback)" Ids="@ID(cp-texture),@ID(readback-texture)" />
          <Link Id="@ID(l-size-readback)" Ids="@ID(rs-result),@ID(readback-size)" />
          <Link Id="@ID(l-tag-readback)" Ids="@ID(cp-tag),@ID(readback-frametag)" />
          <Link Id="@ID(l-readback-scheduler)" Ids="@ID(readback-output),@ID(scheduler-renderer)" />
          <Link Id="@ID(l-readback-image)" Ids="@ID(readback-image),@ID(pack-image)" />
          <Link Id="@ID(l-readback-apply)" Ids="@ID(readback-result),@ID(pack-apply)" />
          <Link Id="@ID(l-readback-tag)" Ids="@ID(readback-imagetag),@ID(pack-frametag)" />
          <Link Id="@ID(l-clients-first)" Ids="{WS_CLIENTS},@ID(first-input)" />
          <Link Id="@ID(l-first-pack)" Ids="@ID(first-result),@ID(pack-client)" />
          <Link Id="@ID(l-first-send)" Ids="@ID(first-result),@ID(send-client)" />
          <Link Id="@ID(l-queue-pack)" Ids="{WS_QUEUE},@ID(pack-queue)" />
          <Link Id="@ID(l-ws-pack)" Ids="{WS_OUTPUT},@ID(pack-owner)" />
          <Link Id="@ID(l-ws-send)" Ids="{WS_OUTPUT},@ID(send-input)" />
          <Link Id="@ID(l-pack-data)" Ids="@ID(pack-packet),@ID(send-data)" />
          <Link Id="@ID(l-pack-apply)" Ids="@ID(pack-result),@ID(send-apply)" />'''

DEPENDENCY = r'''
  <NugetDependency Id="@ID(dep-stride)" Location="VL.Stride" Version="2025.7.1" />'''

# The liminal-room pin (57e800f): VL.CEF 0.7.2, the version vvvv 7.2 and 7.5 ship.
CEF_UPSTREAM = '<NugetDependency Id="G3a6CNjID7kOWzilC83hEQ" Location="VL.CEF" Version="0.8.2" />'
CEF_LIMINAL = '<NugetDependency Id="G3a6CNjID7kOWzilC83hEQ" Location="VL.CEF" Version="0.7.2" />'

# ---------------------------------------------------------------------------------------------------------------
# Timings: Frame Id, Frame Latency (ms) and Frame Tag. GetTimers' value leaves the If region through a feedback border
# (null until the first timers message) into Timers.FrameInfoOf on the canvas, which gives -1 for no timers: inside the
# Try region, as the three upstream outputs are, the outputs would read 0 (the type default) before the first message.
TIMINGS_IF_CPS = r'''
              <ControlPoint Id="@ID(t-if-bottom-timers)" Bounds="590,485" Alignment="Bottom" />
              <ControlPoint Id="@ID(t-if-top-timers)" Bounds="610,244" Alignment="Top" />'''

TIMINGS_CANVAS = r'''
            <Node Bounds="585,500,105,19" Id="@ID(t-frameinfoof)">
              <p:NodeReference LastCategoryFullName="VL.MediaPipe.TimerResult.Timers" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                <Choice Kind="OperationCallFlag" Name="FrameInfoOf" />
              </p:NodeReference>
              <Pin Id="@ID(t-fio-timers)" Name="Timers" Kind="InputPin" />
              <Pin Id="@ID(t-fio-frameid)" Name="Frame Id" Kind="OutputPin" />
              <Pin Id="@ID(t-fio-latency)" Name="Frame Latency" Kind="OutputPin" />
              <Pin Id="@ID(t-fio-tag)" Name="Frame Tag" Kind="OutputPin" />
            </Node>
            <ControlPoint Id="@ID(t-cp-id)" Bounds="590,545" />
            <ControlPoint Id="@ID(t-cp-lat)" Bounds="650,545" />
            <ControlPoint Id="@ID(t-cp-tag)" Bounds="710,545" />'''

TIMINGS_PINS = r'''
            <Pin Id="@ID(t-pin-frameid)" Name="Frame Id" Kind="OutputPin" Bounds="706,657" Summary="Source = Texture: the frame these timings belong to. -1 for camera input and before the first timings.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(t-pin-latency)" Name="Frame Latency (ms)" Kind="OutputPin" Bounds="806,657" Summary="Source = Texture: milliseconds from sending the frame to receiving its timings, on the vvvv clock. Includes Detection Time; excludes the texture readback before sending. -1 for camera input and before the first timings.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Float32" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(t-pin-tag)" Name="Frame Tag" Kind="OutputPin" Bounds="906,657" Summary="Source = Texture: the MediaPipe node's Input Frame Tag of the frame these timings belong to, e.g. its camera frame id. -1 for camera input, without a tag, and before the first timings.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>'''

GET_TIMERS_VALUE = "Da1bwDv141sPLRhqiWJ7oB"

TIMINGS_LINKS = rf'''
          <Link Id="@ID(t-l-timers-if)" Ids="{GET_TIMERS_VALUE},@ID(t-if-bottom-timers)" />
          <Link Id="@ID(t-l-timers-fb)" Ids="@ID(t-if-top-timers),@ID(t-if-bottom-timers)" IsFeedback="true" />
          <Link Id="@ID(t-l-timers-fio)" Ids="@ID(t-if-bottom-timers),@ID(t-fio-timers)" />
          <Link Id="@ID(t-l-id-cp)" Ids="@ID(t-fio-frameid),@ID(t-cp-id)" />
          <Link Id="@ID(t-l-id-io)" Ids="@ID(t-cp-id),@ID(t-pin-frameid)" IsHidden="true" />
          <Link Id="@ID(t-l-lat-cp)" Ids="@ID(t-fio-latency),@ID(t-cp-lat)" />
          <Link Id="@ID(t-l-lat-io)" Ids="@ID(t-cp-lat),@ID(t-pin-latency)" IsHidden="true" />
          <Link Id="@ID(t-l-tag-cp)" Ids="@ID(t-fio-tag),@ID(t-cp-tag)" />
          <Link Id="@ID(t-l-tag-io)" Ids="@ID(t-cp-tag),@ID(t-pin-tag)" IsHidden="true" />'''

# ---------------------------------------------------------------------------------------------------------------
# GetHands: public, so other documents reach the hand results object (MediaPipe.HandResults.Handresults: per hand the
# 21 normalized landmarks and handedness categories, plus the result's Frame Tag), e.g. VL.HandTracking's
# MediaPipeHandSource. Upstream keeps every Get* accessor internal; this one only changes its visibility.
GET_HANDS_INTERNAL = '<Patch Id="R6Y9nI8otstLlKGSyiASW7" Name="GetHands (Internal)">'
GET_HANDS_PUBLIC = '<Patch Id="R6Y9nI8otstLlKGSyiASW7" Name="GetHands">'
# The MediaPipe class carries Aspects="Internal", which hides all of its operations from other documents whatever their
# names ("Not found: GetHands"). Its other operations stay hidden by their "(Internal)" names; Create/Update are the
# process node's own.
MEDIAPIPE_CLASS_INTERNAL = '<Node Name="MediaPipe" Aspects="Internal" Bounds="150,238" Id="Q5HejFxEzF3O35hE7Mpbcl">'
MEDIAPIPE_CLASS_PUBLIC = '<Node Name="MediaPipe" Bounds="150,238" Id="Q5HejFxEzF3O35hE7Mpbcl">'

# HandDetector: Frame Tag (open question Q6). GetHands' value leaves the outer If region through a feedback border into
# Handresults.FrameTagOf on the canvas (-1 before the first results), as Timings does with its timers.
GET_HANDS_VALUE = "G2Ns2phzDJ8Qa4a1EmlGmh"

HAND_IF_CPS = r'''
              <ControlPoint Id="@ID(hd-if-bottom-results)" Bounds="420,789" Alignment="Bottom" />
              <ControlPoint Id="@ID(hd-if-top-results)" Bounds="420,218" Alignment="Top" />'''

HAND_CANVAS = r'''
            <Node Bounds="415,810,85,19" Id="@ID(hd-frametagof)">
              <p:NodeReference LastCategoryFullName="MediaPipe.HandResults.Handresults" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                <Choice Kind="OperationCallFlag" Name="FrameTagOf" />
              </p:NodeReference>
              <Pin Id="@ID(hd-fto-results)" Name="Hand Results" Kind="InputPin" />
              <Pin Id="@ID(hd-fto-tag)" Name="Frame Tag" Kind="OutputPin" />
            </Node>
            <ControlPoint Id="@ID(hd-cp-tag)" Bounds="420,850" />'''

HAND_PINS = r'''
            <Pin Id="@ID(hd-pin-tag)" Name="Frame Tag" Kind="OutputPin" Bounds="420,870" Summary="Source = Texture: the MediaPipe node's Input Frame Tag of the frame the current hand results were detected on, e.g. its camera frame id. -1 for camera input, without a tag, and before the first results.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>'''

HAND_LINKS = rf'''
          <Link Id="@ID(hd-l-results-if)" Ids="{GET_HANDS_VALUE},@ID(hd-if-bottom-results)" />
          <Link Id="@ID(hd-l-results-fb)" Ids="@ID(hd-if-top-results),@ID(hd-if-bottom-results)" IsFeedback="true" />
          <Link Id="@ID(hd-l-results-fto)" Ids="@ID(hd-if-bottom-results),@ID(hd-fto-results)" />
          <Link Id="@ID(hd-l-tag-cp)" Ids="@ID(hd-fto-tag),@ID(hd-cp-tag)" />
          <Link Id="@ID(hd-l-tag-io)" Ids="@ID(hd-cp-tag),@ID(hd-pin-tag)" IsHidden="true" />'''

# ---------------------------------------------------------------------------------------------------------------
# MediaPipe: the web root when VL.MediaPipe is linked as a file. AppHost.GetPackagePath("VL.MediaPipe") is null unless
# vvvv knows VL.MediaPipe as a package; MakePath(null, "web") then threw every frame ("path2") and the node went pink.
# PackageFolder.Resolve (src/PackageFolder.cs) passes a resolved path through and otherwise finds the folder that holds
# VL.MediaPipe.vl and web/ above the loaded DLL. It sits between GetPackagePath's Result and MakePath's Input.
GET_PACKAGE_PATH_RESULT = "Qzl9xud6WxNMKJDo1Tqekh"
MAKE_PATH_INPUT = "HBOT9Ktv2rmO9gdfaLFYYL"
PACKAGE_PATH_LINK = f'<Link Id="B1iEy8O1VDAPTG7pXI8d97" Ids="{GET_PACKAGE_PATH_RESULT},{MAKE_PATH_INPUT}" />'
PACKAGE_FOLDER_NODE = r'''
            <Node Bounds="41,180,75,19" Id="@ID(pf-node)">
              <p:NodeReference LastCategoryFullName="MediaPipe.PackageFolder" LastDependency="VL.MediaPipe.dll">
                <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                <Choice Kind="OperationCallFlag" Name="Resolve" />
              </p:NodeReference>
              <Pin Id="@ID(pf-in)" Name="Package Path" Kind="InputPin" />
              <Pin Id="@ID(pf-out)" Name="Output" Kind="OutputPin" />
            </Node>'''
PACKAGE_FOLDER_LINKS = (f'<Link Id="B1iEy8O1VDAPTG7pXI8d97" Ids="{GET_PACKAGE_PATH_RESULT},@ID(pf-in)" />\n'
                        f'          <Link Id="@ID(pf-l-out)" Ids="@ID(pf-out),{MAKE_PATH_INPUT}" />')


class Doc:
    def __init__(self, text: str):
        self.text = text

    def _find_once(self, anchor: str, start: int = 0) -> int:
        first = self.text.find(anchor, start)
        if first < 0:
            raise SystemExit(f"anchor not found: {anchor[:120]}")
        if self.text.find(anchor, first + 1) >= 0 and start == 0:
            raise SystemExit(f"anchor matches more than once: {anchor[:120]}")
        return first

    def insert_after_line(self, anchor: str, snippet: str) -> None:
        pos = self._find_once(anchor)
        end = self.text.index("\n", pos)
        self.text = self.text[:end] + expand(snippet) + self.text[end:]

    def insert_before_line(self, anchor: str, snippet: str) -> None:
        pos = self._find_once(anchor)
        start = self.text.rindex("\n", 0, pos)
        self.text = self.text[:start] + expand(snippet) + self.text[start:]

    def insert_after_block(self, anchor: str, closing: str, snippet: str) -> None:
        pos = self._find_once(anchor)
        close = self.text.index(closing, pos)
        end = self.text.index("\n", close)
        self.text = self.text[:end] + expand(snippet) + self.text[end:]

    def replace_once(self, old: str, new: str) -> None:
        self._find_once(old)
        self.text = self.text.replace(old, new, 1)


def build(base_text: str) -> str:
    doc = Doc(base_text.replace("\r\n", "\n"))
    # MediaPipe definition
    doc.insert_after_line('<Pin Id="OVs9RDxaP3dN1wztUoUfCZ" Name="Image Size" Kind="InputPin" DefaultValue="640, 360" />', UPDATE_PINS)
    doc.insert_after_line('<Pad Id="D4qXaoHXASuN6I4BI09UYG" Bounds="343,700" />', CANVAS)
    doc.insert_before_line('<Patch Id="RAjM2C25AeyMcMHy6wfJFl" Name="Create" />', LINKS)
    # Timings definition
    doc.insert_after_line('<ControlPoint Id="EMNdDDY0R66MlTugHt3VIm" Bounds="551,244" Alignment="Top" />', TIMINGS_IF_CPS)
    doc.insert_after_line('<ControlPoint Id="FqPlIL7brwBP6nA0IFxJVq" Bounds="469,525" />', TIMINGS_CANVAS)
    doc.insert_after_block('<Pin Id="CdKBx5xwST6OM4Vs6LaWzY" Name="Source Frame Rate"', "</Pin>", TIMINGS_PINS)
    doc.insert_after_line('<Link Id="QdEpuHGCGDDPp2ZTkCEt7k" Ids="FFj96acS77kLWXmr2YPXMr,QVfeJv9HdgXL0Mn5dI0Awl" />', TIMINGS_LINKS)
    doc.replace_once('<Node Bounds="192,238,387,253" Id="KfJeXH6k9EDL834Hvieacg">', '<Node Bounds="192,238,440,253" Id="KfJeXH6k9EDL834Hvieacg">')
    # MediaPipe: GetHands public; HandDetector definition: Frame Tag
    doc.replace_once(MEDIAPIPE_CLASS_INTERNAL, MEDIAPIPE_CLASS_PUBLIC)
    doc.replace_once(GET_HANDS_INTERNAL, GET_HANDS_PUBLIC)
    doc.insert_after_line('<ControlPoint Id="AE21COVXsXzOIG34pCPnk5" Bounds="241,218" Alignment="Top" />', HAND_IF_CPS)
    doc.insert_after_line('<ControlPoint Id="TJjUBp4qIcQOrTl9taSiZU" Bounds="330,819" />', HAND_CANVAS)
    doc.insert_after_line('<Pin Id="E2HQCAWRudwNYXiZzgUR0k" Name="World Landmarks" Kind="OutputPin" Bounds="304,751" />', HAND_PINS)
    doc.insert_after_line(f'<Link Id="Cmt1vJRwogyLGjU5QE0SS5" Ids="{GET_HANDS_VALUE},RZLSdyvBABkLzfgLI5jw9P" />', HAND_LINKS)
    # MediaPipe: the web root without a package registration (PackageFolder.Resolve)
    doc.insert_before_line('<Node Bounds="124,208,60,19" Id="IreqYOEIfvnNjrwjqQXCY3">', PACKAGE_FOLDER_NODE)
    doc.replace_once(PACKAGE_PATH_LINK, expand(PACKAGE_FOLDER_LINKS))
    # Document
    doc.insert_after_line('<NugetDependency Id="NUSiv3FPG3gMWwRu79ffAI" Location="VL.IO.WebServer" Version="1.0.1" />', DEPENDENCY)
    doc.replace_once(CEF_UPSTREAM, CEF_LIMINAL)
    return doc.text


def verify(text: str) -> None:
    dom = xml.dom.minidom.parseString(text.encode("utf-8"))
    ids = {}
    for element in dom.getElementsByTagName("*"):
        if element.hasAttribute("Id"):
            value = element.getAttribute("Id")
            if not re.fullmatch(r"[A-Za-z0-9]{22}", value):
                raise SystemExit(f"bad Id {value}")
            if value in ids:
                raise SystemExit(f"duplicate Id {value}")
            ids[value] = element.tagName
    for link in dom.getElementsByTagName("Link"):
        for ref in link.getAttribute("Ids").split(","):
            if ref not in ids:
                raise SystemExit(f"link {link.getAttribute('Id')} references unknown {ref}")
    if "<!--" in "".join(line for line in text.splitlines() if "@" in line):
        raise SystemExit("unexpanded placeholder")
    if "@ID(" in text:
        raise SystemExit("unexpanded @ID placeholder")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default=UPSTREAM_BASE, help="git revision holding the upstream VL.MediaPipe.vl")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    base = subprocess.run(["git", "-C", str(REPO), "show", f"{args.base}:VL.MediaPipe.vl"], check=True, capture_output=True).stdout.decode("utf-8")
    patched = build(base)
    verify(patched)
    out = patched.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8")
    if args.check:
        same = DOC.read_bytes() == out
        print("up to date" if same else "VL.MediaPipe.vl differs from the generated document")
        return 0 if same else 1
    DOC.write_bytes(out)
    added = len(re.findall(r'Id="', patched)) - len(re.findall(r'Id="', base))
    print(f"wrote {DOC} ({len(out)} bytes, {added} new Ids)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
