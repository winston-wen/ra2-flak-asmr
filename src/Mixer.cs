using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace FlakASMR {
sealed class Mixer {
    public const int Rate=48000, MaxUnits=32;
    public volatile Settings Config=new Settings(3,2,500,500,50,50,70,30,false,false);
    readonly float[][][] clips=new float[2][][];
    readonly Random random;
    readonly AttackUnit[] units=new AttackUnit[64];
    internal readonly AttackSoundBus Attacks;
    long clock; bool wasPlaying, wasVolley;
    bool hasNormal;double spareNormal;
    double master, normalization=1, width=.7;
    double[] left,right,oilBus;
    internal readonly OilIncome Oil=new OilIncome();
    double[][] envelope;
    int capacity;
    const double UnitStep=1.0/720, MasterStep=1.0/576, NormStep=1.0/2400;
    const double Settle=1e-12, TailCutoff=1e-5;
    public void Reserve(int frames) {
        if(frames<=capacity)return;
        capacity=Math.Max(frames,2048);left=new double[capacity];right=new double[capacity];oilBus=new double[capacity];
        envelope=new double[64][];for(int i=0;i<64;i++) {envelope[i]=new double[capacity];units[i].Envelope=envelope[i];}
    }
    public volatile float Peak;
    public long Shots;
    public int ActiveVoices { get { return Attacks.ActiveVoices; } }
    public double SampleInterval(int mean,int std) {
        return Interval(random,ref hasNormal,ref spareNormal,mean,std);
    }
    static double Interval(Random source,ref bool hasSpare,ref double spare,int mean,int std) {
        if(std==0)return mean;
        double normal;
        if(hasSpare) {normal=spare;hasSpare=false;}
        else {
            double radius=Math.Sqrt(-2*Math.Log(1-source.NextDouble()));
            double angle=2*Math.PI*source.NextDouble();
            normal=radius*Math.Cos(angle);spare=radius*Math.Sin(angle);hasSpare=true;
        }
        return Math.Max(50,mean+std*normal);
    }
    static int NewSeed() {
        byte[] seed=new byte[4];
        using(RandomNumberGenerator source=RandomNumberGenerator.Create())source.GetBytes(seed);
        return BitConverter.ToInt32(seed,0);
    }
    public Mixer() : this(NewSeed()) {}
    internal Mixer(int seed) {
        random=new Random(seed);
        for(int i=0;i<units.Length;i++)units[i]=new AttackUnit {Pan=(i%32)*.61803398875%1*2-1,Timing=new Random(unchecked(seed^((i+1)*(int)0x9E3779B9)))};
        Reserve(2048);
        for(int g=0;g<2;g++) {
            clips[g]=new float[4][];
            for(int k=0;k<4;k++) {
                string name=g==0 ? "bflaatt"+(char)('a'+k) : "vflaat2"+(char)('a'+k);
                using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name+".wav")) {
                    if(s==null) throw new InvalidDataException("缺少音效："+name);
                    using(BinaryReader r=new BinaryReader(s)) {
                        if(new string(r.ReadChars(4))!="RIFF") throw new InvalidDataException(name);
                        r.ReadInt32(); r.ReadBytes(4);
                        while(s.Position<s.Length) {
                            string id=new string(r.ReadChars(4)); int size=r.ReadInt32();
                            if(id=="data") {
                                clips[g][k]=new float[size/2];
                                for(int i=0;i<size/2;i++) clips[g][k][i]=r.ReadInt16()/32768f;
                                break;
                            }
                            s.Seek(size+(size&1),SeekOrigin.Current);
                        }
                    }
                }
                if(clips[g][k]==null) throw new InvalidDataException(name);
            }
        }
        Attacks=new AttackSoundBus(clips,units,unchecked(seed^0x4F1BBCDC));
    }
    public bool BoundariesZero() {
        foreach(float[][] group in clips) foreach(float[] clip in group)
            if(clip[0]!=0 || clip[clip.Length-1]!=0) return false;
        return Oil.BoundariesZero;
    }
    // A voice is traversed once per buffer, with contiguous clip reads. Unit
    // envelopes are evaluated only during transitions; settled units need no array.
    void PrepareEnvelopes(int frames) {
        for(int i=0;i<64;i++) {
            AttackUnit u=units[i];double target=u.Enabled?1:0,gain=u.Gain;
            u.Steady=gain==target;
            if(u.Steady)continue;
            double[] ramp=envelope[i];
            for(int n=0;n<frames;n++) {
                gain+=(target-gain)*UnitStep;
                // Removed voices were already discarded below -100 dB. Store an
                // exact zero as well, so a tail cannot turn into a subnormal value.
                if(target==0 && gain<TailCutoff)gain=0;
                else if(target==1 && 1-gain<Settle)gain=1;
                ramp[n]=gain;
            }
            u.Gain=gain;
        }
    }
    static double Settled(double value,double target) {
        return Math.Abs(target-value)<Settle?target:value;
    }
    public void Render(short[] output,int frames) {
        if(frames<0 || frames>output.Length/2)throw new ArgumentOutOfRangeException("frames");
        if(frames==0)return;
        if(frames>capacity)Reserve(frames);
        Settings c=Config;long end=clock+frames;
        width=c.Width/100.0;
        bool starting=c.Playing && !wasPlaying,modeChanged=c.Volley!=wasVolley;
        for(int i=0;i<64;i++) {
            AttackUnit u=units[i];bool enabled=c.Playing && i%32<(i<32?c.B:c.V);
            int interval=(i<32?c.IntervalB:c.IntervalV)*Rate/1000;
            if(enabled && (!u.Enabled || starting || modeChanged))
                // Every unit gets an independent phase, including each group's
                // first unit. Re-enabling a unit draws a fresh phase as well.
                u.Next=clock+(c.Volley?0:(long)(u.Timing.NextDouble()*interval));
            else if(enabled && u.Interval!=interval && u.Interval>0 && u.Next>clock)
                // Preserve fractional time remaining instead of clamping many
                // pending shots onto the same deadline when speed increases.
                u.Next=clock+(long)Math.Round((u.Next-clock)*(double)interval/u.Interval);
            u.Enabled=enabled;
            if(!enabled)continue;
            u.Interval=interval;
        }
        Attacks.CancelDisabled();
        wasPlaying=c.Playing;wasVolley=c.Volley;
        PrepareEnvelopes(frames);
        double targetMaster=c.Playing?c.Volume/100.0:0;
        int audibleUnits=Math.Min(AttackSoundBus.Limit,c.B)+Math.Min(AttackSoundBus.Limit,c.V);
        double targetNorm=.7/Math.Sqrt(Math.Max(1,audibleUnits));
        bool oilAudible=Oil.Render(oilBus,frames,c.OilWells,c.Playing),attackAudible=false;
        Array.Clear(left,0,frames);Array.Clear(right,0,frames);
        int offset=0;
        while(offset<frames) {
            long now=clock+offset;
            if(now==Attacks.NextService)Attacks.Service(now,width);
            int count=(int)Math.Min(frames-offset,Attacks.NextService-now);
            long stop=now+count;
            for(int i=0;i<64;i++) {
                AttackUnit unit=units[i];if(!unit.Enabled)continue;
                while(unit.Next<stop) {
                    Attacks.Queue(i,Math.Max(now,unit.Next));Shots++;
                    int mean=i<32?c.IntervalB:c.IntervalV,std=c.Volley?0:(i<32?c.StdB:c.StdV);
                    unit.Next+=(long)Math.Round(Interval(unit.Timing,ref unit.HasNormal,ref unit.SpareNormal,mean,std)*Rate/1000);
                }
            }
            attackAudible|=Attacks.Mix(left,right,offset,count);offset+=count;
        }
        if(!attackAudible && !oilAudible) {
            master=Settled(targetMaster+(master-targetMaster)*Math.Pow(1-MasterStep,frames),targetMaster);
            normalization=Settled(targetNorm+(normalization-targetNorm)*Math.Pow(1-NormStep,frames),targetNorm);
            Array.Clear(output,0,frames*2);clock=end;Peak=0;return;
        }
        float peak=0;
        for(int n=0;n<frames;n++) {
            master=Settled(master+(targetMaster-master)*MasterStep,targetMaster);
            normalization=Settled(normalization+(targetNorm-normalization)*NormStep,targetNorm);
            double l=Limit((left[n]*normalization+oilBus[n])*master),r=Limit((right[n]*normalization+oilBus[n])*master);
            output[n*2]=(short)Math.Round(l*32767);output[n*2+1]=(short)Math.Round(r*32767);
            peak=Math.Max(peak,(float)Math.Max(Math.Abs(l),Math.Abs(r)));
        }
        clock=end;Peak=peak;
    }
    static double Limit(double x) {
        double a=Math.Abs(x); if(a<=.75) return x;
        return Math.Sign(x)*(.75+.23*Math.Tanh((a-.75)/.23));
    }
}

}
