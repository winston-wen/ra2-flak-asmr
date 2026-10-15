using System;
using System.Diagnostics;

namespace FlakASMR {
static class MixerRegression {
    static Settings Phase(int phase) {
        switch(phase) {
            case 0:return new Settings(4,10,500,500,50,50,70,30,true,false);
            case 1:return new Settings(4,10,500,500,50,50,70,30,false,false);
            case 2:return new Settings(5,14,180,200,20,35,100,70,true,false);
            case 3:return new Settings(2,3,800,700,0,0,0,10,true,false);
            case 4:return new Settings(0,0,800,800,0,0,0,10,true,false);
            case 5:return new Settings(32,32,50,50,50,50,70,100,true,false);
            case 6:return new Settings(32,32,50,50,0,0,100,100,true,true);
            default:return new Settings(4,10,500,500,50,50,70,30,true,false);
        }
    }
    static int Main() {
        try {
            long compared=0;int maxDifference=0;
            foreach(int frames in new[]{127,480,1440,4096}) {
                Mixer current=new Mixer();ReferenceMixer reference=new ReferenceMixer();
                current.Reserve(frames);
                short[] actual=new short[frames*2],expected=new short[frames*2];
                long clock=0;int previous=-1;
                while(clock<Mixer.Rate*12) {
                    int phase=(int)(clock/(Mixer.Rate*1.5));
                    if(phase!=previous) {current.Config=reference.Config=Phase(phase);previous=phase;}
                    current.Render(actual,frames);reference.Render(expected,frames);
                    for(int i=0;i<frames*2;i++) {
                        int difference=Math.Abs((int)actual[i]-expected[i]);
                        maxDifference=Math.Max(maxDifference,difference);
                        if(difference>1)throw new Exception("PCM changed by "+difference+" LSB at frames="+frames+", sample="+(clock*2+i));
                    }
                    if(current.Shots!=reference.Shots)throw new Exception("Shot scheduling changed");
                    compared+=frames*2;clock+=frames;
                }
            }
            // Zero-width blocks must not advance timing or touch output.
            Mixer empty=new Mixer();short[] sentinel={123,-456};empty.Render(sentinel,0);
            if(sentinel[0]!=123 || sentinel[1]!=-456)throw new Exception("Zero frame block changed output");
            Console.WriteLine("PASS: "+compared+" PCM samples versus frozen mixer; max delta="+maxDifference+" LSB; four block sizes; play/pause, counts, width, volume, distributions, fixed timing and volley.");
            return 0;
        }catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
}
