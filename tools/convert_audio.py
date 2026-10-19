"""Recreate the embedded 48 kHz PCM clips from the included OGG originals."""
from pathlib import Path
import argparse, json, wave
import numpy as np
import soundfile as sf

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source',type=Path,default=ROOT/'assets/audio/original')
parser.add_argument('--output',type=Path,default=ROOT/'assets/audio')
args=parser.parse_args()
args.output.mkdir(parents=True,exist_ok=True)
report=[]
prepared=[]
for path in sorted(args.source.glob('*.ogg')):
    data,rate=sf.read(path,dtype='float32',always_2d=True)
    mono=data.mean(axis=1)
    if rate!=48000:
        mono=np.interp(np.arange(round(len(mono)*48000/rate))*rate/48000,np.arange(len(mono)),mono)
    mono-=mono.mean()
    attack=min(48,len(mono)//4);release=min(384,len(mono)//4)
    mono[:attack]*=(1-np.cos(np.linspace(0,np.pi,attack)))/2
    mono[-release:]*=(1+np.cos(np.linspace(0,np.pi,release)))/2
    peak=float(np.max(np.abs(mono)))
    prepared.append((path,mono,peak))
# One common headroom gain preserves the original relative levels of all variants.
common_gain=min(1.0,0.98/max(peak for _,_,peak in prepared))
for path,mono,peak in prepared:
    mono*=common_gain
    pcm=np.round(np.clip(mono,-1,1)*32767).astype('<i2')
    with wave.open(str(args.output/(path.stem+'.wav')),'wb') as out:
        out.setnchannels(1);out.setsampwidth(2);out.setframerate(48000);out.writeframes(pcm.tobytes())
    report.append(dict(file=path.name,samples=len(pcm),duration=len(pcm)/48000,boundary=[int(pcm[0]),int(pcm[-1])],source_peak=peak,common_gain=common_gain,output_peak=float(np.max(np.abs(pcm.astype(float))))/32767))
if len(report)!=8:raise SystemExit(f'Expected 8 OGG files; converted {len(report)}.')
(args.output/'conversion.json').write_text(json.dumps(dict(common_gain=common_gain,clips=report),indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,indent=2))

from convert_credit_audio import convert
convert(ROOT)
