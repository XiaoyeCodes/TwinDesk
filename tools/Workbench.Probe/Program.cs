using System.Text.Json;
using System.Globalization;
using Workbench.Windows;

WindowCatalog.SetDpiAwareness();
var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
try
{
    if(args.Length==3 && args[0]=="--benchmark-identity" && args[1]=="--process")
    {
        var processes=System.Diagnostics.Process.GetProcessesByName(args[2]);
        if(processes.Length!=1){foreach(var process in processes)process.Dispose();throw new InvalidOperationException("Identity benchmark requires exactly one matching live process.");}
        int pid=processes[0].Id;foreach(var process in processes)process.Dispose();
        var startTimes=new List<double>();var sessions=new List<double>();var directSessions=new List<double>();var modules=new List<double>();var desktops=new List<double>();
        for(int i=0;i<210;i++)
        {
            using var process=System.Diagnostics.Process.GetProcessById(pid);
            var watch=System.Diagnostics.Stopwatch.StartNew();_ = process.StartTime.ToUniversalTime();double startMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();int processSession=process.SessionId;double sessionMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();int directSession=WindowsInputEnvironment.ReadSessionId(pid);double directSessionMs=watch.Elapsed.TotalMilliseconds;
            if(processSession!=directSession)throw new InvalidDataException("Direct and Process session identity differ.");
            watch.Restart();_ = process.MainModule?.FileName;double moduleMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();_ = WindowsInputEnvironment.InteractiveDesktop();double desktopMs=watch.Elapsed.TotalMilliseconds;
            if(i>=10){startTimes.Add(startMs);sessions.Add(sessionMs);directSessions.Add(directSessionMs);modules.Add(moduleMs);desktops.Add(desktopMs);}
        }
        static object Dist(List<double> samples){samples.Sort();return new {p50Ms=samples[99],p95Ms=samples[189],maxMs=samples[^1]};}
        await PrintReportAsync(new {time=DateTimeOffset.Now,scope="Read-only metadata timing in fresh Process objects; no target integrity token or native input; NOT end-to-end latency",
            iterations=200,processStart=Dist(startTimes),session=Dist(sessions),directSession=Dist(directSessions),mainModule=Dist(modules),interactiveDesktop=Dist(desktops)});
        return 0;
    }
    if(args.Length==3 && args[0]=="--benchmark-catalog" && args[1]=="--process")
    {
        var processes=System.Diagnostics.Process.GetProcessesByName(args[2]);
        if(processes.Length!=1){foreach(var process in processes)process.Dispose();throw new InvalidOperationException("Catalog benchmark requires exactly one matching live process.");}
        using var benchmarkProcess=processes[0];
        var oldValues=new List<double>();var targetValues=new List<double>();int count=0;
        for(int i=0;i<210;i++)
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();var byName=WindowCatalog.Find(args[2]);
            double oldMs=watch.Elapsed.TotalMilliseconds;
            using var fresh=System.Diagnostics.Process.GetProcessById(benchmarkProcess.Id);
            watch.Restart();var byTarget=WindowCatalog.Find(fresh);
            double targetMs=watch.Elapsed.TotalMilliseconds;
            var expected=byName.Where(w=>w.ProcessId==benchmarkProcess.Id).ToArray();
            if(!expected.SequenceEqual(byTarget))throw new InvalidDataException("Catalog routes did not return identical target windows.");
            count=byTarget.Count;
            if(i>=10){oldValues.Add(oldMs);targetValues.Add(targetMs);}
        }
        static object Summary(List<double> values){values.Sort();return new {medianMs=values[100],p95Ms=values[189],maxMs=values[^1],meanMs=values.Average()};}
        await PrintReportAsync(new {time=DateTimeOffset.Now,scope="Read-only exact-window catalog comparison; NOT end-to-end input latency",iterations=200,count,
            byName=Summary(oldValues),byVerifiedProcess=Summary(targetValues),
            binarySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(WindowCatalog).Assembly.Location)))});
        return 0;
    }
    ValidateArguments();
    if (args.Contains("--help"))
    {
        Console.WriteLine("Workbench.Probe [--process ugraf] [--window <handle>] [--list | --children | --encoders | --displays] [--include-hidden] [--report <new.json>] [--output <new.png>] [--seconds 0..600]");
        Console.WriteLine("Workbench.Probe --encode-test [--software] [--frames 1..18000] [--output <new.h264>] [--report <new.json>]");
        Console.WriteLine("Workbench.Probe --encode-window [--owned] [--process ugraf] [--window <handle>] [--seconds 1..600] [--output <new.h264>] [--report <new.json>]");
        Console.WriteLine("Local diagnostic tool only. Never injects input or closes applications. --encoders only activates; --encode-test encodes a generated NV12 pattern, not NX/TIA.");
        return 0;
    }
    if (args.Contains("--displays"))
    {
        await PrintReportAsync(new { recordedAt = DateTimeOffset.Now, displays = DisplayCatalog.Enumerate() });
        return 0;
    }
    if (args.Contains("--encoders"))
    {
        await PrintReportAsync(new { recordedAt = DateTimeOffset.Now, stage = "enumeration-and-activation-only", encoders = EncoderCatalog.ProbeH264() });
        return 0;
    }
    if (args.Contains("--encode-test") || args.Contains("--encode-window"))
    {
        bool liveWindow = args.Contains("--encode-window");
        var observedSeconds = double.Parse(GetOption("--seconds") ?? "3", CultureInfo.InvariantCulture);
        if (!double.IsFinite(observedSeconds) || observedSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException("--seconds", "Use 1..600 seconds.");
        WindowInfo? captureTarget = liveWindow ? SelectWindow() : null;
        var encodedPath = Path.GetFullPath(GetOption("--output") ?? Path.Combine("artifacts", "verification", $"h264-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.h264"));
        var encodedReport = GetOption("--report") ?? Path.ChangeExtension(encodedPath, ".json");
        if (File.Exists(encodedReport) || File.Exists(encodedPath)) throw new IOException("Evidence output already exists; select new output paths.");
        var count = liveWindow ? (int)Math.Ceiling(observedSeconds * 30) : int.Parse(GetOption("--frames") ?? "90", CultureInfo.InvariantCulture);
        if (count is < 1 or > 18000) throw new ArgumentOutOfRangeException("--frames", "Use 1..18000 frames.");
        if (Path.GetFullPath(encodedReport).Equals(encodedPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Encoded data and report require different paths.");
        Directory.CreateDirectory(Path.GetDirectoryName(encodedPath)!);
        using var encodedFile = new FileStream(encodedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var frameIndex = new List<object>();
        using var encodeCancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; encodeCancellation.Cancel(); };
        WgcNv12Source? windowSource = null;
        WgcOwnedNv12Source? ownedSource = null;
        var encodedResult = await Task.Run(() => H264Probe.Run(count, !args.Contains("--software"), frame =>
        {
            frameIndex.Add(new { offset = encodedFile.Position, length = frame.Data.Length, frame.TimestampUs, frame.KeyFrame, frame.NalTypes, frame.Scene });
            encodedFile.Write(frame.Data);
        }, encodeCancellation.Token, sourceFactory: !liveWindow ? null : args.Contains("--owned")
            ? () => ownedSource = new WgcOwnedNv12Source(captureTarget!, 1280, 720)
            : () => windowSource = new WgcNv12Source(captureTarget!, 1280, 720)));
        encodedFile.Flush();
        await PrintReportAsync(new { result = encodedResult, path = encodedPath, target = captureTarget,
            capturedFrames = ownedSource?.CapturedFrames ?? windowSource?.CapturedFrames,
            supersededFrames = ownedSource?.SupersededFrames ?? windowSource?.SupersededFrames,
            scenes = ownedSource?.SceneHistory, frames = frameIndex }, encodedReport);
        return 0;
    }
    var processName = GetOption("--process") ?? "ugraf";
    var windows = WindowCatalog.Find(processName);
    if (args.Contains("--list"))
    {
        await PrintReportAsync(windows);
        return 0;
    }
    var selectedWindow = GetOption("--window");
    var rootWindows = selectedWindow is null ? windows.Where(w => w.Owner == 0).ToArray()
        : windows.Where(w => w.Handle == long.Parse(selectedWindow, CultureInfo.InvariantCulture)).ToArray();
    if (rootWindows.Length != 1)
        throw new InvalidOperationException($"Expected exactly one matching window for {processName}; found {rootWindows.Length}. Use --list and select --window explicitly.");
    var target = rootWindows[0];
    if (args.Contains("--children"))
    {
        await PrintReportAsync(WindowCatalog.Children((nint)target.Handle, args.Contains("--include-hidden")));
        return 0;
    }
    var output = GetOption("--output") ?? Path.Combine("artifacts", "verification", $"wgc-{DateTime.Now:yyyyMMdd-HHmmss}.png");
    var secondsOption = GetOption("--seconds");
    var seconds = secondsOption is null ? 3 : double.Parse(secondsOption, CultureInfo.InvariantCulture);
    if (!double.IsFinite(seconds) || seconds < 0 || seconds > 600)
        throw new ArgumentOutOfRangeException("--seconds", "Use a finite duration between 0 and 600 seconds.");
    var reportPath = GetOption("--report") ?? Path.ChangeExtension(Path.GetFullPath(output), ".json");
    if (File.Exists(reportPath) || File.Exists(output)) throw new IOException("Evidence output already exists; select new output paths.");
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var result = await WgcProbe.RunAsync(target, output, TimeSpan.FromSeconds(seconds), cancellation.Token);
    await PrintReportAsync(result, reportPath);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error=exception.Message, type=exception.GetType().Name, hresult=$"0x{exception.HResult:X8}" }, options));
    return 1;
}

string? GetOption(string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"Missing value for {name}.");
    return args[index + 1];
}

void ValidateArguments()
{
    string[] valueNames = ["--process", "--window", "--report", "--output", "--seconds", "--frames"];
    string[] flags = ["--help", "--list", "--children", "--encoders", "--displays", "--include-hidden", "--encode-test", "--encode-window", "--software", "--owned"];
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i++)
    {
        var name = args[i];
        if (!seen.Add(name)) throw new ArgumentException($"Duplicate option: {name}.");
        if (valueNames.Contains(name)) { _ = GetOption(name); i++; }
        else if (!flags.Contains(name)) throw new ArgumentException($"Unknown option: {name}.");
    }
    if (new[] {"--list","--children","--encoders","--displays","--encode-test","--encode-window"}.Count(seen.Contains) > 1)
        throw new ArgumentException("Choose one probe mode.");
    if (!seen.Contains("--encode-test") && (seen.Contains("--software") || seen.Contains("--frames")))
        throw new ArgumentException("--software/--frames require --encode-test.");
    if (seen.Contains("--encode-test") && new[] {"--seconds","--process","--window","--include-hidden"}.Any(seen.Contains))
        throw new ArgumentException("Window capture options are not valid for generated encoding tests.");
    if (seen.Contains("--include-hidden") && !seen.Contains("--children"))
        throw new ArgumentException("--include-hidden requires --children.");
    if (seen.Contains("--owned") && !seen.Contains("--encode-window"))
        throw new ArgumentException("--owned requires --encode-window.");
}

WindowInfo SelectWindow()
{
    var processName = GetOption("--process") ?? "ugraf";
    var windows = WindowCatalog.Find(processName);
    var handle = GetOption("--window");
    var selected = handle is null ? windows.Where(w => w.Owner == 0).ToArray()
        : windows.Where(w => w.Handle == long.Parse(handle, CultureInfo.InvariantCulture)).ToArray();
    if (selected.Length != 1) throw new InvalidOperationException("Select exactly one current window with --list and --window.");
    if (selected[0].Minimized) throw new InvalidOperationException("Target is minimized; restore locally before capturing.");
    return selected[0];
}

async Task PrintReportAsync<T>(T result, string? explicitPath = null)
{
    var json = JsonSerializer.Serialize(result, options);
    var reportPath = explicitPath ?? GetOption("--report");
    if (reportPath is not null)
    {
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await using var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(json);
    }
    Console.WriteLine(json);
}
