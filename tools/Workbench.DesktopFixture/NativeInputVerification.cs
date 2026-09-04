using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.MediaFoundation;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// Finite integration test of the PRODUCT native backend, restricted to windows created by this test process.
// No external application is selected, no arbitrary HWND argument, no network listener and no engineering files.
internal static class NativeInputVerification
{
    public static int Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output)){Console.Error.WriteLine("Output must be new.");return 1;}
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var form=new TestForm();using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        bool done=false,started=false;int result=1;
        form.FormClosing+=(_,e)=>{if(started&&!done){cancel.Cancel();e.Cancel=true;}};
        using var armDeadline=new System.Windows.Forms.Timer {Interval=250};
        armDeadline.Tick+=(_,_)=>{if(!started&&cancel.IsCancellationRequested)form.Close();};armDeadline.Start();
        form.Start.Click+=async(_,_)=>
        {
            if(started)return;started=true;form.Start.Enabled=false;
            try { result=await Task.Run(()=>Verify(form,output,cancel.Token)); }
            catch(Exception e){Console.Error.WriteLine(e);}
            finally {done=true;form.Close();}
        };
        Application.Run(form);
        if(!started)
        {
            using var report=new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew);
            JsonSerializer.Serialize(report,new {status="NOT_RUN",scope="No native input submitted; test was not armed",error="TEST_NOT_ARMED"});
            Console.Error.WriteLine("Test was not armed; no input verification ran.");
        }
        return result;
    }
    private static async Task<int> Verify(TestForm form,string output,CancellationToken cancellation)
    {
        var checks=new List<object>();ExecutorDriver? session=null;NativeInputBackend? backend=null;
        WindowInfo? root=null;OwnedWindowScene? scene=null;Exception? failure=null;
        using var heartbeatStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task? watchdog=null;int keepAlive=1;long sequence=0;int lastPacketUps=0;
        Task? captureTask=null;
        using var captureStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var firstCapture=new TaskCompletionSource<WgcOwnedNv12Source>(TaskCreationOptions.RunContinuationsAsynchronously);
        WgcOwnedNv12Source? capture=null;
        var lease=new InputLease(Guid.NewGuid(),1);var stamp=new InputStamp(Guid.NewGuid(),1,1,1);
        async Task Ui(Action action)
        {
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            form.BeginInvoke((Action)(()=>{try{action();completion.SetResult();}catch(Exception e){completion.SetException(e);}}));
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2),cancellation);
        }
        async Task Wait(string name,Func<bool> predicate,int timeoutMs=2000)
        {
            var clock=Stopwatch.StartNew();bool passed=false;
            while(clock.ElapsedMilliseconds<timeoutMs)
            {
                cancellation.ThrowIfCancellationRequested();await Ui(()=>passed=predicate());
                if(passed&&clock.ElapsedMilliseconds<timeoutMs){checks.Add(new{name,status="PASS",elapsedMs=clock.Elapsed.TotalMilliseconds,deadlineMs=timeoutMs});Console.WriteLine("PASS "+name);return;}
                await Task.Delay(20,cancellation);
            }
            checks.Add(new{name,status="FAIL"});throw new InvalidOperationException(name+": native event/visible control result not observed.");
        }
        InputOutcome Send(InputKind kind,string? key=null,InputButton? button=null,ScreenPoint? point=null,int wheel=0,string? text=null,int wheelX=0)
        {
            double? u=point is { } p?(p.X-scene!.Bounds.X+0.5)/scene.Bounds.Width:null;
            double? v=point is { } q?(q.Y-scene!.Bounds.Y+0.5)/scene.Bounds.Height:null;
            var result=session!.Dispatch(new(lease,++sequence,stamp,1,kind,Key:key,Button:button,U:u,V:v,WheelX:wheelX,WheelY:wheel,Text:text));
            if(!result.Accepted)throw new InvalidOperationException($"{kind}: {result.Code}; native={backend!.LastCode}");
            return result;
        }
        void ReadySession(INativeInputTransport transport)
        {
            session?.Stop();
            var environment=new WindowsInputEnvironment(root!,capture!.InputBindings.Verify);
            backend=new(environment,transport);lease=new(Guid.NewGuid(),1);sequence=0;
            session=new(lease,root!,backend);session.UpdateScene(stamp,scene!);
            // Explicit synthetic display gate for L1 native-only testing, not a browser display ACK.
            session.FrameSent(stamp,1);session.Displayed(lease,stamp,1);
        }
        async Task Click(ScreenPoint point,InputButton button=InputButton.Left)
        {Send(InputKind.ButtonDown,button:button,point:point);await Task.Delay(30,cancellation);Send(InputKind.ButtonUp,button:button);}
        try
        {
            if(!WindowsInputEnvironment.InteractiveDesktop())throw new InvalidOperationException("Interactive default desktop required; test did not inject.");
            if(new[]{1,2,4,16,17,18}.Any(k=>(GetAsyncKeyState(k)&0x8000)!=0))throw new InvalidOperationException("Release physical buttons/modifiers before running the test.");
            ScreenPoint padPoint=default,textPoint=default;
            await Ui(()=>
            {
                form.Activate();form.Pad.Focus();
                var p=form.Pad.PointToScreen(new Point(160,120));padPoint=new(p.X,p.Y);
                var t=form.Entry.PointToScreen(new Point(80,12));textPoint=new(t.X,t.Y);
            });
            await Task.Delay(150,cancellation);
            using var process=Process.GetCurrentProcess();
            root=WindowCatalog.Find(process.ProcessName).Single(w=>w.ProcessId==process.Id && w.Handle==form.LiveHandle) with {BindingGeneration=1};
            captureTask=Task.Run(()=>
            {
                MediaFactory.MFStartup(false).CheckError();
                try
                {
                    using var source=new WgcOwnedNv12Source(root,1280,720);
                    while(!captureStop.IsCancellationRequested)
                    {
                        using var sample=source.TryGetSample();
                        if(sample is not null)firstCapture.TrySetResult(source);
                        captureStop.Token.WaitHandle.WaitOne(20);
                    }
                }
                catch(Exception e){firstCapture.TrySetException(e);session?.Invalidate("CAPTURE_FAILED");throw;}
                finally {MediaFactory.MFShutdown();}
            },captureStop.Token);
            capture=await firstCapture.Task.WaitAsync(TimeSpan.FromSeconds(5),cancellation);
            var captured=capture.InputBindings.Current??throw new InvalidOperationException("Complete capture binding required.");
            scene=captured.Geometry;stamp=stamp with {Scene=captured.Version};ReadySession(new WindowsInputTransport());
            watchdog=Task.Run(async()=>
            {
                while(!heartbeatStop.IsCancellationRequested)
                {
                    if(Volatile.Read(ref keepAlive)!=0)session?.Heartbeat(lease);
                    await Task.Delay(100,heartbeatStop.Token); // Executor's independent thread owns the watchdog tick.
                }
            },heartbeatStop.Token);
            foreach(var button in new[]{InputButton.Left,InputButton.Right,InputButton.Middle})
            {
                await Click(padPoint,button);
                var expected=button switch {InputButton.Left=>MouseButtons.Left,InputButton.Right=>MouseButtons.Right,_=>MouseButtons.Middle};
                await Wait("mouse-"+button,()=>form.Pad.Downs.Contains(expected)&&form.Pad.Ups.Contains(expected)&&form.Pad.Buttons.Count==0);
            }
            Send(InputKind.Wheel,point:padPoint,wheel:120);await Wait("wheel-positive-120",()=>form.Pad.Wheel==120);
            Send(InputKind.Wheel,point:padPoint,wheel:-120);await Wait("wheel-negative-120",()=>form.Pad.Wheel==0);
            Send(InputKind.Wheel,point:padPoint,wheelX:120);await Wait("horizontal-wheel-120",()=>form.Pad.HorizontalWheel==120);
            await Click(padPoint);await Task.Delay(50,cancellation);await Click(padPoint);
            await Wait("native-double-click",()=>form.Pad.DoubleClicks>0);
            Send(InputKind.KeyDown,key:"ControlRight");await Wait("right-control-down",()=>form.Pad.Keys.Contains(Keys.ControlKey)&&form.Pad.ExtendedControl);
            Send(InputKind.KeyUp,key:"ControlRight");await Wait("right-control-up",()=>!form.Pad.Keys.Contains(Keys.ControlKey));
            Send(InputKind.KeyDown,key:"NumpadEnter");Send(InputKind.KeyUp,key:"NumpadEnter");
            await Wait("numpad-enter-extended",()=>form.Pad.ExtendedEnter==true&&form.Pad.Keys.Count==0);
            Send(InputKind.KeyDown,key:"Enter");Send(InputKind.KeyUp,key:"Enter");
            await Wait("main-enter-not-extended",()=>form.Pad.ExtendedEnter==false&&form.Pad.Keys.Count==0);
            Send(InputKind.KeyDown,key:"F5");await Wait("function-key-f5",()=>form.Pad.Keys.Contains(Keys.F5));Send(InputKind.KeyUp,key:"F5");
            Send(InputKind.KeyDown,key:"ShiftLeft");Send(InputKind.ButtonDown,button:InputButton.Left,point:padPoint);
            var moved=new ScreenPoint(padPoint.X+80,padPoint.Y+40);Send(InputKind.Move,point:moved);
            Send(InputKind.ButtonUp,button:InputButton.Left);Send(InputKind.KeyUp,key:"ShiftLeft");
            await Wait("shift-drag-physical-coordinate",()=>form.Pad.DraggedWithShift && form.Pad.LastPoint==new Point(240,160)
                &&form.Pad.Keys.Count==0&&form.Pad.Buttons.Count==0);
            await Click(textPoint);
            const string text="TwinDesk 输入，ABC 123 🧪";
            Send(InputKind.Text,text:text);await Wait("unicode-visible-exact",()=>form.Entry.Text==text);
            Send(InputKind.KeyDown,key:"ControlLeft");Send(InputKind.KeyDown,key:"KeyA");Send(InputKind.KeyUp,key:"KeyA");Send(InputKind.KeyUp,key:"ControlLeft");
            string longText=new string('A',63)+"🧪中文"+new string('B',65);
            Send(InputKind.Text,text:longText);await Wait("select-all-and-surrogate-batch-boundary",()=>form.Entry.Text==longText);
            await Click(padPoint);Send(InputKind.KeyDown,key:"ControlLeft");Send(InputKind.ButtonDown,button:InputButton.Middle,point:padPoint);
            await Wait("held-before-heartbeat-loss",()=>form.Pad.Keys.Contains(Keys.ControlKey)&&form.Pad.Buttons.Contains(MouseButtons.Middle));
            Volatile.Write(ref keepAlive,0);
            await Wait("real-clock-watchdog-releases-held-input",()=>!session!.Status.Active&&form.Pad.Keys.Count==0&&form.Pad.Buttons.Count==0,6000);
            if(session!.Status.Reason!="HEARTBEAT_EXPIRED")throw new InvalidOperationException("Unexpected watchdog reason.");
            // Real partial insertion fault: transport submits only Unicode down; backend/session must deliver its up.
            await Ui(()=>{form.Entry.Clear();form.Entry.Focus();lastPacketUps=form.Entry.PacketUps;});
            ReadySession(new PartialOnceTransport());Volatile.Write(ref keepAlive,1);
            var partial=session!.Dispatch(new(lease,++sequence,stamp,1,InputKind.Text,Text:"中"));
            if(partial.Accepted)throw new InvalidOperationException("Partial Unicode send incorrectly reported complete.");
            await Wait("partial-unicode-up-and-no-replay",()=>form.Entry.Text=="中"&&form.Entry.PacketUps>lastPacketUps
                &&!backend!.HasPendingTransient&&!session.Status.Active);
            // A custom Control can preprocess WM_KEYUP differently from TextBox. Inspect real raw messages,
            // independently of managed OnKeyUp and of SendInput's successful return count.
            ReadySession(new WindowsInputTransport());
            await Ui(()=>form.Pad.Focus());
            Send(InputKind.Text,text:"中A");
            await Wait("custom-pad-unicode-raw-pairs-and-released-state",()=>form.Pad.PacketDowns==2&&form.Pad.PacketUps==2
                &&(GetAsyncKeyState(0xe7)&0x8000)==0);
            // A same-process unrelated top-level window must not inherit the root's input permission.
            ReadySession(new WindowsInputTransport());
            await Ui(()=>form.ShowUnrelated());await Task.Delay(100,cancellation);
            var denied=session!.Dispatch(new(lease,++sequence,stamp,1,InputKind.KeyDown,Key:"KeyA"));
            if(denied.Accepted||backend!.LastCode!="FOCUS_DENIED")throw new InvalidOperationException("Unrelated foreground was not denied: "+backend!.LastCode);
            await Wait("same-process-unrelated-focus-no-key",()=>form.UnrelatedKeyCount==0&&!session.Status.Active);
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        finally
        {
            heartbeatStop.Cancel();if(watchdog is not null)try{await watchdog;}catch(OperationCanceledException){}
            session?.Invalidate("TEST_FINISHED");
            if(session is not null)try{session.Stop();}catch(Exception e){failure??=e;}
            captureStop.Cancel();if(captureTask is not null)try{await captureTask.WaitAsync(TimeSpan.FromSeconds(3));}catch(Exception e){failure??=e;}
            try {await Ui(()=>form.CloseUnrelated());}catch(Exception e){failure??=e;}
        }
        var identity=new[]{typeof(NativeInputVerification).Assembly.Location,typeof(NativeInputBackend).Assembly.Location}.Select(path=>new
            {file=Path.GetFileName(path),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}).ToArray();
        using var report=new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew);
        JsonSerializer.Serialize(report,new {status=failure is null?"PASS":"FAIL",at=DateTimeOffset.Now,scope="L1 bounded executor + live WGC lifecycle binding + real SendInput into this process's own synthetic window; synthetic display gate, no browser, NX/TIA or true network-fault acceptance",
            buildIdentity=identity,root,checks,session=session?.Status,nativeCode=backend?.LastCode,pendingUnicode=backend?.HasPendingTransient,
            padPacket=new {form.Pad.PacketDowns,form.Pad.PacketUps,managedKeys=form.Pad.Keys.Select(k=>k.ToString()).ToArray(),asyncDown=(GetAsyncKeyState(0xe7)&0x8000)!=0},error=failure?.ToString()},
            new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true});
        Console.WriteLine("Evidence: "+output);return failure is null?0:1;
    }
    private sealed class ExecutorDriver(InputLease lease,WindowInfo root,NativeInputBackend backend)
    {
        private readonly BoundedInputExecutor executor=new(lease,root,backend);
        public InputSessionStatus Status=>executor.Status.Session;
        public InputOutcome Dispatch(InputCommand command)=>executor.Submit(command).GetAwaiter().GetResult();
        public bool UpdateScene(InputStamp stamp,OwnedWindowScene scene)=>executor.UpdateScene(stamp,scene).GetAwaiter().GetResult();
        public bool FrameSent(InputStamp stamp,uint sequence)=>executor.FrameSent(stamp,sequence).GetAwaiter().GetResult();
        public bool Displayed(InputLease owner,InputStamp stamp,uint sequence)=>executor.Displayed(owner,stamp,sequence).GetAwaiter().GetResult();
        public bool Heartbeat(InputLease owner)=>executor.Heartbeat(owner).GetAwaiter().GetResult();
        public void Invalidate(string code)=>executor.Invalidate(code);
        public void Stop()
        {
            var stopped=executor.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if(!stopped.Completed||!stopped.Released)throw new InvalidOperationException("Executor has not stopped safely; no replacement allowed.");
        }
    }
    private sealed class PartialOnceTransport : INativeInputTransport
    {
        private readonly WindowsInputTransport real=new();private bool partial=true;
        public uint Send(NativeInputEvent[] events){if(partial&&events.Length>1){partial=false;return real.Send([events[0]]);}return real.Send(events);}
    }
    private sealed class TestForm : Form
    {
        private long liveHandle;internal long LiveHandle=>Volatile.Read(ref liveHandle);
        internal readonly ObservedText Entry=new(){Location=new(20,35),Size=new(710,30)};
        internal readonly ObservedPad Pad=new(){Location=new(20,100),Size=new(740,420),TabStop=true};
        internal readonly Button Start=new(){Text="开始本机键鼠测试（仅此测试窗口）",Location=new(20,545),Size=new(400,32)};
        private Form? unrelated;internal int UnrelatedKeyCount;
        public TestForm()
        {
            Text="TwinDesk native input test — OWN WINDOW ONLY";FormBorderStyle=FormBorderStyle.None;
            StartPosition=FormStartPosition.Manual;Bounds=new(120,140,800,600);AutoScaleMode=AutoScaleMode.None;
            Controls.Add(new Label {Text="Finite native input test · do not touch keyboard/mouse · NOT NX/TIA",AutoSize=true,Location=new(20,10)});
            Controls.Add(Entry);Controls.Add(Pad);Controls.Add(Start);
            HandleCreated+=(_,_)=>Volatile.Write(ref liveHandle,(long)Handle);HandleDestroyed+=(_,_)=>Volatile.Write(ref liveHandle,0);
        }
        internal void ShowUnrelated()
        {
            unrelated=new Form {Text="TwinDesk unrelated focus test",Size=new(300,200),KeyPreview=true};
            unrelated.KeyDown+=(_,_)=>UnrelatedKeyCount++;unrelated.Show();unrelated.Activate();
        }
        internal void CloseUnrelated(){unrelated?.Dispose();unrelated=null;}
    }
    private sealed class ObservedText : TextBox
    {
        internal int PacketUps;
        protected override void WndProc(ref Message m){if(m.Msg==0x101&&m.WParam==0xe7)PacketUps++;base.WndProc(ref m);}
    }
    private sealed class ObservedPad : Control
    {
        internal int PacketDowns,PacketUps;
        internal readonly HashSet<MouseButtons> Downs=[],Ups=[],Buttons=[];
        internal readonly HashSet<Keys> Keys=[];internal int Wheel,HorizontalWheel,DoubleClicks;internal bool ExtendedControl,DraggedWithShift;internal bool? ExtendedEnter;internal Point LastPoint;
        public ObservedPad(){BackColor=Color.DarkSlateBlue;ForeColor=Color.White;SetStyle(ControlStyles.StandardClick|ControlStyles.StandardDoubleClick,true);}
        protected override bool IsInputKey(Keys keyData)=>true;
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);Focus();Downs.Add(e.Button);Buttons.Add(e.Button);Capture=true;Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);Ups.Add(e.Button);Buttons.Remove(e.Button);if(Buttons.Count==0)Capture=false;Invalidate();}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);LastPoint=e.Location;if(Buttons.Count>0&&(ModifierKeys&System.Windows.Forms.Keys.Shift)!=0)DraggedWithShift=true;Invalidate();}
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);Wheel+=e.Delta;Invalidate();}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);Keys.Add(e.KeyCode);Invalidate();}
        protected override void OnKeyUp(KeyEventArgs e){base.OnKeyUp(e);Keys.Remove(e.KeyCode);Invalidate();}
        protected override void OnMouseDoubleClick(MouseEventArgs e){base.OnMouseDoubleClick(e);DoubleClicks++;}
        protected override void WndProc(ref Message m)
        {
            if(m.WParam==0xe7){if(m.Msg==0x100)PacketDowns++;if(m.Msg==0x101)PacketUps++;}
            if(m.Msg==0x100&&m.WParam==0x11)ExtendedControl=(((long)m.LParam>>24)&1)!=0;
            if(m.Msg==0x100&&m.WParam==0x0d)ExtendedEnter=(((long)m.LParam>>24)&1)!=0;
            if(m.Msg==0x20e)HorizontalWheel+=unchecked((short)((long)m.WParam>>16));
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);TextRenderer.DrawText(e.Graphics,$"Point {LastPoint} | wheel {Wheel} | held keys {Keys.Count} | held buttons {Buttons.Count}",Font,new Point(10,10),Color.White);
        }
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
