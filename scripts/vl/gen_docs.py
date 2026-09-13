"""Generates the hand-authored VL documents of the texture input from templates.

    python scripts/vl/gen_docs.py [--check]

Each template in scripts/vl/templates/ is copied to its output path with every @ID(name) replaced by a 22-character
base62 Id derived from (output path, name), so regeneration is byte-identical. Output uses CRLF like the repo's .vl
files. --check exits 1 when a generated document differs from the file on disk.
Once vvvv has re-saved a generated document, that file is the source of truth and its template is stale.
"""
import argparse
import hashlib
import re
import sys
import xml.dom.minidom
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TEMPLATES = Path(__file__).resolve().parent / "templates"
OUTPUTS = {
    "TextureInputHarness.vl.tmpl": "test/TextureInputHarness.vl",
    "HowTo Track hands on a texture.vl.tmpl": "help/HowTo Track hands on a texture.vl",
}
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"


def make_id(scope: str, name: str) -> str:
    digest = int.from_bytes(hashlib.sha256(f"{scope}/{name}".encode()).digest(), "big")
    chars = []
    for _ in range(22):
        digest, r = divmod(digest, 62)
        chars.append(ALPHABET[r])
    chars[0] = ALPHABET[10 + hashlib.md5(f"{scope}/{name}".encode()).digest()[0] % 26]
    return "".join(chars)


def render(template: str, scope: str) -> str:
    text = re.sub(r"@ID\(([^)]+)\)", lambda m: make_id(scope, m.group(1)), template)
    dom = xml.dom.minidom.parseString(text.encode("utf-8"))
    ids = set()
    for element in dom.getElementsByTagName("*"):
        if element.hasAttribute("Id"):
            value = element.getAttribute("Id")
            if value in ids:
                raise SystemExit(f"{scope}: duplicate Id {value}")
            ids.add(value)
    for link in dom.getElementsByTagName("Link"):
        for ref in link.getAttribute("Ids").split(","):
            if ref not in ids:
                raise SystemExit(f"{scope}: link {link.getAttribute('Id')} references unknown {ref}")
    if "<!--" in text:
        raise SystemExit(f"{scope}: XML comments are not allowed in hand-authored .vl documents")
    return text.replace("\r\n", "\n").replace("\n", "\r\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    status = 0
    for name, output in OUTPUTS.items():
        text = render((TEMPLATES / name).read_text(encoding="utf-8"), output)
        target = REPO / output
        data = text.encode("utf-8")
        if args.check:
            same = target.exists() and target.read_bytes() == data
            print(f"{output}: {'up to date' if same else 'DIFFERS'}")
            status |= 0 if same else 1
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            print(f"wrote {output} ({len(data)} bytes)")
    return status


if __name__ == "__main__":
    sys.exit(main())
