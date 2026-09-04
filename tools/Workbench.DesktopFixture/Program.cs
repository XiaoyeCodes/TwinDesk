using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.MediaFoundation;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--verify-input")return NativeInputVerification.Run(args[1]);
        if (args.SequenceEqual(["--interactive"]))
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var workbench=new InputFixtureForm();
            Application.Run(workbench);
            return 0;
        }
        if (args.Length != 4 || args[0] != "--verify-scene" || args[2] != "--cycles"
            || !int.TryParse(args[3], out int cycles) || cycles is < 1 or > 40)
        {
            Console.Error.WriteLine("Local synthetic Windows fixture only. --verify-scene <new output directory> --cycles <1..40>, or --interactive. No NX/TIA or injected input.");
            return 1;
        }
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("Output must not exist; historical evidence is never overwritten."); return 1; }
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var root = new Form { Text = "TwinDesk SC02 synthetic fixture — not NX/TIA", FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(120,160,960,600), BackColor = Color.Blue,
            AutoScaleMode = AutoScaleMode.None };
        var area = Screen.PrimaryScreen!.WorkingArea;
        root.Location = new Point(area.Left+80, area.Top+100);
        root.Controls.Add(new Label { Text = "TwinDesk · SC02 Windows fixture · automatic finite test · no injected input",
            ForeColor = Color.White, AutoSize = true, Location = new Point(20,20) });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        bool completed = false;
        int exit = 1;
        root.FormClosing += (_,e) => { if (!completed) { cancel.Cancel(); e.Cancel = true; } };
        root.Shown += async (_,_) =>
        {
            try { exit = await Task.Run(() => Verify(root, cycles, output, cancel.Token)); }
            catch (Exception e) { Console.Error.WriteLine(e); }
            finally { completed = true; root.Close(); }
        };
        Application.Run(root);
        return exit;
    }

    private static int Verify(Form root, int cycles, string output, CancellationToken cancellation)
    {
        var checks = new List<object>(); var resources = new List<object>();
        object? occlusionSetup = null;
        WgcOwnedNv12Source? source = null;
        LayeredFixtureWindow? popup = null;
        Form? unrelated = null, nested = null, modal = null;
        Exception? failure = null;
        bool mfStarted = false;
        var timer = Stopwatch.StartNew();
        WindowInfo? rootInfo = null;
        void Ui(Action action)
        {
            cancellation.ThrowIfCancellationRequested();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            root.BeginInvoke((Action)(() => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }));
            done.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation).GetAwaiter().GetResult();
        }
        void Resource(string name)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            resources.Add(new { name, atMs=timer.Elapsed.TotalMilliseconds, process.WorkingSet64, process.PrivateMemorySize64,
                process.HandleCount, gdi=GetGuiResources(process.Handle,0), user=GetGuiResources(process.Handle,1),
                activeCaptures=source?.ActiveCaptureCount, bindingsCreated=source?.CaptureBindingsCreated });
        }
        ScenePixelSnapshot Check(string name, int nodes, Func<ScenePixelSnapshot,bool> predicate, bool save = false)
        {
            var clock = Stopwatch.StartNew(); ScenePixelSnapshot? latest = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                cancellation.ThrowIfCancellationRequested();
                using var sample = source!.TryGetSample();
                if (sample is not null)
                {
                    latest = source.ReadbackForDiagnostics();
                    if (latest.Scene.Nodes.Count == nodes && predicate(latest))
                    {
                        checks.Add(new { name, status="PASS", version=latest.Version, nodes, elapsedMs=clock.Elapsed.TotalMilliseconds,
                            geometry=latest.Scene, pixels=ProbePixels(latest, rootInfo!) });
                        if (save) Save(latest,Path.Combine(output,name+".png"));
                        Console.WriteLine($"PASS {name}: version={latest.Version}, nodes={nodes}");
                        return latest;
                    }
                }
                Thread.Sleep(20);
            }
            if (latest is not null) Save(latest,Path.Combine(output,name+"-failed.png"));
            checks.Add(new { name, status="FAIL", version=latest?.Version, geometry=latest?.Scene,
                pixels=latest is null?null:ProbePixels(latest,rootInfo!) });
            throw new InvalidDataException($"{name}: expected {nodes} nodes and reference pixels within 5 seconds.");
        }
        try
        {
            MediaFactory.MFStartup(false).CheckError();
            mfStarted = true;
            nint rootHandle=0; Ui(()=>rootHandle=root.Handle);
            using var process=Process.GetCurrentProcess();
            rootInfo=WindowCatalog.Find(process.ProcessName).Single(w=>w.Handle==rootHandle && w.ProcessId==process.Id);
            source=new(rootInfo,1280,720);
            Check("root-blue",1,s=>Is(s,rootInfo,210,275,0,0,255),true);
            Resource("initial-root");
            Point position=new(rootInfo.Bounds.X+160,rootInfo.Bounds.Y+200);
            Ui(()=>popup=new(rootHandle,position));
            Check("alpha-columns",2,s=>Alpha(s,rootInfo) && s.Scene.Nodes.Any(n=>n.Window.Layered),true);
            nint occluderHandle = 0;
            Ui(()=> {
                unrelated=new Form { Text="TwinDesk unrelated magenta occluder", FormBorderStyle=FormBorderStyle.None,
                    StartPosition=FormStartPosition.Manual,Bounds=new(rootInfo.Bounds.X+20,rootInfo.Bounds.Y+360,200,160),BackColor=Color.Magenta,AutoScaleMode=AutoScaleMode.None,
                    TopMost=true }; // Only our own temporary occluder; do not move or hide other applications.
                unrelated.Show(); occluderHandle = unrelated.Handle;
                // A newly painted marker proves this is not a cached pre-occlusion root frame.
                root.Controls.Add(new Panel { Location=new Point(20,60),Size=new Size(40,30),BackColor=Color.Lime });
                root.Update();
            });
            Marshal.ThrowExceptionForHR(DwmFlush());
            nint cover = WindowFromPoint(new Point(rootInfo.Bounds.X+80,rootInfo.Bounds.Y+420));
            occlusionSetup = new { expectedHandle=(long)occluderHandle, actualHandle=(long)cover,
                point=new { x=rootInfo.Bounds.X+80,y=rootInfo.Bounds.Y+420 }, matches=cover==occluderHandle };
            if (cover != occluderHandle) throw new InvalidOperationException("Fixture occluder is not actually covering the reference point.");
            Check("unrelated-occluder-excluded",2,s=>Alpha(s,rootInfo) && Is(s,rootInfo,80,420,0,0,255)
                && Is(s,rootInfo,30,70,0,255,0) && s.Scene.Nodes.All(n=>n.Window.Handle!=occluderHandle),true);
            Ui(()=> {
                nested=new Form { Text="TwinDesk nested owner green", FormBorderStyle=FormBorderStyle.None,
                    StartPosition=FormStartPosition.Manual,Bounds=new(rootInfo.Bounds.X+900,rootInfo.Bounds.Y+470,160,100),BackColor=Color.Lime,AutoScaleMode=AutoScaleMode.None };
                nested.Show(popup);
            });
            Check("nested-outside-root",3,s=>Alpha(s,rootInfo) && Is(s,rootInfo,1010,520,0,255,0)
                && s.Scene.Bounds.Width>rootInfo.Bounds.Width,true);
            Ui(()=>{ nested!.Close(); nested.Dispose(); nested=null; popup!.Dispose();popup=null;unrelated!.Close();unrelated.Dispose();unrelated=null; });
            Check("owned-closed-root-restored",1,s=>Is(s,rootInfo,310,275,0,0,255),true);
            var modalShown=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(()=> {
                modal=new Form { Text="TwinDesk real modal fixture",FormBorderStyle=FormBorderStyle.None,
                    StartPosition=FormStartPosition.Manual,Bounds=new(rootInfo.Bounds.X+500,rootInfo.Bounds.Y+200,240,180),BackColor=Color.Yellow,AutoScaleMode=AutoScaleMode.None };
                modal.Shown+=(_,_)=>modalShown.TrySetResult();
                root.BeginInvoke((Action)(()=>{try { modal.ShowDialog(root); } catch(Exception e) { modalShown.TrySetException(e); }}));
            });
            modalShown.Task.WaitAsync(TimeSpan.FromSeconds(5),cancellation).GetAwaiter().GetResult();
            Check("modal-disables-root",2,s=>!s.Scene.Nodes.Single(n=>n.Window.Handle==rootHandle).Window.Enabled
                && Is(s,rootInfo,620,290,255,255,0),true);
            Ui(()=>{modal!.Close();modal.Dispose();modal=null;});
            Check("modal-closed-root-enabled",1,s=>s.Scene.Nodes[0].Window.Enabled && Is(s,rootInfo,620,290,0,0,255));
            Resource("before-cycles");
            for(int i=0;i<cycles;i++)
            {
                Ui(()=>popup=new(rootHandle,position));
                Check($"cycle-{i+1:00}-open",2,s=>Alpha(s,rootInfo));
                Ui(()=>{popup!.Dispose();popup=null;});
                Check($"cycle-{i+1:00}-closed",1,s=>Is(s,rootInfo,310,275,0,0,255));
                Resource($"cycle-{i+1:00}");
            }
        }
        catch(Exception e) { failure=e;Console.Error.WriteLine(e); }
        finally
        {
            // Close only windows this fixture owns; never terminate NX/TIA or unrelated processes.
            try { root.Invoke((Action)(()=>{modal?.Dispose();nested?.Dispose();popup?.Dispose();unrelated?.Dispose();})); } catch(Exception e) { failure??=e; }
            try { source?.Dispose(); Resource("after-source-disposed"); } catch(Exception e) { failure??=e; }
            if (mfStarted) MediaFactory.MFShutdown();
        }
        var identity=new[]{typeof(Program).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}.Select(path=>new {
            file=Path.GetFileName(path),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        using(var report=new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew))
            JsonSerializer.Serialize(report,new { status=failure is null?"PASS":"FAIL",scope="SC02 synthetic real Windows/WGC/GPU fixture; explicit CPU pixel readback; not browser/input/NX/TIA acceptance",
                at=DateTimeOffset.Now,buildIdentity=identity,rootInfo,cycles,checks,resources,scenes=source?.SceneHistory,
                resourceStatus="OBSERVED_NOT_ENDURANCE", occlusionSetup, error=failure?.ToString(),captured=source?.CapturedFrames,
                activeCapturesAfterDispose=source?.ActiveCaptureCount },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true});
        Console.WriteLine($"Evidence: {output}");
        return failure is null?0:1;
    }

    private static bool Alpha(ScenePixelSnapshot s, WindowInfo root) => Is(s,root,210,275,0,0,255)
        && Is(s,root,310,275,128,0,127) && Is(s,root,410,275,255,0,0);
    private static int[] Pixel(ScenePixelSnapshot s, WindowInfo root,int x,int y)
    {
        x+=root.Bounds.X-s.Scene.Bounds.X;y+=root.Bounds.Y-s.Scene.Bounds.Y;
        if(x<0 || y<0 || x>=s.Scene.Bounds.Width || y>=s.Scene.Bounds.Height)return [];
        int offset=y*s.Stride+x*4;return s.Bgra.AsSpan(offset,4).ToArray().Select(b=>(int)b).ToArray();
    }
    private static bool Is(ScenePixelSnapshot s, WindowInfo root,int x,int y,int r,int g,int b)
    {
        var p=Pixel(s,root,x,y);return p.Length==4 && Math.Abs(p[0]-b)<=2 && Math.Abs(p[1]-g)<=2 && Math.Abs(p[2]-r)<=2 && p[3]==255;
    }
    private static object ProbePixels(ScenePixelSnapshot s,WindowInfo root) => new {
        transparent=Pixel(s,root,210,275),half=Pixel(s,root,310,275),opaque=Pixel(s,root,410,275),
        occluded=Pixel(s,root,80,420),freshMarker=Pixel(s,root,30,70),outside=Pixel(s,root,1010,520),modal=Pixel(s,root,620,290) };
    private static void Save(ScenePixelSnapshot snapshot,string path)
    {
        using var bitmap=new Bitmap(snapshot.Scene.Bounds.Width,snapshot.Scene.Bounds.Height,PixelFormat.Format32bppArgb);
        var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try { for(int y=0;y<bitmap.Height;y++) Marshal.Copy(snapshot.Bgra,y*snapshot.Stride,data.Scan0+y*data.Stride,snapshot.Stride); }
        finally { bitmap.UnlockBits(data); }
        using var output=new FileStream(path,FileMode.CreateNew);bitmap.Save(output,ImageFormat.Png);
    }
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process,uint flag);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
