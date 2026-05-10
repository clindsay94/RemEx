import os
import re
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESX_DIR = os.path.join(BASE_DIR, "Remex.Client", "Localization")
MASTER_PATH = os.path.join(RESX_DIR, "Strings.resx")

LOCALES = [
    "Strings.es.resx",
    "Strings.fr.resx",
    "Strings.hi.resx",
    "Strings.id.resx",
    "Strings.pl.resx",
    "Strings.pt-BR.resx",
    "Strings.tr.resx",
    "Strings.uk.resx",
]

def parse_resx(path):
    if not os.path.exists(path):
        return {}
    
    tree = ET.parse(path)
    root = tree.getroot()
    data = {}
    for entry in root.findall('data'):
        name = entry.get('name')
        value_elem = entry.find('value')
        if value_elem is not None:
            data[name] = value_elem.text
    return data

def sync_resx(master_path, locale_path):
    master_data = parse_resx(master_path)
    locale_data = parse_resx(locale_path)
    
    # We want to preserve the master structure (comments, spacing) as much as possible
    # but .resx files are complex. A simple approach is to read the master file
    # and replace values if they exist in the locale file, otherwise keep English.
    
    with open(master_path, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    
    output_lines = []
    current_data_name = None
    
    # Regex to find <data name="..."
    data_pattern = re.compile(r'<data name="([^"]+)"')
    value_pattern = re.compile(r'(\s*)<value>(.*?)</value>')
    
    i = 0
    while i < len(lines):
        line = lines[i]
        data_match = data_pattern.search(line)
        if data_match:
            name = data_match.group(1)
            output_lines.append(line)
            i += 1
            # Look for <value> in subsequent lines until </data>
            while i < len(lines) and '</data>' not in lines[i]:
                value_match = value_pattern.search(lines[i])
                if value_match:
                    indent = value_match.group(1)
                    english_value = value_match.group(2)
                    if name in locale_data and locale_data[name] is not None:
                        val = escape(locale_data[name])
                        output_lines.append(f'{indent}<value>{val}</value>\n')
                    else:
                        # english_value is already escaped because it's read from the file raw
                        output_lines.append(f'{indent}<value>{english_value}</value>\n')
                else:
                    output_lines.append(lines[i])
                i += 1
            if i < len(lines):
                output_lines.append(lines[i]) # append </data>
        else:
            output_lines.append(line)
        i += 1
        
    with open(locale_path, 'w', encoding='utf-8') as f:
        f.writelines(output_lines)
    
    return len(locale_data)

def main():
    for locale in LOCALES:
        locale_path = os.path.join(RESX_DIR, locale)
        count = sync_resx(MASTER_PATH, locale_path)
        print(f"  {locale}: synced ({count} existing translations preserved)")
    print(f"\nDone — {len(LOCALES)} .resx files updated.")

if __name__ == "__main__":
    main()
