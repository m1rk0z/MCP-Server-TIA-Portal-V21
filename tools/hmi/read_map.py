import zipfile
import xml.etree.ElementTree as ET
import glob

# Read sheet XMLs from xlsx
def inspect_xlsx(path):
    print("=== " + path + " ===")
    with zipfile.ZipFile(path, 'r') as z:
        # shared strings
        ss_xml = z.read('xl/sharedStrings.xml')
        ss_tree = ET.fromstring(ss_xml)
        strings = [elem.text for elem in ss_tree.iter('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}t')]
        
        # sheet1
        sheet_xml = z.read('xl/worksheets/sheet1.xml')
        sheet_tree = ET.fromstring(sheet_xml)
        rows = sheet_tree.findall('.//{http://schemas.openxmlformats.org/spreadsheetml/2006/main}row')
        for r in rows[:25]:
            row_vals = []
            for c in r.findall('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}c'):
                v = c.find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}v')
                if v is not None:
                    val = v.text
                    if c.get('t') == 's':
                        val = strings[int(val)] if int(val) < len(strings) else val
                    row_vals.append(val)
            if row_vals:
                print(" | ".join(row_vals))

if len(sys.argv) < 2:
    print("uso: python read_map.py <file.xlsx>")
    sys.exit(1)
inspect_xlsx(sys.argv[1])
