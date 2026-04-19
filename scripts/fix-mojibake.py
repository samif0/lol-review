#!/usr/bin/env python3
"""Repair double-encoded UTF-8 in XAML / C# source files.

A PowerShell 5.1 write pass earlier in this session re-encoded already
UTF-8-encoded text as if it were cp1252. Result: characters like em
dash (U+2014) got stored as the 3-byte sequence their visual cp1252
rendering required, which is the multi-char mojibake "â€\"" — and when
UTF-8-decoded, that's 3 cp1252 glyphs (â + € + something). In the raw
bytes the file now contains 5-8 bytes per original Unicode char.

This script takes the target mojibake sequences we actually see in the
source tree and replaces them with the correct Unicode codepoint.
Saves as UTF-8 WITH BOM (matching repo convention).
"""

import os
import re
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP_DIR = os.path.join(REPO_ROOT, "src", "LoLReview.App")

# Byte-sequence replacements. Each key is the mangled sequence as it
# appears in file bytes; value is the correct UTF-8 encoded character.
# Derived by observing the actual bytes in CoachPage.xaml etc. for
# known-mojibake characters.
REPLACEMENTS = [
    # â€" (was em dash U+2014) — visible in-app as "â€" followed by curly-quote
    (b"\xc3\xa2\xe2\x82\xac\xe2\x80\x9d", "\u2014".encode("utf-8")),
    (b"\xc3\xa2\xe2\x82\xac\xe2\x80\x94", "\u2014".encode("utf-8")),
    (b"\xc3\xa2\xe2\x82\xac\xe2\x80\x93", "\u2013".encode("utf-8")),
    # â€™ curly right apostrophe
    (b"\xc3\xa2\xe2\x82\xac\xe2\x84\xa2", "\u2019".encode("utf-8")),
    (b"\xc3\xa2\xe2\x82\xac\xe2\x80\x99", "\u2019".encode("utf-8")),
    # â€œ/â€ curly double quotes
    (b"\xc3\xa2\xe2\x82\xac\xc5\x93", "\u201C".encode("utf-8")),
    (b"\xc3\xa2\xe2\x82\xac\xc2\x9d", "\u201D".encode("utf-8")),
    # â€¢ bullet
    (b"\xc3\xa2\xe2\x82\xac\xc2\xa2", "\u2022".encode("utf-8")),
    # â†' right arrow
    (b"\xc3\xa2\xe2\x80\xa0\xe2\x80\x99", "\u2192".encode("utf-8")),
    (b"\xc3\xa2\xe2\x80\xa0'", "\u2192".encode("utf-8")),
    # generic fallback: â + some mangled char; NOT replacing bare "â"
    # (0xC3 0xA2) because it could be legitimate content. We only touch
    # the multi-char triples produced by the double-encoding.
]

# Also match decorative box-drawing sequences the earlier script
# mangled (saw these as "â•â•â•" in screenshots for === separator
# lines in comments).
REPLACEMENTS.append(
    (b"\xc3\xa2\xe2\x80\xa2", "\u2022".encode("utf-8"))  # fallback bullet
)


def process(path: str) -> bool:
    with open(path, "rb") as f:
        data = f.read()
    original = data
    for old, new in REPLACEMENTS:
        data = data.replace(old, new)
    if data == original:
        return False
    # Preserve BOM if present, add one if not (repo convention is WITH BOM).
    bom = b"\xef\xbb\xbf"
    if not data.startswith(bom):
        data = bom + data
    with open(path, "wb") as f:
        f.write(data)
    return True


def main() -> int:
    touched = 0
    for root, _, files in os.walk(APP_DIR):
        if "\\obj\\" in root or "\\bin\\" in root or "/obj/" in root or "/bin/" in root:
            continue
        for name in files:
            if not name.endswith((".xaml", ".cs")):
                continue
            path = os.path.join(root, name)
            if process(path):
                rel = os.path.relpath(path, REPO_ROOT)
                print(f"fixed: {rel}")
                touched += 1
    print(f"\nFiles fixed: {touched}")
    return 0 if touched else 1


if __name__ == "__main__":
    sys.exit(main())
