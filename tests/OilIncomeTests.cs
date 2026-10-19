using System;

namespace FlakASMR {
static class OilIncomeTests {
    static void Check(bool condition,string name) {if(!condition)throw new Exception("Oil: "+name);}
    static int CatchUp(int amount) {
        OilEconomy e=new OilEconomy();e.Balance=amount;
        while(e.Tick(0)) {}
        Check(e.Displayed==amount,"counter reaches exact balance");return (int)e.SoundRequests;
    }
    static Settings Config(int oil,bool play,int volume) {return new Settings(0,0,500,500,50,50,70,volume,play,false,oil);}
    internal static void Run() {
        Check(CatchUp(20)==17 && CatchUp(40)==23 && CatchUp(100)==31 && CatchUp(1000)==48,"original integer step, cap and global aggregation");
        OilEconomy e=new OilEconomy();e.Tick(2);
        Check(e.CaptureIncome==2000 && e.RecurringIncome==0,"one capture bonus per new well");
        for(int i=1;i<100;i++)e.Tick(2);
        Check(e.RecurringIncome==0,"no early payment");e.Tick(2);
        Check(e.RecurringIncome==40,"two wells settle on frame 100");
        e.Tick(3);Check(e.CaptureIncome==3000,"adding one preserves existing wells");
        for(int i=102;i<=200;i++)e.Tick(3);
        Check(e.RecurringIncome==80,"old timers do not restart on count change");e.Tick(3);
        Check(e.RecurringIncome==100,"new well has its own capture-relative timer");
        long balance=e.Balance;e.Tick(0);Check(e.Balance==balance,"removing wells never removes earned money");
        for(int i=0;i<300;i++)e.Tick(0);Check(e.Balance==balance && e.Displayed==balance,"removed wells stop paying; pending display catches up");
        // Oil-only PCM must be independent of callback boundaries and flak normalization.
        const int length=48000*8;short[] reference=null;
        foreach(int block in new[]{127,480,1440,4096}) {
            Mixer m=new Mixer(1);m.Reserve(block);m.Config=Config(2,true,100);
            short[] all=new short[length*2],buffer=new short[block*2];int offset=0;bool sound=false;
            while(offset<length) {
                int count=Math.Min(block,length-offset);m.Render(buffer,count);
                Array.Copy(buffer,0,all,offset*2,count*2);offset+=count;
            }
            for(int i=0;i<all.Length;i++) {if(all[i]!=0)sound=true;Check(Math.Abs((int)all[i])<4000,"original 20% times 0.5 gain");}
            Check(sound && m.ActiveVoices==0,"oil-only output without flak voices");
            Check(m.Oil.Economy.CaptureIncome==2000 && m.Oil.Economy.RecurringIncome==160,"8 seconds of 60 fps income");
            Check(m.Oil.VoiceStarts==m.Oil.Economy.SoundRequests,"one global interrupting voice per counter request");
            if(reference==null)reference=all;
            else for(int i=0;i<all.Length;i++)Check(Math.Abs((int)all[i]-reference[i])<=1,"sample timing independent of callback size");
            long frame=m.Oil.Economy.Frame,capture=m.Oil.Economy.CaptureIncome;
            m.Config=Config(2,false,100);for(int i=0;i<100;i++)m.Render(buffer,block);
            Check(m.Oil.Economy.Frame==frame && m.Oil.Economy.CaptureIncome==capture,"pause freezes economy");
            foreach(short value in buffer)Check(value==0,"pause fades to silence");
            m.Config=Config(2,true,0);m.Render(buffer,block);
            Check(m.Oil.Economy.CaptureIncome==capture,"resume never grants another capture bonus");
        }
        Mixer stressed=new Mixer(2);stressed.Config=new Settings(32,32,50,50,50,50,70,100,true,false,32);
        short[] pcm=new short[960];for(int i=0;i<1000;i++) {stressed.Render(pcm,480);foreach(short sample in pcm)Check(Math.Abs((int)sample)<32767,"64 flak plus 32 oil no clipping");}
        Check(stressed.Oil.Economy.RecurringIncome==32*20*5,"32 oil wells share payment clock");
        Console.WriteLine("PASS: oil capture/timers, 60 fps global counter and interrupt voice, 20/40/100/1000 credit traces, four block sizes, pause/resume, oil-only PCM and 64+32 stress.");
    }
}
}
