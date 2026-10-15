using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace FlakASMR {
static class PerformanceTests {
    public static void Live(string path) {
        try {
            var queue=new List<double>();var paint=new List<double>();
            using(var audio=new AudioEngine())using(var form=new MainForm(2,true))using(var stop=new ManualResetEvent(false)) {
                audio.Mixer.Config=new Settings(4,10,500,500,50,50,70,0,true,false);
                form.StartPosition=FormStartPosition.Manual;form.Location=new System.Drawing.Point(-10000,-10000);form.ShowInTaskbar=false;
                int pending=0,count=0;
                Thread probe=new Thread(()=> {
                    while(!stop.WaitOne(10)) {
                        if(Interlocked.CompareExchange(ref pending,1,0)!=0)continue;
                        long sent=Stopwatch.GetTimestamp();
                        try {form.BeginInvoke(new Action(()=> {
                            if(!stop.WaitOne(0)) {
                                queue.Add((Stopwatch.GetTimestamp()-sent)*1000.0/Stopwatch.Frequency);
                                paint.Add(form.InteractionProbe(count++));
                            }
                            Interlocked.Exchange(ref pending,0);
                        }));}catch(InvalidOperationException) {break;}
                    }
                }) {IsBackground=true,Name="UI latency probe"};
                using(var timer=new System.Windows.Forms.Timer {Interval=8000}) {
                    timer.Tick+=(s,e)=> {stop.Set();timer.Stop();form.Close();};
                    form.Shown+=(s,e)=> {probe.Start();timer.Start();};
                    Application.Run(form);stop.Set();probe.Join(1000);
                }
                if(audio.Error!=null)throw new Exception(audio.Error);
                if(queue.Count<100)throw new Exception("Insufficient UI probe replies");
                queue.Sort();paint.Sort();
                string report=string.Format(CultureInfo.InvariantCulture,
                    "PASS: 14 units / 500ms on real WASAPI at zero output volume; {0} UI messages.\r\nUI queue p95={1:F3}ms, p99={2:F3}ms, max={3:F3}ms\r\nUI interaction+paint p95={4:F3}ms, p99={5:F3}ms, max={6:F3}ms\r\n{7}\r\n",
                    queue.Count,queue[(int)(queue.Count*.95)],queue[(int)(queue.Count*.99)],queue[queue.Count-1],
                    paint[(int)(paint.Count*.95)],paint[(int)(paint.Count*.99)],paint[paint.Count-1],audio.Diagnostics());
                File.WriteAllText(path,report);
            }
        }catch(Exception e){File.WriteAllText(path,"FAIL: "+e);Environment.ExitCode=1;}
    }
    public static void Regression(string path) {
        Mixer mixer=new Mixer();short[] pcm=new short[960];
        using(var writer=new BinaryWriter(File.Create(path))) {
            for(int block=0;block<1000;block++) {
                if(block==0)mixer.Config=new Settings(4,10,500,500,50,50,70,30,true,false);
                if(block==200)mixer.Config=new Settings(4,10,500,500,50,50,70,30,false,false);
                if(block==300)mixer.Config=new Settings(5,14,180,180,20,20,100,70,true,false);
                if(block==400)mixer.Config=new Settings(2,3,800,800,0,0,0,10,true,false);
                if(block==600)mixer.Config=new Settings(0,0,800,800,0,0,0,10,true,false);
                if(block==800)mixer.Config=new Settings(32,32,50,50,50,50,70,100,true,false);
                if(block==900)mixer.Config=new Settings(32,32,50,50,50,50,70,100,false,false);
                mixer.Render(pcm,480);foreach(short sample in pcm)writer.Write(sample);
            }
        }
    }
    static Settings Configuration(int b,int v,int interval,bool playing) {return new Settings(b,v,interval,interval,50,50,70,30,playing,false);}
    static void Advance(Mixer mixer,int frames,double seconds) {
        short[] pcm=new short[frames*2];int blocks=(int)Math.Ceiling(seconds*Mixer.Rate/frames);
        for(int i=0;i<blocks;i++)mixer.Render(pcm,frames);
    }
    static void Measure(StringBuilder report,string name,Mixer mixer,int frames,double seconds) {
        short[] pcm=new short[frames*2];int blocks=(int)Math.Ceiling(seconds*Mixer.Rate/frames);
        double[] elapsed=new double[blocks];int gc=GC.CollectionCount(0);
        for(int i=0;i<blocks;i++) {
            long start=Stopwatch.GetTimestamp();mixer.Render(pcm,frames);
            elapsed[i]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;
        }
        Array.Sort(elapsed);double sum=0;foreach(double value in elapsed)sum+=value;
        report.AppendFormat(CultureInfo.InvariantCulture,"{0}: frames={1}, realtime_CPU={2:F3}%, mean={3:F4}ms, p95={4:F4}ms, p99={5:F4}ms, max={6:F4}ms, gen0={7}\r\n",
            name,frames,sum/(blocks*frames*1000.0/Mixer.Rate)*100,sum/blocks,
            elapsed[(int)(blocks*.95)],elapsed[(int)(blocks*.99)],elapsed[blocks-1],GC.CollectionCount(0)-gc);
    }
    public static void Run(string path) {
        try {
            var text=new StringBuilder("Mixer benchmark; percentage of one core; virtual playback; includes OS scheduling variation.\r\n");
            foreach(int frames in new[]{480,1440}) {
                Mixer m=new Mixer();m.Config=Configuration(4,10,500,true);Advance(m,frames,3);
                Measure(text,"14 units / 500ms",m,frames,20);
                m=new Mixer();m.Config=Configuration(32,32,50,true);Advance(m,frames,3);
                Measure(text,"64 units / 50ms",m,frames,5);
                m=new Mixer();m.Config=Configuration(32,32,500,true);Advance(m,frames,3);
                m.Config=Configuration(4,10,500,true);Advance(m,frames,18);
                Measure(text,"64 -> 14 units / settled",m,frames,5);
                m.Config=Configuration(4,10,500,false);Advance(m,frames,18);
                Measure(text,"paused / settled",m,frames,5);
            }
            using(var form=new MainForm(2,true)) {
                form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;form.Location=new System.Drawing.Point(-10000,-10000);form.ShowInTaskbar=false;
                form.Show();System.Windows.Forms.Application.DoEvents();text.Append(form.PaintBenchmark());form.Close();
            }
            File.WriteAllText(path,text.ToString());
        }catch(Exception e) {File.WriteAllText(path,"FAIL: "+e);Environment.ExitCode=1;}
    }
}
}
