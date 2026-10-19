using System;

namespace FlakASMR {
sealed class AttackSoundProfile {
    internal readonly int MinShift,MaxShift,Volume,VolumeShift;
    internal AttackSoundProfile(int minShift,int maxShift,int volume,int volumeShift=0) {
        if(minShift<=-100 || minShift>maxShift || volume<0 || volume>100 || volumeShift<0 || volumeShift>100)
            throw new ArgumentOutOfRangeException("sound profile");
        MinShift=minShift;MaxShift=maxShift;Volume=volume;VolumeShift=volumeShift;
    }
    // Original FlakCannonAttack / FlakTroopAttackAir. Missing VShift defaults to 0.
    internal static readonly AttackSoundProfile[] Original={new AttackSoundProfile(-5,5,70),new AttackSoundProfile(-10,10,95)};
}
sealed class AttackUnit {
    public bool Enabled,Steady,HasNormal;
    public long Next;
    public int Last=-1,Interval;
    public double Gain,Pan,SpareNormal;
    public double[] Envelope;
    public Random Timing;
}

// The original same-volume interrupt ordering: new pending events precede
// playing events; FIFO within pending events; newest playing events precede
// older playing events. Trimming the tail therefore drops late requests first,
// then interrupts the oldest playing event. Each column is one sound event.
sealed class AttackSoundBus {
    internal const int Limit=5,ServiceSamples=Mixer.Rate*34/1000,BridgeSamples=48;
    readonly float[][][] clips;
    readonly AttackUnit[] units;
    readonly Random random;
    readonly AttackSoundProfile[] profiles;
    struct Request {public int Unit;public long Time;}
    sealed class Voice {
        public bool Active;
        public int Unit,Bridge,FrequencyPercent,VolumeDrop;
        public long Position; // hundredths of a source sample; exact integer FShift
        public long Start,Order;
        public float[] Clip;
        public double L,R,LastL,LastR,FromL,FromR,Gain;
    }
    // Keep the whole batch until service: a later settings snapshot may cancel
    // an early request, in which case the next valid request can still survive.
    readonly Request[][] pending={new Request[Mixer.MaxUnits*2],new Request[Mixer.MaxUnits*2]};
    readonly int[] pendingCount=new int[2];
    readonly Voice[] voices=new Voice[Limit*2];
    long order;
    internal long NextService=ServiceSamples;
    internal long Started,Dropped,Interrupted,Cancelled;
    internal int ActiveVoices {get {int n=0;foreach(Voice v in voices)if(v.Active)n++;return n;}}
    internal int ActiveInGroup(int group) {int n=0;for(int i=group*Limit;i<(group+1)*Limit;i++)if(voices[i].Active)n++;return n;}
    internal int ActiveUnit(int slot) {return voices[slot].Active?voices[slot].Unit:-1;}
    internal long ActiveStart(int slot) {return voices[slot].Start;}
    internal int ActiveFrequencyPercent(int slot) {return voices[slot].FrequencyPercent;}
    internal int ActiveVolumeDrop(int slot) {return voices[slot].VolumeDrop;}
    internal double ActiveGain(int slot) {return voices[slot].Gain;}
    internal AttackSoundBus(float[][][] source,AttackUnit[] owners,int seed,AttackSoundProfile[] settings=null) {
        clips=source;units=owners;random=new Random(seed);profiles=settings??AttackSoundProfile.Original;
        if(profiles.Length!=2)throw new ArgumentException("Two sound profiles are required");
        for(int i=0;i<voices.Length;i++)voices[i]=new Voice();
    }
    internal void Queue(int unit,long time) {
        int group=unit/32,count=pendingCount[group],at=0;Request[] queue=pending[group];
        while(at<count && (queue[at].Time<time || (queue[at].Time==time && queue[at].Unit<unit)))at++;
        if(count==queue.Length) {Dropped++;if(at==queue.Length)return;count--;}
        for(int i=count;i>at;i--)queue[i]=queue[i-1];
        queue[at]=new Request {Unit=unit,Time=time};pendingCount[group]=count+1;
    }
    internal void CancelDisabled() {
        for(int group=0;group<2;group++) {
            int keep=0;
            for(int i=0;i<pendingCount[group];i++) {
                Request request=pending[group][i];
                if(units[request.Unit].Enabled)pending[group][keep++]=request;else Cancelled++;
            }
            pendingCount[group]=keep;
        }
    }
    internal void Service(long now,double width) {
        if(now!=NextService)throw new InvalidOperationException("Sound service clock drift");
        NextService+=ServiceSamples;
        for(int group=0;group<2;group++) {
            int accepted=0;
            for(int n=0;n<pendingCount[group];n++) {
                Request request=pending[group][n];AttackUnit unit=units[request.Unit];
                if(!unit.Enabled) {Cancelled++;continue;}
                if(accepted==Limit) {Dropped++;continue;}
                accepted++;
                int slot=group*Limit;
                for(int i=slot;i<(group+1)*Limit;i++) {
                    if(!voices[i].Active) {slot=i;break;}
                    if(voices[i].Order<voices[slot].Order)slot=i;
                }
                Voice voice=voices[slot];
                voice.FromL=voice.Active?voice.LastL:0;voice.FromR=voice.Active?voice.LastR:0;
                if(voice.Active)Interrupted++;
                int variant=random.Next(3);if(unit.Last>=0 && variant>=unit.Last)variant++;
                if(unit.Last<0)variant=random.Next(4);unit.Last=variant;
                AttackSoundProfile profile=profiles[group];
                voice.FrequencyPercent=100+(profile.MinShift==profile.MaxShift?profile.MinShift:random.Next(profile.MinShift,profile.MaxShift+1));
                voice.VolumeDrop=profile.VolumeShift==0?0:random.Next(profile.VolumeShift+1);
                // YR 0x4054EB: VShift draws 0..VShift and attenuates in Q14.
                // It does not add a symmetric +/- amplitude modulation.
                voice.Gain=profile.Volume/100.0*(16384-16384*voice.VolumeDrop/100)/16384.0;
                double angle=(unit.Pan*width+1)*Math.PI/4;
                voice.Active=true;voice.Clip=clips[group][variant];voice.Unit=request.Unit;
                voice.Start=now;voice.Order=order++;voice.Position=0;voice.Bridge=0;
                voice.L=Math.Cos(angle);voice.R=Math.Sin(angle);Started++;
            }
            pendingCount[group]=0;
        }
    }
    internal bool Mix(double[] left,double[] right,int offset,int count) {
        bool active=false;
        foreach(Voice voice in voices) {
            if(!voice.Active)continue;
            AttackUnit unit=units[voice.Unit];
            if(!unit.Enabled && unit.Steady && unit.Gain==0) {voice.Active=false;continue;}
            active=true;
            long remaining=voice.Clip.Length*100L-voice.Position;
            int stop=offset+(int)Math.Min(count,(remaining+voice.FrequencyPercent-1)/voice.FrequencyPercent);
            for(int n=offset;n<stop;n++) {
                double gain=unit.Steady?unit.Gain:unit.Envelope[n];
                int index=(int)(voice.Position/100);double fraction=voice.Position%100/100.0;
                double first=voice.Clip[index],second=voice.Clip[Math.Min(index+1,voice.Clip.Length-1)];
                double sample=(first+(second-first)*fraction)*gain*voice.Gain;
                voice.Position+=voice.FrequencyPercent;
                double l=sample*voice.L,r=sample*voice.R;
                if(voice.Bridge<BridgeSamples) {
                    double t=++voice.Bridge/(double)BridgeSamples;
                    l=voice.FromL*(1-t)+l*t;r=voice.FromR*(1-t)+r*t;
                }
                left[n]+=l;right[n]+=r;voice.LastL=l;voice.LastR=r;
            }
            if(voice.Position>=voice.Clip.Length*100L || (!unit.Enabled && (unit.Steady?unit.Gain:unit.Envelope[offset+count-1])==0)) {
                voice.Active=false;voice.LastL=voice.LastR=0;
            }
        }
        return active;
    }
}
}
