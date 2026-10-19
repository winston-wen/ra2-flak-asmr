using System;

namespace FlakASMR {
sealed class Settings {
    public readonly int B, V, IntervalB, IntervalV, Jitter, StdB, StdV, Width, Volume;
    public readonly bool Playing, Volley;
    public Settings(int b, int v, int ib, int iv, int jitter, int width, int volume, bool playing, bool volley) {
        B=b; V=v; IntervalB=ib; IntervalV=iv; Jitter=jitter;
        StdB=LimitStd(ib,(int)Math.Round(ib*jitter/100.0/Math.Sqrt(3)));StdV=LimitStd(iv,(int)Math.Round(iv*jitter/100.0/Math.Sqrt(3)));
        Width=width; Volume=volume; Playing=playing; Volley=volley;
    }
    public Settings(int b,int v,int ib,int iv,int stdB,int stdV,int width,int volume,bool playing,bool volley) {
        B=b;V=v;IntervalB=ib;IntervalV=iv;StdB=LimitStd(ib,stdB);StdV=LimitStd(iv,stdV);Jitter=0;
        Width=width;Volume=volume;Playing=playing;Volley=volley;
    }
    static int LimitStd(int mean,int std) {return Math.Max(0,Math.Min(mean,std));}
}

}
