using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FlakASMR {
// WASAPI shared mode: the device reports exactly how many frames it can accept.
// No per-shot handles, file access, timers for fire timing, or waveOut padding.
sealed class AudioEngine : IDisposable {
    public readonly Mixer Mixer=new Mixer();
    readonly AutoResetEvent signal=new AutoResetEvent(false);
    readonly ManualResetEvent initialized=new ManualResetEvent(false);
    Thread thread; volatile bool exit; bool disposed;
    public volatile string Error;
    public long CompletedFrames;
    public uint BufferFrames;
    public long DeviceLatency;
    public bool MultimediaScheduling;
    public long BufferCount,ZeroPaddingWakeups,WaitTimeouts;
    long maxRenderTicks,maxServiceGapTicks;
    public AudioEngine() {
        thread=new Thread(Pump) {IsBackground=true,Name="Flak WASAPI",Priority=ThreadPriority.AboveNormal};
        thread.SetApartmentState(ApartmentState.MTA);thread.Start();
        if(!initialized.WaitOne(10000)) {Dispose();throw new InvalidOperationException("音频设备初始化超时。");}
        if(Error!=null) {string e=Error;Dispose();throw new InvalidOperationException(e);}
    }
    static void Check(int hr) {if(hr<0) Marshal.ThrowExceptionForHR(hr);}
    static void Release(object value) {if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);}
    void Pump() {
        object enumeratorObject=null,deviceObject=null,clientObject=null,renderObject=null;
        IAudioClient client=null;IntPtr format=IntPtr.Zero;
        IntPtr multimedia=IntPtr.Zero;
        try {
            uint task=0;multimedia=AvSetMmThreadCharacteristics("Audio",ref task);
            MultimediaScheduling=multimedia!=IntPtr.Zero;
            if(MultimediaScheduling)AvSetMmThreadPriority(multimedia,1);
            enumeratorObject=new MMDeviceEnumerator();
            IMMDevice device; Check(((IMMDeviceEnumerator)enumeratorObject).GetDefaultAudioEndpoint(0,1,out device));deviceObject=device;
            Guid clientId=typeof(IAudioClient).GUID;Check(device.Activate(ref clientId,23,IntPtr.Zero,out clientObject));client=(IAudioClient)clientObject;
            var f=new WaveFormat {Tag=1,Channels=2,Rate=Mixer.Rate,ByteRate=Mixer.Rate*4,Align=4,Bits=16};
            format=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormat)));Marshal.StructureToPtr(f,format,false);
            // Event callback, automatic PCM conversion and high-quality resampling.
            Check(client.Initialize(0,0x88040000,300000,0,format,IntPtr.Zero));
            Check(client.GetBufferSize(out BufferFrames));Check(client.GetStreamLatency(out DeviceLatency));
            Check(client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()));
            Guid renderId=typeof(IAudioRenderClient).GUID;Check(client.GetService(ref renderId,out renderObject));
            var render=(IAudioRenderClient)renderObject;
            short[] pcm=new short[checked((int)BufferFrames*2)];
            Mixer.Reserve((int)BufferFrames);
            IntPtr ptr;Check(render.GetBuffer(BufferFrames,out ptr));Check(render.ReleaseBuffer(BufferFrames,2)); // prefill silence
            Check(client.Start());initialized.Set();
            long previousService=Stopwatch.GetTimestamp();
            while(!exit) {
                if(!signal.WaitOne(100))WaitTimeouts++;
                if(exit) break;
                uint padding;Check(client.GetCurrentPadding(out padding));
                uint frames=BufferFrames-padding;
                if(frames==0) continue;
                long started=Stopwatch.GetTimestamp(),gap=started-previousService;previousService=started;
                if(gap>maxServiceGapTicks)maxServiceGapTicks=gap;
                if(padding==0 && CompletedFrames>Mixer.Rate/2)ZeroPaddingWakeups++;
                Mixer.Render(pcm,(int)frames);
                long renderTicks=Stopwatch.GetTimestamp()-started;
                if(renderTicks>maxRenderTicks)maxRenderTicks=renderTicks;
                Check(render.GetBuffer(frames,out ptr));
                Marshal.Copy(pcm,0,ptr,(int)frames*2);
                Check(render.ReleaseBuffer(frames,0));
                Interlocked.Add(ref CompletedFrames,frames);
                BufferCount++;
            }
        } catch(Exception e) {Error="音频输出错误："+e.Message;}
        finally {
            initialized.Set();
            if(client!=null) {client.Stop();client.Reset();}
            Release(renderObject);Release(clientObject);Release(deviceObject);Release(enumeratorObject);
            if(format!=IntPtr.Zero)Marshal.FreeHGlobal(format);
            if(multimedia!=IntPtr.Zero)AvRevertMmThreadCharacteristics(multimedia);
        }
    }
    public string Diagnostics() {
        return "frames="+CompletedFrames+"; buffer="+BufferFrames+" frames ("+(BufferFrames*1000.0/Mixer.Rate).ToString("F1")+" ms); reported latency="+(DeviceLatency/10000.0).ToString("F1")+" ms; MMCSS="+MultimediaScheduling+
            "; buffers="+BufferCount+"; max render="+(Interlocked.Read(ref maxRenderTicks)*1000.0/Stopwatch.Frequency).ToString("F3")+
            " ms; max service gap="+(Interlocked.Read(ref maxServiceGapTicks)*1000.0/Stopwatch.Frequency).ToString("F3")+
            " ms; empty-buffer wakes="+ZeroPaddingWakeups+"; wait timeouts="+WaitTimeouts+"; error="+Error;
    }
    [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr AvSetMmThreadCharacteristics(string taskName,ref uint taskIndex);
    [DllImport("avrt.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AvSetMmThreadPriority(IntPtr handle,int priority);
    [DllImport("avrt.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    public void Dispose() {
        if(disposed)return;disposed=true;
        Settings c=Mixer.Config;
        Mixer.Config=new Settings(c.B,c.V,c.IntervalB,c.IntervalV,c.StdB,c.StdV,c.Width,c.Volume,false,c.Volley);
        if(thread!=null && thread.IsAlive) {
            Thread.Sleep(180);exit=true;signal.Set();
            // Handles and COM objects stay alive until the audio thread releases them.
            thread.Join();
        }
        signal.Dispose();initialized.Dispose();
    }
    [StructLayout(LayoutKind.Sequential,Pack=2)] struct WaveFormat {public ushort Tag,Channels;public uint Rate,ByteRate;public ushort Align,Bits,Extra;}
    [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator {}
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice {
        [PreserveSig] int Activate(ref Guid id,uint context,IntPtr activation,[MarshalAs(UnmanagedType.IUnknown)]out object value);
        [PreserveSig] int OpenPropertyStore(uint access,out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)]out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioClient {
        [PreserveSig] int Initialize(int share,uint flags,long duration,long periodicity,IntPtr format,IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int share,IntPtr format,out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal,out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid id,[MarshalAs(UnmanagedType.IUnknown)]out object value);
    }
    [ComImport,Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioRenderClient {
        [PreserveSig] int GetBuffer(uint frames,out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames,uint flags);
    }
}
}
