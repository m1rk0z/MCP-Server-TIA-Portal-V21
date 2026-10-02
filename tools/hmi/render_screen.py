"""Anteprima schematica di una pagina WinCC classic esportata (box per oggetto, testo, nome immagine/tag).

Uso: python render_screen.py <screen.xml> <out.png> [x0 y0 x1 y1]
"""
import sys
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont

src, out = sys.argv[1], sys.argv[2]
crop = tuple(map(int, sys.argv[3:7])) if len(sys.argv) >= 7 else None
root = ET.parse(src).getroot()
scr = root.find("Hmi.Screen.Screen")
W = int(scr.find("AttributeList").findtext("Width"))
H = int(scr.find("AttributeList").findtext("Height"))
img = Image.new("RGB", (W, H), (182, 182, 182))
d = ImageDraw.Draw(img)
try:
    font = ImageFont.truetype("arial.ttf", 11)
except Exception:
    font = ImageFont.load_default()

COL = {"TextField": (0, 0, 160), "GraphicView": (0, 120, 0), "IOField": (160, 0, 160), "Button": (160, 80, 0),
       "Rectangle": (90, 90, 90), "Circle": (180, 0, 0), "Line": (0, 0, 0), "Polyline": (0, 0, 0), "Polygon": (60, 60, 60)}


def text_of(el):
    for mt in el.iter("MultilingualText"):
        if mt.get("CompositionName") in ("Text", "TextOn"):
            for t in mt.iter("Text"):
                s = "".join(t.itertext()).strip()
                if s:
                    return s
    return ""


for el in scr.iter():
    if not el.tag.startswith("Hmi.Screen.") or el.tag in ("Hmi.Screen.Screen", "Hmi.Screen.ScreenLayer", "Hmi.Screen.Group", "Hmi.Screen.Property"):
        continue
    a = el.find("AttributeList")
    if a is None or a.findtext("Left") is None:
        continue
    x, y = int(a.findtext("Left")), int(a.findtext("Top"))
    w, h = int(a.findtext("Width") or 0), int(a.findtext("Height") or 0)
    kind = el.tag[11:]
    c = COL.get(kind, (100, 100, 100))
    if kind == "Line":
        d.line([x, y, x + w, y + h], fill=c, width=2)
        continue
    d.rectangle([x, y, x + max(w, 1), y + max(h, 1)], outline=c, width=1)
    label = text_of(el)
    if not label:
        pic = el.find("LinkList/Picture/Name")
        label = pic.text if pic is not None else ""
        tag = next((t.findtext("Name") for t in el.iter("Tag")), None)
        if tag:
            label = (label + " " + tag.split("_", 1)[-1]).strip()
    if label:
        d.text((x + 2, y + 1), label[:30], fill=c, font=font)

if crop:
    img = img.crop(crop)
img.save(out)
print(out, img.size)
