"""Add string resources to all nine Android strings.xml files from one JSON map.

Usage:  uv run python scripts/android_add_strings.py <strings.json>

JSON shape (every key needs all nine languages; the script refuses otherwise):
{
  "files_open_in_folder": {"en": "…", "es": "…", "fr": "…", "hi": "…", "id": "…",
                           "pl": "…", "pt-BR": "…", "tr": "…", "uk": "…"}
}
Plain text in, Android-escaped out (& < > ' " and a leading @ or ?), so pass the text as the user
should read it. Format args (%1$s, %d) pass through. Text-level insertion before </resources>,
keeping each file's line endings, so the diff is only the added entries. The PC twin is
scripts/resx_add_keys.py.
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = os.path.join(ROOT, "remex.android", "app", "src", "main", "res")
DIRS = {
    "en": "values", "es": "values-es", "fr": "values-fr", "hi": "values-hi", "id": "values-in",
    "pl": "values-pl", "pt-BR": "values-pt-rBR", "tr": "values-tr", "uk": "values-uk",
}


def android_escape(text: str) -> str:
    text = (text.replace("\\", "\\\\").replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;").replace("'", "\\'").replace('"', '\\"'))
    if text[:1] in ("@", "?"):
        text = "\\" + text
    return text.replace("\n", "\\n")


def main(path: str) -> None:
    with open(path, "rb") as f:
        keys = json.loads(f.read().decode("utf-8"))
    for lang in DIRS:
        missing = [k for k, v in keys.items() if lang not in v or not v[lang].strip()]
        if missing:
            sys.exit(f"no {lang} text for {missing}")
    for key in keys:
        if not re.fullmatch(r"[a-z][a-z0-9_]*", key):
            sys.exit(f"bad resource name {key!r}: use lower_snake_case")
    for lang, folder in DIRS.items():
        full = os.path.join(RES, folder, "strings.xml")
        with open(full, "rb") as f:
            raw = f.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        nl = "\r\n" if "\r\n" in text else "\n"
        block = ""
        for key, values in keys.items():
            if re.search(r'<string name="' + re.escape(key) + '"', text):
                sys.exit(f"{folder}/strings.xml: key {key} already exists")
            block += f'    <string name="{key}">{android_escape(values[lang])}</string>{nl}'
        idx = text.rfind("</resources>")
        if idx < 0:
            sys.exit(f"{folder}/strings.xml: no </resources>")
        text = text[:idx] + block + text[idx:]
        if "\x00" in text:
            sys.exit(f"{folder}/strings.xml: refusing to write a NUL byte")
        with open(full, "wb") as f:
            f.write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        print(f"{folder}/strings.xml: +{len(keys)}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
