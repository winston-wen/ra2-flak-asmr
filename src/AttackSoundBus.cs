using System;

namespace FlakASMR {
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
    struct Request {public int Unit;public long Time;}
    sealed class Voice {
        public bool Active;
        public int Unit,Position,Bridge;
        public long Start,Order;
        public float[] Clip;
        public double L,R,LastL,LastR,FromL,FromR;
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
    internal AttackSoundBus(float[][][] source,AttackUnit[] owners,int seed) {
        clips=source;units=owners;random=new Random(seed);
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
                double angle=(unit.Pan*width+1)*Math.PI/4;
                voice.Active=true;voice.Clip=clips[group][variant];voice.Unit=request.Unit;
                voice.Start=now;voice.Order=order++;voice.Position=voice.Bridge=0;
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
            int stop=offset+Math.Min(count,voice.Clip.Length-voice.Position);
            for(int n=offset;n<stop;n++) {
                double gain=unit.Steady?unit.Gain:unit.Envelope[n];
                double sample=voice.Clip[voice.Position++]*gain;
                double l=sample*voice.L,r=sample*voice.R;
                if(voice.Bridge<BridgeSamples) {
                    double t=++voice.Bridge/(double)BridgeSamples;
                    l=voice.FromL*(1-t)+l*t;r=voice.FromR*(1-t)+r*t;
                }
                left[n]+=l;right[n]+=r;voice.LastL=l;voice.LastR=r;
            }
            if(voice.Position==voice.Clip.Length || (!unit.Enabled && (unit.Steady?unit.Gain:unit.Envelope[offset+count-1])==0)) {
                voice.Active=false;voice.LastL=voice.LastR=0;
            }
        }
        return active;
    }
}
}
