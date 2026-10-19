using System;
using System.Collections.Generic;

namespace FlakASMR {
static class AttackBatchTests {
    static readonly AttackSoundProfile[] Neutral={new AttackSoundProfile(0,0,100),new AttackSoundProfile(0,0,100)};
    struct Shot {public int Unit;public int Time;}
    sealed class RefVoice {public int Unit,Start,Order;public double LastL,LastR,FromL,FromR;}
    static void Check(bool condition,string name) {if(!condition)throw new Exception("Attack batch: "+name);}
    static AttackUnit[] Units() {
        AttackUnit[] units=new AttackUnit[64];
        for(int i=0;i<64;i++)units[i]=new AttackUnit {Enabled=true,Steady=true,Gain=1,Pan=(i%7-3)/3.0};
        return units;
    }
    static float[][][] Clips() {
        float[][][] clips=new float[2][][];
        for(int group=0;group<2;group++) {
            clips[group]=new float[4][];float[] source=new float[group==0?6901:4903];
            for(int i=1;i<source.Length-1;i++)source[i]=(float)(.2*Math.Sin(i*(group==0?.031:.067))+.1*Math.Cos(i*.019));
            for(int k=0;k<4;k++)clips[group][k]=source;
        }
        return clips;
    }
    static Shot[] Trace() {
        List<Shot> shots=new List<Shot>();
        for(int i=0;i<64;i++)shots.Add(new Shot {Unit=i,Time=i%32*13});
        for(int i=0;i<70;i++)shots.Add(new Shot {Unit=i%64,Time=1700+i*71});
        for(int i=0;i<12;i++)shots.Add(new Shot {Unit=5+i,Time=8500+i*49});
        shots.Sort((a,b)=>a.Time!=b.Time?a.Time.CompareTo(b.Time):a.Unit.CompareTo(b.Unit));return shots.ToArray();
    }
    // Deliberately simple, allocation-heavy, sample-at-a-time reference. It
    // keeps the complete event history and applies limits only on service.
    static double[][] Reference(Shot[] trace,float[][][] clips,AttackUnit[] units,int length,out long started,out long dropped,out long interrupted) {
        double[][] result={new double[length],new double[length]};
        List<RefVoice> live=new List<RefVoice>();int seen=0,order=0;
        started=dropped=interrupted=0;
        for(int now=0;now<length;now++) {
            live.RemoveAll(v=>now-v.Start>=clips[v.Unit/32][0].Length);
            if(now>0 && now%1632==0) {
                int stop=seen;while(stop<trace.Length && trace[stop].Time<now)stop++;
                for(int group=0;group<2;group++) {
                    int accepted=0;
                    for(int i=seen;i<stop;i++) {
                        Shot shot=trace[i];if(shot.Unit/32!=group)continue;
                        if(accepted++>=5) {dropped++;continue;}
                        RefVoice replaced=null;int count=0;
                        foreach(RefVoice voice in live)if(voice.Unit/32==group) {
                            count++;if(replaced==null || voice.Order<replaced.Order)replaced=voice;
                        }
                        RefVoice next=new RefVoice {Unit=shot.Unit,Start=now,Order=order++};
                        if(count==5) {
                            next.FromL=replaced.LastL;next.FromR=replaced.LastR;live.Remove(replaced);interrupted++;
                        }
                        live.Add(next);started++;
                    }
                }
                seen=stop;
            }
            foreach(RefVoice voice in live) {
                int age=now-voice.Start;double sample=clips[voice.Unit/32][0][age];
                double angle=(units[voice.Unit].Pan*.7+1)*Math.PI/4;
                double l=sample*Math.Cos(angle),r=sample*Math.Sin(angle);
                if(age<48) {double t=(age+1)/48.0;l=voice.FromL*(1-t)+l*t;r=voice.FromR*(1-t)+r*t;}
                result[0][now]+=l;result[1][now]+=r;voice.LastL=l;voice.LastR=r;
            }
        }
        return result;
    }
    internal static void Run() {
        float[][][] clips=Clips();AttackUnit[] owners=Units();
        AttackSoundBus batch=new AttackSoundBus(clips,owners,19,Neutral);
        for(int i=0;i<32;i++) {batch.Queue(i,32-i);batch.Queue(i+32,i);}
        batch.Service(1632,.7);
        Check(batch.ActiveInGroup(0)==5 && batch.ActiveInGroup(1)==5 && batch.Dropped==54,"independent five-event limits");
        for(int i=0;i<5;i++) {Check(batch.ActiveUnit(i)==31-i,"earliest requests survive regardless of enqueue order");Check(batch.ActiveUnit(5+i)==32+i,"separate sound event FIFO");}
        batch.Queue(10,1700);batch.Queue(11,1701);batch.Service(3264,.7);
        Check(batch.Interrupted==2 && batch.ActiveUnit(0)==10 && batch.ActiveUnit(1)==11,"interrupt replaces oldest playing events");
        Check(batch.ActiveUnit(5)==32,"one event never steals another event's voice");
        owners=Units();batch=new AttackSoundBus(clips,owners,19,Neutral);
        for(int i=0;i<7;i++)batch.Queue(i,i);
        owners[1].Enabled=false;batch.CancelDisabled();batch.Service(1632,.7);
        Check(batch.Cancelled==1 && batch.Dropped==1 && batch.ActiveUnit(4)==5,"cancelled pending event admits the next valid request");
        Mixer paused=new Mixer(3);short[] pauseBuffer=new short[4096];
        paused.Config=new Settings(32,32,500,500,0,0,70,30,true,true);paused.Render(pauseBuffer,512);
        Check(paused.Shots==64 && paused.Attacks.Started==0,"requests wait for service");
        paused.Config=new Settings(32,32,500,500,0,0,70,30,false,true);paused.Render(pauseBuffer,2048);
        Check(paused.Attacks.Cancelled==64 && paused.Attacks.Started==0,"pause cancels the pending batch");

        Shot[] trace=Trace();const int length=20000;long expectedStarts,expectedDrops,expectedInterrupts;
        double[][] reference=Reference(trace,clips,Units(),length,out expectedStarts,out expectedDrops,out expectedInterrupts);
        foreach(int block in new[]{1,127,480,1440,4096}) {
            owners=Units();AttackSoundBus bus=new AttackSoundBus(clips,owners,71,Neutral);
            double[] left=new double[block],right=new double[block];int clock=0,next=0;
            while(clock<length) {
                int frames=Math.Min(block,length-clock),offset=0;Array.Clear(left,0,frames);Array.Clear(right,0,frames);
                while(offset<frames) {
                    int now=clock+offset;if(now==bus.NextService)bus.Service(now,.7);
                    int count=(int)Math.Min(frames-offset,bus.NextService-now);
                    while(next<trace.Length && trace[next].Time<now+count) {bus.Queue(trace[next].Unit,trace[next].Time);next++;}
                    bus.Mix(left,right,offset,count);offset+=count;
                    Check(bus.ActiveInGroup(0)<=5 && bus.ActiveInGroup(1)<=5,"voice limit at every segment");
                }
                for(int i=0;i<frames;i++) {
                    Check(Math.Abs(left[i]-reference[0][clock+i])<1e-12 && Math.Abs(right[i]-reference[1][clock+i])<1e-12,"raw waveform versus independent per-sample reference, block="+block+", sample="+(clock+i));
                }
                clock+=frames;
            }
            Check(bus.Started==expectedStarts && bus.Dropped==expectedDrops && bus.Interrupted==expectedInterrupts,"reference admission and interruption counts");
        }
        Console.WriteLine("PASS: attack 34ms batches; independent Limit=5; FIFO pending admission; oldest playing interruption; cancelled requests; 1ms seams; raw waveform vs independent per-sample reference at five block sizes.");
    }
}
}
