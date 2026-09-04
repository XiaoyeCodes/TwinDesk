using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// F0 event target only. Observes messages received by its own controls; never injects OS input.
internal sealed class InputFixtureForm : Form
{
    private readonly Label status = new() { AutoSize=false, Dock=DockStyle.Bottom, Height=50 };
    private readonly InputPad pad = new() { Dock=DockStyle.Fill };
    private LayeredFixtureWindow? alpha;
    private long controlEvents;
    private readonly List<object> displayObservations=[];
    private readonly string evidenceDirectory=Path.GetFullPath(Path.Combine("artifacts","verification","f0-interactive-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff")));

    public InputFixtureForm()
    {
        Text="TwinDesk F0 input fixture — NOT NX / TIA";
        StartPosition=FormStartPosition.CenterScreen;
        Size=new Size(1000,700);
        MinimumSize=new Size(640,480);
        var toolbar=new FlowLayoutPanel { Dock=DockStyle.Top,Height=110,AutoScroll=true };
        var english=new TextBox { Width=200,AccessibleName="F0 English text",PlaceholderText="English / shortcuts" };
        var chinese=new TextBox { Width=230,AccessibleName="F0 中文文本",PlaceholderText="中文、英文、标点" };
        english.TextChanged+=(_,_)=>ReportControl("English",english.TextLength);
        chinese.TextChanged+=(_,_)=>ReportControl("Chinese",chinese.TextLength);
        toolbar.Controls.Add(english);toolbar.Controls.Add(chinese);
        void Button(string text,Action action)
        {
            var button=new Button { Text=text,AutoSize=true };
            button.Click+=(_,_)=>action();toolbar.Controls.Add(button);
        }
        Button("Alpha popup",()=> {
            if(alpha is not null){alpha.Dispose();alpha=null;return;}
            alpha=new(Handle,PointToScreen(new Point(100,200)));
        });
        Button("Modal editor",()=> {
            using var modal=new Form { Text="F0 模态编辑测试",Size=new Size(430,220),StartPosition=FormStartPosition.CenterParent };
            var text=new TextBox { Dock=DockStyle.Top,AccessibleName="F0 模态文本",Text="测试参数 123" };
            var cancel=new Button { Text="取消",Dock=DockStyle.Bottom,DialogResult=DialogResult.Cancel };
            var okay=new Button { Text="确认",Dock=DockStyle.Bottom,DialogResult=DialogResult.OK };
            modal.Controls.Add(text);modal.Controls.Add(okay);modal.Controls.Add(cancel);
            modal.AcceptButton=okay;modal.CancelButton=cancel;
            var result=modal.ShowDialog(this); ReportControl($"modal-{result}",text.TextLength);
        });
        Button("Native Open (selection only)",()=> {
            using var dialog=new OpenFileDialog { Title="F0 本地文件选择（不读取或修改文件）",CheckFileExists=true,Multiselect=false };
            var result=dialog.ShowDialog(this);
            ReportControl($"native-open-{result}",0); // Do not log paths or open file content.
        });
        Button("Next display (own fixture only)",()=> {
            var screens=Screen.AllScreens;
            int index=Array.FindIndex(screens,s=>s.DeviceName==Screen.FromHandle(Handle).DeviceName);
            var next=screens[(index+1)%screens.Length].WorkingArea;
            Location=new Point(next.X+40,next.Y+40);
            BeginInvoke(()=>RecordDisplay("moved-own-fixture"));
        });
        pad.EventObserved+=message=>status.Text=message;
        Controls.Add(pad);Controls.Add(toolbar);Controls.Add(status);
        Shown+=(_,_)=>RecordDisplay("shown");
        DpiChanged+=(_,_)=>BeginInvoke(()=>RecordDisplay("dpi-changed"));
        FormClosed+=(_,_)=>{alpha?.Dispose();alpha=null;SaveEvidence();};
        status.Text="仅接收此测试窗口的事件；不是远控输入实现。蓝色网格可拖拽，右键含子菜单。";
    }
    private void ReportControl(string control,int length) => status.Text=$"Control event {++controlEvents}: {control}; text length={length}. No body/path persisted.";
    private void RecordDisplay(string reason)
    {
        if(displayObservations.Count==64)displayObservations.RemoveAt(0);
        displayObservations.Add(new {reason,time=DateTimeOffset.Now,dpi=DeviceDpi,screen=Screen.FromHandle(Handle).DeviceName,
            bounds=new {Left,Top,Width,Height}});
        status.Text=$"F0 {reason}: actual DPI {DeviceDpi}; {Screen.FromHandle(Handle).DeviceName}; position {Left},{Top}";
        pad.Invalidate();
    }
    private void SaveEvidence()
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var binaries=new[]{typeof(InputFixtureForm).Assembly.Location,typeof(WindowCatalog).Assembly.Location}.Select(path=>new {
                file=Path.GetFileName(path),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}).ToArray();
            var report=new {status="OBSERVED_NOT_ACCEPTANCE",scope="Actual own WinForms received events; no text body or file path recorded; correlate with browser report and visible effects",
                time=DateTimeOffset.Now,binaries,displayObservations,controlEvents,pad=pad.Diagnostics};
            using var output=new FileStream(Path.Combine(evidenceDirectory,"report.json"),FileMode.CreateNew);
            JsonSerializer.Serialize(output,report,new JsonSerializerOptions {WriteIndented=true});
            Console.WriteLine("F0 own-event evidence: "+evidenceDirectory);
        }
        catch(Exception e){Console.Error.WriteLine("F0 evidence failed: "+e.GetType().Name+": "+e.Message);}
    }

    private sealed class InputPad : Control
    {
        private readonly HashSet<Keys> heldKeys=[];
        private readonly HashSet<MouseButtons> heldButtons=[];
        private readonly Queue<string> history=new();
        private long eventSequence;
        private int packetDowns,packetUps;
        private readonly System.Diagnostics.Stopwatch eventClock=System.Diagnostics.Stopwatch.StartNew();
        private Point marker=new(120,120);
        private readonly ContextMenuStrip menu=new();
        public event Action<string>? EventObserved;
        public object Diagnostics=>new {eventSequence,actualDpi=DeviceDpi,heldKeys=heldKeys.Select(k=>k.ToString()).ToArray(),heldButtons=heldButtons.Select(b=>b.ToString()).ToArray(),
            rawPacket=new {downs=packetDowns,ups=packetUps,asyncDown=(GetAsyncKeyState(0xe7)&0x8000)!=0},
            marker=new {marker.X,marker.Y},recent=history.ToArray()};

        public InputPad()
        {
            TabStop=true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer,true);
            var submenu=new ToolStripMenuItem("F0 子菜单");
            submenu.DropDownItems.Add("确认菜单命中",null,(_,_)=>Observe("submenu-selected"));
            menu.Items.Add(submenu);menu.Items.Add("普通命令",null,(_,_)=>Observe("menu-selected"));
            ContextMenuStrip=menu;
        }
        protected override bool IsInputKey(Keys keyData) => true;
        protected override void WndProc(ref Message m)
        {
            if(m.WParam==0xe7)
            {
                if(m.Msg==0x100)packetDowns++;
                if(m.Msg==0x101){packetUps++;Observe("raw-packet-up");}
            }
            base.WndProc(ref m);
        }
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);Focus();heldButtons.Add(e.Button);marker=e.Location;Capture=true;
            Observe($"down {e.Button} {e.X},{e.Y}");
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);heldButtons.Remove(e.Button);marker=e.Location;
            if(heldButtons.Count==0)Capture=false;
            Observe($"up {e.Button} {e.X},{e.Y}");
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(heldButtons.Count>0){marker=e.Location;Observe($"drag {e.X},{e.Y}");}
        }
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);Observe($"wheel {e.Delta}");}
        protected override void OnMouseDoubleClick(MouseEventArgs e){base.OnMouseDoubleClick(e);Observe($"double {e.Button}");}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);heldKeys.Add(e.KeyCode);Observe($"key-down {e.KeyCode} modifiers={e.Modifiers}");}
        protected override void OnKeyUp(KeyEventArgs e){base.OnKeyUp(e);heldKeys.Remove(e.KeyCode);Observe($"key-up {e.KeyCode} modifiers={e.Modifiers}");}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Observe("focus-lost (held-state preserved for diagnosis)");}
        private void Observe(string detail)
        {
            eventSequence++;
            string message=$"F0 event={eventSequence}; tMs={eventClock.Elapsed.TotalMilliseconds:F3}; DPI={DeviceDpi}; {detail}; keys=[{string.Join(',',heldKeys)}]; buttons=[{string.Join(',',heldButtons)}]";
            if(history.Count==128)history.Dequeue();history.Enqueue(message);
            EventObserved?.Invoke(message);Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(20,40,65));
            for(int x=0;x<Width;x+=40)e.Graphics.DrawLine(Pens.SlateGray,x,0,x,Height);
            for(int y=0;y<Height;y+=40)e.Graphics.DrawLine(Pens.SlateGray,0,y,Width,y);
            e.Graphics.FillEllipse(Brushes.Orange,marker.X-6,marker.Y-6,12,12);
            // Local event counter only, not the product's inputSeq or an end-to-end latency proof.
            for(int bit=0;bit<16;bit++)e.Graphics.FillRectangle((eventSequence & (1L<<bit))==0?Brushes.Black:Brushes.White,20+bit*14,20,12,12);
            TextRenderer.DrawText(e.Graphics,$"Local event counter {eventSequence}; marker {marker.X},{marker.Y}; DPI {DeviceDpi}",Font,new Point(20,45),Color.White);
            TextRenderer.DrawText(e.Graphics,history.LastOrDefault()??"Click/drag; left/right/middle; wheel; keyboard. No injected input.",Font,new Point(20,70),Color.White);
        }
        protected override void Dispose(bool disposing){if(disposing)menu.Dispose();base.Dispose(disposing);}
    }
}
