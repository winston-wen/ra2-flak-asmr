using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FlakASMR {
sealed class Settings {
    public readonly int B, V, IntervalB, IntervalV, Jitter, StdB, StdV, Width, Volume;
    public readonly bool Playing, Volley;
    public Settings(int b, int v, int ib, int iv, int jitter, int width, int volume, bool playing, bool volley) {
        B=b; V=v; IntervalB=ib; IntervalV=iv; Jitter=jitter;
        StdB=(int)Math.Round(ib*jitter/100.0/Math.Sqrt(3));StdV=(int)Math.Round(iv*jitter/100.0/Math.Sqrt(3));
        Width=width; Volume=volume; Playing=playing; Volley=volley;
    }
    public Settings(int b,int v,int ib,int iv,int stdB,int stdV,int width,int volume,bool playing,bool volley) {
        B=b;V=v;IntervalB=ib;IntervalV=iv;StdB=stdB;StdV=stdV;Jitter=0;
        Width=width;Volume=volume;Playing=playing;Volley=volley;
    }
}

sealed class Mixer {
    public const int Rate=48000, MaxUnits=32;
    public volatile Settings Config=new Settings(3,2,500,500,50,50,70,30,false,false);
    readonly float[][][] clips=new float[2][][];
    readonly Random random=new Random(72491);
    readonly Unit[] units=new Unit[64];
    readonly Voice[] voices=new Voice[2048];
    int voiceCount; long clock; bool wasPlaying, wasVolley;
    bool hasNormal;double spareNormal;
    double master, normalization=1, width=.7;
    public volatile float Peak;
    public long Shots;
    public int ActiveVoices { get { return voiceCount; } }
    public double SampleInterval(int mean,int std) {
        if(std==0)return mean;
        double normal;
        if(hasNormal) {normal=spareNormal;hasNormal=false;}
        else {
            double radius=Math.Sqrt(-2*Math.Log(1-random.NextDouble()));
            double angle=2*Math.PI*random.NextDouble();
            normal=radius*Math.Cos(angle);spareNormal=radius*Math.Sin(angle);hasNormal=true;
        }
        return Math.Max(50,mean+std*normal);
    }
    sealed class Unit { public bool Enabled; public long Next; public int Last=-1, Interval; public double Gain; public double Pan; }
    struct Voice { public float[] Clip; public int Unit; public long Start; public double L,R; }
    public Mixer() {
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
        for(int i=0;i<units.Length;i++) units[i]=new Unit {Pan=(i%32)*.61803398875%1*2-1};
    }
    public bool BoundariesZero() {
        foreach(float[][] group in clips) foreach(float[] clip in group)
            if(clip[0]!=0 || clip[clip.Length-1]!=0) return false;
        return true;
    }
    void Shot(int index,long start,int count) {
        if(voiceCount==voices.Length) return;
        Unit u=units[index]; int variant=random.Next(3); if(variant>=u.Last && u.Last>=0) variant++;
        if(u.Last<0) variant=random.Next(4); u.Last=variant;
        double angle=(u.Pan*width+1)*Math.PI/4;
        voices[voiceCount++]=new Voice {Clip=clips[index/32][variant],Unit=index,Start=start,L=Math.Cos(angle),R=Math.Sin(angle)};
        Shots++;
    }
    // Only the audio thread owns timing and voices. UI updates swap an immutable snapshot.
    public void Render(short[] output,int frames) {
        Settings c=Config; long end=clock+frames;
        int total=c.B+c.V;
        width += (c.Width/100.0-width)*.1;
        bool starting=c.Playing && !wasPlaying;
        bool modeChanged=c.Volley!=wasVolley;
        for(int i=0;i<64;i++) {
            Unit u=units[i]; bool enabled=c.Playing && i%32<(i<32?c.B:c.V);
            int interval=(i<32?c.IntervalB:c.IntervalV)*Rate/1000;
            if(enabled && (!u.Enabled || starting || modeChanged))
                u.Next=clock+(c.Volley || i%32==0 ? 0 : (long)(random.NextDouble()*interval));
            u.Enabled=enabled;
            if(!enabled) continue;
            // Changing speed takes effect within the next interval, even after a long pause.
            if(u.Interval!=interval && u.Next>clock+interval) u.Next=clock+interval;
            u.Interval=interval;
            while(u.Next<end) {
                Shot(i,Math.Max(clock,u.Next),total);
                int std=c.Volley?0:(i<32?c.StdB:c.StdV);
                u.Next+=(long)Math.Round(SampleInterval(i<32?c.IntervalB:c.IntervalV,std)*Rate/1000);
            }
        }
        wasPlaying=c.Playing; wasVolley=c.Volley;
        double targetMaster=c.Playing?c.Volume/100.0:0;
        double targetNorm=.7/Math.Sqrt(Math.Max(1,total));
        float peak=0;
        for(int n=0;n<frames;n++) {
            // 12 ms master envelope; disabled units have a 15 ms tail envelope.
            master+=(targetMaster-master)/576;
            normalization+=(targetNorm-normalization)/2400;
            for(int i=0;i<64;i++) units[i].Gain+=((units[i].Enabled?1:0)-units[i].Gain)/720;
            double l=0,r=0; long now=clock+n;
            for(int j=voiceCount-1;j>=0;j--) {
                Voice v=voices[j]; long position=now-v.Start;
                if(position>=v.Clip.Length || (units[v.Unit].Gain<.00001 && !units[v.Unit].Enabled)) {
                    voices[j]=voices[--voiceCount]; voices[voiceCount]=new Voice(); continue;
                }
                if(position<0) continue;
                double a=v.Clip[(int)position]*units[v.Unit].Gain;
                l+=a*v.L; r+=a*v.R;
            }
            l=Limit(l*normalization*master); r=Limit(r*normalization*master);
            output[n*2]=(short)Math.Round(l*32767); output[n*2+1]=(short)Math.Round(r*32767);
            peak=Math.Max(peak,(float)Math.Max(Math.Abs(l),Math.Abs(r)));
        }
        clock=end; Peak=peak;
    }
    static double Limit(double x) {
        double a=Math.Abs(x); if(a<=.75) return x;
        return Math.Sign(x)*(.75+.23*Math.Tanh((a-.75)/.23));
    }
}

static class Program {
    [STAThread] static void Main(string[] args) {
        if(args.Length>3 && args[0]=="--compare-preview") {
            try {
                using(var expected=new Bitmap(args[1]))using(var actual=new Bitmap(args[2])) {
                    Assert(expected.Size==actual.Size,"Screenshot dimensions differ");
                    for(int y=0;y<expected.Height;y++)for(int x=0;x<expected.Width;x++)
                        Assert(expected.GetPixel(x,y).ToArgb()==actual.GetPixel(x,y).ToArgb(),"Screenshot mismatch at "+x+","+y);
                }
                File.WriteAllText(args[3],"PASS: screenshot matches approved prototype pixel-for-pixel\r\n");
            }catch(Exception e) {File.WriteAllText(args[3],"FAIL: "+e);Environment.ExitCode=1;}
            return;
        }
        if(args.Length>0 && args[0]=="--self-test") {SelfTest(args.Length>1?args[1]:"self-test.txt");return;}
        if(args.Length>0 && args[0]=="--device-test") {DeviceTest(args.Length>1?args[1]:"device-test.txt");return;}
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>1 && args[0]=="--ui-test") {
            try {
                using(var form=new MainForm(2,args.Length>2 && args[2]=="--no-device")) {
                    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.ShowInTaskbar=false;
                    form.Show();Application.DoEvents();form.ControlTest();form.Close();
                }
                File.WriteAllText(args[1],"PASS: 500/50 ms defaults; independent left/right timing; play/pause hit targets and pressed selection; plus/minus hit targets and 4px gaps; horizontal volume drag and endpoints; bitmap numeric editor, commit/cancel and volume editing; range limits; 20px caption buttons; readout alignment; hover/pressed states\r\n");
            }catch(Exception e) {File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}
            return;
        }
        if(args.Length>1 && args[0]=="--render-preview") {
            using(var form=new MainForm(2,true)) {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.ShowInTaskbar=false;
                form.Show();Application.DoEvents();form.PerformLayout();
                if(args.Length>2)form.PreviewTransport(args[2]=="playing");
                using(var bitmap=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);}
                form.Close();
            }
            return;
        }
        try {Application.Run(new MainForm());} catch(Exception e) {MessageBox.Show(e.ToString(),"Flak ASMR",MessageBoxButtons.OK,MessageBoxIcon.Error);}
    }
    static void Assert(bool value,string message) {if(!value) throw new Exception(message);}
    static void SelfTest(string path) {
        try {
            Mixer m=new Mixer();short[] pcm=new short[512];Assert(m.BoundariesZero(),"Clip boundary");
            Mixer distribution=new Mixer();
            foreach(int[] pair in new[]{new[]{300,50},new[]{200,20}}) {
                double sum=0,sumSquare=0;
                for(int i=0;i<100000;i++) {double interval=distribution.SampleInterval(pair[0],pair[1]);sum+=interval;sumSquare+=interval*interval;}
                double mean=sum/100000, std=Math.Sqrt(sumSquare/100000-mean*mean);
                Assert(Math.Abs(mean-pair[0])<1 && Math.Abs(std-pair[1])<1,"Normal interval distribution");
            }
            Assert(distribution.SampleInterval(300,0)==300,"Zero standard deviation");
            m.Render(pcm,256);foreach(short v in pcm) Assert(v==0,"Idle silence");
            Stopwatch w=Stopwatch.StartNew();int peak=0;bool sound=false;
            m.Config=new Settings(32,32,100,100,60,100,100,true,false);
            for(int i=0;i<48000*10/256;i++) {m.Render(pcm,256);foreach(short v in pcm) {peak=Math.Max(peak,Math.Abs((int)v));if(v!=0)sound=true;}}
            double stressSeconds=w.Elapsed.TotalSeconds;Assert(sound,"No sound");Assert(peak<32767,"Clipping");Assert(m.Shots>5000,"Scheduling");
            m.Config=new Settings(0,0,100,100,0,0,100,true,false);
            for(int i=0;i<100;i++)m.Render(pcm,256);foreach(short v in pcm)Assert(v==0,"Unit removal fade");
            m.Config=new Settings(3,2,480,560,0,70,30,true,true);
            for(int i=0;i<400;i++)m.Render(pcm,256);
            m.Config=new Settings(3,2,480,560,0,70,30,false,true);
            for(int i=0;i<100;i++)m.Render(pcm,256);foreach(short v in pcm)Assert(v==0,"Stop fade");
            for(int cycle=0;cycle<50;cycle++) {
                m.Config=new Settings(1,1,100,100,20,50,30,true,false);m.Render(pcm,256);
                m.Config=new Settings(1,1,100,100,20,50,30,false,false);
                for(int i=0;i<100;i++)m.Render(pcm,256);foreach(short v in pcm)Assert(v==0,"Rapid restart");
            }
            m.Config=new Settings(32,32,50,50,0,0,70,100,true,false);long before=m.Shots;
            for(int i=0;i<48000*3/256;i++) {m.Render(pcm,256);foreach(short v in pcm)Assert(Math.Abs((int)v)<32767,"50 ms clipping");}
            Assert(m.Shots-before>3700,"50 ms scheduling capacity");
            File.WriteAllText(path,"PASS\r\n8 embedded clips decoded; boundaries zero\r\nNormal timing distribution: 300/50 ms and 200/20 ms; std=0 fixed timing\r\nIdle, 64 units at 100 ms and 50 ms, no PCM clipping, remove units, volley, stop fade, 50 restart cycles\r\n10 s stress rendering: "+stressSeconds.ToString("F3")+" s; peak="+peak+"; shots="+m.Shots+"\r\n");
        } catch(Exception e) {File.WriteAllText(path,"FAIL\r\n"+e);Environment.ExitCode=1;}
    }
    static void DeviceTest(string path) {
        try {using(AudioEngine e=new AudioEngine()) {e.Mixer.Config=new Settings(32,32,100,100,60,100,0,true,false);Thread.Sleep(2000);long first=e.CompletedFrames;Thread.Sleep(1000);long second=e.CompletedFrames-first;Assert(e.Error==null,e.Error);Assert(e.CompletedFrames>48000,"Insufficient completed buffers: "+e.Diagnostics());Assert(second>=44000 && second<=52000,"Output rate mismatch: frames per second="+second);File.WriteAllText(path,"PASS: WASAPI output with 64 units mixed at zero master volume; "+e.Diagnostics()+"; warm frames per second="+second+"\r\n");}}
        catch(Exception e) {File.WriteAllText(path,"FAIL: "+e);Environment.ExitCode=1;}
    }
}
}
