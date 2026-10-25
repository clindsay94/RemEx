#!/usr/bin/env python3
"""Import Google Play Console's machine translations of the Android strings (RemEx-fwhqw).

Play auto-translates the app strings of a draft release into ~49 languages with Gemini. Those
translations only reach Play installs. This copies the languages RemEx does not ship yet into
remex.android's res/ folders, so the GitHub APK carries them too.

Input is the JSON the in-browser exporter downloads:

    {"source": "play-console-app-translations", "exportedAt": "...",
     "locales": {"ar": {"app_name": "...", "a_plural": {"one": "...", "other": "..."},
                        "an_array": ["...", "..."]}}}

A string value is a <string>, an object is a <plurals> (quantity -> text) and a list is a
<string-array>. Texts are raw display text; this script escapes them for Android.

The 9 reviewed languages (values, -es, -fr, -hi, -in, -pl, -pt-rBR, -tr, -uk) are never touched,
whatever the export holds. Existing machine-translated values are kept unless --update is given.

    uv run python scripts/import_play_translations.py play-translations.json --dry-run
    uv run python scripts/import_play_translations.py play-translations.json [--update]
"""

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_RES = REPO_ROOT / "remex.android" / "app" / "src" / "main" / "res"

EXPORT_SOURCE = "play-console-app-translations"

# Hand-reviewed translations. "" is the English default folder.
REVIEWED_QUALIFIERS = frozenset({"", "es", "fr", "hi", "in", "pl", "pt-rBR", "tr", "uk"})

# Play codes for languages we already ship reviewed. Play's "pt" is not pt-BR, which we ship.
SKIPPED_PLAY_CODES = frozenset({"en", "es", "fr", "hi", "id", "pl", "tr", "uk", "pt"})

# ResourcesImpl.adjustLanguageTag rewrites he/id/yi to iw/in/ji before resource lookup, so a
# values-he folder never matches a device. Norwegian devices report Bokmal as nb, which is also
# where AOSP and AppCompat keep their Norwegian strings; values-no would match nothing.
PLAY_LANGUAGE_TO_ANDROID = {"he": "iw", "id": "in", "yi": "ji", "no": "nb"}
# locales_config.xml takes BCP 47 tags, so the legacy folder codes go back to the modern ones.
ANDROID_LANGUAGE_TO_TAG = {"iw": "he", "in": "id", "ji": "yi"}

PLURAL_QUANTITIES = ("zero", "one", "two", "few", "many", "other")

LOCALE_DIR_RE = re.compile(r"^values-(?:(?P<lang>[a-z]{2,3})(?:-r(?P<region>[A-Z]{2}))?|b\+(?P<bcp>[A-Za-z0-9+]+))$")

# Same grammar as Get-PlaceholderSet in scripts/check-localization.ps1, so the two agree on what a
# format argument is. No space flag: "100% done" must not read as a "% d" specifier.
FORMAT_ARG_RE = re.compile(r"%%|%(?:(\d+)\$)?[-#+0,(]*\d*(?:\.\d+)?([a-zA-Z])")

# C0 controls other than tab and newline cannot be written to XML 1.0 at all.
CONTROL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f]")

MACHINE_NOTE = (
    "<!-- Machine-translated by Google Play (Gemini) and not reviewed. Written by\n"
    "     scripts/import_play_translations.py; re-run it rather than hand-editing in bulk. -->"
)


class ExportError(Exception):
    """A problem with the input that stops the whole import."""


# ---------------------------------------------------------------------------------------------
# Locale codes
# ---------------------------------------------------------------------------------------------

def play_code_to_qualifier(code):
    """Map a Play locale code (ar, zh-CN, es-419, zh-Hant-TW) to an Android values- qualifier."""
    parts = [p for p in re.split(r"[-_]", code.strip()) if p]
    if not parts or not re.fullmatch(r"[A-Za-z]{2,3}", parts[0]):
        raise ValueError(f"'{code}' is not a locale code")
    lang = PLAY_LANGUAGE_TO_ANDROID.get(parts[0].lower(), parts[0].lower())
    rest = parts[1:]
    if not rest:
        return lang
    if len(rest) == 1 and re.fullmatch(r"[A-Za-z]{2}", rest[0]):
        return f"{lang}-r{rest[0].upper()}"
    # Scripts (Hant) and numeric regions (419) only exist in the BCP 47 qualifier form.
    subtags = [lang]
    for part in rest:
        if re.fullmatch(r"[A-Za-z]{4}", part):
            subtags.append(part.title())
        elif re.fullmatch(r"[A-Za-z]{2}|\d{3}", part):
            subtags.append(part.upper())
        else:
            raise ValueError(f"'{code}' has a subtag Android cannot express: '{part}'")
    return "b+" + "+".join(subtags)


def qualifier_to_tag(qualifier):
    """The BCP 47 tag locales_config.xml wants for a values- qualifier."""
    if qualifier.startswith("b+"):
        subtags = qualifier[2:].split("+")
    else:
        subtags = qualifier.split("-r")
    subtags[0] = ANDROID_LANGUAGE_TO_TAG.get(subtags[0], subtags[0])
    return "-".join(subtags)


def locale_qualifiers(res_dir):
    """Qualifiers of every values-<locale> folder that holds a strings.xml."""
    found = []
    if not res_dir.is_dir():
        return found
    for child in res_dir.iterdir():
        if not child.is_dir() or not (child / "strings.xml").is_file():
            continue
        m = LOCALE_DIR_RE.match(child.name)
        if m:
            found.append(child.name[len("values-"):])
    return sorted(found)


# ---------------------------------------------------------------------------------------------
# Android text
# ---------------------------------------------------------------------------------------------

def format_args(text):
    """The format arguments in a value, as a set of "index:conversion" identities."""
    found = set()
    implicit = 0
    for m in FORMAT_ARG_RE.finditer(text or ""):
        if m.group(0) == "%%":
            continue
        if m.group(1):
            index = m.group(1)
        else:
            implicit += 1
            index = str(implicit)
        found.add(f"{index}:{m.group(2)}")
    return found


def escape_android(text):
    """Escape raw display text so aapt2 reads it back as exactly that text."""
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    out = []
    for ch in text:
        if ch == "\\":
            out.append("\\\\")
        elif ch == "'":
            out.append("\\'")
        elif ch == '"':
            out.append('\\"')
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\t":
            out.append("\\t")
        elif ch == "&":
            out.append("&amp;")
        elif ch == "<":
            out.append("&lt;")
        elif ch == ">":
            out.append("&gt;")
        else:
            out.append(ch)
    escaped = "".join(out)

    # aapt2 trims leading and trailing spaces and collapses runs, so the ones that matter are
    # written as  , which it keeps.
    lead = len(escaped) - len(escaped.lstrip(" "))
    trail = len(escaped) - len(escaped.rstrip(" "))
    if lead == len(escaped):
        escaped = "\\u0020" * lead
    else:
        core = escaped[lead:len(escaped) - trail]
        core = re.sub(r" {2,}", lambda m: " " + "\\u0020" * (len(m.group(0)) - 1), core)
        escaped = "\\u0020" * lead + core + "\\u0020" * trail

    if escaped[:1] in ("@", "?"):
        escaped = "\\" + escaped
    return escaped


def render_entry(name, kind, value):
    """One top-level element, indented the way the existing strings.xml files are."""
    if kind == "string":
        return f'    <string name="{name}">{escape_android(value)}</string>'
    if kind == "plurals":
        lines = [f'    <plurals name="{name}">']
        for quantity in PLURAL_QUANTITIES:
            if quantity in value:
                lines.append(f'        <item quantity="{quantity}">{escape_android(value[quantity])}</item>')
        lines.append("    </plurals>")
        return "\n".join(lines)
    lines = [f'    <string-array name="{name}">']
    lines.extend(f"        <item>{escape_android(item)}</item>" for item in value)
    lines.append("    </string-array>")
    return "\n".join(lines)


# ---------------------------------------------------------------------------------------------
# Resource files
# ---------------------------------------------------------------------------------------------

def element_text(element):
    return "".join(element.itertext())


def read_english(path):
    """English entries in file order: name -> (kind, value). Skips translatable="false"."""
    root = ET.parse(path).getroot()
    entries = {}
    for el in root:
        name = el.get("name")
        if not name or el.get("translatable") == "false":
            continue
        if el.tag == "string":
            entries[name] = ("string", element_text(el))
        elif el.tag == "plurals":
            entries[name] = ("plurals", {i.get("quantity"): element_text(i) for i in el.findall("item")})
        elif el.tag == "string-array":
            entries[name] = ("string-array", [element_text(i) for i in el.findall("item")])
    return entries


TOP_LEVEL_RE = re.compile(
    r'^[ \t]*<(?P<tag>string-array|plurals|string)\s[^>]*?name="(?P<name>[^"]+)"[^>]*>.*?</(?P=tag)>',
    re.MULTILINE | re.DOTALL,
)


def read_existing(path):
    """An already-imported locale file: name -> (kind, its element exactly as written)."""
    if not path.is_file():
        return {}
    text = path.read_text(encoding="utf-8")
    try:
        ET.fromstring(text.encode("utf-8"))
    except ET.ParseError as e:
        raise ExportError(f"{path} is not valid XML ({e}); fix or delete it before importing.")
    found = {}
    for m in TOP_LEVEL_RE.finditer(text):
        found[m.group("name")] = (m.group("tag"), "    " + m.group(0).lstrip(" \t"))
    return found


def render_file(elements):
    body = "\n".join(elements)
    return f'<?xml version="1.0" encoding="utf-8"?>\n{MACHINE_NOTE}\n<resources>\n{body}\n</resources>\n'


def render_locales_config(qualifiers):
    tags = sorted({qualifier_to_tag(q) for q in qualifiers})
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<locale-config xmlns:android="http://schemas.android.com/apk/res/android">',
        '    <locale android:name="en"/>',
    ]
    lines.extend(f'    <locale android:name="{tag}"/>' for tag in tags if tag != "en")
    lines.append("</locale-config>")
    return "\n".join(lines) + "\n"


def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


# ---------------------------------------------------------------------------------------------
# One translation
# ---------------------------------------------------------------------------------------------

def check_value(kind, english, value):
    """None if the translation can be written, otherwise why it cannot."""
    def bad_text(text):
        if not isinstance(text, str):
            return "not text"
        if not text.strip():
            return "empty"
        if CONTROL_RE.search(text):
            return "control character"
        return None

    if kind == "string":
        if not isinstance(value, str):
            return "type"
        problem = bad_text(value)
        if problem:
            return problem
        if format_args(value) != format_args(english):
            return "format args"
        return None

    if kind == "plurals":
        if not isinstance(value, dict):
            return "type"
        if "other" not in value or any(q not in PLURAL_QUANTITIES for q in value):
            return "plural quantities"
        for text in value.values():
            problem = bad_text(text)
            if problem:
                return problem
        # A language may drop the number from "one" ("One file"), so each item only has to stay
        # within English's arguments. "other" is the general case and must match English's exactly,
        # which is also the item check-localization.ps1 compares.
        english_args = set().union(*(format_args(t) for t in english.values())) if english else set()
        if any(not format_args(t) <= english_args for t in value.values()):
            return "format args"
        if format_args(value["other"]) != format_args(english.get("other", "")):
            return "format args"
        return None

    if not isinstance(value, list):
        return "type"
    if len(value) != len(english):
        return "array length"
    for text, source in zip(value, english):
        problem = bad_text(text)
        if problem:
            return problem
        if format_args(text) != format_args(source):
            return "format args"
    return None


# ---------------------------------------------------------------------------------------------
# Import
# ---------------------------------------------------------------------------------------------

def import_locale(code, translations, english, res_dir, update, dry_run, log):
    """Merge one Play locale into its values- folder. Returns its summary dict, or None if skipped."""
    try:
        qualifier = play_code_to_qualifier(code)
    except ValueError as e:
        log(f"  {code}: skipped - {e}")
        return None
    if code.lower() in SKIPPED_PLAY_CODES or qualifier in REVIEWED_QUALIFIERS:
        log(f"  {code}: skipped - values-{qualifier} holds reviewed translations and is never touched")
        return None
    if not isinstance(translations, dict):
        log(f"  {code}: skipped - its entry is not an object of key -> translation")
        return None

    path = res_dir / f"values-{qualifier}" / "strings.xml"
    existing = read_existing(path)
    summary = {"code": code, "qualifier": qualifier, "added": 0, "changed": 0, "kept": 0,
               "differs": 0, "skipped": {}, "missing": 0, "dropped": 0, "unknown": 0,
               "wrote": False, "has_file": False}

    summary["unknown"] = sum(1 for key in translations if key not in english)

    elements = []
    for name, (kind, source) in english.items():
        value = translations.get(name)
        rendered = None
        if value is not None:
            problem = check_value(kind, source, value)
            if problem:
                summary["skipped"][problem] = summary["skipped"].get(problem, 0) + 1
                log(f"    {code} {name}: not written ({problem})")
            else:
                rendered = render_entry(name, kind, value)

        old = None
        if name in existing:
            old_kind, old = existing[name]
            if old_kind != kind:
                # English changed the key's type; the old element would no longer compile.
                old = None
                summary["dropped"] += 1
        if rendered is None:
            if old is not None:
                elements.append(old)
                summary["kept"] += 1
            else:
                summary["missing"] += 1
        elif old is None:
            elements.append(rendered)
            summary["added"] += 1
        elif old == rendered:
            elements.append(old)
            summary["kept"] += 1
        elif update:
            elements.append(rendered)
            summary["changed"] += 1
        else:
            elements.append(old)
            summary["kept"] += 1
            summary["differs"] += 1

    summary["dropped"] += sum(1 for name in existing if name not in english)

    if not elements:
        return summary
    summary["has_file"] = True
    content = render_file(elements)
    current = path.read_text(encoding="utf-8") if path.is_file() else None
    if content != current:
        summary["wrote"] = True
        if not dry_run:
            write_text(path, content)
    return summary


def format_summary(s):
    skipped = sum(s["skipped"].values())
    detail = f" ({', '.join(f'{k} {v}' for k, v in sorted(s['skipped'].items()))})" if skipped else ""
    line = (f"  {s['code']} -> values-{s['qualifier']}: added {s['added']}, changed {s['changed']}, "
            f"kept {s['kept']}, skipped {skipped}{detail}, missing {s['missing']}")
    extras = []
    if s["differs"]:
        extras.append(f"{s['differs']} differ from the export (--update overwrites them)")
    if s["dropped"]:
        extras.append(f"dropped {s['dropped']} key(s) English no longer has")
    if s["unknown"]:
        extras.append(f"ignored {s['unknown']} key(s) English does not have or marks untranslatable")
    if extras:
        line += "\n      " + "; ".join(extras)
    return line


def run(export_path, res_dir, update=False, dry_run=False, out=print):
    try:
        data = json.loads(Path(export_path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise ExportError(f"Could not read {export_path}: {e}")
    if not isinstance(data, dict) or data.get("source") != EXPORT_SOURCE:
        raise ExportError(f"{export_path} is not a Play translations export (expected source '{EXPORT_SOURCE}').")
    locales = data.get("locales")
    if not isinstance(locales, dict):
        raise ExportError(f"{export_path} has no 'locales' object.")

    english_path = res_dir / "values" / "strings.xml"
    if not english_path.is_file():
        raise ExportError(f"English strings not found at {english_path}.")
    english = read_english(english_path)

    out(f"Importing {len(locales)} Play locale(s) from {export_path}"
        + (" (dry run, nothing is written)" if dry_run else ""))
    summaries = []
    for code in sorted(locales):
        s = import_locale(code, locales[code], english, res_dir, update, dry_run, out)
        if s is not None:
            summaries.append(s)

    out("")
    out("Summary:")
    for s in summaries:
        out(format_summary(s))
    if not summaries:
        out("  Nothing to import: every locale in the export is already shipped as a reviewed translation.")

    qualifiers = set(locale_qualifiers(res_dir))
    qualifiers.update(s["qualifier"] for s in summaries if s["has_file"])
    config_path = res_dir / "xml" / "locales_config.xml"
    config = render_locales_config(qualifiers)
    current = config_path.read_text(encoding="utf-8") if config_path.is_file() else None
    if config != current:
        out(f"locales_config.xml {'would list' if dry_run else 'now lists'} {config.count('<locale ')} languages.")
        if not dry_run:
            write_text(config_path, config)

    out("")
    if dry_run:
        out("Dry run finished. Run it again without --dry-run to write the files.")
    else:
        out("Done. Next: build the release APK and run the Android unit tests "
            "(cd remex.android; ./gradlew :app:testReleaseUnitTest).")
    return summaries


def main(argv=None):
    parser = argparse.ArgumentParser(description="Add Play Console's machine translations to the Android app.")
    parser.add_argument("export", help="play-translations.json from the in-browser exporter")
    parser.add_argument("--res", type=Path, default=DEFAULT_RES, help="Android res folder (default: the app's)")
    parser.add_argument("--update", action="store_true",
                        help="overwrite values already imported (never touches the 9 reviewed languages)")
    parser.add_argument("--dry-run", action="store_true", help="print the summary and write nothing")
    args = parser.parse_args(argv)
    # The Windows console is cp1252 by default; locale names and paths may not be.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    try:
        run(args.export, args.res, update=args.update, dry_run=args.dry_run)
    except ExportError as e:
        print(f"Import stopped: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
