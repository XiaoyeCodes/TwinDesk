using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using Workbench.Windows;

// Diagnostic source chosen locally at startup, never by a browser-supplied HWND/process.
// Input is explicit local opt-in only; no LAN/public exposure or authenticated product Host.
WindowCatalog.SetDpiAwareness();
var arguments=args.ToList();
int port=8091;
if(arguments.Contains("--port"))
{
    int index=arguments.IndexOf("--port");
    if(arguments.Count(a=>a=="--port")!=1||index+1>=arguments.Count||
        !int.TryParse(arguments[index+1],out port)||port is not (8091 or 8093))
        throw new ArgumentException("Diagnostic port must be 8091 or 8093.");
    arguments.RemoveRange(index,2);
}
string loopbackOrigin=$"http://127.0.0.1:{port}";
bool localConsole=arguments.Remove("--local-console");
string? nxCopy=null;
if(arguments.Contains("--input-nx-copy"))
{
    int index=arguments.IndexOf("--input-nx-copy");
    if(arguments.Count(a=>a=="--input-nx-copy")!=1 || index+1>=arguments.Count || arguments[index+1].StartsWith("--"))
        throw new ArgumentException("--input-nx-copy requires one locally verified isolated .prt path.");
    nxCopy=arguments[index+1];arguments.RemoveRange(index,2);
}
string? dualFixtureProcess=null;string? dualFixtureHandle=null;
foreach(var option in new[]{"--dual-fixture-process","--dual-fixture-window"})
{
    if(!arguments.Contains(option))continue;
    int index=arguments.IndexOf(option);
    if(arguments.Count(a=>a==option)!=1||index+1>=arguments.Count||arguments[index+1].StartsWith("--"))throw new ArgumentException("Invalid dual fixture selection.");
    if(option=="--dual-fixture-process")dualFixtureProcess=arguments[index+1];else dualFixtureHandle=arguments[index+1];
    arguments.RemoveRange(index,2);
}
if((dualFixtureProcess is null)!=(dualFixtureHandle is null))throw new ArgumentException("Both dual fixture selectors are required.");
string? dualNxHandle=null;
if(arguments.Contains("--dual-nx-window"))
{
    int index=arguments.IndexOf("--dual-nx-window");
    if(arguments.Count(a=>a=="--dual-nx-window")!=1||index+1>=arguments.Count||arguments[index+1].StartsWith("--"))
        throw new ArgumentException("An explicit second NX HWND is required.");
    dualNxHandle=arguments[index+1];arguments.RemoveRange(index,2);
}
bool dualNx=dualNxHandle is not null;
bool dual=dualFixtureHandle is not null||dualNx;
bool fixtureInput=arguments.Contains("--input-fixture");
bool inputEnabled = fixtureInput || nxCopy is not null;
if(dualNx && (dualFixtureHandle is not null||inputEnabled||localConsole))
    throw new ArgumentException("Dual NX probe is read-only; no fixture or local input mode may be combined.");
if(localConsole && (!inputEnabled || dual))throw new ArgumentException("Local console requires one explicit input target, no dual source.");
if(fixtureInput && nxCopy is not null)throw new ArgumentException("Input profiles are mutually exclusive.");
bool jpeg = args.Contains("--jpeg");
bool highFrameRate=arguments.Remove("--60fps");
int fps=highFrameRate?60:30;
bool delayedSceneOutput=args.Contains("--delay-scene-output");
var profile=args.Contains("--1080p")?ProbeVideoProfile.FullHd:ProbeVideoProfile.Hd;
uint h264TargetBitrate=profile==ProbeVideoProfile.FullHd?10_000_000u:4_000_000u;
if(args.Count(a=>a=="--jpeg")>1 || args.Count(a=>a=="--input-fixture")>1 || args.Count(a=>a=="--1080p")>1 || args.Count(a=>a=="--60fps")>1)throw new ArgumentException("Duplicate mode flag.");
if(highFrameRate && jpeg)throw new ArgumentException("60 fps is an H.264-only experiment; JPEG remains on its bounded compatibility path.");
WindowInfo? captureTarget = SelectLocalTarget(arguments.Where(a=>a!="--input-fixture" && a!="--jpeg" && a!="--1080p" && a!="--delay-scene-output").ToArray());
bool ownedScene = args.Contains("--owned");
if(delayedSceneOutput && (args.Count(a=>a=="--delay-scene-output")!=1 || jpeg || inputEnabled || !ownedScene ||
    captureTarget?.Title!="TwinDesk SC03 media scene fixture — NOT NX / TIA"))
    throw new ArgumentException("Encoded delay is restricted to the read-only SC03 hardware fixture.");
if(jpeg && (!ownedScene || captureTarget is null))throw new ArgumentException("JPEG comparison requires an explicitly selected real --owned source.");
if(fixtureInput && (!ownedScene || captureTarget is null || captureTarget.Title!="TwinDesk F0 input fixture — NOT NX / TIA"
    || Path.GetFileName(captureTarget.ExecutablePath)!="dotnet.exe" && Path.GetFileName(captureTarget.ExecutablePath)!="Workbench.DesktopFixture.exe"))
    throw new ArgumentException("Input experiment requires the explicitly selected local F0 fixture and --owned; NX/TIA input is not enabled by this flag.");
if(nxCopy is not null && (!ownedScene || captureTarget is null))throw new ArgumentException("NX input requires an explicitly selected owned root.");
var nxScope=nxCopy is null?null:new NxProbeInputScope(nxCopy,Path.GetFullPath("artifacts/verification"),captureTarget!);
string inputScope=nxScope is null?"F0 loopback input only; no product authentication or NX/TIA acceptance"
    :"NX isolated-copy loopback input experiment; native path verification required; no production, TIA, LAN or endurance acceptance";
var sourceKind = captureTarget is null ? "pattern" : "window";
using var sharedGraphics=ownedScene?new CaptureGraphicsDevice():null;
WindowInfo? dualFixture=null;
if(dual)
{
    if(!ownedScene||delayedSceneOutput)throw new ArgumentException("Dual capture requires owned window scenes.");
    if(dualNx)
    {
        if(captureTarget is null||!string.Equals(captureTarget.ProcessName,"ugraf",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("First source must be an explicitly selected NX window.");
        dualFixture=SelectLocalTarget(new[]{"--process","ugraf","--window",dualNxHandle!});
        if(dualFixture is null||dualFixture.Handle==captureTarget.Handle||dualFixture.ProcessId==captureTarget.ProcessId||
            !string.Equals(dualFixture.ExecutablePath,captureTarget.ExecutablePath,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Two distinct NX processes and root windows are required.");
    }
    else
    {
        if(nxScope is null)throw new ArgumentException("Dual fixture comparison requires explicit NX copy input profile.");
        dualFixture=SelectLocalTarget(new[]{"--process",dualFixtureProcess!,"--window",dualFixtureHandle!});
        if(dualFixture is null||dualFixture.Title!="TwinDesk F0 input fixture — NOT NX / TIA"||
            Path.GetFileName(dualFixture.ExecutablePath)!="dotnet.exe"&&Path.GetFileName(dualFixture.ExecutablePath)!="Workbench.DesktopFixture.exe")
            throw new ArgumentException("Second source must be the explicit own F0 fixture, never an inferred TIA window.");
    }
}
using var firstLifetime=ownedScene?new ProbeCaptureLifetime(sharedGraphics!,captureTarget!,profile.Width,profile.Height,jpeg):null;
using var secondLifetime=dual?new ProbeCaptureLifetime(sharedGraphics!,dualFixture!,profile.Width,profile.Height,jpeg):null;
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls(loopbackOrigin);
var app = builder.Build();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var controlAdmission=new ProbeControlAdmission();
var hostInstanceId = Guid.NewGuid();
using var dualOverlay=dualNx?new DualNxOverlay(captureTarget!,dualFixture!):null;
if(dualOverlay is not null)app.Lifetime.ApplicationStopping.Register(dualOverlay.Dispose);

var buildIdentity = new[] { typeof(Program).Assembly.Location, typeof(H264Probe).Assembly.Location }.Select(path =>
{
    using var file = File.OpenRead(path);
    return new { file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(file)) };
}).ToArray();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
        || context.Request.Host.Value != $"127.0.0.1:{port}")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-src 'self'; frame-ancestors 'self'";
    await next(context);
});
app.UseWebSockets();
app.MapGet("/local-console.js",async context=>
{
    context.Response.ContentType="text/javascript; charset=utf-8";
    using var source=typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.local-console.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
app.MapGet("/scene-timeline.js", async context =>
{
    context.Response.ContentType = "text/javascript; charset=utf-8";
    using var source = typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.scene-timeline.js")!;
    await source.CopyToAsync(context.Response.Body, context.RequestAborted);
});
app.MapGet("/input-client.js", async context =>
{
    context.Response.ContentType="text/javascript; charset=utf-8";
    using var source=typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.input-client.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
app.MapGet("/frame-presenter.js", async context =>
{
    context.Response.ContentType="text/javascript; charset=utf-8";
    using var source=typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.frame-presenter.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
app.MapGet("/media-timings.js", async context =>
{
    context.Response.ContentType = "text/javascript; charset=utf-8";
    using var source = typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.media-timings.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
app.MapGet("/f0-pointer-calibration.js", async context =>
{
    context.Response.ContentType="text/javascript; charset=utf-8";
    using var source=typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.f0-pointer-calibration.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
app.MapGet("/jpeg-decoder.js", async context =>
{
    context.Response.ContentType="text/javascript; charset=utf-8";
    using var source=typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.jpeg-decoder.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
app.MapGet("/video-profile.js", async context =>
{
    context.Response.ContentType="text/javascript; charset=utf-8";
    using var source=typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.video-profile.js")!;
    await source.CopyToAsync(context.Response.Body,context.RequestAborted);
});
if(dual)
{
    app.MapGet("/",async context=>
    {
        context.Response.ContentType="text/html; charset=utf-8";
        using var html=typeof(Program).Assembly.GetManifestResourceStream(dualNx?"Workbench.MediaProbe.dual-nx.html":"Workbench.MediaProbe.dual.html")!;
        await html.CopyToAsync(context.Response.Body,context.RequestAborted);
    });
    if(dualNx)
    {
        app.MapGet("/overlay/status",() => Results.Json(dualOverlay!.Status()));
        app.MapPost("/overlay/start",async (HttpContext context) =>
        {
            if(context.Request.Headers.Origin != loopbackOrigin ||
               context.Request.ContentType is null ||
               !context.Request.ContentType.StartsWith("application/json",StringComparison.OrdinalIgnoreCase) ||
               context.Request.ContentLength is not long length || length is < 1 or > 2048)
            {
                dualOverlay!.RecordError("请求来源、内容类型或长度校验失败；仅本机同源页面可启动跨栏控制。");
                return Results.Json(new { error="仅本机同源页面可启动跨栏控制。" },statusCode:403);
            }
            try
            {
                var request=await context.Request.ReadFromJsonAsync<OverlayRequest>(cancellationToken:context.RequestAborted);
                if(request is null)return Results.Json(new { error="跨栏布局为空。" },statusCode:400);
                return Results.Json(new { message=dualOverlay!.Start(request) });
            }
            catch(Exception e) when(e is InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
            { dualOverlay!.RecordError(e.Message); return Results.Json(new { error=e.Message },statusCode:409); }
        });
        MapChannel("/left",1,captureTarget,firstLifetime,null,"Read-only first NX window; no input endpoint");
        MapChannel("/right",2,dualFixture,secondLifetime,null,"Read-only second NX window; no input endpoint");
    }
    else
    {
        MapChannel("/nx",1,captureTarget,firstLifetime,nxScope,inputScope);
        MapChannel("/f0",2,dualFixture,secondLifetime,null,"F0 loopback input only; dual NX comparison fixture, never TIA acceptance");
    }
}
else MapChannel("",1,captureTarget,firstLifetime,nxScope,inputScope);
await app.RunAsync();
void MapChannel(string prefix,uint streamId,WindowInfo? captureTarget,ProbeCaptureLifetime? captureLifetime,NxProbeInputScope? nxScope,string inputScope)
{
    var routes=app.MapGroup(prefix);
    int busy=0;
    var inputProbe=inputEnabled?new LoopbackInputProbe(captureTarget!,hostInstanceId,inputScope,nxScope is null?null:nxScope.Allows,streamId,controlAdmission,localConsole):null;
routes.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    using var source = typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.probe.html")!;
    await source.CopyToAsync(context.Response.Body, context.RequestAborted);
});
if(inputProbe is not null)routes.Map("/control",inputProbe.Serve);
routes.MapGet("/health/live", () => Results.Json(new { mode = inputEnabled?(nxScope is null?"loopback-f0-input-experiment":"loopback-nx-copy-input-experiment"):"loopback-readonly-media-probe", inputEnabled, localConsole, dualNx, inputTarget=dualNx||nxScope is not null?"NX":"F0", inputScope, partName=nxScope?.PartName, delayedSceneOutput, codec=jpeg?"jpeg":"h264", fps, profile, h264TargetBitrate=jpeg?(uint?)null:h264TargetBitrate, streamId, dual, source = sourceKind, ownedScene, busy = Volatile.Read(ref busy) != 0 }));
routes.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin != loopbackOrigin)
    { context.Response.StatusCode = 403; return; }
    bool observe=dual&&context.Request.Query["observe"]=="1";
    bool continuous=(localConsole||dualNx) && context.Request.Query["live"]=="1";
    int frameCount=18000;
    if (continuous ? context.Request.Query.Count!=1 || context.Request.Query["live"].Count!=1 :
        context.Request.Query.Count != (observe?2:1) || context.Request.Query["frames"].Count != 1
        || !int.TryParse(context.Request.Query["frames"], out frameCount) || frameCount is < 30 or > 18000)
    { context.Response.StatusCode = 400; return; }
    if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { context.Response.StatusCode = 409; return; }
    LoopbackInputProbe.VideoSession? inputVideo=null;
    try{inputVideo=observe?null:inputProbe?.BeginVideo();}
    catch(InvalidOperationException){Interlocked.Exchange(ref busy,0);context.Response.StatusCode=409;return;}
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    var h264Timings=jpeg?null:new H264ProbeTimings();
    var socketSendMs=new LatencyWindow();
    var frameEmissionIntervalMs=new LatencyWindow();
    var sourceFrameIntervalMs=new LatencyWindow();
    if(!continuous)cancellation.CancelAfter(TimeSpan.FromSeconds(frameCount / 30.0 + 30));
    try
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var receive = ObserveClient();
        uint sequence = 0;
        WgcNv12Source? windowSource = null;
        WgcOwnedNv12Source? sceneSource = null;
        try
        {
            uint announcedScene = 0;
            long previousEmissionAt=0,previousSourceTimestampUs=-1;
            void SendFrame(byte[] data,long timestampUs,bool keyFrame,string codecString,ProbeSceneConfig? scene)
            {
                long emissionAt=Stopwatch.GetTimestamp();
                if(previousEmissionAt!=0)frameEmissionIntervalMs.Record(Stopwatch.GetElapsedTime(previousEmissionAt,emissionAt).TotalMilliseconds);
                previousEmissionAt=emissionAt;
                if(!delayedSceneOutput && previousSourceTimestampUs>=0)
                {
                    if(timestampUs<=previousSourceTimestampUs)throw new InvalidDataException("Source frame timestamps must increase.");
                    sourceFrameIntervalMs.Record((timestampUs-previousSourceTimestampUs)/1000.0);
                }
                previousSourceTimestampUs=timestampUs;
                var version = scene?.Version ?? 1u;
                if(scene is not null)profile.RequireFrame(scene.Width,scene.Height);
                if (sequence == 0)
                    SendText(socket, new { type = "streamConfig", hostInstanceId, streamId, sceneVersion = version, epoch = 1,
                        width = profile.Width, height = profile.Height, codec = jpeg?"jpeg":"h264", codecString, format = jpeg?"jpeg":"annexb", source = sourceKind,
                        ownedScene, scene }, cancellation.Token);
                else if (version != announcedScene)
                    SendText(socket, new { type = "sceneConfig", hostInstanceId, streamId, epoch = 1, scene }, cancellation.Token);
                announcedScene = version;
                inputVideo?.BeforeFrame(scene,sequence+1);
                if(data.Length is < 1 or > 8*1024*1024)throw new InvalidDataException("Encoded payload out of budget.");
                var packet = new byte[checked(40 + data.Length)];
                "UGWB"u8.CopyTo(packet);
                packet[4] = 1; packet[5] = jpeg?(byte)2:(byte)1;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)(keyFrame ? 1 : 0));
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), streamId);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), version);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 1);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), ++sequence);
                BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24), checked((ulong)timestampUs));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(32), checked((ushort)profile.Width));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), checked((ushort)profile.Height));
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(36), (uint)data.Length);
                data.CopyTo(packet, 40);
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                sendTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                long sendStart=Stopwatch.GetTimestamp();
                try{socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, sendTimeout.Token).AsTask().GetAwaiter().GetResult();}
                finally{socketSendMs.Record(Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds);}
            }
            using var encodedDelay=delayedSceneOutput?new ProbeEncodedSceneDelay(frame=>SendFrame(frame.Data,frame.TimestampUs,frame.KeyFrame,frame.CodecString,frame.Scene)):null;
            void Encoded(EncodedAccessUnit frame)
            {if(encodedDelay is not null)encodedDelay.Push(frame);else SendFrame(frame.Data,frame.TimestampUs,frame.KeyFrame,frame.CodecString,frame.Scene);}
            ProbeCaptureLifetime.Lease RentScene()
            {
                var lease=captureLifetime!.Rent();
                try {sceneSource=lease.Source;inputVideo?.Bind(sceneSource);return lease;}
                catch {lease.Dispose();throw;}
            }
            object result = jpeg ? await Task.Run(() => JpegProbe.Run(TimeSpan.FromSeconds(frameCount/30.0),
                RentScene,
                frame=>SendFrame(frame.Data,frame.TimestampUs,true,"jpeg",frame.Scene),cancellation.Token,profile,continuous),cancellation.Token)
                : await Task.Run(() => H264Probe.Run(continuous?frameCount:checked(frameCount*(fps/30)), true,
                Encoded,
                cancellation.Token, width:profile.Width, height:profile.Height,fps:fps,sourceFactory: captureTarget is null ? null : ownedScene
                ? RentScene
                : () => windowSource = new WgcNv12Source(captureTarget, profile.Width, profile.Height),continuous:continuous,timings:h264Timings,targetBitrate:h264TargetBitrate), cancellation.Token);
            encodedDelay?.Complete();
            SendText(socket, new { type = "probeEnd", result }, cancellation.Token);
            var browser = await receive.WaitAsync(TimeSpan.FromSeconds(10));
            var directory = Path.GetFullPath("artifacts/verification/media-probe");
            Directory.CreateDirectory(directory);
            var reportPath = Path.Combine(directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.json");
            await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(report, new { scope = sourceKind == "pattern"
                ? "Generated pattern through actual hardware MFT + WS + browser decoder; not NX acceptance"
                : inputVideo is not null ? inputScope
                : "Read-only WGC window + WS + browser decoder; no product input or NX workflow acceptance",
                codec=jpeg?"jpeg":"h264",fps, profile,h264TargetBitrate=jpeg?(uint?)null:h264TargetBitrate, compatibilityReason=jpeg?"Explicit local comparison; not automatic fallback or hardware performance PASS":null,
                buildIdentity, encodedDelay, streamId,inputScope=inputVideo is not null?inputScope:null, diagnosticPart=nxScope?.PartName, inputDiagnostics=inputVideo?.Diagnostics, target = captureTarget, ownedScene, scenes = sceneSource?.SceneHistory,
                capturedFrames = sceneSource?.CapturedFrames ?? windowSource?.CapturedFrames,
                supersededFrames = sceneSource?.SupersededFrames ?? windowSource?.SupersededFrames,
                captureGeometryRetries = sceneSource?.CaptureGeometryRetries,
                transientBindingRetries = sceneSource?.TransientBindingRetries,
                sceneHistoryDropped = sceneSource?.SceneHistoryDropped,
                graphicsDeviceIdentity=sceneSource?.GraphicsDeviceIdentity,
                captureCountersScope="Persistent application capture lifetime; encoder result and browser counts are per connection",
                transportTiming=new {socketSendMs=socketSendMs.Snapshot(),frameEmissionIntervalMs=frameEmissionIntervalMs.Snapshot(),sourceFrameIntervalMs=sourceFrameIntervalMs.Snapshot()},
                receivedDestroyEvents = sceneSource?.ReceivedDestroyEvents, result, browser }, jsonOptions);
            app.Logger.LogInformation("Probe evidence: {ReportPath}", reportPath);
        }
        catch (Exception e)
        {
            bool userStopped=continuous && e is OperationCanceledException && inputVideo?.NormalLocalStop==true;
            if(userStopped)app.Logger.LogInformation("Local NX experiment ended with F12 after {Frames} frames", sequence);
            else app.Logger.LogWarning("Media probe failed: {Type} {Error}", e.GetType().Name, e.Message);
            var failureDirectory=Path.GetFullPath("artifacts/verification/media-probe");Directory.CreateDirectory(failureDirectory);
            await using(var failureReport=new FileStream(Path.Combine(failureDirectory,$"{(userStopped?"stopped":"failed")}-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.json"),FileMode.CreateNew))
                await JsonSerializer.SerializeAsync(failureReport,new {status=userStopped?"STOPPED":"FAIL",scope=userStopped?"User-ended local NX experiment; not workflow or latency PASS":"M1 probe attempt; not workflow acceptance",buildIdentity,inputEnabled,profile,fps,codec=jpeg?"jpeg":"h264",h264TargetBitrate=jpeg?(uint?)null:h264TargetBitrate,
                    streamId,inputScope=inputVideo is not null?inputScope:null, diagnosticPart=nxScope?.PartName,inputDiagnostics=inputVideo?.Diagnostics,errorType=e.GetType().Name,error=e.Message,
                    errorHresult=$"0x{e.HResult:X8}",errorStack=e.ToString(),
                    framesSent=sequence,h264Timing=h264Timings?.Snapshot(),
                    transportTiming=new {socketSendMs=socketSendMs.Snapshot(),frameEmissionIntervalMs=frameEmissionIntervalMs.Snapshot(),sourceFrameIntervalMs=sourceFrameIntervalMs.Snapshot()},scenes=sceneSource?.SceneHistory,
                    capturedFrames=sceneSource?.CapturedFrames??windowSource?.CapturedFrames,
                    captureGeometryRetries=sceneSource?.CaptureGeometryRetries,
                    transientBindingRetries=sceneSource?.TransientBindingRetries,
                    sceneHistoryDropped=sceneSource?.SceneHistoryDropped,
                    receivedDestroyEvents=sceneSource?.ReceivedDestroyEvents},jsonOptions);
            if (socket.State == WebSocketState.Open)
            {
                try { SendText(socket, new { type = "probeError", error = e.Message }, CancellationToken.None); }
                catch (Exception) { /* Broken transport is already being torn down. */ }
            }
        }
        finally
        {
            inputVideo?.Stop();
            cancellation.Cancel();
            socket.Abort();
            try { await receive; } catch (Exception) { }
        }

        async Task<JsonElement> ObserveClient()
        {
            try { return await ReceiveClientReport(socket, cancellation.Token); }
            catch { cancellation.Cancel(); throw; }
        }
    }
    finally { Interlocked.Exchange(ref busy, 0); }
});

}


static WindowInfo? SelectLocalTarget(string[] arguments)
{
    if (arguments.Length == 0) return null;
    if (arguments.Length == 5 && arguments[^1] == "--owned") arguments = arguments[..^1];
    if (arguments.Length != 4 || arguments.Where((_, i) => i % 2 == 0).Distinct().Count() != 2
        || !arguments.Contains("--process") || !arguments.Contains("--window"))
        throw new ArgumentException("Use no arguments for pattern, or --process <name> --window <current HWND> for read-only window capture.");
    string Value(string name)
    {
        int index = Array.IndexOf(arguments, name);
        if (index < 0 || index % 2 != 0 || index + 1 >= arguments.Length) throw new ArgumentException("Invalid source arguments.");
        return arguments[index + 1];
    }
    string processName = Value("--process");
    if (processName.Length is < 1 or > 128 || processName.IndexOfAny(['/', '\\', ':']) >= 0)
        throw new ArgumentException("Expected an already-running process name, not a path.");
    long handle = long.Parse(Value("--window"), System.Globalization.CultureInfo.InvariantCulture);
    var target = WindowCatalog.Find(processName).SingleOrDefault(w => w.Handle == handle)
        ?? throw new InvalidOperationException("Selected window no longer exists.");
    if (target.Minimized) throw new InvalidOperationException("Restore the selected window before starting the probe.");
    return target;
}

void SendText(WebSocket socket, object value, CancellationToken token)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(1));
    socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, jsonOptions)).AsMemory(),
        WebSocketMessageType.Text, true, timeout.Token).AsTask().GetAwaiter().GetResult();
}

static async Task<JsonElement> ReceiveClientReport(WebSocket socket, CancellationToken token)
{
    var buffer = new byte[128 * 1024];
    int offset = 0;
    while (true)
    {
        var received = await socket.ReceiveAsync(buffer.AsMemory(offset), token);
        if (received.MessageType != WebSocketMessageType.Text) throw new IOException("Browser closed or sent a non-report message.");
        offset += received.Count;
        if (received.EndOfMessage) break;
        if (offset == buffer.Length) throw new InvalidDataException("Browser report too large.");
    }
    using var json = JsonDocument.Parse(buffer.AsMemory(0, offset));
    if (json.RootElement.GetProperty("type").GetString() != "browserResult") throw new InvalidDataException("Expected browserResult.");
    return json.RootElement.Clone();
}
