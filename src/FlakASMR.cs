using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FlakASMR {
static class Program {
    [STAThread] static void Main(string[] args) {
        if(args.Length>1 && args[0]=="--render-regression") {PerformanceTests.Regression(args[1]);return;}
        if(args.Length>1 && args[0]=="--live-benchmark") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);PerformanceTests.Live(args[1]);return;}
        if(args.Length>1 && args[0]=="--drag-benchmark") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);PerformanceTests.Live(args[1],true);return;}
        if(args.Length>1 && args[0]=="--benchmark") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);PerformanceTests.Run(args[1]);return;}
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
        if(args.Length>0 && args[0]=="--notification-test") {NotificationTest(args.Length>1?args[1]:"notification-test.txt");return;}
        if(args.Length>0 && args[0]=="--soak-test") {SoakTest(args.Length>1?args[1]:"soak-test.txt");return;}
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>1 && args[0]=="--ui-test") {
            try {
                using(var form=new MainForm(2,args.Length>2 && args[2]=="--no-device")) {
                    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.ShowInTaskbar=false;
                    form.Show();Application.DoEvents();form.ControlTest();form.Close();
                }
                File.WriteAllText(args[1],"PASS: defaults; independent timing; standard deviation <= mean, automatic truncation and settings invariants; window-wide Space commits every numeric editor then toggles, including during title drag; captured window move commits editor, stops caret timer and preserves audio snapshot; rail unchanged and thumb recessed on press; volume drag; numeric input; bounds; layout; incremental logical and retained physical presentation equal full repaint\r\n");
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
            Mixer m=new Mixer(72491);short[] pcm=new short[512];Assert(m.BoundariesZero(),"Clip boundary");
            Mixer distribution=new Mixer(72491);
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
            Mixer continuous=new Mixer(72491);continuous.Config=new Settings(10,10,500,500,100,100,70,30,true,false);
            short[] window=new short[960];long silence=0,maxSilence=0;
            for(int block=0;block<60000;block++) {
                continuous.Render(window,480);
                for(int n=0;n<480;n++) {
                    // Random initial phases intentionally permit startup silence.
                    silence=block>=100 && window[n*2]==0 && window[n*2+1]==0?silence+1:0;
                    maxSilence=Math.Max(maxSilence,silence);
                }
            }
            Assert(maxSilence<Mixer.Rate/50,"Unexpected global PCM silence during 10-minute 20-unit playback");
            Assert(continuous.Shots>23000 && continuous.Shots<25000,"Continuous shot scheduling");
            File.WriteAllText(path,"PASS\r\n8 embedded clips decoded; boundaries zero\r\nNormal timing distribution: 300/50 ms and 200/20 ms; std=0 fixed timing\r\nIdle, 64 units at 100 ms and 50 ms, no PCM clipping, remove units, volley, stop fade, 50 restart cycles\r\n10 s stress rendering: "+stressSeconds.ToString("F3")+" s; peak="+peak+"; shots="+m.Shots+"\r\n600 s virtual playback, both columns 10/500/100: longest exact PCM silence after 1 s warmup="+(maxSilence*1000.0/Mixer.Rate).ToString("F3")+" ms; shots="+continuous.Shots+"\r\n");
        } catch(Exception e) {File.WriteAllText(path,"FAIL\r\n"+e);Environment.ExitCode=1;}
    }
    static void DeviceTest(string path) {
        try {using(AudioEngine e=new AudioEngine()) {e.Mixer.Config=new Settings(32,32,100,100,60,100,0,true,false);Thread.Sleep(2000);long first=e.CompletedFrames;Thread.Sleep(1000);long second=e.CompletedFrames-first;Assert(e.Error==null,e.Error);Assert(e.CompletedFrames>48000,"Insufficient completed buffers: "+e.Diagnostics());Assert(second>=44000 && second<=52000,"Output rate mismatch: frames per second="+second);File.WriteAllText(path,"PASS: WASAPI output with 64 units mixed at zero master volume; "+e.Diagnostics()+"; warm frames per second="+second+"\r\n");}}
        catch(Exception e) {File.WriteAllText(path,"FAIL: "+e);Environment.ExitCode=1;}
    }
    static void NotificationTest(string path) {
        try {
            using(AudioEngine e=new AudioEngine(true)) {
                e.Mixer.Config=new Settings(10,10,500,500,100,100,70,0,true,false);
                Thread.Sleep(2000);long first=Interlocked.Read(ref e.CompletedFrames);Thread.Sleep(1000);
                long rate=Interlocked.Read(ref e.CompletedFrames)-first;
                Assert(e.Error==null,e.Error);Assert(e.FallbackPolls>0,"Notification fault was not exercised");
                Assert(rate>=44000 && rate<=52000,"Lost notifications starved output: "+e.Diagnostics());
                Assert(e.ZeroPaddingWakeups==0,"Audio buffer drained during lost-notification test: "+e.Diagnostics());
                File.WriteAllText(path,"PASS: all device notifications intentionally ignored; both columns 10/500/100, zero master; warm frames per second="+rate+"; "+e.Diagnostics()+"\r\n");
            }
        }catch(Exception e) {File.WriteAllText(path,"FAIL: "+e);Environment.ExitCode=1;}
    }
    static void SoakTest(string path) {
        try {
            using(AudioEngine e=new AudioEngine()) {
                e.Mixer.Config=new Settings(10,10,500,500,100,100,70,0,true,false);
                Stopwatch watch=Stopwatch.StartNew();long previous=0,lastAdvance=0,maxStall=0;
                while(watch.ElapsedMilliseconds<60000) {
                    long frames=Interlocked.Read(ref e.CompletedFrames),now=watch.ElapsedMilliseconds;
                    if(frames!=previous) {maxStall=Math.Max(maxStall,now-lastAdvance);lastAdvance=now;previous=frames;}
                    Assert(e.Error==null,e.Error);Thread.Sleep(10);
                }
                Assert(e.CompletedFrames>=Mixer.Rate*59L,"Sustained playback lost output time: "+e.Diagnostics());
                Assert(e.ZeroPaddingWakeups==0,"Sustained playback underrun: "+e.Diagnostics());
                File.WriteAllText(path,"PASS: 60s real WASAPI, both columns 10/500/100, zero master; max observed frame-progress gap="+maxStall+" ms; "+e.Diagnostics()+"\r\n");
            }
        }catch(Exception e) {File.WriteAllText(path,"FAIL: "+e);Environment.ExitCode=1;}
    }
}
}
