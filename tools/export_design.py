"""Regenerate the latest editable draw.io and SVG design from tools/ui_scene.py."""
from pathlib import Path
from xml.etree import ElementTree as E
from html import escape
import base64
import ui_scene as base
from ui_scene import Scene, ROOT, FONT, FAMILY
ASSETS=base.ASSETS
FONT_BYTES=(ROOT/'assets/fonts/WestwoodImprovedFont/WestwoodImprovedPreview.ttf').read_bytes()
FONT_URL='data:font/ttf;base64,'+base64.b64encode(FONT_BYTES).decode()
ATTRIBUTION=(ROOT/'assets/fonts/FONT-LICENSE.txt').read_text(encoding='utf-8')

def style_parts(it):
    return 'rounded=0;shadow=0;fillColor=' + str(it.get('fill') or 'none') + ';strokeColor=' + str(it.get('stroke') or 'none') + ';strokeWidth=' + str(it.get('sw', 1)) + ';'

def graph_page(mxfile, scene, index):
    diagram = E.SubElement(mxfile, 'diagram', name='01 暂停' if not scene.playing else '02 播放', id='soviet-' + str(index))
    model = E.SubElement(diagram, 'mxGraphModel', dx='900', dy='600', grid='1', gridSize='1', guides='1', tooltips='1', connect='0', arrows='0', fold='1', page='1', pageScale='1', pageWidth='420', pageHeight='320', math='0', shadow='0', background='#F3F2ED')
    root = E.SubElement(model, 'root')
    E.SubElement(root, 'mxCell', id='0')
    E.SubElement(root, 'mxCell', id='1', parent='0')
    for ident, (x, y, w, h, parent, label) in scene.groups.items():
        cell = E.SubElement(root, 'mxCell', id=ident, value='', tooltip=label, style='group;connectable=0;recursiveResize=1;', vertex='1', parent=parent or '1')
        px, py = scene.groups[parent][:2] if parent else (-60, -60)
        E.SubElement(cell, 'mxGeometry', x=str(x - px), y=str(y - py), width=str(w), height=str(h), **{'as': 'geometry'})
    for it in scene.items:
        parent = it.get('group') or 'frame'
        px, py = scene.groups[parent][:2]
        x, y, w, h = [it[a] for a in ('x', 'y', 'w', 'h')]
        k = it['kind']
        style = style_parts(it)
        value = ''
        if k == 'text':
            value = it['text']
            style = 'text;html=0;whiteSpace=nowrap;overflow=visible;strokeColor=none;fillColor=none;spacing=0;verticalAlign=middle;fontColor=' + it['color'] + ';fontFamily=' + it['family'] + ';fontSize=' + str(it['size']) + ';align=' + it['align'] + ';'
        elif k == 'image':
            data = base64.b64encode((ASSETS / it['file']).read_bytes()).decode()
            style = 'shape=image;aspect=fixed;imageAspect=0;strokeColor=none;fillColor=none;image=data:image/png,' + data + ';'
        elif k == 'ellipse':
            style += 'shape=ellipse;'
        elif k == 'triangle':
            style += 'shape=triangle;direction=' + it.get('direction', 'east') + ';'
        elif k == 'parallelogram':
            style += 'shape=parallelogram;size=0.2;fixedSize=0;'
        if k == 'line':
            style += 'endArrow=none;startArrow=none;edgeStyle=none;rounded=0;'
            cell = E.SubElement(root, 'mxCell', id=it['id'], value='', parent=parent, style=style, edge='1')
            geo = E.SubElement(cell, 'mxGeometry', relative='1', **{'as': 'geometry'})
            E.SubElement(geo, 'mxPoint', x=str(x - px), y=str(y - py), **{'as': 'sourcePoint'})
            E.SubElement(geo, 'mxPoint', x=str(x + w - px), y=str(y + h - py), **{'as': 'targetPoint'})
        else:
            cell = E.SubElement(root, 'mxCell', id=it['id'], value=value, parent=parent, style=style, vertex='1')
            E.SubElement(cell, 'mxGeometry', x=str(x - px), y=str(y - py), width=str(w), height=str(h), **{'as': 'geometry'})

def font_graph_page(document, scene, index):
    graph_page(document, scene, index)
    model = document.findall('diagram')[-1].find('mxGraphModel')
    model.set('extFonts', FAMILY + '^' + FONT_URL)
    model.set('fontAttribution', ATTRIBUTION)
    cells = {cell.attrib['id']: cell for cell in model.find('root').findall('mxCell')}
    for item in scene.items:
        if item['kind'] == 'gradient':
            cell = cells[item['id']]
            cell.set('style', style_parts(item) + 'gradientColor=' + item['end'] + ';gradientDirection=south;')
    return model
def make_svg(scene, path, document):
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="600" height="400" viewBox="0 0 300 200" content="{escape(document, quote=True)}">', '<title>RA2 Flak ASMR — Westwood Improved Font 原型</title>', '<desc>原始点阵字形；字体和单位图像已内嵌；间隔 500 ms，标准差 50 ms。</desc>', '<metadata>' + escape(ATTRIBUTION) + '</metadata>', '<defs><style>@font-face{font-family:"' + FAMILY + '";src:url("' + FONT_URL + '") format("truetype");} text{font-kerning:none;font-synthesis:none;}</style>']
    for it in scene.items:
        if it['kind'] == 'gradient':
            parts.append(f'''<linearGradient id="grad-{it['id']}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{it['fill']}"/><stop offset="1" stop-color="{it['end']}"/></linearGradient>''')
    parts.append('</defs>')
    for it in scene.items:
        k = it['kind']
        x, y, w, h = [it[a] for a in ('x', 'y', 'w', 'h')]
        ident = escape(it['id'])
        fill = it.get('fill') or 'none'
        stroke = it.get('stroke') or 'none'
        sw = it.get('sw', 1)
        attrs = f'id="{ident}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"'
        if k == 'rect':
            parts.append(f'<rect {attrs} x="{x}" y="{y}" width="{w}" height="{h}"/>')
        elif k == 'gradient':
            parts.append(f'<rect id="{ident}" x="{x}" y="{y}" width="{w}" height="{h}" fill="url(#grad-{ident})"/>')
        elif k == 'ellipse':
            parts.append(f'<ellipse {attrs} cx="{x + w / 2}" cy="{y + h / 2}" rx="{w / 2}" ry="{h / 2}"/>')
        elif k == 'line':
            parts.append(f'<line {attrs} x1="{x}" y1="{y}" x2="{x + w}" y2="{y + h}"/>')
        elif k == 'triangle':
            parts.append(f'<polygon {attrs} points="' + ' '.join((f'{a},{b}' for a, b in base.triangle_points(it))) + '"/>')
        elif k == 'image':
            data = base64.b64encode((base.ASSETS / it['file']).read_bytes()).decode()
            parts.append(f'<image id="{ident}" x="{x}" y="{y}" width="{w}" height="{h}" href="data:image/png;base64,{data}" style="image-rendering:pixelated"/>')
        elif k == 'text':
            if it['family'] == FAMILY:
                tw = FONT.width(it['text'])
                tx = round(x) if it['align'] == 'left' else round(x + (w - tw) / 2) if it['align'] == 'center' else round(x + w - tw)
                baseline = round(y + (h - 16) / 2) + 13
                for offset, color, suffix in [(1, '#000000', '-shadow'), (0, it['color'], '')]:
                    parts.append(f'''<text id="{ident + suffix}" x="{tx + offset}" y="{baseline + offset}" fill="{color}" font-family="{FAMILY}" font-size="16">{escape(it['text'])}</text>''')
            else:
                parts.append(f'''<text id="{ident}" x="{x + w / 2}" y="{y + h / 2}" fill="{it['color']}" font-family="Segoe UI Symbol" font-size="{it['size']}" text-anchor="middle" dominant-baseline="central">{escape(it['text'])}</text>''')
    parts.append('</svg>')
    path.write_text('\n'.join(parts), encoding='utf-8')

def write_svg(scene, path, xml):
    make_svg(scene, path, xml)
    ns = '{http://www.w3.org/2000/svg}'
    E.register_namespace('', ns[1:-1])
    tree = E.parse(path)
    root = tree.getroot()
    height = int(scene.groups['frame'][3])
    root.set('height', str(height * 2))
    root.set('shape-rendering', 'crispEdges')
    root.set('viewBox', f'0 0 300 {height}')
    for it in scene.items:
        if not it.get('ink_centered'):
            continue
        box = FONT.mask(it['text']).getbbox()
        ty = round(it['y'] + (it['h'] - (box[3] - box[1])) / 2 - box[1])
        for offset, suffix in [(0, ''), (1, '-shadow')]:
            text = root.find(ns + 'text[@id="' + it['id'] + suffix + '"]')
            text.set('y', str(ty + 13 + offset))
    tree.write(path, encoding='unicode')

def main():
    scenes=[Scene(False).build(),Scene(True).build(),base.state_sheet()]
    for scene in scenes[:2]:base.validate_layout(scene)
    doc=E.Element('mxfile',host='app.diagrams.net',type='device',compressed='false',agent='RA2 Flak ASMR final design')
    for index,scene in enumerate(scenes,1):
        font_graph_page(doc,scene,index)
        doc.findall('diagram')[-1].set('name',['01 Paused','02 Playing','03 Interaction states'][index-1])
    E.indent(doc)
    xml=E.tostring(doc,encoding='unicode')
    (ROOT/'design/RA2FlakASMR.drawio').write_text(xml,encoding='utf-8')
    write_svg(scenes[0],ROOT/'design/RA2FlakASMR.drawio.svg',xml)
    for scene,name in zip(scenes,['preview','playing','states']):
        base.render(scene,ROOT/f'docs/images/{name}.png',canvas=(300,int(scene.groups['frame'][3])))
    print('PASS: final editable design and pixel previews regenerated.')
if __name__=='__main__':main()
