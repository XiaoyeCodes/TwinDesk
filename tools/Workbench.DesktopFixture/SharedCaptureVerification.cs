using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

internal static class SharedCaptureVerification
{
    public static int Run(string output,bool bgra=false,bool trend=false)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence directory must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var root=CreateForm("TwinDesk shared GPU A — NOT NX/TIA",Color.Blue,new(100,180));
        using var second=CreateForm("TwinDesk shared GPU B — NOT NX/TIA",Color.Lime,new(630,180));
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(trend?6:3));
        bool finished=false;int exit=1;
        root.FormClosing+=(_,e)=>{if(!finished){cancellation.Cancel();e.Cancel=true;}};
        root.Shown+=async(_,_)=>{
            second.Show();
            try {exit=await Task.Run(()=>Verify(root,second,output,cancellation.Token,bgra,trend));}
            finally {finished=true;second.Close();root.Close();}
        };
        Application.Run(root);return exit;
    }
    private static Form CreateForm(string title,Color color,Point location)=>new() {
        Text=title,FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,
        Location=location,ClientSize=new(480,320),BackColor=color,AutoScaleMode=AutoScaleMode.None};

    private static int Verify(Form root,Form second,string output,CancellationToken token,bool bgra,bool trend)
    {
        var checks=new List<object>();var resources=new List<object>();Exception? failure=null;
        var clock=Stopwatch.StartNew();int appCycles=trend?180:12;
        var binaries=new[]{typeof(SharedCaptureVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
        void Ui(Action action){token.ThrowIfCancellationRequested();root.Invoke(action);}
        WindowInfo Target(Form form)
        {
            nint handle=0;Ui(()=>handle=form.Handle);
            using var p=Process.GetCurrentProcess();return WindowCatalog.Find(p.ProcessName).Single(w=>w.Handle==handle);
        }
        void Check(string name,bool pass){if(!pass)throw new InvalidDataException(name);checks.Add(new {name,status="PASS"});Console.WriteLine($"PASS {name}");}
        void Resource(string phase,int cycle)
        {
            using var p=Process.GetCurrentProcess();p.Refresh();var types=OwnHandleTypes.Snapshot();
            resources.Add(new {phase,cycle,elapsedMs=clock.Elapsed.TotalMilliseconds,p.HandleCount,p.PrivateMemorySize64,
                gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2),handleTypes=types});
            Console.WriteLine($"{phase} {cycle}: handles={p.HandleCount} events={types.GetValueOrDefault("Event")} alpc={types.GetValueOrDefault("ALPC Port")}");
        }
        bool Reject(Action action){try {action();return false;}catch(InvalidOperationException){return true;}}
        void WaitFrame(ProbeCaptureLifetime.Lease lease)
        {
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(3))
            {
                token.ThrowIfCancellationRequested();
                if(bgra){if(lease.TryGetBgraFrame() is not null)return;}
                else {using var frame=lease.TryGetSample();if(frame is not null)return;}
                Thread.Sleep(10);
            }
            throw new TimeoutException("Stream lease failed to produce a fresh complete frame.");
        }
        try
        {
            using(var graphics=new CaptureGraphicsDevice())
            using(var appA=new ProbeCaptureLifetime(graphics,Target(root),1280,720,true))
            using(var appB=new ProbeCaptureLifetime(graphics,Target(second),1280,720,true))
            {
                using var a=appA.Rent();using var b=appB.Rent();
                Check("two-source-device-identity",a.Source.GraphicsDeviceIdentity==b.Source.GraphicsDeviceIdentity && graphics.ActiveSources==2);
                Check("device-dispose-rejects-active-sources",Reject(graphics.Dispose));
                Check("third-source-budget",Reject(()=>{using var extra=new WgcOwnedNv12Source(graphics,Target(root),1280,720);}));
                Task Worker(Form form,ProbeCaptureLifetime.Lease lease,Color first,Color next)=>Task.Run(()=>{
                    for(int i=0;i<20;i++)
                    {
                        var expected=(i&1)==0?first:next;Ui(()=>{form.BackColor=expected;form.Refresh();});
                        var watch=Stopwatch.StartNew();bool matched=false;
                        while(watch.Elapsed<TimeSpan.FromSeconds(3))
                        {
                            token.ThrowIfCancellationRequested();var frame=lease.TryGetBgraFrame();
                            if(frame is not null)
                            {
                                int offset=(frame.Height/2*frame.Width+frame.Width/2)*4;
                                var pixel=frame.Pixels;
                                if(pixel[offset]==expected.B && pixel[offset+1]==expected.G && pixel[offset+2]==expected.R){matched=true;break;}
                                bool ownPalette=(pixel[offset]==first.B&&pixel[offset+1]==first.G&&pixel[offset+2]==first.R)||
                                    (pixel[offset]==next.B&&pixel[offset+1]==next.G&&pixel[offset+2]==next.R);
                                if(!ownPalette)throw new InvalidDataException("Cross-source or invalid GPU color.");
                            }
                            Thread.Sleep(10);
                        }
                        if(!matched)throw new TimeoutException("Source did not show its own changed pixels.");
                    }
                },token);
                Task.WhenAll(Worker(root,a,Color.Blue,Color.Red),Worker(second,b,Color.Lime,Color.Yellow)).GetAwaiter().GetResult();
                Check("40-concurrent-real-WGC-color-changes",true);
                var oldA=a.Source.InputBindings.Current!.Geometry.Nodes.Single().Window;
                var oldB=b.Source.InputBindings.Current!.Geometry.Nodes.Single().Window;
                graphics.Retire();
                Check("explicit-retirement-revokes-both-input-bindings",!a.Source.InputBindings.Verify(oldA)&&!b.Source.InputBindings.Verify(oldB));
                Check("retired-device-rejects-both-streams",Reject(()=>a.TryGetBgraFrame())&&Reject(()=>b.TryGetBgraFrame()));
                Check("retired-device-rejects-new-source",Reject(()=>{using var extra=new WgcOwnedNv12Source(graphics,Target(root),1280,720);}));
            }
            using(var graphics=new CaptureGraphicsDevice())
            {
                using(var app=new ProbeCaptureLifetime(graphics,Target(root),1280,720,bgra))
                {
                    WgcOwnedNv12Source? expectedSource=null;
                    for(int cycle=1;cycle<=60;cycle++)
                    {
                        var lease=app.Rent();
                        using(lease)
                        {
                            expectedSource??=lease.Source;
                            if(!ReferenceEquals(expectedSource,lease.Source))throw new InvalidDataException("Reconnect recreated application capture.");
                            if(cycle==1){Check("double-encoder-lease-rejected",Reject(()=>app.Rent()));Check("active-encoder-owner-dispose-rejected",Reject(app.Dispose));}
                            WaitFrame(lease);
                        }
                        if(expectedSource.InputBindings.Current is not null)throw new InvalidDataException("Ended stream retained input readiness.");
                        bool expired=false;try {using var sample=lease.TryGetSample();}catch(ObjectDisposedException){expired=true;}
                        if(!expired)throw new InvalidDataException("Expired stream lease still sampled.");
                        if(cycle%10==0){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Resource("persistent-capture-after-diagnostic-gc",cycle);}
                    }
                    Check("60-reconnects-fresh-frames-no-stale-lease-or-input",true);
                }
                Check("application-dispose-releases-GPU-lease",graphics.ActiveSources==0);
                for(int cycle=1;cycle<=appCycles;cycle++)
                {
                    Form? transient=null;
                    try
                    {
                        Ui(()=>{transient=CreateForm("TwinDesk owned test application lifetime",Color.Blue,new(100,180));transient.Show();});
                        using var app=new ProbeCaptureLifetime(graphics,Target(transient!),1280,720,bgra);
                        using var lease=app.Rent();WaitFrame(lease);
                        Ui(()=>transient!.Dispose());
                        var watch=Stopwatch.StartNew();
                        while(lease.Source.ReceivedDestroyEvents==0&&watch.Elapsed<TimeSpan.FromSeconds(1)){token.ThrowIfCancellationRequested();Thread.Sleep(1);}
                        if(!Reject(()=>{if(bgra)lease.TryGetBgraFrame();else {using var sample=lease.TryGetSample();}}))throw new InvalidDataException("Closed application allowed its stream.");
                    }
                    finally {root.Invoke((Action)(()=>transient?.Dispose()));}
                    if(trend)
                    {
                        if(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))token.ThrowIfCancellationRequested();
                        if(cycle%10==0)Resource("actual-app-close-without-forced-gc",cycle);
                    }
                    else {GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Resource("actual-app-close-after-diagnostic-gc",cycle);}
                }
                Check($"{appCycles}-actual-app-closures-reject-old-stream",true);
                Check("all-GPU-leases-released",graphics.ActiveSources==0);
            }
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Resource("all-devices-disposed",0);
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {
            status=failure is null?"PASS":"FAIL",scope="Real Windows fixture / two GPU sources / capture lifetime / explicit retirement only. Not browser input, NX/TIA, native device reset or endurance",
            at=DateTimeOffset.Now,bgra,trend,appCycles,buildIdentity=binaries,checks,resources,error=failure?.ToString()
        },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine($"Shared capture evidence: {output}");return failure is null?0:1;
    }
}
