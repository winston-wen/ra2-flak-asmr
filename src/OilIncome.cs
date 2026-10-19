using System;
using System.IO;
using System.Reflection;

namespace FlakASMR {
// One player's account and displayed credit counter, shared by every oil well.
// Frame numbers are simulation time, so pausing never re-captures existing wells.
sealed class OilEconomy {
    public const int MaxWells=32, FramesPerSecond=60, PaymentFrames=100;
    readonly long[] due=new long[MaxWells];
    int wells;
    public long Frame, Balance, Displayed, CaptureIncome, RecurringIncome, SoundRequests;
    public bool Tick(int requested) {
        requested=Math.Max(0,Math.Min(MaxWells,requested));
        for(int i=wells;i<requested;i++) {due[i]=Frame+PaymentFrames;Balance+=1000;CaptureIncome+=1000;}
        wells=requested;
        // Settle all payments before advancing the single display counter.
        for(int i=0;i<wells;i++)if(Frame>=due[i]) {
            Balance+=20;RecurringIncome+=20;due[i]+=PaymentFrames;
        }
        Frame++;
        long difference=Balance-Displayed;
        if(difference<=0)return false;
        Displayed+=Math.Min(143L,Math.Max(1L,difference>>3));
        SoundRequests++;return true;
    }
}

sealed class OilIncome {
    const int SamplesPerFrame=Mixer.Rate/OilEconomy.FramesPerSecond, BridgeSamples=48;
    readonly float[] clip;
    internal readonly OilEconomy Economy=new OilEconomy();
    int untilTick,position,bridge;
    double last,bridgeFrom;
    internal long VoiceStarts;
    internal bool BoundariesZero {get {return clip[0]==0 && clip[clip.Length-1]==0;}}
    internal OilIncome() {
        using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream("ucredup.wav")) {
            if(s==null)throw new InvalidDataException("缺少音效：ucredup");
            using(BinaryReader r=new BinaryReader(s)) {
                if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException("ucredup");
                r.ReadInt32();r.ReadBytes(4);
                while(s.Position<s.Length) {
                    string id=new string(r.ReadChars(4));int size=r.ReadInt32();
                    if(id=="data") {clip=new float[size/2];for(int i=0;i<clip.Length;i++)clip[i]=r.ReadInt16()/32768f;break;}
                    s.Seek(size+(size&1),SeekOrigin.Current);
                }
            }
        }
        if(clip==null)throw new InvalidDataException("ucredup PCM");position=clip.Length;
    }
    internal bool Render(double[] output,int frames,int wells,bool playing) {
        Array.Clear(output,0,frames);bool audible=false;int dest=0;
        while(dest<frames) {
            if(playing && untilTick==0) {
                if(Economy.Tick(wells)) {
                    // CreditUp Control=interrupt, Limit=1: replace the one voice.
                    // Bridge its last sample over 1 ms to avoid an artificial click.
                    bridgeFrom=last;bridge=0;position=0;VoiceStarts++;
                }
                untilTick=SamplesPerFrame;
            }
            int count=playing?Math.Min(frames-dest,untilTick):frames-dest;
            for(int n=0;(position<clip.Length || bridge<BridgeSamples) && n<count;n++) {
                double sample=position<clip.Length?clip[position++]*.1:0;
                if(bridge<BridgeSamples) {double t=++bridge/(double)BridgeSamples;sample=bridgeFrom*(1-t)+sample*t;}
                last=sample;output[dest+n]=sample;if(sample!=0)audible=true;
            }
            dest+=count;if(playing)untilTick-=count;
        }
        return audible;
    }
}
}
