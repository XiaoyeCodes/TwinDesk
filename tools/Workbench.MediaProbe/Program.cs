using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Workbench.Windows;

// Diagnostic source chosen locally at startup, never by a browser-supplied HWND/process.
// No input and no LAN/public exposure. This is not the authenticated product Host.
WindowCatalog.SetDpiAwareness();
WindowInfo? captureTarget = SelectLocalTarget(args);
bool ownedScene = args.Contains("--owned");
var sourceKind = captureTarget is null ? "pattern" : "window";
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:8091");
var app = builder.Build();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
int busy = 0;
var hostInstanceId = Guid.NewGuid();
var buildIdentity = new[] { typeof(Program).Assembly.Location, typeof(H264Probe).Assembly.Location }.Select(path =>
{
    using var file = File.OpenRead(path);
    return new { file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(file)) };
}).ToArray();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
        || context.Request.Host.Value != "127.0.0.1:8091")
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
    await next(context);
});
app.UseWebSockets();
app.MapGet("/", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    using var source = typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.probe.html")!;
    await source.CopyToAsync(context.Response.Body, context.RequestAborted);
});
app.MapGet("/scene-timeline.js", async context =>
{
    context.Response.ContentType = "text/javascript; charset=utf-8";
    using var source = typeof(Program).Assembly.GetManifestResourceStream("Workbench.MediaProbe.scene-timeline.js")!;
    await source.CopyToAsync(context.Response.Body, context.RequestAborted);
});
app.MapGet("/health/live", () => Results.Json(new { mode = "loopback-readonly-media-probe", source = sourceKind, ownedScene, busy = Volatile.Read(ref busy) != 0 }));
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin != "http://127.0.0.1:8091")
    { context.Response.StatusCode = 403; return; }
    if (context.Request.Query.Count != 1 || context.Request.Query["frames"].Count != 1
        || !int.TryParse(context.Request.Query["frames"], out int frameCount) || frameCount is < 30 or > 18000)
    { context.Response.StatusCode = 400; return; }
    if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { context.Response.StatusCode = 409; return; }
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    cancellation.CancelAfter(TimeSpan.FromSeconds(frameCount / 30.0 + 30));
    try
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var receive = ObserveClient();
        try
        {
            uint sequence = 0;
            uint announcedScene = 0;
            WgcNv12Source? windowSource = null;
            WgcOwnedNv12Source? sceneSource = null;
            var result = await Task.Run(() => H264Probe.Run(frameCount, true, frame =>
            {
                var version = frame.Scene?.Version ?? 1u;
                if (sequence == 0)
                    SendText(socket, new { type = "streamConfig", hostInstanceId, streamId = 1, sceneVersion = version, epoch = 1,
                        width = 1280, height = 720, codec = "h264", codecString = frame.CodecString, format = "annexb", source = sourceKind,
                        ownedScene, scene = frame.Scene }, cancellation.Token);
                else if (version != announcedScene)
                    SendText(socket, new { type = "sceneConfig", hostInstanceId, streamId = 1, epoch = 1, scene = frame.Scene }, cancellation.Token);
                announcedScene = version;
                var packet = new byte[checked(40 + frame.Data.Length)];
                "UGWB"u8.CopyTo(packet);
                packet[4] = 1; packet[5] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)(frame.KeyFrame ? 1 : 0));
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 1);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), version);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 1);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), ++sequence);
                BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(24), checked((ulong)frame.TimestampUs));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(32), 1280);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(34), 720);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(36), (uint)frame.Data.Length);
                frame.Data.CopyTo(packet, 40);
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                sendTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, sendTimeout.Token).AsTask().GetAwaiter().GetResult();
            }, cancellation.Token, sourceFactory: captureTarget is null ? null : ownedScene
                ? () => sceneSource = new WgcOwnedNv12Source(captureTarget, 1280, 720)
                : () => windowSource = new WgcNv12Source(captureTarget, 1280, 720)), cancellation.Token);
            SendText(socket, new { type = "probeEnd", result }, cancellation.Token);
            var browser = await receive.WaitAsync(TimeSpan.FromSeconds(10));
            var directory = Path.GetFullPath("artifacts/verification/media-probe");
            Directory.CreateDirectory(directory);
            var reportPath = Path.Combine(directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.json");
            await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(report, new { scope = sourceKind == "pattern"
                ? "Generated pattern through actual hardware MFT + WS + browser decoder; not NX acceptance"
                : "Read-only WGC window + D3D NV12 + hardware MFT + WS + browser decoder; no product input or NX workflow acceptance",
                buildIdentity, target = captureTarget, ownedScene, scenes = sceneSource?.SceneHistory,
                capturedFrames = sceneSource?.CapturedFrames ?? windowSource?.CapturedFrames,
                supersededFrames = sceneSource?.SupersededFrames ?? windowSource?.SupersededFrames, result, browser }, jsonOptions);
            app.Logger.LogInformation("Probe evidence: {ReportPath}", reportPath);
        }
        catch (Exception e)
        {
            app.Logger.LogWarning("Media probe failed: {Type} {Error}", e.GetType().Name, e.Message);
            if (socket.State == WebSocketState.Open)
            {
                try { SendText(socket, new { type = "probeError", error = e.Message }, CancellationToken.None); }
                catch (Exception) { /* Broken transport is already being torn down. */ }
            }
        }
        finally
        {
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
await app.RunAsync();

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
    var buffer = new byte[16 * 1024];
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
