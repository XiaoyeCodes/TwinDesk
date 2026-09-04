using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Windows;

// M1 opt-in local probe. Not product authentication or IPC. Network scope stays loopback.
internal sealed class LoopbackInputProbe(WindowInfo root, Guid host,string scope,Func<WindowInfo,bool>? allowDiagnosticRoot=null,
    uint streamId=1,ProbeControlAdmission? admission=null,bool localConsole=false)
{
    private readonly object gate = new();
    private Controller? current;
    public bool HasController { get { lock(gate) return current is not null; } }
    public bool IsSafeToRestart { get { lock(gate) return current is null; } }

    public async Task Serve(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin != "http://127.0.0.1:8091"
            || context.Request.Query.Count != 0) { context.Response.StatusCode=403;return; }
        Controller control;
        var owner=Guid.NewGuid();
        lock(gate)
        {
            if(current is not null){context.Response.StatusCode=409;return;}
            if(admission is not null&&!admission.TryAcquire(owner)){context.Response.StatusCode=409;return;}
            try{current=control=new(root,host,scope,allowDiagnosticRoot,streamId,localConsole);}
            catch{admission?.Release(owner,true,true);throw;}
        }
        try
        {
            using var socket=await context.WebSockets.AcceptWebSocketAsync();
            await control.Run(socket,context.RequestAborted);
        }
        finally
        {
            var stopped=await control.Executor.StopAsync(TimeSpan.FromSeconds(2));
            // Fail closed if the old input thread is stuck or releases remain unconfirmed.
            if(stopped.Completed&&stopped.Released)lock(gate)
            {if(ReferenceEquals(current,control)){current=null;admission?.Release(owner,true,true);}}
        }
    }
    public VideoSession BeginVideo()
    {
        lock(gate)
        {
            if(current is null||current.VideoClaimed)throw new InvalidOperationException("A fresh control connection is required.");
            current.VideoClaimed=true;return new(current);
        }
    }
    internal sealed class VideoSession(Controller controller)
    {
        public void Bind(WgcOwnedNv12Source source)=>controller.Bind(source);
        public void BeforeFrame(ProbeSceneConfig? scene,uint sequence)=>controller.BeforeFrame(scene,sequence);
        public void Stop()=>controller.Executor.Invalidate("VIDEO_DISCONNECTED");
        public object Diagnostics=>controller.Diagnostics;
    }

    internal sealed class Controller
    {
        private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
        { Converters={new JsonStringEnumConverter(allowIntegerValues:false)}, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
        private readonly object sceneGate=new();
        private readonly Guid host;
        private readonly string scope;
        private readonly uint streamId;
        private readonly InputLease lease=new(Guid.NewGuid(),1);
        private WgcOwnedNv12Source? source;
        private uint applied;
        private bool paused;
        private readonly NativeInputBackend backend;
        private readonly WindowsInputEnvironment localEnvironment;
        private readonly bool localConsole;
        private LocalConsoleBridge? localBridge;
        private long localArmAt;
        private bool localRequested;
        private string? localArmCode;
        private bool localActivationRequested;
        private bool? localActivationAccepted;
        private long dispatchCount;
        private double dispatchTotalMs,dispatchMaximumMs;
        private readonly SemaphoreSlim writer=new(1,1);
        private readonly Queue<object> outcomes=new();
        public BoundedInputExecutor Executor {get;}
        public bool VideoClaimed;
        public object Diagnostics { get {lock(outcomes)return new {status=Executor.Status,nativeCode=backend.LastCode,recent=outcomes.ToArray(),
            localArmCode,localActivationAccepted,dispatchCount,meanDispatchMs=dispatchCount==0?0:dispatchTotalMs/dispatchCount,dispatchMaximumMs,
            localConsole=localBridge is null?null:new {localBridge.Active,localBridge.Reason,localBridge.PhysicalEvents,localBridge.IgnoredInjected}};} }
        public Controller(WindowInfo root,Guid host,string scope,Func<WindowInfo,bool>? allowDiagnosticRoot,uint streamId,bool localConsole)
        {
            this.host=host;
            this.scope=scope;
            this.streamId=streamId;
            this.localConsole=localConsole;
            localEnvironment=new(root,w=>Volatile.Read(ref source)?.InputBindings.Verify(w)==true,allowDiagnosticRoot);
            backend=new(new WindowsInputEnvironment(root,w=>Volatile.Read(ref source)?.InputBindings.Verify(w)==true,allowDiagnosticRoot),new WindowsInputTransport());
            Executor=new(lease,root,backend);
        }
        public void Bind(WgcOwnedNv12Source capture)
        {
            lock(sceneGate)
            {
                if(source is not null)throw new InvalidOperationException("One video lifetime per control connection.");
                source=capture;
            }
        }
        private InputStamp Stamp(uint version)=>new(host,streamId,1,version);
        private void SynchronizeScene()
        {
            lock(sceneGate)
            {
                if(source is null)return;
                var captured=source.InputBindings.Current;
                if(captured is null)
                {
                    if(!paused){Wait(Executor.PauseScene());paused=true;}
                    return;
                }
                if(captured.Version!=applied)
                {
                    if(!Wait(Executor.UpdateScene(Stamp(captured.Version),captured.Geometry)))throw new InvalidOperationException("Scene input transaction rejected: "+Executor.Status.Session.Reason+" / "+backend.LastCode);
                    applied=captured.Version;paused=false;
                }
                else if(!paused && Executor.Status.Session.Reason=="SCENE_UPDATING")
                {
                    // Native input saw a transient change before the capture poll. Force a fresh
                    // version even if the popup has already disappeared by the next snapshot.
                    source.RequestInputSceneRefresh();paused=true;
                }
            }
        }
        public void BeforeFrame(ProbeSceneConfig? scene,uint sequence)
        {
            SynchronizeScene();
            if(scene is null)throw new InvalidOperationException("Input requires owned scene metadata.");
            // Old encoded frames remain decodable but cannot grant input to a newer geometry.
            Wait(Executor.FrameSent(Stamp(scene.Version),sequence));
        }
        private static T Wait<T>(Task<T> task)=>task.WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();

        public async Task Run(WebSocket socket,CancellationToken outer)
        {
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(outer);
            if(!localConsole)cancel.CancelAfter(TimeSpan.FromMinutes(12));
            var token=cancel.Token;
            async Task Send(object message)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(1));
                await writer.WaitAsync(deadline.Token);
                try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message,Json),WebSocketMessageType.Text,true,deadline.Token); }
                finally {writer.Release();}
            }
            await Send(new {type="inputHello",lease,hostInstanceId=host,streamId,epoch=1,scope,localConsole});
            var monitor=Task.Run(async()=>
            {
                try
                {
                    int ticks=0;
                    while(!token.IsCancellationRequested)
                    {
                        SynchronizeScene();
                        if(Interlocked.Read(ref localArmAt) is var arm && arm!=0 && Environment.TickCount64>=arm)
                        {
                            var binding=source?.InputBindings.Current;
                            localArmCode=binding is null?"CAPTURE_NOT_READY":!Executor.Status.Session.Ready?"DISPLAY_NOT_ACKNOWLEDGED":
                                Executor.Status.Session.HeldCount!=0?"INPUT_HELD":localEnvironment.Check(binding.Geometry,null).Code;
                            if(localArmCode=="FOCUS_DENIED" && !localActivationRequested)
                            {
                                localActivationRequested=true;localActivationAccepted=LocalConsoleActivation.Request(binding!.Geometry);
                                await Send(new {type="localConsoleState",state="ARMING",reason=localActivationAccepted==true?"正在激活 NX 并同步画面…":"Windows 未允许自动激活。点击一次原生 NX 标题栏即可继续，无倒计时。"});
                            }
                            else if(localArmCode!="NATIVE_TARGET_READY")
                            {
                                if(localArmCode is not ("FOCUS_DENIED" or "DISPLAY_NOT_ACKNOWLEDGED" or "CAPTURE_NOT_READY" or "SCENE_CHANGED"))
                                {Interlocked.Exchange(ref localArmAt,0);await Send(new {type="localConsoleState",state="FAILED",reason="接管未开始："+localArmCode});Executor.Invalidate("LOCAL_ARM_NOT_READY");}
                            }
                            else
                            {
                                try
                                {
                                    localBridge=new(binding!.Geometry.Nodes.Select(n=>n.Window.Handle),Executor.Invalidate);
                                    Interlocked.Exchange(ref localArmAt,0);
                                    await Send(new {type="localConsoleState",state="ACTIVE",reason="实体键鼠已转发到网页；F12 退出"});
                                }
                                catch(Exception e)
                                {
                                    localArmCode=e.Message;
                                    if(localArmCode.StartsWith("LOCAL_RELEASE_PHYSICAL_KEYS_FIRST",StringComparison.Ordinal))
                                    {await Task.Delay(16,token);continue;}
                                    Interlocked.Exchange(ref localArmAt,0);
                                    await Send(new {type="localConsoleState",state="FAILED",reason="接管未开始："+localArmCode});
                                    Executor.Invalidate("LOCAL_ARM_FAILED");
                                }
                            }
                        }
                        if(localBridge is { } bridge)
                        {
                            if(source?.InputBindings.Current is { } binding && Executor.Status.Accepting && WindowsInputEnvironment.InteractiveDesktop())
                                bridge.Refresh(binding.Geometry.Nodes.Select(n=>n.Window.Handle));
                            if(!bridge.Active){await Send(new {type="localConsoleState",state="STOPPED",reason=bridge.Reason});Executor.Invalidate(bridge.Reason);}
                            else if(bridge.Drain() is {Length:>0} events)await Send(new {type="localDevices",events});
                        }
                        if(++ticks%(localConsole?15:5)==0)await Send(new {type="inputState",status=Executor.Status,nativeCode=backend.LastCode});
                        if(!Executor.Status.Accepting)
                        {
                            await Send(new {type="inputTerminated",reason=Executor.Status.Session.Reason,nativeCode=backend.LastCode});
                            using var closeDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);
                            closeDeadline.CancelAfter(TimeSpan.FromSeconds(1));
                            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,"INPUT_STOPPED",closeDeadline.Token);
                            cancel.Cancel();break;
                        }
                        await Task.Delay(localConsole?16:50,token);
                    }
                }
                catch {cancel.Cancel();socket.Abort();throw;}
            },token);
            try
            {
                while(socket.State==WebSocketState.Open)
                {
                    using var message=await Receive(socket,token);
                    var json=message.RootElement;
                    string? type=json.GetProperty("type").GetString();
                    switch(type)
                    {
                        case "localStart":
                            if(!localConsole || localRequested || !Executor.Status.Session.Ready || Executor.Status.Session.HeldCount!=0)
                                throw new InvalidDataException("Local console is not available or ready.");
                            localRequested=true;Interlocked.Exchange(ref localArmAt,Environment.TickCount64);
                            await Send(new {type="localConsoleState",state="ARMING",reason="正在接管本机键鼠，请松开鼠标按钮；F12 退出"});break;
                        case "localStop":
                            localBridge?.Stop();Executor.Invalidate("LOCAL_CLIENT_STOPPED");return;
                        case "heartbeat":
                            if(!await Executor.Heartbeat(lease))throw new InvalidDataException("Lease expired.");
                            break;
                        case "displayed":
                            var stamp=json.GetProperty("stamp").Deserialize<InputStamp>(Json);
                            bool accepted=await Executor.Displayed(lease,stamp,json.GetProperty("frame").GetUInt32());
                            await Send(new {type="displayAck",accepted,stamp,frame=json.GetProperty("frame").GetUInt32()});break;
                        case "input":
                            var command=json.GetProperty("command").Deserialize<InputCommand>(Json)??throw new InvalidDataException("Missing input.");
                            var dispatchWatch=System.Diagnostics.Stopwatch.StartNew();
                            var outcome=await Executor.Submit(command).WaitAsync(TimeSpan.FromMilliseconds(500),token);
                            double dispatchMs=dispatchWatch.Elapsed.TotalMilliseconds;
                            lock(outcomes){dispatchCount++;dispatchTotalMs+=dispatchMs;dispatchMaximumMs=Math.Max(dispatchMaximumMs,dispatchMs);if(outcomes.Count==64)outcomes.Dequeue();outcomes.Enqueue(new {command.Sequence,command.Kind,command.Button,command.WheelX,command.WheelY,
                                command.U,command.V,scene=command.Stamp.Scene,textLength=command.Text?.Length,outcome,nativeCode=backend.LastCode});}
                            await Send(new {type="inputResult",sequence=command.Sequence,outcome,nativeCode=backend.LastCode,dispatchMs});break;
                        case "stop": Executor.Invalidate("CLIENT_STOPPED");return;
                        default:throw new InvalidDataException("Unsupported control message.");
                    }
                }
            }
            catch(Exception e) when(e is OperationCanceledException or WebSocketException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or TimeoutException)
            { Executor.Invalidate("CONTROL_DISCONNECTED_OR_INVALID"); }
            finally
            {
                cancel.Cancel();socket.Abort();
                try{await monitor;}catch(Exception){}
                localBridge?.Dispose();
                Executor.Invalidate("CONTROL_DISCONNECTED");
            }
        }
        private static async Task<JsonDocument> Receive(WebSocket socket,CancellationToken token)
        {
            byte[] buffer=new byte[65536];int length=0;
            while(true)
            {
                var part=await socket.ReceiveAsync(buffer.AsMemory(length),token);
                if(part.MessageType!=WebSocketMessageType.Text)throw new InvalidDataException("Text control messages only.");
                length+=part.Count;
                if(part.EndOfMessage)return JsonDocument.Parse(buffer.AsMemory(0,length),new JsonDocumentOptions{MaxDepth=12});
                if(length==buffer.Length)throw new InvalidDataException("Control message too large.");
            }
        }
    }
}
