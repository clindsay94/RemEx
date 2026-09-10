"""Add localisation keys to all nine Strings*.resx files from one JSON map.

Usage:  uv run python scripts/resx_add_keys.py <keys.json>

JSON shape (every key needs all nine languages; the script refuses otherwise):
{
  "Custom_Example": {"en": "…", "es": "…", "fr": "…", "hi": "…", "id": "…",
                     "pl": "…", "pt-BR": "…", "tr": "…", "uk": "…"}
}
Text-level insertion before </root>, preserving each file's BOM and line endings, so the diff is
only the added entries. Refuses a key that already exists in any file and refuses to write a NUL.
"""
import json
import os
import re
import sys
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOC = os.path.join(ROOT, "remex.desktop", "Localization")
FILES = {
    "en": "Strings.resx", "es": "Strings.es.resx", "fr": "Strings.fr.resx",
    "hi": "Strings.hi.resx", "id": "Strings.id.resx", "pl": "Strings.pl.resx",
    "pt-BR": "Strings.pt-BR.resx", "tr": "Strings.tr.resx", "uk": "Strings.uk.resx",
}


def main(path: str) -> None:
    with open(path, "rb") as f:
        keys = json.loads(f.read().decode("utf-8"))
    for lang in FILES:
        missing = [k for k, v in keys.items() if lang not in v or not v[lang].strip()]
        if missing:
            sys.exit(f"no {lang} text for {missing}")
    for lang, name in FILES.items():
        full = os.path.join(LOC, name)
        with open(full, "rb") as f:
            raw = f.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        nl = "\r\n" if "\r\n" in text else "\n"
        block = ""
        for key, values in keys.items():
            if re.search(r'<data name="' + re.escape(key) + '"', text):
                sys.exit(f"{name}: key {key} already exists")
            block += (f'  <data name="{key}" xml:space="preserve">{nl}'
                      f'    <value>{escape(values[lang])}</value>{nl}'
                      f'  </data>{nl}')
        idx = text.rfind("</root>")
        if idx < 0:
            sys.exit(f"{name}: no </root>")
        text = text[:idx] + block + text[idx:]
        if "\x00" in text:
            sys.exit(f"{name}: refusing to write a NUL byte")
        with open(full, "wb") as f:
            f.write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        print(f"{name}: +{len(keys)}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
