"""Remove localisation keys from all nine Strings*.resx files.

Usage:  uv run python scripts/resx_remove_keys.py Key_One Key_Two …

Refuses to run when any key is still referenced by a .cs or .axaml file under remex.desktop
(Strings.Designer.cs excluded), because a removed-but-referenced key renders as its raw name on
screen in every language. Preserves BOM and line endings; refuses to write a NUL.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DESKTOP = os.path.join(ROOT, "remex.desktop")
LOC = os.path.join(DESKTOP, "Localization")
FILES = ["Strings.resx", "Strings.es.resx", "Strings.fr.resx", "Strings.hi.resx", "Strings.id.resx",
         "Strings.pl.resx", "Strings.pt-BR.resx", "Strings.tr.resx", "Strings.uk.resx"]


def references(key: str) -> list[str]:
    # Whole identifier only. A bare substring test blocks Custom_TonalRamp because
    # Custom_TonalRamp_Primary is referenced; '_' is a word character, so \b never falls
    # between them and the longer key does not count as a reference to the shorter one.
    needle = re.compile(rb"\b" + re.escape(key.encode("utf-8")) + rb"\b")
    hits = []
    for dirpath, dirnames, filenames in os.walk(DESKTOP):
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin", "Localization")]
        for name in filenames:
            if not (name.endswith(".cs") or name.endswith(".axaml")):
                continue
            path = os.path.join(dirpath, name)
            with open(path, "rb") as f:
                if needle.search(f.read()):
                    hits.append(os.path.relpath(path, ROOT))
    return hits


def main(keys: list[str]) -> None:
    blocked = {k: references(k) for k in keys}
    blocked = {k: v for k, v in blocked.items() if v}
    if blocked:
        for k, v in blocked.items():
            print(f"{k} is still referenced by: {', '.join(v)}")
        sys.exit("refusing to remove referenced keys")
    for name in FILES:
        full = os.path.join(LOC, name)
        with open(full, "rb") as f:
            raw = f.read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        removed = 0
        for key in keys:
            pattern = re.compile(r'[ \t]*<data name="' + re.escape(key) + r'"[^>]*>.*?</data>[ \t]*\r?\n', re.DOTALL)
            text, n = pattern.subn("", text)
            removed += n
        if "\x00" in text:
            sys.exit(f"{name}: refusing to write a NUL byte")
        with open(full, "wb") as f:
            f.write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
        print(f"{name}: -{removed}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])
