using System;
using System.IO;
using System.Reflection;

namespace FlakASMR {
static class AttackVariationTests {
    static void Check(bool condition,string name) {if(!condition)throw new Exception("Attack variation: "+name);}
    static AttackUnit[] Units() {
        AttackUnit[] units=new AttackUnit[64];
        for(int i=0;i<units.Length;i++)units[i]=new AttackUnit {Enabled=true,Steady=true,Gain=1,Pan=0};
        return units;
    }
    static float[][][] Clips(int length) {
        float[] clip=new float[length];
        for(int i=1;i<length-1;i++)clip[i]=(float)(.3*Math.Sin(i*.083)+.1*Math.Sin(i*.019));
        float[][][] result=new float[2][][];
        for(int group=0;group<2;group++) {result[group]=new float[4][];for(int k=0;k<4;k++)result[group][k]=clip;}
        return result;
    }
    static int NewSlot(AttackSoundBus bus,int group,long now) {
        for(int i=group*5;i<group*5+5;i++)if(bus.ActiveUnit(i)>=0 && bus.ActiveStart(i)==now)return i;
        throw new Exception("No admitted voice");
    }
    static void FixedWaveform(int percent) {
        float[][][] clips=Clips(1001);
        AttackSoundProfile[] profile={new AttackSoundProfile(percent-100,percent-100,70),new AttackSoundProfile(0,0,95)};
        AttackSoundBus bus=new AttackSoundBus(clips,Units(),31,profile);
        bus.Queue(0,0);bus.Service(1632,.7);
        int duration=(1001*100+percent-1)/percent;
        double[] left=new double[duration+64],right=new double[duration+64];
        bus.Mix(left,right,0,duration-1);Check(bus.ActiveVoices==1,"pitched clip survives until the exact final sample");
        bus.Mix(left,right,duration-1,1);Check(bus.ActiveVoices==0,"duration follows reciprocal pitch, percent="+percent);
        bus.Mix(left,right,duration,64);
        for(int n=0;n<left.Length;n++) {
            double expected=0;
            if(n<duration) {
                // Independent interpolation from absolute output age, rather
                // than the production mixer's incremental fixed-point cursor.
                double position=n*percent/100.0;int first=(int)Math.Floor(position);
                double f=position-first;
                expected=(clips[0][0][first]*(1-f)+clips[0][0][Math.Min(first+1,1000)]*f)*.7;
                if(n<48)expected*=(n+1)/48.0;
                expected*=Math.Sqrt(.5);
            }
            Check(Math.Abs(left[n]-expected)<1e-12 && Math.Abs(right[n]-expected)<1e-12,"interpolated waveform, gain and tail, percent="+percent);
        }
    }
    static double Peak(string name) {
        using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using(BinaryReader reader=new BinaryReader(stream)) {
            reader.ReadBytes(12);
            while(stream.Position<stream.Length) {
                string id=new string(reader.ReadChars(4));int size=reader.ReadInt32();
                if(id=="data") {double peak=0;for(int i=0;i<size/2;i++)peak=Math.Max(peak,Math.Abs(reader.ReadInt16()/32768.0));return peak;}
                stream.Seek(size+(size&1),SeekOrigin.Current);
            }
        }
        throw new Exception("Missing PCM data");
    }
    internal static void Run() {
        foreach(int percent in new[]{90,95,100,105,110})FixedWaveform(percent);
        float[][][] clips=Clips(1001);AttackSoundBus original=new AttackSoundBus(clips,Units(),721);
        int[][] bins={new int[11],new int[21]};
        for(int tick=1;tick<=2200;tick++) {
            long now=tick*1632L;original.Queue(0,now-1);original.Queue(32,now-1);original.Service(now,.7);
            for(int group=0;group<2;group++) {
                int slot=NewSlot(original,group,now),percent=original.ActiveFrequencyPercent(slot),minimum=group==0?95:90;
                Check(percent>=minimum && percent<=200-minimum,"original FShift bounds");bins[group][percent-minimum]++;
                Check(original.ActiveVolumeDrop(slot)==0,"missing original VShift defaults to zero");
                Check(Math.Abs(original.ActiveGain(slot)-(group==0?.7:.95))<1e-14,"original event base volumes 70 and 95");
            }
        }
        for(int group=0;group<2;group++)foreach(int count in bins[group])Check(count>50 && count<300,"uniform inclusive integer pitch choices, endpoints included");
        // A nonzero profile exercises the original generic VShift logic without
        // inventing volume jitter for the two shipped VShift=0 attack events.
        AttackSoundProfile[] shifted={new AttackSoundProfile(0,0,70,10),new AttackSoundProfile(0,0,95)};
        AttackSoundBus varied=new AttackSoundBus(clips,Units(),54,shifted);bool low=false,high=false;
        for(int tick=1;tick<=1000;tick++) {
            long now=tick*1632L;varied.Queue(0,now-1);varied.Service(now,.7);
            int slot=NewSlot(varied,0,now),drop=varied.ActiveVolumeDrop(slot);double gain=varied.ActiveGain(slot);
            Check(drop>=0 && drop<=10 && gain>=.63 && gain<=.7,"VShift is bounded attenuation, never a boost");
            if(drop==10) {low=true;Check(Math.Abs(gain-.7*14746/16384)<1e-14,"original Q14 attenuation floor");}
            if(drop==0) {high=true;Check(gain==.7,"VShift upper gain endpoint");}
        }
        Check(low && high,"VShift inclusive endpoints");
        double a=Peak("vflaat2a.wav"),b=Peak("vflaat2b.wav"),c=Peak("vflaat2c.wav");
        Check(a-b>.15 && c-b>.2,"source variant loudness is preserved instead of equalizing each file");
        Console.WriteLine("PASS: original +/-5% and +/-10% inclusive pitch ranges, 70/95 base gain, VShift=0 defaults and generic Q14 attenuation; resampling waveform and exact pitched duration at five rates; source variant loudness preserved.");
    }
}
}
