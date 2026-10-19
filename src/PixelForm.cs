using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FlakASMR {
// Render at the game's pixel resolution. Only the finished canvas is enlarged.
// Neither the Windows font rasterizer nor OS button theming alters the artwork.
sealed class PixelFont : IDisposable {
    sealed class Glyph { public int Width; public byte[] Pixels; }
    readonly Dictionary<char,Glyph> glyphs=new Dictionary<char,Glyph>();
    readonly Dictionary<long,Bitmap> images=new Dictionary<long,Bitmap>();
    public PixelFont() {
        using(Stream s=Resources.Open("westwood.bin"))using(BinaryReader r=new BinaryReader(s)) {
            if(Encoding.ASCII.GetString(r.ReadBytes(4))!="WWF1")throw new InvalidDataException("字体资源损坏");
            int count=r.ReadInt32();
            for(int i=0;i<count;i++) {
                char c=(char)r.ReadInt32();int width=r.ReadInt32();
                glyphs[c]=new Glyph {Width=width,Pixels=r.ReadBytes(width*16)};
            }
        }
        // Warm every editable glyph before the window is shown, not on a drag event.
        using(Bitmap warm=new Bitmap(96,18))using(Graphics g=Graphics.FromImage(warm)) {
            Draw(g,"0123456789%",0,0,Color.Yellow);
            Draw(g,"0123456789%",0,0,Color.FromArgb(255,224,115));
        }
    }
    public int Width(string text) {int n=0;foreach(char c in text) {Glyph g;if(glyphs.TryGetValue(c,out g))n+=g.Width+1;}return n;}
    public void Draw(Graphics g,string text,int x,int y,Color color) {
        foreach(char c in text) {
            Glyph glyph;if(!glyphs.TryGetValue(c,out glyph))continue;
            long key=((long)color.ToArgb()<<16)|(ushort)c;Bitmap image;
            if(!images.TryGetValue(key,out image)) {
                image=new Bitmap(glyph.Width+1,17,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                for(int py=0;py<16;py++)for(int px=0;px<glyph.Width;px++)
                    if(glyph.Pixels[py*glyph.Width+px]!=0)image.SetPixel(px+1,py+1,Color.Black);
                for(int py=0;py<16;py++)for(int px=0;px<glyph.Width;px++)
                    if(glyph.Pixels[py*glyph.Width+px]!=0)image.SetPixel(px,py,color);
                images.Add(key,image);
            }
            g.DrawImageUnscaled(image,x,y);x+=glyph.Width+1;
        }
    }
    public void Dispose() {foreach(Bitmap image in images.Values)image.Dispose();images.Clear();}
}

static class Resources {
    public static Stream Open(string name) {
        Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if(s==null)throw new InvalidDataException("缺少内置资源："+name);return s;
    }
    public static Bitmap Image(string name) {using(Stream s=Open(name))using(Bitmap b=new Bitmap(s))return new Bitmap(b);}
}

sealed class MainForm : Form {
    const int LogicalWidth=300,LogicalHeight=236;
    static readonly Color Gold=Color.FromArgb(255,224,115),Yellow=Color.Yellow;
    static readonly Rectangle VolumeRect=new Rectangle(60,208,182,16);
    static readonly int[] CaptionX={202,226,250,274};
    static readonly int[] EditOrder={0,1,2,3,4,5,7,6};
    readonly int uiScale;
    readonly bool previewOnly;
    bool diagnosticMute;
    Bitmap probeTarget;
    Bitmap presentation;
    Rectangle damage;
    bool windowDrag;
    Point windowGrabOffset,windowDragOrigin;
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    readonly int[] values={2,500,50,3,500,50,0,0};
    readonly Bitmap panel=Resources.Image("panel.png"),buttons=Resources.Image("buttons.png"),
        rail=Resources.Image("rail.png"),thumb=Resources.Image("thumb.png");
    readonly Bitmap canvas=new Bitmap(LogicalWidth,LogicalHeight);
    readonly PixelFont font=new PixelFont();
    readonly int[] paintedButtons=new int[18],paintedValues=new int[8];
    int paintedEditing=-2,paintedVolume=-1,paintedVolumeState=-1;
    bool canvasReady;
    readonly ToolTip tip=new ToolTip {InitialDelay=450,ReshowDelay=100,AutoPopDelay=6000};
    readonly Timer repeat=new Timer(),monitor=new Timer {Interval=250},caretTimer=new Timer {Interval=500};
    AudioEngine engine;
    bool ready,playing,closing,fault,volumeDrag,caretVisible=true;
    int volume=30,hover=-1,pressed=-1,editing=-1,caret,anchor;
    float dragAnchor;
    string editText="",lastTip="";

    public MainForm() : this(0,false) {}
    internal MainForm(int scale,bool preview) {
        previewOnly=preview;
        for(int i=0;i<paintedButtons.Length;i++)paintedButtons[i]=-1;
        for(int i=0;i<paintedValues.Length;i++)paintedValues[i]=-1;
        using(Graphics desktop=Graphics.FromHwnd(IntPtr.Zero))uiScale=scale>0?scale:Math.Max(2,(int)Math.Ceiling(desktop.DpiX/96f));
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;
        ClientSize=new Size(LogicalWidth*uiScale,LogicalHeight*uiScale);
        presentation=new Bitmap(ClientSize.Width,ClientSize.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        MinimumSize=Size;MaximumSize=Size;StartPosition=FormStartPosition.CenterScreen;
        Text="RA2 Flak ASMR";BackColor=Color.FromArgb(9,6,6);ForeColor=Yellow;
        AccessibleName=Text;AccessibleDescription="左侧与右侧分别调整单位数量、平均开火间隔和标准差；油井数量控制进账音；底部调整音量。空格播放或暂停，Tab 输入数值。";
        DoubleBuffered=true;KeyPreview=true;MaximizeBox=false;
        SetStyle(ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint,true);
        using(Stream s=Resources.Open("flak.ico"))Icon=new Icon(s);
        repeat.Tick+=(s,e)=> {
            repeat.Interval=65;
            if(pressed>=0 && pressed<14 && hover==pressed)Adjust(StepField(pressed),(pressed%2==1)?1:-1);
        };
        caretTimer.Tick+=(s,e)=> {if(editing>=0) {caretVisible=!caretVisible;Invalidate();}};
        monitor.Tick+=(s,e)=> {
            if(engine!=null && engine.Error!=null && !fault) {
                fault=true;playing=false;Publish();
                MessageBox.Show(this,engine.Error+"\n请重新打开程序。",Text,MessageBoxButtons.OK,MessageBoxIcon.Error);
            }
        };
        Shown+=(s,e)=> {
            if(previewOnly) {ready=true;Invalidate();return;}
            try {engine=new AudioEngine();ready=true;Publish();monitor.Start();}
            catch(Exception ex) {fault=true;Invalidate();MessageBox.Show(this,ex.Message,"音频设备未就绪",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        };
        FormClosing+=(s,e)=> {closing=true;repeat.Stop();monitor.Stop();caretTimer.Stop();if(engine!=null)engine.Dispose();};
        FormClosed+=(s,e)=> {panel.Dispose();buttons.Dispose();rail.Dispose();thumb.Dispose();canvas.Dispose();presentation.Dispose();if(probeTarget!=null)probeTarget.Dispose();font.Dispose();tip.Dispose();repeat.Dispose();monitor.Dispose();caretTimer.Dispose();};
    }

    static int StepField(int id) {return id<12?id/2:7;}
    static int StepId(int field) {return field==7?12:field*2;}
    static int Row(int field) {return field%3;}
    static Rectangle ValueRect(int field) {return field==6?new Rectangle(246,208,44,16):field==7?new Rectangle(210,180,32,20):new Rectangle(field<3?60:210,96+Row(field)*28,32,20);}
    static Rectangle ButtonRect(int field,int direction) {return new Rectangle((field<3?60:210)+(direction>0?60:36),field==7?180:96+Row(field)*28,20,20);}
    static Rectangle CaptionRect(int index) {return new Rectangle(CaptionX[index],7,20,20);}
    static bool Contains(Rectangle r,PointF p) {return p.X>=r.Left && p.X<r.Right && p.Y>=r.Top && p.Y<r.Bottom;}
    PointF Logical(Point p) {return new PointF(p.X/(float)uiScale,p.Y/(float)uiScale);}
    int Hit(PointF p) {
        for(int i=0;i<4;i++)if(Contains(CaptionRect(i),p))return 100+i;
        for(int field=0;field<8;field++) {
            if(field==6)continue;
            if(Contains(ValueRect(field),p))return 300+field;
            if(Contains(ButtonRect(field,-1),p))return StepId(field);
            if(Contains(ButtonRect(field,1),p))return StepId(field)+1;
        }
        if(Contains(VolumeRect,p))return 200;
        if(Contains(ValueRect(6),p))return 306;
        return -1;
    }
    void TextAt(Graphics g,string text,Rectangle r,bool right,bool centered,Color color) {
        int x=centered?(int)Math.Round(r.X+(r.Width-font.Width(text))/2.0):right?r.Right-font.Width(text):r.X;
        int y=(int)Math.Round(r.Y+(r.Height-16)/2.0);
        font.Draw(g,text,x,y,color);
    }
    int ButtonState(int id,bool selected) {return selected || pressed==id?2:hover==id?1:0;}
    void PaintButton(Graphics g,int kind,Rectangle target,int id,bool selected) {
        int state=ButtonState(id,selected)+(selected?3:0),index=id>=100?id-100+14:id;
        if(paintedButtons[index]==state)return;
        g.DrawImage(buttons,target,new Rectangle(kind*20,state*20,20,20),GraphicsUnit.Pixel);
        Damage(target);
        paintedButtons[index]=state;
    }
    void Damage(Rectangle region) {damage=damage.IsEmpty?region:Rectangle.Union(damage,region);}
    void Restore(Graphics g,Rectangle region) {g.DrawImage(panel,region,region,GraphicsUnit.Pixel);Damage(region);}
    Bitmap RenderCanvas() {
        using(Graphics g=Graphics.FromImage(canvas)) {
            g.SmoothingMode=SmoothingMode.None;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;
            if(!canvasReady) {g.DrawImageUnscaled(panel,0,0);canvasReady=true;Damage(new Rectangle(0,0,LogicalWidth,LogicalHeight));}
            for(int i=0;i<4;i++)PaintButton(g,i+2,CaptionRect(i),100+i,(i==0 && playing)||(i==1 && !playing));
            for(int field=0;field<8;field++) {
                if(field==6)continue;
                Rectangle r=ValueRect(field);
                if(paintedValues[field]!=values[field] || editing==field || paintedEditing==field) {
                    Restore(g,r);
                    if(editing!=field) {
                        string text=values[field].ToString(CultureInfo.InvariantCulture);
                        Rectangle area=font.Width(text)>26?new Rectangle(r.X+1,r.Y,30,20):new Rectangle(r.X+3,r.Y,26,20);
                        TextAt(g,text,area,true,false,Yellow);
                    }
                    paintedValues[field]=values[field];
                }
                PaintButton(g,1,ButtonRect(field,-1),StepId(field),false);
                PaintButton(g,0,ButtonRect(field,1),StepId(field)+1,false);
            }
            int state=volumeDrag?2:hover==200?1:0;
            if(paintedVolume!=volume || paintedVolumeState!=state) {
                g.DrawImage(rail,VolumeRect,new Rectangle(0,0,182,16),GraphicsUnit.Pixel);
                Damage(VolumeRect);
                int tx=62+(int)Math.Round(170*volume/100.0);
                g.DrawImage(thumb,new Rectangle(tx,209,8,14),new Rectangle(0,state*14,8,14),GraphicsUnit.Pixel);
                paintedVolume=volume;paintedVolumeState=state;
            }
            if(paintedValues[6]!=volume || editing==6 || paintedEditing==6) {
                Restore(g,ValueRect(6));
                if(editing!=6)TextAt(g,volume+"%",new Rectangle(247,208,42,16),false,true,Yellow);
                paintedValues[6]=volume;
            }
            if(editing>=0)PaintEditor(g);
            paintedEditing=editing;
        }
        return canvas;
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        RenderCanvas();
        if(!damage.IsEmpty) {
            using(Graphics g=Graphics.FromImage(presentation)) {
                g.SmoothingMode=SmoothingMode.None;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;
                Rectangle target=new Rectangle(damage.X*uiScale,damage.Y*uiScale,damage.Width*uiScale,damage.Height*uiScale);
                g.DrawImage(canvas,target,damage,GraphicsUnit.Pixel);
            }
            damage=Rectangle.Empty;
        }
        e.Graphics.DrawImageUnscaled(presentation,0,0);
    }
    void Publish() {
        if(engine!=null)engine.Mixer.Config=new Settings(values[3],values[0],values[4],values[1],values[5],values[2],70,diagnosticMute?0:volume,playing,false,values[7]);
        Invalidate();
    }
    Settings CurrentSettings {get {return engine!=null?engine.Mixer.Config:new Settings(values[3],values[0],values[4],values[1],values[5],values[2],70,volume,playing,false,values[7]);}}
    void Play(bool state) {CommitEdit();if(!ready || fault)return;playing=state;Publish();}
    void Adjust(int field,int direction) {
        int step=field==6 || field==7?1:Row(field)==0?1:Row(field)==1?10:5;
        SetValue(field,(field==6?volume:values[field])+direction*step);
    }
    void SetValue(int field,int value) {
        if(field==6) {int next=Math.Max(0,Math.Min(100,value));if(volume==next)return;volume=next;}
        else {
            int row=field==7?0:Row(field),min=row==1?50:0,max=row==0?32:row==1?10000:Math.Min(5000,values[field-1]);
            int next=Math.Max(min,Math.Min(max,value));if(values[field]==next)return;values[field]=next;
            if(row==1)values[field+1]=Math.Min(values[field+1],next);
        }
        Publish();
    }
    void BeginEdit(int field) {
        CommitEdit();editing=field;editText=(field==6?volume:values[field]).ToString(CultureInfo.InvariantCulture);
        anchor=0;caret=editText.Length;caretVisible=true;caretTimer.Start();Invalidate();
    }
    void EndEdit() {editing=-1;caretTimer.Stop();Invalidate();}
    void CommitEdit() {
        if(editing<0)return;int field=editing,value;string text=editText;
        EndEdit();if(int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out value))SetValue(field,value);
    }
    void PaintEditor(Graphics g) {
        Rectangle box=ValueRect(editing);Rectangle area=new Rectangle(box.X+1,box.Y+1,box.Width-2,box.Height-2);
        int x=Math.Max(box.X+1,box.Right-2-font.Width(editText)),y=(int)Math.Round(box.Y+(box.Height-16)/2.0);
        var saved=g.Save();g.SetClip(area);
        int start=Math.Min(caret,anchor),end=Math.Max(caret,anchor);
        int sx=x+font.Width(editText.Substring(0,start)),ex=x+font.Width(editText.Substring(0,end));
        if(start!=end)using(Brush selection=new SolidBrush(Color.FromArgb(78,30,16)))g.FillRectangle(selection,sx,y+2,ex-sx,13);
        font.Draw(g,editText,x,y,Gold);
        if(caretVisible)using(Brush line=new SolidBrush(Gold))g.FillRectangle(line,x+font.Width(editText.Substring(0,caret)),y+2,1,13);
        g.Restore(saved);
        using(Brush focus=new SolidBrush(Gold)) {
            g.FillRectangle(focus,box.Left,box.Top,box.Width,1);
            g.FillRectangle(focus,box.Left,box.Bottom-1,box.Width,1);
            g.FillRectangle(focus,box.Left,box.Top,1,box.Height);
            g.FillRectangle(focus,box.Right-1,box.Top,1,box.Height);
        }
    }
    void ReplaceSelection(string replacement) {
        int first=Math.Min(caret,anchor),last=Math.Max(caret,anchor);
        string next=editText.Substring(0,first)+replacement+editText.Substring(last);
        if(next.Length>5)return;
        editText=next;caret=anchor=first+replacement.Length;caretVisible=true;Invalidate();
    }
    void SetVolume(PointF p) {SetValue(6,(int)Math.Round((p.X-62-dragAnchor)*100/170.0));}
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left)return;
        Focus();PointF p=Logical(e.Location);CommitEdit();int hit=Hit(p);hover=hit;
        if(hit<0 && Contains(new Rectangle(4,4,292,26),p)) {
            tip.Hide(this);windowGrabOffset=e.Location;windowDragOrigin=Location;windowDrag=true;Capture=true;return;
        }
        if(hit>=300 && hit<=307) {BeginEdit(hit-300);return;}
        if(hit==200) {
            tip.Hide(this);
            int tx=62+(int)Math.Round(170*volume/100.0);
            dragAnchor=p.X>=tx && p.X<tx+8?p.X-tx:4;
            volumeDrag=true;pressed=hit;Capture=true;SetVolume(p);return;
        }
        if((hit>=0 && hit<14)||(hit>=100 && hit<104)) {
            pressed=hit;Capture=true;
            if(hit<14) {Adjust(StepField(hit),hit%2==1?1:-1);repeat.Interval=350;repeat.Start();}
            Invalidate();
        }
    }
    void ReleasePress() {repeat.Stop();pressed=-1;volumeDrag=windowDrag=false;Capture=false;Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);if(e.Button!=MouseButtons.Left)return;
        int action=pressed,target=Hit(Logical(e.Location));ReleasePress();
        if(action!=target)return;
        if(action==100)Play(true);else if(action==101)Play(false);
        else if(action==102)WindowState=FormWindowState.Minimized;else if(action==103)Close();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) {
        base.OnMouseCaptureChanged(e);
        if(!Capture) {repeat.Stop();pressed=-1;volumeDrag=windowDrag=false;Invalidate();}
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);PointF p=Logical(e.Location);
        if(windowDrag) {
            Point screen=PointToScreen(e.Location);
            // Keep the ordinary UI message pump running; no native modal move loop,
            // no repaint or audio publication per mouse move. WM_MOUSEMOVE coalesces.
            SetWindowPos(Handle,IntPtr.Zero,screen.X-windowGrabOffset.X,screen.Y-windowGrabOffset.Y,0,0,0x415);
            return;
        }
        if(volumeDrag) {SetVolume(p);return;}
        int next=Hit(p);if(next==hover)return;
        string help="";
        if(next>=100 && next<104)help=new[]{"播放 · 空格播放/暂停","暂停 · 平滑淡出","最小化","关闭 · 平滑淡出"}[next-100];
        else if(next>=300 && next<=307) {
            int field=next-300;
            help=field==7?"油井数量 0–32；新增油井奖励 1000，每 100 逻辑帧进账 20（60 帧/秒）；共用进账计数器":field==6?"音量 0–100% · 点击输入":(field<3?"左侧单位组 · ":"右侧单位组 · ")+new[]{"单位数量 0–32","平均间隔 50–10000 ms；降低时自动截断标准差","标准差不超过均值及 5000 ms；0 为固定节奏"}[Row(field)]+" · 点击输入";
        }
        else if(next>=0 && next<14)help="按住连续调整 · 滚轮调整";
        else if(next==200)help="音量 "+volume+"% · 拖动或滚轮调整";
        else if(Contains(new Rectangle(4,4,292,26),p))help="拖动窗口 · F1 字体信息";
        if(next!=hover) {hover=next;Invalidate();}
        if(help!=lastTip) {lastTip=help;tip.SetToolTip(this,help);}
        Cursor=next>=300?Cursors.IBeam:next>=0?Cursors.Hand:Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) {base.OnMouseLeave(e);hover=-1;Invalidate();}
    protected override void OnMouseWheel(MouseEventArgs e) {
        base.OnMouseWheel(e);PointF p=Logical(e.Location);int direction=Math.Sign(e.Delta);if(direction==0)return;
        CommitEdit();
        for(int field=0;field<8;field++)if(field!=6 && Contains(new Rectangle(ValueRect(field).X,ValueRect(field).Y,80,20),p)) {Adjust(field,direction);return;}
        if(Contains(new Rectangle(60,204,230,24),p))SetValue(6,volume+direction*5);
    }
    protected override void OnKeyPress(KeyPressEventArgs e) {
        if(editing>=0 && e.KeyChar>='0' && e.KeyChar<='9') {ReplaceSelection(e.KeyChar.ToString());e.Handled=true;}
        else base.OnKeyPress(e);
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys key) {
        if(key==Keys.Space) {CommitEdit();Play(!playing);return true;}
        if(key==Keys.Escape && windowDrag) {Location=windowDragOrigin;ReleasePress();return true;}
        if(editing>=0) {
            if(key==Keys.Enter) {CommitEdit();return true;}
            if(key==Keys.Escape) {EndEdit();return true;}
            if(key==Keys.Tab || key==(Keys.Shift|Keys.Tab)) {int next=(Array.IndexOf(EditOrder,editing)+(key==Keys.Tab?1:7))%8;CommitEdit();BeginEdit(EditOrder[next]);return true;}
            if(key==(Keys.Control|Keys.A)) {anchor=0;caret=editText.Length;Invalidate();return true;}
            if(key==(Keys.Control|Keys.C)) {if(anchor!=caret)Clipboard.SetText(editText.Substring(Math.Min(caret,anchor),Math.Abs(caret-anchor)));return true;}
            if(key==(Keys.Control|Keys.V)) {
                string text=Clipboard.ContainsText()?Clipboard.GetText().Trim():"";int parsed;
                if(text.Length>0 && int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out parsed))ReplaceSelection(text);
                return true;
            }
            if(key==Keys.Back || key==Keys.Delete) {
                if(anchor==caret) {if(key==Keys.Back && caret>0)anchor=caret-1;else if(key==Keys.Delete && caret<editText.Length)anchor=caret+1;}
                ReplaceSelection("");return true;
            }
            Keys code=key&Keys.KeyCode;
            if(code==Keys.Left || code==Keys.Right || code==Keys.Home || code==Keys.End) {
                caret=code==Keys.Home?0:code==Keys.End?editText.Length:Math.Max(0,Math.Min(editText.Length,caret+(code==Keys.Left?-1:1)));
                if((key&Keys.Shift)==0)anchor=caret;caretVisible=true;Invalidate();return true;
            }
            if(key==Keys.Up || key==Keys.Down) {
                int field=editing;CommitEdit();Adjust(field,key==Keys.Up?1:-1);BeginEdit(field);return true;
            }
        } else {
            if(key==Keys.Tab || key==(Keys.Shift|Keys.Tab)) {BeginEdit(key==Keys.Tab?0:6);return true;}
            if(key==Keys.Escape) {ReleasePress();return true;}
        }
        if(key==(Keys.Control|Keys.Up) || key==(Keys.Control|Keys.Down)) {CommitEdit();SetValue(6,volume+(key==(Keys.Control|Keys.Up)?5:-5));return true;}
        if(key==Keys.F1) {
            using(Stream s=Resources.Open("FONT-LICENSE.txt"))using(StreamReader r=new StreamReader(s))MessageBox.Show(this,r.ReadToEnd(),"Westwood Improved Font",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return true;
        }
        return base.ProcessCmdKey(ref msg,key);
    }
    protected override void OnDeactivate(EventArgs e) {base.OnDeactivate(e);if(!closing) {CommitEdit();ReleasePress();}}
    protected override void WndProc(ref Message m) {
        if(m.Msg==0xA3)return;
        base.WndProc(ref m);
    }

    static void Require(bool value,string name) {if(!value)throw new Exception(name);}
    Point PhysicalPoint(int x,int y) {return new Point(x*uiScale+uiScale/2,y*uiScale+uiScale/2);}
    void MouseClickAt(int x,int y) {
        Point p=PhysicalPoint(x,y);var e=new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0);OnMouseDown(e);OnMouseUp(e);
    }
    void RequireFullRepaintMatches() {
        using(Bitmap displayBefore=new Bitmap(ClientSize.Width,ClientSize.Height))
        using(Bitmap displayAfter=new Bitmap(ClientSize.Width,ClientSize.Height))
        using(Bitmap incremental=new Bitmap(RenderCanvas())) {
            using(Graphics g=Graphics.FromImage(displayBefore))OnPaint(new PaintEventArgs(g,ClientRectangle));
            canvasReady=false;paintedEditing=-2;paintedVolume=paintedVolumeState=-1;
            for(int i=0;i<paintedButtons.Length;i++)paintedButtons[i]=-1;
            for(int i=0;i<paintedValues.Length;i++)paintedValues[i]=-1;
            Bitmap complete=RenderCanvas();
            for(int y=0;y<LogicalHeight;y++)for(int x=0;x<LogicalWidth;x++)
                if(incremental.GetPixel(x,y).ToArgb()!=complete.GetPixel(x,y).ToArgb())
                    throw new Exception("Incremental repaint mismatch at "+x+","+y);
            using(Graphics g=Graphics.FromImage(displayAfter))OnPaint(new PaintEventArgs(g,ClientRectangle));
            for(int y=0;y<displayBefore.Height;y++)for(int x=0;x<displayBefore.Width;x++)
                Require(displayBefore.GetPixel(x,y)==displayAfter.GetPixel(x,y),"Retained presentation equals full repaint");
        }
    }
    internal void ControlTest() {
        Require(ready && (engine!=null || previewOnly),"Audio engine or headless preview ready");
        diagnosticMute=true;
        Settings defaults=CurrentSettings;
        Require(defaults.IntervalV==500 && defaults.IntervalB==500 && defaults.StdV==50 && defaults.StdB==50,"500/50 defaults");
        SetValue(6,0);SetValue(0,7);SetValue(3,11);SetValue(1,430);SetValue(4,250);SetValue(2,55);SetValue(5,25);
        Settings c=CurrentSettings;
        Require(c.V==7 && c.B==11 && c.IntervalV==430 && c.IntervalB==250 && c.StdV==55 && c.StdB==25,"Left/right independent mapping");
        MouseClickAt(210,16);Require(playing && CurrentSettings.Playing && ButtonState(100,true)==2,"Play hit and selection");
        MouseClickAt(234,16);Require(!playing && !CurrentSettings.Playing && ButtonState(101,true)==2,"Pause hit and selection");
        Require(Hit(new PointF(224,16))==-1,"Caption gap does not activate buttons");
        int old=values[0];MouseClickAt(125,104);Require(values[0]==old+1,"Plus hit");
        MouseClickAt(105,104);Require(values[0]==old,"Minus hit");
        Require(Hit(new PointF(94,104))==-1 && Hit(new PointF(118,104))==-1,"Stepper gaps");
        Point start=PhysicalPoint(66,214),end=PhysicalPoint(236,214);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,start.X,start.Y,0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,end.X,end.Y,0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,end.X,end.Y,0));Require(volume==100,"Horizontal volume drag");
        dragAnchor=4;SetVolume(new PointF(66,214));Require(volume==0,"Volume lower endpoint");
        MouseClickAt(70,104);Require(editing==0,"Value click opens editor");
        OnKeyPress(new KeyPressEventArgs('9'));Message msg=new Message();ProcessCmdKey(ref msg,Keys.Enter);Require(values[0]==9,"Typed field commit");
        BeginEdit(0);OnKeyPress(new KeyPressEventArgs('1'));ProcessCmdKey(ref msg,Keys.Escape);Require(values[0]==9,"Escape cancels edit");
        MouseClickAt(268,216);Require(editing==6,"Volume numeric editor hit");
        OnKeyPress(new KeyPressEventArgs('4'));OnKeyPress(new KeyPressEventArgs('2'));ProcessCmdKey(ref msg,Keys.Enter);Require(volume==42,"Typed volume");
        SetValue(6,0);SetValue(3,100);SetValue(1,0);Require(values[3]==32 && values[1]==50,"Value bounds");
        foreach(int mean in new[]{1,4}) {
            SetValue(mean,500);SetValue(mean+1,5000);Require(values[mean+1]==500,"Std capped at mean");
            SetValue(mean,130);Require(values[mean+1]==130,"Lowering mean truncates std atomically");
            SetValue(mean,700);Require(values[mean+1]==130,"Raising mean preserves std");
            BeginEdit(mean+1);editText="9999";CommitEdit();Require(values[mean+1]==700,"Typed std capped");
            BeginEdit(mean);editText="50";CommitEdit();Require(values[mean+1]==50,"Typed mean truncates std");
        }
        SetValue(7,99);Require(CurrentSettings.OilWells==32,"Oil count limit");MouseClickAt(255,188);Require(CurrentSettings.OilWells==31,"Oil minus");MouseClickAt(280,188);Require(CurrentSettings.OilWells==32,"Oil plus");
        BeginEdit(7);editText="4";CommitEdit();Require(CurrentSettings.OilWells==4,"Oil numeric input");RequireFullRepaintMatches();
        BeginEdit(5);ProcessCmdKey(ref msg,Keys.Tab);Require(editing==7,"Tab enters oil before volume");
        ProcessCmdKey(ref msg,Keys.Tab);Require(editing==6,"Tab enters volume last");ProcessCmdKey(ref msg,Keys.Shift|Keys.Tab);Require(editing==7,"Reverse tab enters oil");CommitEdit();
        Settings capped=new Settings(1,1,100,200,500,-10,70,0,false,false);
        Require(capped.StdB==100 && capped.StdV==0,"Immutable settings defend explicit std limits");
        capped=new Settings(1,1,100,200,1000,70,0,false,false);
        Require(capped.StdB==100 && capped.StdV==200,"Legacy settings defend std limits");
        for(int field=0;field<8;field++) {
            bool wasPlaying=playing;BeginEdit(field);editText=field==6?"42":Row(field)==0?"9":"100";
            Require(ProcessCmdKey(ref msg,Keys.Space) && editing<0 && playing!=wasPlaying,"Space commits and toggles every numeric editor");
        }
        MouseClickAt(30,60);bool beforeSpace=playing;ProcessCmdKey(ref msg,Keys.Space);Require(playing!=beforeSpace,"Space after image focus");
        Point original=Location,grab=PhysicalPoint(50,15);
        BeginEdit(1);editText="250";
        OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,grab.X,grab.Y,0));
        Require(windowDrag && Capture && editing<0 && !caretTimer.Enabled && values[1]==250,"Title drag commits editor and stops caret timer");
        Settings beforeMove=CurrentSettings;
        OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,grab.X+24,grab.Y+12,0));
        Require(Location==new Point(original.X+24,original.Y+12),"Captured window moves by physical mouse displacement");
        Require(engine==null || object.ReferenceEquals(beforeMove,CurrentSettings),"Captured window move keeps audio snapshot");
        beforeSpace=playing;ProcessCmdKey(ref msg,Keys.Space);Require(playing!=beforeSpace,"Space while captured title drag");
        OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,grab.X,grab.Y,0));Require(!windowDrag && !Capture,"Title drag releases capture");
        Location=original;
        for(int y=0;y<16;y++)for(int x=0;x<182;x++)Require(rail.GetPixel(x,y)==rail.GetPixel(x,y+32),"Volume rail remains unchanged when pressed");
        Require(thumb.GetPixel(0,0)!=thumb.GetPixel(0,28),"Volume thumb retains recessed edge");
        for(int i=0;i<4;i++)Require(CaptionRect(i).Size==new Size(20,20),"20px caption controls");
        Require(ValueRect(6).Left==ButtonRect(3,-1).Left && ValueRect(6).Right==ButtonRect(3,1).Right,"Aligned readout edges");
        hover=0;Require(ButtonState(0,false)==1,"Hover state");pressed=0;Require(ButtonState(0,false)==2,"Pressed state");ReleasePress();
        hover=0;RequireFullRepaintMatches();
        hover=1;pressed=1;RequireFullRepaintMatches();
        hover=pressed=-1;BeginEdit(1);editText="10000";caret=5;anchor=0;RequireFullRepaintMatches();
        EndEdit();SetValue(1,10000);RequireFullRepaintMatches();
        SetValue(1,50);RequireFullRepaintMatches();
        BeginEdit(0);RequireFullRepaintMatches();EndEdit();BeginEdit(3);RequireFullRepaintMatches();EndEdit();
        SetValue(6,100);volumeDrag=true;RequireFullRepaintMatches();
        SetValue(6,0);volumeDrag=false;playing=true;RequireFullRepaintMatches();playing=false;RequireFullRepaintMatches();
        Require(engine==null || engine.Error==null,"No audio thread error");
    }
    internal void PreviewTransport(bool state) {volume=0;Play(state);volume=30;hover=pressed=-1;Invalidate();}
    internal void AttachDiagnosticAudio(AudioEngine audio) {engine=audio;diagnosticMute=true;values[0]=values[3]=10;values[1]=values[4]=500;values[2]=values[5]=50;values[7]=32;volume=0;playing=true;}
    internal double DragProbe(int sequence) {
        if(probeTarget==null)probeTarget=new Bitmap(ClientSize.Width,ClientSize.Height);
        Point grab=PhysicalPoint(50,15);
        if(sequence==0)OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,grab.X,grab.Y,0));
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        int offset=(sequence%24)*2;
        Point target=new Point(-10000+offset,-10000+offset/2);
        OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,grab.X+target.X-Left,grab.Y+target.Y-Top,0));
        using(Graphics graphics=Graphics.FromImage(probeTarget))OnPaint(new PaintEventArgs(graphics,ClientRectangle));
        return (System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
    }
    internal double InteractionProbe(int sequence) {
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        values[0]=10;values[3]=4;hover=sequence%12;volume=sequence%101;RenderCanvas();
        return (System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
    }
    internal string PaintBenchmark() {
        values[0]=10;values[3]=4;double[] elapsed=new double[1000];
        for(int i=0;i<50;i++){hover=i%12;RenderCanvas();}
        int gc=GC.CollectionCount(0);
        for(int i=0;i<elapsed.Length;i++) {
            hover=i%12;long start=System.Diagnostics.Stopwatch.GetTimestamp();RenderCanvas();
            elapsed[i]=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;
        }
        Array.Sort(elapsed);double sum=0;foreach(double value in elapsed)sum+=value;
        return string.Format(CultureInfo.InvariantCulture,"UI paint: mean={0:F4}ms, p95={1:F4}ms, p99={2:F4}ms, max={3:F4}ms, gen0={4}\r\n",sum/elapsed.Length,elapsed[950],elapsed[990],elapsed[999],GC.CollectionCount(0)-gc);
    }
    internal string PresentationBenchmark() {
        using(Bitmap target=new Bitmap(ClientSize.Width,ClientSize.Height))using(Graphics g=Graphics.FromImage(target)) {
            var args=new PaintEventArgs(g,ClientRectangle);double[] elapsed=new double[1000];
            for(int i=0;i<50;i++)OnPaint(args);
            int gc=GC.CollectionCount(0);
            for(int i=0;i<elapsed.Length;i++) {
                long start=System.Diagnostics.Stopwatch.GetTimestamp();OnPaint(args);
                elapsed[i]=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;
            }
            Array.Sort(elapsed);double sum=0;foreach(double value in elapsed)sum+=value;
            return string.Format(CultureInfo.InvariantCulture,"Retained full presentation (offscreen): mean={0:F4}ms, p95={1:F4}ms, p99={2:F4}ms, max={3:F4}ms, gen0={4}\r\n",sum/elapsed.Length,elapsed[950],elapsed[990],elapsed[999],GC.CollectionCount(0)-gc);
        }
    }
}
}
