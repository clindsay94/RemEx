#!/usr/bin/env python3
"""Tests for scripts/import_play_translations.py (RemEx-fwhqw).

Run: uv run python scripts/tests/test_import_play_translations.py
verify.ps1 runs it next to the edit guard's tests.
"""

import json
import re
import shutil
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.dont_write_bytecode = True  # no scripts/__pycache__ left in the working tree

import import_play_translations as imp  # noqa: E402

FIXTURE = HERE / "fixtures" / "play_import"
EXPORT = FIXTURE / "play-translations.json"


def aapt_text(raw):
    """What aapt2 makes of a <string> body (after XML entity decoding): escapes, quotes, whitespace."""
    chars = []  # (char, literal) - literal chars survive whitespace collapsing and trimming
    quoted = False
    i = 0
    while i < len(raw):
        c = raw[i]
        if c == "\\" and i + 1 < len(raw):
            n = raw[i + 1]
            if n == "u":
                chars.append((chr(int(raw[i + 2:i + 6], 16)), True))
                i += 6
                continue
            chars.append(({"n": "\n", "t": "\t"}.get(n, n), True))
            i += 2
            continue
        if c == '"':
            quoted = not quoted
            i += 1
            continue
        if c.isspace() and not quoted:
            if not chars or chars[-1] != (" ", False):
                chars.append((" ", False))
            i += 1
            continue
        chars.append((c, True))
        i += 1
    while chars and chars[0] == (" ", False):
        chars.pop(0)
    while chars and chars[-1] == (" ", False):
        chars.pop()
    return "".join(c for c, _ in chars)


def strings_of(path):
    root = ET.parse(path).getroot()
    out = {}
    for el in root:
        if el.tag == "string":
            out[el.get("name")] = aapt_text(el.text or "")
        elif el.tag == "plurals":
            out[el.get("name")] = {i.get("quantity"): aapt_text(i.text or "") for i in el}
        elif el.tag == "string-array":
            out[el.get("name")] = [aapt_text(i.text or "") for i in el]
    return out


class ImporterTests(unittest.TestCase):

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="play-import-"))
        self.res = self.tmp / "res"
        shutil.copytree(FIXTURE / "values", self.res / "values")
        shutil.copytree(FIXTURE / "values-es", self.res / "values-es")
        self.log = []

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_import(self, export=EXPORT, **kwargs):
        self.log = []
        summaries = imp.run(export, self.res, out=self.log.append, **kwargs)
        return {s["code"]: s for s in summaries}

    def snapshot(self):
        return {p.relative_to(self.res).as_posix(): p.read_bytes() for p in self.res.rglob("*") if p.is_file()}

    # -- locale codes ------------------------------------------------------------------------

    def test_play_codes_map_to_the_qualifiers_android_resolves(self):
        expected = {
            "ar": "ar", "bn": "bn", "bg": "bg", "ca": "ca", "zh-CN": "zh-rCN", "hr": "hr", "cs": "cs",
            "da": "da", "nl": "nl", "et": "et", "fi": "fi", "de": "de", "el": "el", "gu": "gu",
            "he": "iw", "iw": "iw", "hu": "hu", "is": "is", "it": "it", "ja": "ja", "kn": "kn",
            "ko": "ko", "lv": "lv", "lt": "lt", "ml": "ml", "mr": "mr", "no": "nb", "nb": "nb",
            "fa": "fa", "pa": "pa", "ro": "ro", "ru": "ru", "sk": "sk", "sl": "sl", "sw": "sw",
            "sv": "sv", "ta": "ta", "te": "te", "th": "th", "ur": "ur", "vi": "vi", "zu": "zu",
            "id": "in", "pt-BR": "pt-rBR", "es-419": "b+es+419", "zh-Hant-TW": "b+zh+Hant+TW",
        }
        for code, qualifier in expected.items():
            self.assertEqual(qualifier, imp.play_code_to_qualifier(code), code)

    def test_locales_config_uses_modern_tags(self):
        for qualifier, tag in {"iw": "he", "in": "id", "nb": "nb", "zh-rCN": "zh-CN",
                               "pt-rBR": "pt-BR", "b+es+419": "es-419"}.items():
            self.assertEqual(tag, imp.qualifier_to_tag(qualifier), qualifier)

    def test_the_checked_in_locales_config_matches_the_folders(self):
        # Zero new locales must change nothing: the generated file is the committed one, byte for byte.
        config = imp.DEFAULT_RES / "xml" / "locales_config.xml"
        self.assertTrue(imp.locale_qualifiers(imp.DEFAULT_RES), "no locale folders found - scan is broken")
        self.assertEqual(config.read_text(encoding="utf-8"),
                         imp.render_locales_config(imp.locale_qualifiers(imp.DEFAULT_RES)))

    # -- escaping ----------------------------------------------------------------------------

    def test_escaping_round_trips_through_aapt_rules(self):
        for text in ["@Es ist \"fertig\" & <bereit>'s\nZeile 2\tTab \\ Ende", "?x", "  two  spaces ",
                     "50% done", "%1$s's file", "plain"]:
            raw = imp.escape_android(text)
            decoded = ET.fromstring(f"<string>{raw}</string>").text
            self.assertEqual(text, aapt_text(decoded), raw)

    def test_escaping_is_what_android_expects(self):
        self.assertEqual("\\@a \\'b\\' \\\"c\\\" &amp; &lt;d&gt; \\n \\\\",
                         imp.escape_android("@a 'b' \"c\" & <d> \n \\"))
        self.assertEqual("\\?q", imp.escape_android("?q"))
        self.assertEqual("x@y", imp.escape_android("x@y"))
        self.assertEqual("\\u0020a \\u0020b\\u0020", imp.escape_android(" a  b "))

    def test_format_args_keep_index_and_conversion(self):
        self.assertEqual({"1:d", "2:s"}, imp.format_args("%1$d files on %2$s"))
        self.assertEqual({"1:d"}, imp.format_args("%1$d%% done, 100% sure"))
        self.assertEqual({"1:s", "2:d"}, imp.format_args("%s and %d"))

    # -- import ------------------------------------------------------------------------------

    def test_strings_plurals_and_arrays_are_written(self):
        summaries = self.run_import()
        ar = strings_of(self.res / "values-ar" / "strings.xml")
        self.assertEqual("ريمكس", ar["app_name"])
        self.assertEqual("مرحبا، %1$s!", ar["greeting"])
        self.assertEqual(["zero", "one", "two", "few", "many", "other"], list(ar["files"]))
        self.assertEqual("%1$d ملف", ar["files"]["other"])
        self.assertEqual(["الاثنين", "الثلاثاء في %1$s"], ar["days"])
        self.assertNotIn("progress", ar, "translatable=\"false\" keys must not be imported")
        self.assertNotIn("not_a_key", ar)
        self.assertEqual(5, summaries["ar"]["added"])
        self.assertEqual(2, summaries["ar"]["unknown"])

        de = strings_of(self.res / "values-de" / "strings.xml")
        self.assertEqual("@Es ist \"fertig\" & <bereit>'s\nZeile 2\tTab \\ Ende", de["plain"])
        self.assertEqual("?מוכן", strings_of(self.res / "values-iw" / "strings.xml")["plain"])
        self.assertEqual("  Det er klart  nå ", strings_of(self.res / "values-nb" / "strings.xml")["plain"])
        self.assertEqual("你好，%1$s！", strings_of(self.res / "values-zh-rCN" / "strings.xml")["greeting"])

    def test_a_format_arg_mismatch_is_logged_and_not_written(self):
        summaries = self.run_import()
        ar = strings_of(self.res / "values-ar" / "strings.xml")
        self.assertNotIn("count_and_name", ar)
        self.assertEqual({"format args": 1}, summaries["ar"]["skipped"])
        self.assertTrue(any("ar count_and_name: not written (format args)" in line for line in self.log))

        de = strings_of(self.res / "values-de" / "strings.xml")
        self.assertIn("count_and_name", de)
        self.assertNotIn("files", de, "a plural item with an argument English lacks must be skipped")
        self.assertNotIn("days", de, "an array of the wrong length must be skipped")
        self.assertEqual({"format args": 1, "array length": 1}, summaries["de"]["skipped"])

    def test_missing_counts_keys_that_fall_back_to_english(self):
        summaries = self.run_import()
        # untouched + count_and_name (skipped) fall back; progress is untranslatable and not counted.
        self.assertEqual(2, summaries["ar"]["missing"])

    def test_reviewed_locales_are_never_touched(self):
        before = (self.res / "values-es" / "strings.xml").read_bytes()
        english = (self.res / "values" / "strings.xml").read_bytes()
        summaries = self.run_import(update=True)
        self.assertEqual(before, (self.res / "values-es" / "strings.xml").read_bytes())
        self.assertEqual(english, (self.res / "values" / "strings.xml").read_bytes())
        for code in ("es", "pt", "pt-BR", "id", "en"):
            self.assertNotIn(code, summaries)
        for folder in ("values-pt", "values-pt-rBR", "values-in", "values-id", "values-en"):
            self.assertFalse((self.res / folder).exists(), folder)

    def test_rerunning_is_idempotent(self):
        self.run_import()
        first = self.snapshot()
        summaries = self.run_import()
        self.assertEqual(first, self.snapshot())
        self.assertTrue(all(s["added"] == 0 and s["changed"] == 0 and not s["wrote"] for s in summaries.values()))

    def test_existing_values_are_kept_unless_update(self):
        self.run_import()
        changed = self.tmp / "changed.json"
        changed.write_text(EXPORT.read_text(encoding="utf-8").replace("ريمكس", "ريمكس الجديد"), encoding="utf-8")

        summaries = self.run_import(export=changed)
        self.assertEqual("ريمكس", strings_of(self.res / "values-ar" / "strings.xml")["app_name"])
        self.assertEqual(1, summaries["ar"]["differs"])
        self.assertEqual(0, summaries["ar"]["changed"])

        summaries = self.run_import(export=changed, update=True)
        self.assertEqual("ريمكس الجديد", strings_of(self.res / "values-ar" / "strings.xml")["app_name"])
        self.assertEqual(1, summaries["ar"]["changed"])

    def test_a_hand_fix_survives_a_rerun_without_update(self):
        self.run_import()
        path = self.res / "values-de" / "strings.xml"
        path.write_text(path.read_text(encoding="utf-8").replace("Hallo, %1$s!", "Servus, %1$s!"), encoding="utf-8")
        self.run_import()
        self.assertEqual("Servus, %1$s!", strings_of(path)["greeting"])

    def test_keys_english_dropped_are_removed(self):
        self.run_import()
        english = self.res / "values" / "strings.xml"
        english.write_text(english.read_text(encoding="utf-8").replace(
            '    <string name="plain">It\\\'s ready</string>\n', ""), encoding="utf-8")
        summaries = self.run_import()
        self.assertNotIn("plain", strings_of(self.res / "values-de" / "strings.xml"))
        self.assertEqual(1, summaries["de"]["dropped"])

    def test_dry_run_writes_nothing(self):
        before = self.snapshot()
        summaries = self.run_import(dry_run=True)
        self.assertEqual(before, self.snapshot())
        self.assertEqual(5, summaries["ar"]["added"])
        self.assertTrue(any("would list" in line for line in self.log))

    def test_locales_config_lists_every_shipped_language(self):
        self.run_import()
        root = ET.parse(self.res / "xml" / "locales_config.xml").getroot()
        tags = [el.get("{http://schemas.android.com/apk/res/android}name") for el in root]
        self.assertEqual(["en", "ar", "de", "es", "he", "nb", "zh-CN"], tags)

    def test_written_files_are_valid_xml_with_unique_keys(self):
        self.run_import()
        for path in self.res.glob("values-*/strings.xml"):
            names = [el.get("name") for el in ET.parse(path).getroot()]
            self.assertEqual(len(names), len(set(names)), path)
            self.assertNotIn(b"\x00", path.read_bytes())
            self.assertNotIn(b"\r\n", path.read_bytes())

    def test_a_file_that_is_not_an_export_is_refused(self):
        bogus = self.tmp / "bogus.json"
        bogus.write_text('{"locales": {}}', encoding="utf-8")
        with self.assertRaises(imp.ExportError):
            self.run_import(export=bogus)

    # -- keys that stay English --------------------------------------------------------------

    def add_english(self, lines):
        english = self.res / "values" / "strings.xml"
        english.write_text(english.read_text(encoding="utf-8").replace(
            "</resources>", "".join(f"    {line}\n" for line in lines) + "</resources>"), encoding="utf-8")

    def write_json(self, name, data):
        path = self.tmp / name
        path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        return path

    def test_keep_english_keys_are_written_as_english_over_any_translation(self):
        self.add_english(['<string name="cd_key_home">Home</string>',
                          '<string name="cd_key_enter">Enter</string>',
                          '<string name="font_lato">Lato</string>'])
        ar_file = self.res / "values-ar" / "strings.xml"
        ar_file.parent.mkdir()
        ar_file.write_text(imp.render_file(['    <string name="cd_key_home">الرئيسية</string>']), encoding="utf-8")
        export = self.write_json("export.json", {"source": imp.EXPORT_SOURCE, "locales": {
            "ar": {"app_name": "ريمكس", "cd_key_home": "الرئيسية", "cd_key_enter": "إدخال", "font_lato": "لاتو"},
            "es": {"cd_key_home": "Inicio"},
        }})
        es_before = (self.res / "values-es" / "strings.xml").read_bytes()

        # Without --update: an earlier import's translation of a KEEP_ENGLISH key is still replaced.
        summaries = self.run_import(export=export)

        ar = strings_of(ar_file)
        self.assertEqual("Home", ar["cd_key_home"], "a keycap with a Latin legend must read as English")
        self.assertEqual("Lato", ar["font_lato"], "a font label is the family's own name")
        self.assertEqual("إدخال", ar["cd_key_enter"], "a glyph-only key draws no legend, so it is translated")
        self.assertIn('    <string name="cd_key_home">Home</string>', ar_file.read_text(encoding="utf-8"),
                      "the copy is the English element byte for byte")
        self.assertEqual(2, summaries["ar"]["english"])
        # The 6 other fixture keys this export lacks; the two written as English are not counted.
        self.assertEqual(6, summaries["ar"]["missing"])
        self.assertEqual(es_before, (self.res / "values-es" / "strings.xml").read_bytes())

    def test_a_locale_with_only_keep_english_keys_gets_no_file(self):
        self.add_english(['<string name="cd_key_home">Home</string>'])
        export = self.write_json("export.json", {"source": imp.EXPORT_SOURCE, "locales": {"fi": {"cd_key_home": "Koti"}}})
        summaries = self.run_import(export=export)
        self.assertFalse((self.res / "values-fi").exists())
        self.assertFalse(summaries["fi"]["has_file"])

    def test_keep_english_matches_the_legend_keys_the_keycap_test_checks(self):
        kotlin = (imp.REPO_ROOT / "remex.android" / "app" / "src" / "test" / "java" / "com" / "clindsay94"
                  / "remex" / "KeycapAccessibleNameTests.kt").read_text(encoding="utf-8")
        legends = set(re.findall(r'"(cd_key_\w+)" to "', kotlin))
        self.assertTrue(legends, "found no legend keys - the scan is broken")
        self.assertEqual(legends, {k for k in imp.KEEP_ENGLISH if k.startswith("cd_key_")})
        self.assertTrue(imp.keeps_english("font_space_grotesk"))
        self.assertFalse(imp.keeps_english("cd_key_enter"))

    # -- supplement --------------------------------------------------------------------------

    AR_FILES = {"zero": "%1$d ملف (ملحق)", "one": "%1$d ملف (ملحق)", "two": "%1$d ملفان", "few": "%1$d ملفات",
                "many": "%1$d ملفًا", "other": "%1$d ملف (ملحق)"}

    def supplement(self, locales, source=imp.SUPPLEMENT_SOURCE):
        return self.write_json("supplement.json", {"source": source, "locales": locales})

    def test_supplement_plurals_are_merged_over_the_export_and_written(self):
        supplement = self.supplement({
            "ar": {"files": self.AR_FILES},
            "de": {"files": {"one": "%1$d Datei", "other": "%1$d Dateien"}},
            "ja": {"files": {"other": "%1$d 個のファイル"}},
        })
        summaries = self.run_import(supplement_path=supplement)

        self.assertEqual(self.AR_FILES, strings_of(self.res / "values-ar" / "strings.xml")["files"],
                         "the supplement wins over the export's plural")
        de = strings_of(self.res / "values-de" / "strings.xml")
        self.assertEqual({"one": "%1$d Datei", "other": "%1$d Dateien"}, de["files"])
        self.assertEqual("Hallo, %1$s!", de["greeting"], "the export's strings still come through")
        self.assertEqual({"other": "%1$d 個のファイル"}, strings_of(self.res / "values-ja" / "strings.xml")["files"])
        self.assertEqual(1, summaries["de"]["supplemented"])
        self.assertNotIn("files", summaries["de"]["skipped"])

        text = (self.res / "values-ar" / "strings.xml").read_text(encoding="utf-8")
        quantities = re.findall(r'quantity="(\w+)"', text)
        self.assertEqual(list(imp.PLURAL_QUANTITIES), quantities, "items are written in CLDR order")

    def test_the_supplement_flag_alone_uses_the_checked_in_file(self):
        captured = {}
        original = imp.run
        imp.run = lambda *a, **k: captured.update(k)
        try:
            self.assertEqual(0, imp.main(["export.json", "--supplement"]))
            self.assertEqual(imp.DEFAULT_SUPPLEMENT, captured["supplement_path"])
            self.assertEqual(0, imp.main(["export.json"]))
            self.assertIsNone(captured["supplement_path"])
        finally:
            imp.run = original

    def test_a_supplement_needs_its_own_source(self):
        faked = self.supplement({"de": {"files": {"one": "%1$d Datei", "other": "%1$d Dateien"}}},
                                source=imp.EXPORT_SOURCE)
        with self.assertRaises(imp.ExportError):
            self.run_import(supplement_path=faked)

    def test_a_bad_supplement_entry_stops_the_import_before_anything_is_written(self):
        before = self.snapshot()
        for bad in ({"de": {"files": {"one": "%1$d Datei", "other": "%1$s Dateien"}}},
                    {"de": {"files": {"one": "%1$d Datei"}}},
                    {"de": {"files": {"one": "%1$d Datei", "lots": "%1$d", "other": "%1$d Dateien"}}},
                    {"de": {"no_such_key": "x"}},
                    {"de": {"cd_key_home": "Pos1"}}):
            with self.subTest(bad=bad):
                if "cd_key_home" in bad["de"]:
                    self.add_english(['<string name="cd_key_home">Home</string>'])
                    before = self.snapshot()
                with self.assertRaises(imp.ExportError):
                    self.run_import(supplement_path=self.supplement(bad))
                self.assertEqual(before, self.snapshot())

    def test_the_checked_in_supplement_covers_every_english_plural(self):
        english = imp.read_english(imp.DEFAULT_RES / "values" / "strings.xml")
        plurals = {name for name, (kind, _) in english.items() if kind == "plurals"}
        self.assertTrue(plurals, "found no English plurals - the scan is broken")
        data = json.loads(imp.DEFAULT_SUPPLEMENT.read_text(encoding="utf-8"))
        self.assertEqual(imp.SUPPLEMENT_SOURCE, data["source"])
        imp.check_supplement(data["locales"], english, imp.DEFAULT_SUPPLEMENT)
        shipped = {imp.play_code_to_qualifier(c) for c in data["locales"]}
        machine = set(imp.locale_qualifiers(imp.DEFAULT_RES)) - imp.REVIEWED_QUALIFIERS
        self.assertEqual(machine, shipped, "every machine locale needs its plurals, and only those")
        for code, entries in data["locales"].items():
            self.assertEqual(plurals, set(entries), code)

    # -- Play's mangled letters --------------------------------------------------------------

    def test_latin1_letters_play_mangled_into_percent_codes_are_repaired(self):
        export = self.write_json("export.json", {"source": imp.EXPORT_SOURCE, "locales": {
            "ca": {"greeting": "Hola, %1$s! Informaci%00f3", "plain": "Est%00e0 llest"},
        }})
        summaries = self.run_import(export=export)
        ca = strings_of(self.res / "values-ca" / "strings.xml")
        self.assertEqual("Hola, %1$s! Informació", ca["greeting"])
        self.assertEqual("Està llest", ca["plain"])
        self.assertEqual(2, summaries["ca"]["repaired"])
        self.assertEqual({}, summaries["ca"]["skipped"])


if __name__ == "__main__":
    unittest.main(verbosity=1)
