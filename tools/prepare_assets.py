"""Bake the approved editable prototype into lossless, embedded pixel assets."""
import copy, struct, sys
from pathlib import Path
from PIL import Image

HERE=Path(__file__).resolve().parent
import ui_scene as layout
import ui_scene as pixel
import ui_scene as ww

ROOT=HERE.parent
assets=ROOT/'assets/images'
scene=layout.Scene(False).build()
layout.validate_layout(scene)
dynamic={r['key'] for r in scene.state_records}
scene.items=[it for it in scene.items if it.get('group') not in dynamic
             and not it['id'].endswith('-value') and it['id']!='volume-readout']
pixel.render(scene,assets/'panel.png',scale=1,canvas=(300,236))

temp=ROOT/'build/tile.png'
temp.parent.mkdir(parents=True,exist_ok=True)
atlas=Image.new('RGB',(120,120))
kinds=['plus','minus','play','pause','minimize','close']
states=['normal','hover','pressed']
for selected in (False,True):
    for row,state in enumerate(states):
        for column,kind in enumerate(kinds):
            tile=pixel.Scene();tile.group('frame',0,0,20,20)
            tile.button('tile',kind,0,0,20,20,active=selected,state=state)
            pixel.render(tile,temp,scale=1,canvas=(20,20))
            with Image.open(temp) as image:atlas.paste(image,(column*20,(row+3*selected)*20))
atlas.save(assets/'buttons.png')

rail=Image.new('RGB',(182,48));thumb=Image.new('RGB',(8,42))
for row,state in enumerate(states):
    tile=pixel.Scene();tile.group('frame',0,0,182,16)
    tile.slider('tile',0,0,182,16,state,'frame')
    all_items=copy.deepcopy(tile.items)
    tile.items=[it for it in all_items if '-thumb-' not in it['id']]
    pixel.render(tile,temp,scale=1,canvas=(182,16))
    with Image.open(temp) as image:rail.paste(image,(0,row*16))
    tile.items=[it for it in all_items if '-thumb-' in it['id']]
    thumb_x=2+round(170*.30)
    for it in tile.items:it['x']-=thumb_x;it['y']-=1
    pixel.render(tile,temp,scale=1,canvas=(8,14))
    with Image.open(temp) as image:thumb.paste(image,(0,row*14))
rail.save(assets/'rail.png');thumb.save(assets/'thumb.png')

with (ROOT/'assets/fonts/westwood.bin').open('wb') as out:
    out.write(b'WWF1');out.write(struct.pack('<I',95))
    for code in range(32,127):
        width,mask=ww.FONT.glyph(chr(code))
        out.write(struct.pack('<II',code,width))
        out.write(bytes(1 if mask.getpixel((x,y)) else 0 for y in range(16) for x in range(width)))
print('PASS: embedded pixel assets and original font glyphs regenerated.')
