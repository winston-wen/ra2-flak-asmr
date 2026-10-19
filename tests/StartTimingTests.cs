using System;
using System.Collections.Generic;
using System.Reflection;

namespace FlakASMR {
// Observe schedules without adding instrumentation to the realtime mixer.
static class StartTimingTests {
    static readonly FieldInfo Units=typeof(Mixer).GetField("units",BindingFlags.Instance|BindingFlags.NonPublic);
    static readonly FieldInfo Clock=typeof(Mixer).GetField("clock",BindingFlags.Instance|BindingFlags.NonPublic);
    static void Require(bool condition,string message) {if(!condition)throw new Exception(message);}
    static long Now(Mixer mixer) {return (long)Clock.GetValue(mixer);}
    static long[] Deadlines(Mixer mixer,int count) {
        Array units=(Array)Units.GetValue(mixer);long[] result=new long[count*2];
        for(int i=0;i<result.Length;i++) {
            object unit=units.GetValue(i<count?i:32+i-count);
            result[i]=(long)unit.GetType().GetField("Next").GetValue(unit);
        }
        return result;
    }
    static void Config(Mixer mixer,int count,int mean) {mixer.Config=new Settings(count,count,mean,mean,0,0,70,30,true,false);}
    static void Advance(Mixer mixer,int frames) {
        short[] pcm=new short[960];
        while(frames>0) {int block=Math.Min(frames,480);mixer.Render(pcm,block);frames-=block;}
    }
    static bool Same(long[] a,long[] b) {
        for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
        return true;
    }
    public static void Run() {
        const int period=Mixer.Rate/2;int[] bins=new int[4];int immediate=0;double sum=0;
        for(int seed=1;seed<=32;seed++) {
            Mixer mixer=new Mixer(seed);Config(mixer,16,500);Advance(mixer,1);
            foreach(long next in Deadlines(mixer,16)) {
                long phase=next%period;if(phase==0)immediate++;
                bins[(int)(phase*4/period)]++;sum+=phase/(double)period;
            }
        }
        Require(immediate<4,"Group-leading units must not be forced to fire immediately");
        foreach(int bin in bins)Require(bin>160 && bin<350,"Initial phases must cover the full interval");
        Require(sum/1024>.45 && sum/1024<.55,"Initial phases must not cluster near start");

        Mixer fixedTiming=new Mixer(72491);Config(fixedTiming,10,500);Advance(fixedTiming,1);
        long[] first=Deadlines(fixedTiming,10);Advance(fixedTiming,period*4);
        long[] after=Deadlines(fixedTiming,10);
        for(int i=0;i<first.Length;i++)Require(first[i]%period==after[i]%period,"Std=0 keeps independent phases across shots");
        fixedTiming.Config=new Settings(10,10,500,500,0,0,70,30,false,false);Advance(fixedTiming,48000);
        long resumeAt=Now(fixedTiming);Config(fixedTiming,10,500);Advance(fixedTiming,1);
        after=Deadlines(fixedTiming,10);long[] resumedPhases=new long[after.Length];
        for(int i=0;i<after.Length;i++) {
            resumedPhases[i]=(after[i]-resumeAt)%period;
            Require(resumedPhases[i]>=0 && resumedPhases[i]<period,"Resume phase stays inside one interval");
            first[i]%=period;
        }
        Require(!Same(first,resumedPhases),"Resume draws fresh phases");

        Mixer growing=new Mixer(42);Config(growing,2,500);Advance(growing,1);
        long[] existing=Deadlines(growing,2);long addedAt=Now(growing);Config(growing,10,500);Advance(growing,1);
        long[] grown=Deadlines(growing,10);
        Require(grown[0]==existing[0] && grown[1]==existing[1] && grown[10]==existing[2] && grown[11]==existing[3],"Adding units preserves existing schedules");
        var uniqueAdded=new HashSet<long>();
        for(int i=0;i<grown.Length;i++)if(i%10>=2) {
            Require(grown[i]>=addedAt && grown[i]<addedAt+period,"Added unit starts inside one interval");uniqueAdded.Add(grown[i]);
        }
        Require(uniqueAdded.Count>12,"Added units start separately");

        Mixer speeding=new Mixer(42);Config(speeding,16,10000);Advance(speeding,1);
        Config(speeding,16,500);Advance(speeding,1);
        var uniqueSpeed=new HashSet<long>(Deadlines(speeding,16));
        Require(uniqueSpeed.Count>28,"Shortening interval must not collapse deadlines into a group volley");

        Mixer freshA=new Mixer(),freshB=new Mixer();Config(freshA,10,500);Config(freshB,10,500);Advance(freshA,1);Advance(freshB,1);
        Require(!Same(Deadlines(freshA,10),Deadlines(freshB,10)),"Independent launches use different random sequences");
        Console.WriteLine("PASS: random phases for all units; 1024 starts span the interval; independent launch seeds; std=0 preserves offsets; resume/add units randomize; interval changes avoid synchronized deadlines.");
    }
}
}
