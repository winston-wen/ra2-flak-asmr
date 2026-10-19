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
            const int length=Mixer.Rate*12;short[] reference=null;long compared=0;int maxDifference=0;long expectedShots=0,expectedStarts=0;
            foreach(int frames in new[]{127,480,1440,4096}) {
                Mixer current=new Mixer(72491);current.Reserve(frames);
                short[] actual=new short[frames*2],all=new short[length*2];int clock=0,previous=-1;
                while(clock<length) {
                    int phase=clock/72000;
                    if(phase!=previous) {current.Config=Phase(phase);previous=phase;}
                    int count=Math.Min(frames,Math.Min(length-clock,(phase+1)*72000-clock));
                    current.Render(actual,count);Array.Copy(actual,0,all,clock*2,count*2);clock+=count;
                    if(current.ActiveVoices>10)throw new Exception("Attack event limits exceeded");
                }
                if(reference==null) {reference=all;expectedShots=current.Shots;expectedStarts=current.Attacks.Started;}
                else {
                    for(int i=0;i<all.Length;i++) {
                        int difference=Math.Abs((int)all[i]-reference[i]);maxDifference=Math.Max(maxDifference,difference);
                        if(difference>1)throw new Exception("PCM callback-size dependence: "+difference+" LSB at block="+frames+", sample="+i);
                    }
                    if(current.Shots!=expectedShots || current.Attacks.Started!=expectedStarts)throw new Exception("Callback size changed attack timing or batch admission");
                }
                compared+=all.Length;
            }
            // Zero-width blocks must not advance timing or touch output.
            Mixer empty=new Mixer();short[] sentinel={123,-456};empty.Render(sentinel,0);
            if(sentinel[0]!=123 || sentinel[1]!=-456)throw new Exception("Zero frame block changed output");
            StartTimingTests.Run();AttackBatchTests.Run();AttackVariationTests.Run();OilIncomeTests.Run();
            Console.WriteLine("PASS: "+compared+" PCM samples across callback sizes with 1.2.1 batching; max delta="+maxDifference+" LSB; four block sizes; play/pause, counts, width, volume, distributions, fixed timing and volley.");
            return 0;
        }catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
}
