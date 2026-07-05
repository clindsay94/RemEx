#!/usr/bin/env python3
"""Generate/update locale strings.xml files for all supported locales.

Reads the master values/strings.xml, reads each existing locale file to
preserve already-translated entries, then writes updated locale files
that mirror the master structure — using existing translations where
available and English text as placeholder otherwise.
"""

import os
import re

BASE_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "remex.android", "app", "src", "main", "res",
)

MASTER_PATH = os.path.join(BASE_DIR, "values", "strings.xml")

LOCALES = [
    "values-es",
    "values-fr",
    "values-hi",
    "values-in",
    "values-pl",
    "values-pt-rBR",
    "values-tr",
    "values-uk",
]


def read_existing_translations(locale_dir):
    """Read existing translated entries from a locale strings.xml."""
    path = os.path.join(locale_dir, "strings.xml")
    translations = {}
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            content = f.read()
        pattern = re.compile(r'<string name="([^"]+)">(.+?)</string>')
        for m in pattern.finditer(content):
            key, value = m.group(1), m.group(2)
            # Keep all existing translations (including app_name)
            translations[key] = value
    return translations


def generate_locale_file(master_lines, translations, output_path):
    """Generate a locale strings.xml mirroring master structure."""
    string_pattern = re.compile(r'^(\s*)<string name="([^"]+)">(.+?)</string>$')
    output_lines = []

    for line in master_lines:
        m = string_pattern.match(line)
        if m:
            indent, key, english_value = m.group(1), m.group(2), m.group(3)
            if key in translations:
                output_lines.append(
                    f'{indent}<string name="{key}">{translations[key]}</string>'
                )
            else:
                output_lines.append(
                    f'{indent}<string name="{key}">{english_value}</string>'
                )
        else:
            output_lines.append(line)

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        f.write("\n".join(output_lines))


def main():
    with open(MASTER_PATH, "r", encoding="utf-8") as f:
        master_content = f.read()
    master_lines = master_content.split("\n")

    for locale in LOCALES:
        locale_dir = os.path.join(BASE_DIR, locale)
        translations = read_existing_translations(locale_dir)
        output_path = os.path.join(locale_dir, "strings.xml")
        generate_locale_file(master_lines, translations, output_path)
        preserved = len(translations)
        print(f"  {locale}: written ({preserved} existing translations preserved)")

    print(f"\nDone — {len(LOCALES)} locale files updated.")


if __name__ == "__main__":
    main()
