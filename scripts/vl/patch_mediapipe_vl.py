"""Applies the texture-input changes to VL.MediaPipe.vl.

Starts from the upstream document in git (default: HEAD, i.e. the fork point before any texture-input edit of
the .vl), inserts XML snippets at anchors that must each match exactly once, and writes VL.MediaPipe.vl with CRLF
line endings like the committed file. Ids of new elements are derived from their names (sha256 -> 22 base62
characters), so re-running produces a byte-identical document.

    python scripts/vl/patch_mediapipe_vl.py [--base HEAD] [--check]

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
            <Pin Id="@ID(pin-maxsize)" Name="Max Input Size" Kind="InputPin" DefaultValue="0" Summary="Source = Texture: 0 sends the texture at its own size. Otherwise it is scaled down on the GPU before readback so that its longer side is at most this many pixels.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>'''

# MediaPipe: canvas elements (Source sender, texture path in an If region)
CANVAS = r'''
            <ControlPoint Id="@ID(cp-source)" Bounds="2010,110" />
            <ControlPoint Id="@ID(cp-texture)" Bounds="2230,110" />
            <ControlPoint Id="@ID(cp-maxsize)" Bounds="2450,110" />
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
            <Node Bounds="2200,210,600,400" Id="@ID(region-if)">
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
                <Node Bounds="2228,350,105,19" Id="@ID(n-t2i)">
                  <p:NodeReference LastCategoryFullName="Stride.Textures" LastDependency="VL.Stride.Rendering.vl">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="ProcessAppFlag" Name="TextureToImage" />
                  </p:NodeReference>
                  <Pin Id="@ID(t2i-input)" Name="Input" Kind="InputPin" />
                  <Pin Id="@ID(t2i-size)" Name="Size" Kind="InputPin" />
                  <Pin Id="@ID(t2i-newformat)" Name="New Format" Kind="InputPin" DefaultValue="R8G8B8A8_UNorm" />
                  <Pin Id="@ID(t2i-output)" Name="Output" Kind="OutputPin" />
                  <Pin Id="@ID(t2i-available)" Name="Result Available" Kind="OutputPin" />
                </Node>
                <Node Bounds="2468,350,80,19" Id="@ID(n-firstclient)">
                  <p:NodeReference LastCategoryFullName="Collections.Sequence" LastDependency="VL.CoreLib.vl">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="OperationCallFlag" Name="FirstOrDefault" />
                  </p:NodeReference>
                  <Pin Id="@ID(first-input)" Name="Input" Kind="StateInputPin" />
                  <Pin Id="@ID(first-default)" Name="Default Value" Kind="InputPin" />
                  <Pin Id="@ID(first-output)" Name="Output" Kind="StateOutputPin" />
                  <Pin Id="@ID(first-result)" Name="Result" Kind="OutputPin" />
                </Node>
                <Node Bounds="2228,420,360,19" Id="@ID(n-pack)">
                  <p:NodeReference LastCategoryFullName="MediaPipe.FramePacket" LastDependency="VL.MediaPipe.dll">
                    <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                    <Choice Kind="OperationCallFlag" Name="Pack" />
                  </p:NodeReference>
                  <Pin Id="@ID(pack-owner)" Name="Owner" Kind="InputPin" />
                  <Pin Id="@ID(pack-image)" Name="Image" Kind="InputPin" />
                  <Pin Id="@ID(pack-apply)" Name="Apply" Kind="InputPin" />
                  <Pin Id="@ID(pack-client)" Name="Client" Kind="InputPin" />
                  <Pin Id="@ID(pack-queue)" Name="Send Queue Length" Kind="InputPin" />
                  <Pin Id="@ID(pack-acktimeout)" Name="Ack Timeout" Kind="InputPin" DefaultValue="1000" />
                  <Pin Id="@ID(pack-result)" Name="Result" Kind="OutputPin" />
                  <Pin Id="@ID(pack-packet)" Name="Packet" Kind="OutputPin" />
                  <Pin Id="@ID(pack-frameid)" Name="Frame Id" Kind="OutputPin" />
                  <Pin Id="@ID(pack-dropped)" Name="Dropped Frames" Kind="OutputPin" />
                </Node>
                <Node Bounds="2228,500,85,26" Id="@ID(n-senddata)">
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
          <Link Id="@ID(l-io-maxsize)" Ids="@ID(pin-maxsize),@ID(cp-maxsize)" IsHidden="true" />
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
          <Link Id="@ID(l-size-t2i)" Ids="@ID(rs-result),@ID(t2i-size)" />
          <Link Id="@ID(l-texture-t2i)" Ids="@ID(cp-texture),@ID(t2i-input)" />
          <Link Id="@ID(l-t2i-image)" Ids="@ID(t2i-output),@ID(pack-image)" />
          <Link Id="@ID(l-t2i-apply)" Ids="@ID(t2i-available),@ID(pack-apply)" />
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

# ---------------------------------------------------------------------------------------------------------------
# Timings: Frame Id and Frame Latency (ms), following the pattern of its three existing outputs
# (property node inside Try -> Try border -> If border with feedback -> canvas ControlPoint -> IO pin).
TIMINGS_NODE = r'''
                    <Node Bounds="585,370,80,26" Id="@ID(t-getframeinfo)">
                      <p:NodeReference LastCategoryFullName="VL.MediaPipe.TimerResult.Timers" LastDependency="VL.MediaPipe.dll">
                        <Choice Kind="NodeFlag" Name="Node" Fixed="true" />
                        <Choice Kind="OperationCallFlag" Name="GetFrameInfo" />
                      </p:NodeReference>
                      <Pin Id="@ID(t-gfi-input)" Name="Input" Kind="StateInputPin" />
                      <Pin Id="@ID(t-gfi-frameid)" Name="Frame Id" Kind="OutputPin" />
                      <Pin Id="@ID(t-gfi-latency)" Name="Frame Latency" Kind="OutputPin" />
                    </Node>'''

TIMINGS_TRY_CPS = r'''
                  <ControlPoint Id="@ID(t-try-cp-id)" Bounds="590,411" Alignment="Bottom" />
                  <ControlPoint Id="@ID(t-try-cp-lat)" Bounds="650,411" Alignment="Bottom" />'''

TIMINGS_IF_CPS = r'''
              <ControlPoint Id="@ID(t-if-bottom-id)" Bounds="590,485" Alignment="Bottom" />
              <ControlPoint Id="@ID(t-if-top-id)" Bounds="610,244" Alignment="Top" />
              <ControlPoint Id="@ID(t-if-bottom-lat)" Bounds="650,485" Alignment="Bottom" />
              <ControlPoint Id="@ID(t-if-top-lat)" Bounds="670,244" Alignment="Top" />'''

TIMINGS_CANVAS_CPS = r'''
            <ControlPoint Id="@ID(t-cp-id)" Bounds="590,525" />
            <ControlPoint Id="@ID(t-cp-lat)" Bounds="650,525" />'''

TIMINGS_PINS = r'''
            <Pin Id="@ID(t-pin-frameid)" Name="Frame Id" Kind="OutputPin" Bounds="706,657" Summary="Source = Texture: the frame these timings belong to. -1 for camera input.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Integer32" />
              </p:TypeAnnotation>
            </Pin>
            <Pin Id="@ID(t-pin-latency)" Name="Frame Latency (ms)" Kind="OutputPin" Bounds="806,657" Summary="Source = Texture: milliseconds from sending the frame to receiving its timings, on the vvvv clock. Includes Detection Time; excludes the texture readback before sending. -1 for camera input.">
              <p:TypeAnnotation LastCategoryFullName="Primitive" LastDependency="VL.CoreLib.vl">
                <Choice Kind="TypeFlag" Name="Float32" />
              </p:TypeAnnotation>
            </Pin>'''

GET_TIMERS_VALUE = "Da1bwDv141sPLRhqiWJ7oB"

TIMINGS_LINKS = rf'''
          <Link Id="@ID(t-l-gfi)" Ids="{GET_TIMERS_VALUE},@ID(t-gfi-input)" />
          <Link Id="@ID(t-l-id-try)" Ids="@ID(t-gfi-frameid),@ID(t-try-cp-id)" />
          <Link Id="@ID(t-l-id-if)" Ids="@ID(t-try-cp-id),@ID(t-if-bottom-id)" />
          <Link Id="@ID(t-l-id-fb)" Ids="@ID(t-if-top-id),@ID(t-if-bottom-id)" IsFeedback="true" />
          <Link Id="@ID(t-l-id-cp)" Ids="@ID(t-if-bottom-id),@ID(t-cp-id)" />
          <Link Id="@ID(t-l-id-io)" Ids="@ID(t-cp-id),@ID(t-pin-frameid)" IsHidden="true" />
          <Link Id="@ID(t-l-lat-try)" Ids="@ID(t-gfi-latency),@ID(t-try-cp-lat)" />
          <Link Id="@ID(t-l-lat-if)" Ids="@ID(t-try-cp-lat),@ID(t-if-bottom-lat)" />
          <Link Id="@ID(t-l-lat-fb)" Ids="@ID(t-if-top-lat),@ID(t-if-bottom-lat)" IsFeedback="true" />
          <Link Id="@ID(t-l-lat-cp)" Ids="@ID(t-if-bottom-lat),@ID(t-cp-lat)" />
          <Link Id="@ID(t-l-lat-io)" Ids="@ID(t-cp-lat),@ID(t-pin-latency)" IsHidden="true" />'''


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
    doc.insert_before_line('<Patch Id="H9UuX7hmT84OUksMedEdUh" Name="Dispose" />', TIMINGS_NODE)
    doc.insert_after_line('<ControlPoint Id="FFj96acS77kLWXmr2YPXMr" Bounds="475,411" Alignment="Bottom" />', TIMINGS_TRY_CPS)
    doc.insert_after_line('<ControlPoint Id="EMNdDDY0R66MlTugHt3VIm" Bounds="551,244" Alignment="Top" />', TIMINGS_IF_CPS)
    doc.insert_after_line('<ControlPoint Id="FqPlIL7brwBP6nA0IFxJVq" Bounds="469,525" />', TIMINGS_CANVAS_CPS)
    doc.insert_after_block('<Pin Id="CdKBx5xwST6OM4Vs6LaWzY" Name="Source Frame Rate"', "</Pin>", TIMINGS_PINS)
    doc.insert_after_line('<Link Id="QdEpuHGCGDDPp2ZTkCEt7k" Ids="FFj96acS77kLWXmr2YPXMr,QVfeJv9HdgXL0Mn5dI0Awl" />', TIMINGS_LINKS)
    doc.replace_once('<Node Bounds="192,238,387,253" Id="KfJeXH6k9EDL834Hvieacg">', '<Node Bounds="192,238,520,253" Id="KfJeXH6k9EDL834Hvieacg">')
    doc.replace_once('<Node Bounds="250,331,317,86" Id="USXBRbjheeGOmxCeQ5Q101">', '<Node Bounds="250,331,450,86" Id="USXBRbjheeGOmxCeQ5Q101">')
    # Document
    doc.insert_after_line('<NugetDependency Id="NUSiv3FPG3gMWwRu79ffAI" Location="VL.IO.WebServer" Version="1.0.1" />', DEPENDENCY)
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
    parser.add_argument("--base", default="HEAD", help="git revision holding the upstream VL.MediaPipe.vl")
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
