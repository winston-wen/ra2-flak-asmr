using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace FlakASMR {
// Render at the game's pixel resolution. Only the finished canvas is enlarged.
// Neither the Windows font rasterizer nor OS button theming alters the artwork.
sealed class PixelFont {
    sealed class Glyph { public int Width; public byte[] Pixels; }
    readonly Dictionary<char,Glyph> glyphs=new Dictionary<char,Glyph>();
    public PixelFont() {
        using(Stream s=Resources.Open("westwood.bin"))using(BinaryReader r=new BinaryReader(s)) {
            if(Encoding.ASCII.GetString(r.ReadBytes(4))!="WWF1")throw new InvalidDataException("字体资源损坏");
            int count=r.ReadInt32();
            for(int i=0;i<count;i++) {
                char c=(char)r.ReadInt32();int width=r.ReadInt32();
                glyphs[c]=new Glyph {Width=width,Pixels=r.ReadBytes(width*16)};
            }
        }
    }
    public int Width(string text) {int n=0;foreach(char c in text) {Glyph g;if(glyphs.TryGetValue(c,out g))n+=g.Width+1;}return n;}
    void Ink(Graphics g,string text,int x,int y,Brush brush) {
        foreach(char c in text) {
            Glyph glyph;if(!glyphs.TryGetValue(c,out glyph))continue;
            for(int py=0;py<16;py++) {
                int px=0;
                while(px<glyph.Width) {
                    if(glyph.Pixels[py*glyph.Width+px]==0) {px++;continue;}
                    int start=px++;
                    while(px<glyph.Width && glyph.Pixels[py*glyph.Width+px]!=0)px++;
                    g.FillRectangle(brush,x+start,y+py,px-start,1);
                }
            }
            x+=glyph.Width+1;
        }
    }
    public void Draw(Graphics g,string text,int x,int y,Color color) {
        using(Brush shadow=new SolidBrush(Color.Black))Ink(g,text,x+1,y+1,shadow);
        using(Brush foreground=new SolidBrush(color))Ink(g,text,x,y,foreground);
    }
}

static class Resources {
    public static Stream Open(string name) {
        Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if(s==null)throw new InvalidDataException("缺少内置资源："+name);return s;
    }
    public static Bitmap Image(string name) {using(Stream s=Open(name))using(Bitmap b=new Bitmap(s))return new Bitmap(b);}
}

sealed class MainForm : Form {
    const int LogicalWidth=300,LogicalHeight=208;
    static readonly Color Gold=Color.FromArgb(255,224,115),Yellow=Color.Yellow;
    static readonly Rectangle VolumeRect=new Rectangle(60,180,182,16);
    static readonly int[] CaptionX={202,226,250,274};
    readonly int uiScale;
    readonly bool previewOnly;
    readonly int[] values={2,500,50,3,500,50};
    readonly Bitmap panel=Resources.Image("panel.png"),buttons=Resources.Image("buttons.png"),
        rail=Resources.Image("rail.png"),thumb=Resources.Image("thumb.png");
    readonly Bitmap canvas=new Bitmap(LogicalWidth,LogicalHeight);
    readonly PixelFont font=new PixelFont();
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
        using(Graphics desktop=Graphics.FromHwnd(IntPtr.Zero))uiScale=scale>0?scale:Math.Max(2,(int)Math.Ceiling(desktop.DpiX/96f));
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;
        ClientSize=new Size(LogicalWidth*uiScale,LogicalHeight*uiScale);
        MinimumSize=Size;MaximumSize=Size;StartPosition=FormStartPosition.CenterScreen;
        Text="RA2 Flak ASMR";BackColor=Color.FromArgb(9,6,6);ForeColor=Yellow;
        AccessibleName=Text;AccessibleDescription="左侧与右侧分别调整单位数量、平均开火间隔和标准差；底部调整音量。空格播放或暂停，Tab 输入数值。";
        DoubleBuffered=true;KeyPreview=true;MaximizeBox=false;
        SetStyle(ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint,true);
        using(Stream s=Resources.Open("flak.ico"))Icon=new Icon(s);
        repeat.Tick+=(s,e)=> {
            repeat.Interval=65;
            if(pressed>=0 && pressed<12 && hover==pressed)Adjust(pressed/2,(pressed%2==1)?1:-1);
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
        FormClosed+=(s,e)=> {panel.Dispose();buttons.Dispose();rail.Dispose();thumb.Dispose();canvas.Dispose();tip.Dispose();repeat.Dispose();monitor.Dispose();caretTimer.Dispose();};
    }

    static int Row(int field) {return field%3;}
    static Rectangle ValueRect(int field) {return field==6?new Rectangle(246,180,44,16):new Rectangle(field<3?60:210,96+Row(field)*28,32,20);}
    static Rectangle ButtonRect(int field,int direction) {return new Rectangle((field<3?60:210)+(direction>0?60:36),96+Row(field)*28,20,20);}
    static Rectangle CaptionRect(int index) {return new Rectangle(CaptionX[index],7,20,20);}
    static bool Contains(Rectangle r,PointF p) {return p.X>=r.Left && p.X<r.Right && p.Y>=r.Top && p.Y<r.Bottom;}
    PointF Logical(Point p) {return new PointF(p.X/(float)uiScale,p.Y/(float)uiScale);}
    int Hit(PointF p) {
        for(int i=0;i<4;i++)if(Contains(CaptionRect(i),p))return 100+i;
        for(int field=0;field<6;field++) {
            if(Contains(ValueRect(field),p))return 300+field;
            if(Contains(ButtonRect(field,-1),p))return field*2;
            if(Contains(ButtonRect(field,1),p))return field*2+1;
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
        int state=ButtonState(id,selected);
        g.DrawImage(buttons,target,new Rectangle(kind*20,(state+(selected?3:0))*20,20,20),GraphicsUnit.Pixel);
    }
    Bitmap RenderCanvas() {
        using(Graphics g=Graphics.FromImage(canvas)) {
            g.SmoothingMode=SmoothingMode.None;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.Half;
            g.DrawImageUnscaled(panel,0,0);
            for(int i=0;i<4;i++)PaintButton(g,i+2,CaptionRect(i),100+i,(i==0 && playing)||(i==1 && !playing));
            for(int field=0;field<6;field++) {
                Rectangle r=ValueRect(field);
                if(editing!=field) {
                    string text=values[field].ToString(CultureInfo.InvariantCulture);
                    Rectangle area=font.Width(text)>26?new Rectangle(r.X+1,r.Y,30,20):new Rectangle(r.X+3,r.Y,26,20);
                    TextAt(g,text,area,true,false,Yellow);
                }
                PaintButton(g,1,ButtonRect(field,-1),field*2,false);
                PaintButton(g,0,ButtonRect(field,1),field*2+1,false);
            }
            int state=volumeDrag?2:hover==200?1:0;
            g.DrawImage(rail,VolumeRect,new Rectangle(0,state*16,182,16),GraphicsUnit.Pixel);
            int tx=62+(int)Math.Round(170*volume/100.0);
            g.DrawImage(thumb,new Rectangle(tx,181,8,14),new Rectangle(0,state*14,8,14),GraphicsUnit.Pixel);
            if(editing!=6)TextAt(g,volume+"%",new Rectangle(247,180,42,16),false,true,Yellow);
            if(editing>=0)PaintEditor(g);
        }
        return canvas;
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        e.Graphics.SmoothingMode=SmoothingMode.None;e.Graphics.InterpolationMode=InterpolationMode.NearestNeighbor;e.Graphics.PixelOffsetMode=PixelOffsetMode.Half;
        e.Graphics.DrawImage(RenderCanvas(),ClientRectangle,new Rectangle(0,0,LogicalWidth,LogicalHeight),GraphicsUnit.Pixel);
    }
    void Publish() {
        if(engine!=null)engine.Mixer.Config=new Settings(values[3],values[0],values[4],values[1],values[5],values[2],70,volume,playing,false);
        Invalidate();
    }
    Settings CurrentSettings {get {return engine!=null?engine.Mixer.Config:new Settings(values[3],values[0],values[4],values[1],values[5],values[2],70,volume,playing,false);}}
    void Play(bool state) {CommitEdit();if(!ready || fault)return;playing=state;Publish();}
    void Adjust(int field,int direction) {
        int step=field==6?1:Row(field)==0?1:Row(field)==1?10:5;
        SetValue(field,(field==6?volume:values[field])+direction*step);
    }
    void SetValue(int field,int value) {
        if(field==6)volume=Math.Max(0,Math.Min(100,value));
        else {int min=Row(field)==1?50:0,max=Row(field)==0?32:Row(field)==1?10000:5000;values[field]=Math.Max(min,Math.Min(max,value));}
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
        using(Pen focus=new Pen(Gold))g.DrawRectangle(focus,box.X,box.Y,box.Width-1,box.Height-1);
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
        if(hit>=300 && hit<=306) {BeginEdit(hit-300);return;}
        if(hit==200) {
            int tx=62+(int)Math.Round(170*volume/100.0);
            dragAnchor=p.X>=tx && p.X<tx+8?p.X-tx:4;
            volumeDrag=true;pressed=hit;Capture=true;SetVolume(p);return;
        }
        if((hit>=0 && hit<12)||(hit>=100 && hit<104)) {
            pressed=hit;Capture=true;
            if(hit<12) {Adjust(hit/2,hit%2==1?1:-1);repeat.Interval=350;repeat.Start();}
            Invalidate();
        }
    }
    void ReleasePress() {repeat.Stop();pressed=-1;volumeDrag=false;Capture=false;Invalidate();}
    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);if(e.Button!=MouseButtons.Left)return;
        int action=pressed,target=Hit(Logical(e.Location));ReleasePress();
        if(action!=target)return;
        if(action==100)Play(true);else if(action==101)Play(false);
        else if(action==102)WindowState=FormWindowState.Minimized;else if(action==103)Close();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) {
        base.OnMouseCaptureChanged(e);
        if(!Capture) {repeat.Stop();pressed=-1;volumeDrag=false;Invalidate();}
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);PointF p=Logical(e.Location);
        if(volumeDrag)SetVolume(p);
        int next=Hit(p);string help="";
        if(next>=100 && next<104)help=new[]{"播放 · 空格播放/暂停","暂停 · 平滑淡出","最小化","关闭 · 平滑淡出"}[next-100];
        else if(next>=300 && next<=306) {
            int field=next-300;
            help=field==6?"音量 0–100% · 点击输入":(field<3?"左侧单位组 · ":"右侧单位组 · ")+new[]{"单位数量 0–32","平均间隔 50–10000 ms","标准差 0–5000 ms；0 为固定节奏"}[Row(field)]+" · 点击输入";
        }
        else if(next>=0 && next<12)help="按住连续调整 · 滚轮调整";
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
        for(int field=0;field<6;field++)if(Contains(new Rectangle(ValueRect(field).X,ValueRect(field).Y,80,20),p)) {Adjust(field,direction);return;}
        if(Contains(new Rectangle(60,176,230,24),p))SetValue(6,volume+direction*5);
    }
    protected override void OnKeyPress(KeyPressEventArgs e) {
        if(editing>=0 && e.KeyChar>='0' && e.KeyChar<='9') {ReplaceSelection(e.KeyChar.ToString());e.Handled=true;}
        else base.OnKeyPress(e);
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys key) {
        if(editing>=0) {
            if(key==Keys.Enter) {CommitEdit();return true;}
            if(key==Keys.Escape) {EndEdit();return true;}
            if(key==Keys.Tab || key==(Keys.Shift|Keys.Tab)) {int next=(editing+(key==Keys.Tab?1:6))%7;CommitEdit();BeginEdit(next);return true;}
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
            if(key==Keys.Space) {Play(!playing);return true;}
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
        if(m.Msg==0x84) {
            long raw=m.LParam.ToInt64();Point client=PointToClient(new Point((short)(raw&65535),(short)((raw>>16)&65535)));
            PointF p=Logical(client);
            if(Contains(new Rectangle(4,4,292,26),p) && Hit(p)<0) {m.Result=new IntPtr(2);return;}
        }
        if(m.Msg==0xA3)return;
        base.WndProc(ref m);
    }

    static void Require(bool value,string name) {if(!value)throw new Exception(name);}
    Point PhysicalPoint(int x,int y) {return new Point(x*uiScale+uiScale/2,y*uiScale+uiScale/2);}
    void MouseClickAt(int x,int y) {
        Point p=PhysicalPoint(x,y);var e=new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0);OnMouseDown(e);OnMouseUp(e);
    }
    internal void ControlTest() {
        Require(ready && (engine!=null || previewOnly),"Audio engine or headless preview ready");
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
        Point start=PhysicalPoint(66,186),end=PhysicalPoint(236,186);
        OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,start.X,start.Y,0));
        OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,end.X,end.Y,0));
        OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,end.X,end.Y,0));Require(volume==100,"Horizontal volume drag");
        dragAnchor=4;SetVolume(new PointF(66,186));Require(volume==0,"Volume lower endpoint");
        MouseClickAt(70,104);Require(editing==0,"Value click opens editor");
        OnKeyPress(new KeyPressEventArgs('9'));Message msg=new Message();ProcessCmdKey(ref msg,Keys.Enter);Require(values[0]==9,"Typed field commit");
        BeginEdit(0);OnKeyPress(new KeyPressEventArgs('1'));ProcessCmdKey(ref msg,Keys.Escape);Require(values[0]==9,"Escape cancels edit");
        MouseClickAt(268,188);Require(editing==6,"Volume numeric editor hit");
        OnKeyPress(new KeyPressEventArgs('4'));OnKeyPress(new KeyPressEventArgs('2'));ProcessCmdKey(ref msg,Keys.Enter);Require(volume==42,"Typed volume");
        SetValue(6,0);SetValue(3,100);SetValue(1,0);Require(values[3]==32 && values[1]==50,"Value bounds");
        for(int i=0;i<4;i++)Require(CaptionRect(i).Size==new Size(20,20),"20px caption controls");
        Require(ValueRect(6).Left==ButtonRect(3,-1).Left && ValueRect(6).Right==ButtonRect(3,1).Right,"Aligned readout edges");
        hover=0;Require(ButtonState(0,false)==1,"Hover state");pressed=0;Require(ButtonState(0,false)==2,"Pressed state");ReleasePress();
        Require(engine==null || engine.Error==null,"No audio thread error");
    }
    internal void PreviewTransport(bool state) {volume=0;Play(state);volume=30;hover=pressed=-1;Invalidate();}
}
}
