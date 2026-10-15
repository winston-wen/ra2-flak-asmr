"""Final approved RA2 UI: integer pixel geometry and cube-root colour fields."""
from pathlib import Path
import copy, json, math
from PIL import Image, ImageDraw, ImageFont
from pixel_font import WestwoodFont, FAMILY
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'design'
ASSETS=ROOT/'assets/images'
FONT=WestwoodFont()
C={'label':'#FFFF00'}
def blend(a, b, t):
    return '#' + ''.join(('%02X' % round(int(a[i:i + 2], 16) * (1 - t) + int(b[i:i + 2], 16) * t) for i in (1, 3, 5)))
def rgb(c):
    return tuple((int(c[i:i + 2], 16) for i in (1, 3, 5)))
class Scene:
    def __init__(self, playing=False, states=None):
        self.playing=playing
        self.states=states or {}
        self.items=[]
        self.groups={}
        self.label_layouts=[]
        self.state_records=[]

    def group(self, ident, x, y, w, h, parent=None, label=None):
        self.groups[ident] = (x, y, w, h, parent, label or ident)

    def add(self, kind, ident, x, y, w, h, **kwargs):
        self.items.append(dict(kind=kind, id=ident, x=x, y=y, w=w, h=h, **kwargs))

    def rect(self, ident, x, y, w, h, fill, stroke=None, sw=1, group=None):
        self.add('rect', ident, x, y, w, h, fill=fill, stroke=stroke, sw=sw, group=group)

    def line(self, ident, x1, y1, x2, y2, color, sw=1, group=None):
        self.add('line', ident, x1, y1, x2 - x1, y2 - y1, stroke=color, sw=sw, group=group)

    def text(self, ident, text, x, y, w, h, size=9, color=None, family='Arial', align='left', group=None):
        self.add('text', ident, x, y, w, h, text=text, size=size, color=color or C['label'], family=family, align=align, group=group)

    def image(self, ident, name, x, y, w=60, h=48, group=None):
        self.add('image', ident, x, y, w, h, file=name, group=group)

    def triangle(self, ident, x, y, w, h, color, direction='east', group=None):
        self.add('triangle', ident, x, y, w, h, fill=color, direction=direction, group=group)

    def justified_label(self, key, text, x, y, width=44, height=20, parent='frame'):
        self.group(key, x, y, width, height, parent, text + ' · 两端对齐')
        natural = FONT.mask(text).getbbox()[2]
        assert natural <= width
        extra = width - natural
        advance = 0
        positions = []
        for i, char in enumerate(text):
            px = x + advance + round(extra * i / (len(text) - 1))
            self.text(key + '-char-' + str(i), char, px, y, FONT.glyph(char)[0] + 1, height, 16, '#FFFF00', FAMILY, 'left', key)
            positions.append(px)
            advance += FONT.glyph(char)[0] + 1
        self.label_layouts.append(dict(key=key, text=text, x=x, right=x + width, positions=positions))

    def pixel_runs(self, key, x, y, rows, parent):
        """All visible colour areas are editable, integer-grid rectangles."""
        for py, row in enumerate(rows):
            start = 0
            while start < len(row):
                color = row[start]
                end = start + 1
                while end < len(row) and row[end] == color:
                    end += 1
                if color:
                    self.rect(f'{key}-{py}-{start}', x + start, y + py, end - start, 1, color, None, group=parent)
                start = end

    def face(self, key, x, y, w, h, state, active=False, parent=None):
        a, b = ('#130000', '#F01B04')
        rows = []
        for py in range(h):
            row = []
            for px in range(w):
                inward = max(0, 1 - max(abs(2 * px / (w - 1) - 1) if w > 1 else 0, abs(2 * py / (h - 1) - 1) if h > 1 else 0))
                strength = round(inward ** (1 / 3) * 23) / 23
                color = blend(a, b, strength)
                color = blend('#000000', color, {'normal': 0.6, 'hover': 0.3, 'pressed': 0.25}[state])
                row.append(color)
            rows.append(row)
        self.pixel_runs(key, x, y, rows, parent)

    def edge(self, key, x, y, w, h, pressed, parent):
        light, dark = ('#161919', '#B1B5AE') if pressed else ('#B1B5AE', '#161919')
        for part, rx, ry, rw, rh, color in [('top', x, y, w, 1, light), ('left', x, y, 1, h, light), ('bottom', x, y + h - 1, w, 1, dark), ('right', x + w - 1, y, 1, h, dark)]:
            self.rect(key + '-' + part, rx, ry, rw, rh, color, None, group=parent)
        if pressed:
            self.rect(key + '-inset-top', x + 1, y + 1, w - 2, 1, '#030404', None, group=parent)
            self.rect(key + '-inset-left', x + 1, y + 1, 1, h - 2, '#030404', None, group=parent)
        else:
            self.rect(key + '-inner-bottom', x + 1, y + h - 2, w - 2, 1, '#393D3D', None, group=parent)
            self.rect(key + '-inner-right', x + w - 2, y + 1, 1, h - 2, '#393D3D', None, group=parent)

    def button(self, key, kind, x, y, w, h, active=False, parent='frame', state=None):
        state = state or self.states.get(key, 'pressed' if active else 'normal')
        self.group(key, x, y, w, h, parent, kind + ' / ' + state)
        self.rect(key + '-metal', x, y, w, h, '#666B68', None, group=key)
        self.face(key + '-face', x + 2, y + 2, w - 4, h - 4, state, active, key)
        self.edge(key + '-edge', x, y, w, h, state == 'pressed', key)
        mask = Image.new('1', (9, 9))
        d = ImageDraw.Draw(mask)
        thick = 1
        if kind in ('plus', 'minus', 'minimize'):
            d.rectangle((0, 4 - thick // 2, 8, 4 + thick // 2), fill=1)
            if kind == 'plus':
                d.rectangle((4 - thick // 2, 0, 4 + thick // 2, 8), fill=1)
        elif kind == 'play':
            d.polygon([(1, 0), (1, 8), (7, 4)], fill=1)
            if thick == 3:
                d.line((0, 0, 0, 8), fill=1, width=1)
                d.line((2, 0, 8, 4, 2, 8), fill=1, width=1)
        elif kind == 'pause':
            d.rectangle((1, 0, 1 + (2 if thick == 3 else 1), 8), fill=1)
            d.rectangle((6, 0, 6 + (2 if thick == 3 else 1), 8), fill=1)
        elif kind == 'close':
            d.line((0, 0, 8, 8), fill=1, width=thick)
            d.line((0, 8, 8, 0), fill=1, width=thick)
        else:
            raise ValueError(kind)
        gx = x + (w - 9) // 2 + (state == 'pressed')
        gy = y + (h - 9) // 2 + (state == 'pressed')
        fg = '#CC3311' if active else '#FFE073'
        rows = [[fg if mask.getpixel((px, py)) else None for px in range(9)] for py in range(9)]
        self.pixel_runs(key + '-symbol', gx, gy, rows, key)
        self.state_records.append(dict(key=key, kind=kind, state=state, ink=sum((bool(mask.getpixel((px, py))) for py in range(9) for px in range(9))), pressed=state == 'pressed'))

    def slider(self, key, x, y, w, h, state, parent):
        self.group(key, x, y, w, h, parent, 'Volume / ' + state)
        self.rect(key + '-track', x, y, w, h, '#070000', None, group=key)
        self.edge(key + '-edge', x, y, w, h, state == 'pressed', key)
        self.rect(key + '-rail-top', x + 1, y + 1, w - 2, 1, '#851400', None, group=key)
        if state == 'pressed':
            self.rect(key + '-pressed-rail-top', x + 1, y + 1, w - 2, 1, '#030000', None, group=key)
            self.rect(key + '-rail-left', x + 1, y + 1, 1, h - 2, '#030000', None, group=key)
        tx = x + 2 + round((w - 12) * 0.3)
        self.rect(key + '-thumb-base', tx, y + 1, 8, h - 2, '#710B00', None, group=key)
        self.face(key + '-thumb-face', tx + 1, y + 2, 6, h - 4, 'normal', False, key)
        self.edge(key + '-thumb-edge', tx, y + 1, 8, h - 2, state == 'pressed', key)
        self.state_records.append(dict(key=key, kind='volume', state=state, pressed=state == 'pressed'))

    def field(self, side, name, x, y, value):
        key = side + '-' + name
        self.group(key, x, y, 80, 20, 'frame', '左侧参数' if side == 'v' else '右侧参数')
        self.rect(key + '-well', x, y, 32, 20, '#090000', '#E12B0A', 1, key)
        self.text(key + '-value', str(value), x + 3, y, 26, 20, 16, '#FFFF00', FAMILY, 'right', key)
        self.button(key + '-minus', 'minus', x + 36, y, 20, 20, parent=key)
        self.button(key + '-plus', 'plus', x + 60, y, 20, 20, parent=key)

    def volume(self):
        self.group('volume', 10, 172, 280, 16, 'frame', 'Volume · 三部分底边对齐')
        self.justified_label('volume-label', 'Volume', 10, 172, 44, 16, parent='volume')
        self.slider('volume-control', 60, 172, 182, 16, self.states.get('volume-control', 'normal'), 'volume')
        self.rect('volume-value-frame', 246, 172, 44, 16, '#100000', '#E12B0A', 1, 'volume')
        self.text('volume-readout', '30%', 247, 172, 42, 16, 16, '#FFFF00', FAMILY, 'center', 'volume')

    def _build(self):
        self.group('frame', 0, 0, 300, 208, label='RA2 Flak ASMR · 300 × 208')
        self.rect('frame-base', 0, 0, 300, 208, '#626767', '#080909', 1, 'frame')
        self.line('frame-highlight', 1, 1, 298, 1, '#D2D4D3', 1, 'frame')
        self.line('frame-left-highlight', 1, 1, 1, 206, '#A8ACAC', 1, 'frame')
        self.line('frame-right-shadow', 298, 1, 298, 206, '#141717', 1.5, 'frame')
        self.line('frame-bottom-shadow', 1, 206, 298, 206, '#141717', 1.5, 'frame')
        self.rect('body', 4, 32, 292, 168, '#090606', '#040404', 1, 'frame')
        self.group('caption', 4, 4, 292, 26, 'frame', '标题栏 · 四个 20 × 20 按钮')
        self.rect('caption-plate', 4, 4, 292, 26, '#300000', '#040404', 1, 'caption')
        self.line('caption-red-edge', 5, 5, 294, 5, '#8B1609', 1, 'caption')
        self.text('soviet-star', '★', 7, 4, 14, 26, 13, '#FFFF00', 'Segoe UI Symbol', 'center', 'caption')
        self.text('title', 'RA2 Flak ASMR', 25, 4, 170, 26, 16, '#FFE073', FAMILY, 'left', 'caption')
        self.items[-1]['ink_centered'] = True
        for kind, x in [('play', 208), ('pause', 230), ('minimize', 252), ('close', 274)]:
            active = kind == 'play' and self.playing or (kind == 'pause' and (not self.playing))
            self.button('transport-' + kind, kind, x, 7, 20, 20, active, 'caption')
        for name, x in [('trooper.png', 10), ('track.png', 80), ('scorpion.png', 160), ('cannon.png', 230)]:
            key = 'cameo-' + name[:-4]
            self.group(key, x - 2, 37, 64, 52, 'frame')
            self.rect(key + '-border', x - 2, 37, 64, 52, '#040404', '#7E8586', 1, key)
            self.image(key + '-image', name, x, 39, group=key)
        for side, label_x, field_x, values in [('v', 10, 60, [2, 500, 50]), ('b', 160, 210, [3, 500, 50])]:
            for row, (name, label, value) in enumerate(zip(['num', 'itv', 'std'], ['Number', 'itv (ms)', 'std (ms)'], values)):
                y = 96 + row * 28
                self.justified_label(('left' if side == 'v' else 'right') + '-label-' + name, label, label_x, y)
                self.field(side, name, field_x, y, value)
        start = len(self.items)
        groups_before = set(self.groups)
        labels_before = len(self.label_layouts)
        self.volume()
        for it in self.items[start:]:
            it['y'] += 8
        for key in set(self.groups) - groups_before:
            x, y, w, h, parent, label = self.groups[key]
            self.groups[key] = (x, y + 8, w, h, parent, label)
        self.rect('footer-metal', 5, 201, 290, 4, '#353A3C', None, group='frame')
        self.line('footer-shine', 5, 201, 295, 201, '#959A99', 1, 'frame')
        return self

    def build(self):
        self._build()
        for kind, dx in [('play', -6), ('pause', -4), ('minimize', -2), ('close', 0)]:
            key = 'transport-' + kind
            x, y, w, h, parent, label = self.groups[key]
            self.groups[key] = (x + dx, y, w, h, parent, label)
            for it in self.items:
                if it.get('group') == key:
                    it['x'] += dx
        self.items = [it for it in self.items if not (it['id'].startswith('cameo-') and it['id'].endswith('-border'))]
        for it in list(self.items):
            if it['kind'] != 'image':
                continue
            x = it['x']
            key = it['group']
            it['y'] = 36
            self.groups[key] = (x, 36, 60, 48, 'frame', key)
            for name, rx, ry, rw, rh, color in [('top', x, 36, 60, 1, '#7E8586'), ('left', x, 36, 1, 48, '#7E8586'), ('bottom', x, 83, 60, 1, '#343939'), ('right', x + 59, 36, 1, 48, '#343939')]:
                self.rect(key + '-rim-' + name, rx, ry, rw, rh, color, None, group=key)
        return self

def triangle_points(it):
    x, y, w, h = [it[k] for k in ('x', 'y', 'w', 'h')]
    return [(x + w, y), (x + w, y + h), (x, y + h / 2)] if it.get('direction') == 'west' else [(x, y), (x, y + h), (x + w, y + h / 2)]

def render(scene, path, scale=2, canvas=(300, 200)):
    im = Image.new('RGB', canvas, '#090606')
    d = ImageDraw.Draw(im)
    for it in scene.items:
        k = it['kind']
        x, y, w, h = [it[a] for a in ('x', 'y', 'w', 'h')]
        x0, y0, x1, y1 = [round(v) for v in (x, y, x + w, y + h)]
        fill = it.get('fill')
        stroke = it.get('stroke')
        if k in ('rect', 'ellipse'):
            getattr(d, 'rectangle' if k == 'rect' else 'ellipse')((x0, y0, x1 - 1, y1 - 1), fill=fill, outline=stroke, width=max(1, round(it.get('sw', 1))))
        elif k == 'line':
            d.line((x0, y0, x1, y1), fill=stroke, width=max(1, round(it.get('sw', 1))))
        elif k == 'triangle':
            d.polygon(triangle_points(it), fill=fill)
        elif k == 'image':
            tile = Image.open(ASSETS / it['file']).convert('RGBA').resize((round(w), round(h)), Image.Resampling.NEAREST)
            im.paste(tile, (x0, y0), tile)
        elif k == 'text' and it['family'] == FAMILY:
            mask = FONT.mask(it['text'])
            tx = round(x) if it['align'] == 'left' else round(x + (w - mask.width) / 2) if it['align'] == 'center' else round(x + w - mask.width)
            ty = round(y + (h - 16) / 2)
            if it.get('ink_centered'):
                bounds = mask.getbbox()
                ty = round(y + (h - (bounds[3] - bounds[1])) / 2 - bounds[1])
            im.paste('#000000', (tx + 1, ty + 1, tx + 1 + mask.width, ty + 1 + mask.height), mask)
            im.paste(it['color'], (tx, ty, tx + mask.width, ty + mask.height), mask)
        elif k == 'text':
            font = ImageFont.truetype(str(Path(__import__('os').environ.get('WINDIR', 'C:/Windows'))/'Fonts/seguisym.ttf'), round(it['size']))
            bounds = d.textbbox((0, 0), it['text'], font=font)
            d.text((round(x + (w - bounds[2] + bounds[0]) / 2), round(y + (h - bounds[3] + bounds[1]) / 2 - bounds[1])), it['text'], font=font, fill=it['color'])
    im.resize((canvas[0] * scale, canvas[1] * scale), Image.Resampling.NEAREST).save(path)

def state_sheet():
    s = Scene()
    s.group('frame', 0, 0, 300, 200, label='Interaction states: pixel art at 3x')
    s.rect('background', 0, 0, 300, 200, '#090606', None, group='frame')
    for index, (state, label) in enumerate([('normal', 'Normal'), ('hover', 'Hover'), ('pressed', 'Pressed')]):
        bx = 20 + index * 100
        s.text(state + '-label', label, bx - 15, 8, 90, 20, 16, '#FFFF00', FAMILY, 'center', 'frame')
        tmp = Scene()
        tmp.group('frame', 0, 0, 20, 20)
        tmp.button(state + '-plus', 'plus', 0, 0, 20, 20, parent='frame', state=state)
        s.group(state + '-example', bx, 37, 60, 60, 'frame', label + ' / 3x pixels')
        for it in tmp.items:
            c = copy.deepcopy(it)
            c['x'] = bx + it['x'] * 3
            c['y'] = 37 + it['y'] * 3
            c['w'] *= 3
            c['h'] *= 3
            c['group'] = state + '-example'
            s.items.append(c)
        s.state_records.extend(tmp.state_records)
        s.text(state + '-scale', '3x pixels', bx - 15, 102, 90, 20, 16, '#A8ABA5', FAMILY, 'center', 'frame')
        s.slider(state + '-volume', bx - 15, 157, 90, 16, state, 'frame')
    s.text('volume-title', 'Volume', 0, 130, 300, 20, 16, '#FFFF00', FAMILY, 'center', 'frame')
    return s

def glyph_bounds(it):
    box = FONT.mask(it['text']).getbbox()
    x = round(it['x']) if it['align'] == 'left' else round(it['x'] + (it['w'] - FONT.mask(it['text']).width) / 2) if it['align'] == 'center' else round(it['x'] + it['w'] - FONT.mask(it['text']).width)
    y = round(it['y'] + (it['h'] - 16) / 2)
    return (x + box[0], y + box[1], x + box[2], y + box[3])

def validate_layout(s):
    byid = {it['id']: it for it in s.items}
    for side in ('v', 'b'):
        for name in ('num', 'itv', 'std'):
            key = side + '-' + name
            well = byid[key + '-well']
            minus = s.groups[key + '-minus']
            plus = s.groups[key + '-plus']
            assert minus[0] - (well['x'] + well['w']) == 4
            assert plus[0] - (minus[0] + minus[2]) == 4
            assert minus[2:4] == plus[2:4] == (20, 20)
    right_edges = [s.groups['b-' + name + '-plus'][0] + 20 for name in ('num', 'itv', 'std')]
    right_edges.extend([byid['volume-value-frame']['x'] + byid['volume-value-frame']['w'], byid['cameo-cannon-image']['x'] + byid['cameo-cannon-image']['w']])
    assert set(right_edges) == {290}
    left_edges = [byid['cameo-trooper-image']['x']]
    for prefix in ('left-label-num', 'left-label-itv', 'left-label-std', 'volume-label'):
        letters = [it for it in s.items if it['id'].startswith(prefix + '-char-') and it['text'] != ' ']
        left_edges.append(min((glyph_bounds(it)[0] for it in letters)))
    assert set(left_edges) == {10} and 10 == 300 - 290
    track = s.groups['volume-control']
    label = s.groups['volume-label']
    value = byid['volume-value-frame']
    assert value['x'] == s.groups['b-num-minus'][0] == s.groups['b-itv-minus'][0] == s.groups['b-std-minus'][0] == 246
    assert value['x'] - (track[0] + track[2]) == 4
    assert label[1] + label[3] == track[1] + track[3] == value['y'] + value['h'] == 196
    label_ink_bottom = max((glyph_bounds(it)[3] for it in s.items if it['id'].startswith('volume-label-char-')))
    assert label_ink_bottom == glyph_bounds(byid['volume-readout'])[3] == 193
    body = byid['body']
    top_gap = byid['cameo-trooper-image']['y'] - body['y']
    bottom_gap = body['y'] + body['h'] - (track[1] + track[3])
    assert top_gap == bottom_gap == 4
    assert {it['y'] for it in s.items if it['kind'] == 'image'} == {36}
    assert top_gap - 1 == bottom_gap - 1 == 3
    for kind in ('play', 'pause', 'minimize', 'close'):
        assert s.groups['transport-' + kind][2:4] == (20, 20)
    buttons = [s.groups['transport-' + kind] for kind in ('play', 'pause', 'minimize', 'close')]
    assert all((b[0] - (a[0] + a[2]) == 4 for a, b in zip(buttons, buttons[1:])))
    assert buttons[-1][0] + buttons[-1][2] == 294
    return dict(side_margin=10, stepper_gap=4, volume_bottom=196, volume_glyph_bottom=193, icon_top=36, body_top=32, body_bottom=200, vertical_padding=4)
