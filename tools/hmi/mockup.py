# mockup.py - disegna le schermate generate in un PDF, IN SCALA REALE.
#
#   python mockup.py <prefisso> <uscita.pdf> [cartella xml]   es: python mockup.py B_ Mockup_B.pdf screens
#
# Perche in scala reale e non "grande che si vede bene": il punto tipografico e
# un'unita fisica (1 pt = 0,353 mm), e il TP700 ha un'area attiva di
# 152,4 x 91,4 mm per 800 x 480 px. Disegnando la pagina PDF a quella misura
# esatta e i caratteri al loro corpo in punti, il foglio stampato al 100% E il
# pannello. Si appende al muro, si fanno due passi indietro e si vede se i testi
# si leggono da un metro: nessun giudizio a occhio su uno schermo da 27 pollici.
#
# Non e un emulatore: i campi legati ai tag mostrano un segnaposto, non il
# valore del PLC. Serve a giudicare corpo, ingombro e navigazione.

import os, re, sys, glob
import xml.etree.ElementTree as ET
from reportlab.pdfgen import canvas
from reportlab.lib.units import mm

PX_W, PX_H = 800, 480
MM_W, MM_H = 152.4, 91.4          # area attiva del TP700 Comfort, da datasheet
PANEL_W, PANEL_H = MM_W * mm, MM_H * mm
SCALE = PANEL_W / PX_W            # punti PDF per pixel di pannello
MARGIN_TOP, MARGIN = 34, 14

def col(s, default=(0, 0, 0)):
    """ "16, 58, 96" -> (0.06, 0.23, 0.38). WinCC scrive i colori cosi. """
    if not s:
        return default
    p = [q.strip() for q in s.split(',')]
    if len(p) < 3:
        return default
    try:
        return tuple(int(q) / 255.0 for q in p[:3])
    except ValueError:
        return default

def testo(node):
    """Il testo mostrato sta annidato in <body><p>..</p></body> con entita HTML."""
    for t in node.iter():
        if t.tag.endswith('Text') and t.text and 'body' in t.text:
            s = re.sub(r'<[^>]*>', ' ', t.text)
            s = (s.replace('&amp;', '&').replace('&lt;', '<')
                  .replace('&gt;', '>').replace('&nbsp;', ' ')
                  .replace('&#160;', ' '))
            return re.sub(r'\s+', ' ', s).strip()
    return ''

def num(al, name, d=0):
    e = al.find(name)
    if e is None or not (e.text or '').strip():
        return d
    try:
        return int(float(e.text.strip()))
    except ValueError:
        return d

def txt(al, name, d=''):
    e = al.find(name)
    return (e.text or d).strip() if e is not None else d

def corpo(node):
    """(corpo in punti, grassetto) dal MultiLingualFont dell'oggetto."""
    for f in node.iter():
        if f.tag.endswith('FontItem'):
            a = f.find('AttributeList')
            if a is None:
                continue
            s = a.find('FontSize')
            st = a.find('FontStyle')
            return (int(float(s.text)) if s is not None and s.text else 10,
                    bool(st is not None and st.text and 'Bold' in st.text))
    return (10, False)

def disegna(c, path, nome):
    doc = ET.parse(path).getroot()

    ox, oy = MARGIN, MARGIN                      # origine del pannello nella pagina
    def X(px): return ox + px * SCALE
    def Y(py, h=0): return oy + PANEL_H - (py + h) * SCALE   # PDF ha y verso l'alto

    # fondo del pannello e cornice
    sfondo = (0.94, 0.96, 0.97)
    for s in doc.iter():
        if s.tag.endswith('Screen.Screen'):
            a = s.find('AttributeList')
            if a is not None:
                sfondo = col(txt(a, 'BackColor'), sfondo)
            break
    c.setFillColorRGB(*sfondo)
    c.rect(ox, oy, PANEL_W, PANEL_H, fill=1, stroke=0)

    for n in doc.iter():
        comp = n.get('CompositionName')
        if comp != 'ScreenItems':
            continue
        al = n.find('AttributeList')
        if al is None:
            continue
        l, t = num(al, 'Left'), num(al, 'Top')
        w, h = num(al, 'Width'), num(al, 'Height')
        if w <= 0 or h <= 0:
            continue
        tipo = n.tag.rsplit('.', 1)[-1]

        bg = txt(al, 'BackColor')
        bc = txt(al, 'BorderColor')
        bw = num(al, 'BorderWidth')
        rr = num(al, 'RoundCornerWidth')

        # un fondo trasparente in WinCC e "0, 0, 0, 0" (quattro componenti)
        pieno = bool(bg) and len(bg.split(',')) == 3
        if pieno or bw:
            if pieno:
                c.setFillColorRGB(*col(bg))
            if bw:
                c.setStrokeColorRGB(*col(bc, (0.8, 0.86, 0.9)))
                c.setLineWidth(max(0.3, bw * SCALE))
            if rr:
                c.roundRect(X(l), Y(t, h), w * SCALE, h * SCALE,
                            min(rr, 8) * SCALE, fill=1 if pieno else 0, stroke=1 if bw else 0)
            else:
                c.rect(X(l), Y(t, h), w * SCALE, h * SCALE,
                       fill=1 if pieno else 0, stroke=1 if bw else 0)

        s = testo(n)
        if not s and tipo in ('IOField', 'SymbolicIOField', 'DateTimeField', 'Bar'):
            # campi legati a un tag: segnaposto, non il valore del PLC
            s = {'IOField': '8888', 'SymbolicIOField': '\u2014\u2014\u2014',
                 'DateTimeField': '00:00', 'Bar': ''}.get(tipo, '')
        if not s:
            continue

        pt, bold = corpo(n)
        c.setFont('Helvetica-Bold' if bold else 'Helvetica', pt)
        c.setFillColorRGB(*col(txt(al, 'ForeColor'), (0.1, 0.2, 0.3)))
        align = txt(al, 'HorizontalAlignment', 'Left')
        # linea di base: centrata in verticale nel riquadro
        by = Y(t, h) + (h * SCALE - pt * 0.72) / 2
        if align == 'Center':
            c.drawCentredString(X(l) + w * SCALE / 2, by, s)
        elif align == 'Right':
            c.drawRightString(X(l) + w * SCALE, by, s)
        else:
            c.drawString(X(l) + 2, by, s)

    # cornice del pannello e nome, fuori dall'area attiva
    c.setStrokeColorRGB(0.2, 0.2, 0.2)
    c.setLineWidth(0.8)
    c.rect(ox, oy, PANEL_W, PANEL_H, fill=0, stroke=1)
    c.setFont('Helvetica-Bold', 9)
    c.setFillColorRGB(0.1, 0.1, 0.1)
    c.drawString(ox, oy + PANEL_H + 10, nome)
    c.setFont('Helvetica', 7)
    c.setFillColorRGB(0.45, 0.45, 0.45)
    c.drawRightString(ox + PANEL_W, oy + PANEL_H + 10,
                      'scala reale 152,4 x 91,4 mm - stampare al 100%')
    c.showPage()

def main():
    pref = sys.argv[1] if len(sys.argv) > 1 else 'B_'
    out = sys.argv[2] if len(sys.argv) > 2 else 'Mockup.pdf'
    src = sys.argv[3] if len(sys.argv) > 3 else '.'
    files = sorted(glob.glob(os.path.join(src, pref + '*.xml')))
    if not files:
        print('nessuna schermata con prefisso', pref)
        return
    pw, ph = PANEL_W + 2 * MARGIN, PANEL_H + MARGIN + MARGIN_TOP
    c = canvas.Canvas(out, pagesize=(pw, ph))
    for f in files:
        disegna(c, f, os.path.splitext(os.path.basename(f))[0])
    c.save()
    print('%d schermate -> %s' % (len(files), out))

main()
