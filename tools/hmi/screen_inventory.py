"""Inventario oggetti delle pagine HMI esportate: tipo, nome, posizione, testo, tag collegati."""
import glob, json, os, re, sys
import xml.etree.ElementTree as ET

BASE = sys.argv[1] if len(sys.argv) > 1 else "."
tags = {t["name"]: t for t in json.load(open(os.path.join(BASE, "14_hmi_tags.json"), encoding="utf-8"))["tags"]}


def texts(el):
    out = []
    for mt in el.iter("MultilingualText"):
        if mt.get("CompositionName") in ("Text", "TextOn", "TextOff", "Label"):
            for t in mt.iter("Text"):
                s = "".join(t.itertext()).strip()
                if s:
                    out.append(s)
    return out


def linked_tags(el):
    out = []
    for link in el.iter():
        if link.tag == "Tag" and link.get("TargetID") == "@OpenLink":
            n = link.findtext("Name")
            if n and n not in out:
                out.append(n)
    return out


inv = {}
for f in sorted(glob.glob(os.path.join(BASE, "screens", "*.xml"))):
    root = ET.parse(f).getroot()
    scr = root.find("Hmi.Screen.Screen")
    items = []
    for el in scr.iter():
        if not el.tag.startswith("Hmi.Screen.") or el.tag in ("Hmi.Screen.Screen", "Hmi.Screen.ScreenLayer", "Hmi.Screen.Property"):
            continue
        a = el.find("AttributeList")
        g = (lambda k: a.findtext(k) if a is not None else None)
        items.append({
            "type": el.tag.replace("Hmi.Screen.", ""), "name": g("ObjectName") or g("Name"),
            "left": g("Left"), "top": g("Top"), "w": g("Width"), "h": g("Height"),
            "text": texts(el), "tags": [f"{n} [{tags.get(n, {}).get('address', '?')}]" for n in linked_tags(el)],
        })
    inv[os.path.basename(f)[:-4]] = items

json.dump(inv, open(os.path.join(BASE, "15_screen_inventory.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

filt = sys.argv[2] if len(sys.argv) > 2 else None
for s, items in inv.items():
    if filt and not re.search(filt, s):
        continue
    print(f"\n### {s}  ({len(items)} oggetti)")
    for i in items:
        if i["text"] or i["tags"]:
            print(f"  {i['type'][:12]:12} {str(i['name'])[:28]:28} ({i['left']},{i['top']}) {' | '.join(i['text'])[:60]} :: {', '.join(i['tags'])[:260]}")
