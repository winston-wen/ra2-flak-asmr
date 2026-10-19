"""Resample the original CreditUp PCM without changing its original gain."""
from pathlib import Path
import math, struct, wave

def convert(root):
    with wave.open(str(root/'assets/audio/original/ucredup.wav'),'rb') as source:
        assert source.getnchannels()==1 and source.getsampwidth()==2
        rate=source.getframerate()
        samples=struct.unpack('<'+'h'*source.getnframes(),source.readframes(source.getnframes()))
    count=round(len(samples)*48000/rate)
    output=[]
    for index in range(count):
        position=index*rate/48000
        first=min(int(position),len(samples)-1)
        second=min(first+1,len(samples)-1)
        value=samples[first]+(samples[second]-samples[first])*(position-first)
        edge=min(index,count-1-index,48)/48
        output.append(round(value*(1-math.cos(math.pi*edge))/2))
    with wave.open(str(root/'assets/audio/ucredup.wav'),'wb') as target:
        target.setparams((1,2,48000,0,'NONE','not compressed'))
        target.writeframes(struct.pack('<'+'h'*count,*output))
    print(f'PASS: CreditUp {len(samples)} samples/{rate} Hz -> {count} samples/48000 Hz; original gain, 1 ms boundary ramps.')

if __name__=='__main__':convert(Path(__file__).resolve().parents[1])
