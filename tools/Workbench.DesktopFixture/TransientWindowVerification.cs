using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.MediaFoundation;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// Destroy only this fixture's native popup between enumeration and WGC binding.
// The scheduling seam changes timing, never capture pixels or native return values.
internal static class TransientWindowVerification
{
    public static int Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence output must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var root=new Form {Text="TwinDesk transient capture fixture — NOT NX/TIA",ClientSize=new(800,480),BackColor=Color.DarkBlue};
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        bool finished=false;int exit=1;
        root.FormClosing+=(_,e)=>{if(!finished){cancellation.Cancel();e.Cancel=true;}};
        root.Shown+=async(_,_)=>{
            try {exit=await Task.Run(()=>Verify(root,output,cancellation.Token));}
            finally {finished=true;root.Close();}
        };
        Application.Run(root);return exit;
    }

    private static int Verify(Form root,string output,CancellationToken token)
    {
        var checks=new List<object>();Exception? failure=null;WgcOwnedNv12Source? source=null;
        LayeredFixtureWindow? popup=null;int destroyedDuringBinding=0;bool rootCloseRejected=false;
        var binaries=new[]{typeof(TransientWindowVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
        void Ui(Action action) {token.ThrowIfCancellationRequested();root.Invoke(action);}
        try
        {
            MediaFactory.MFStartup(false).CheckError();
            nint handle=0;Ui(()=>handle=root.Handle);
            using var process=Process.GetCurrentProcess();
            var target=WindowCatalog.Find(process.ProcessName).Single(w=>w.Handle==handle);
            source=new(target,1280,720,false,window=>{
                if(window.Handle==target.Handle)return;
                Ui(()=>{if(popup is null||popup.Handle!=window.Handle)throw new InvalidOperationException("Unexpected fixture target.");popup.Dispose();popup=null;});
                destroyedDuringBinding++;
            });
            void WaitComplete(uint previous)
            {
                var wait=Stopwatch.StartNew();
                while(wait.Elapsed<TimeSpan.FromSeconds(3))
                {
                    token.ThrowIfCancellationRequested();using var sample=source.TryGetSample();
                    if(sample is not null && source.LastSampleScene is {NodeCount:1} scene && scene.Version>=previous &&
                        source.InputBindings.Current is { } current && current.Geometry.Nodes.All(n=>source.InputBindings.Verify(n.Window)))return;
                    Thread.Sleep(10);
                }
                throw new TimeoutException("No complete valid root scene after transient close.");
            }
            WaitComplete(0);
            for(int cycle=1;cycle<=20;cycle++)
            {
                Ui(()=>popup=new((nint)target.Handle,new(target.Bounds.X+100,target.Bounds.Y+100)));
                Thread.Sleep(120); // ensure next poll enumerates this actual popup
                var version=source.LastSampleScene!.Version;
                using(var sample=source.TryGetSample())
                    if(sample is not null || source.InputBindings.Current is not null)
                        throw new InvalidDataException("Transient reconciliation published a partial scene or retained input readiness.");
                WaitComplete(version);
                if(destroyedDuringBinding!=cycle||source.ActiveCaptureCount!=1)throw new InvalidDataException("Race was not exercised or dead node survived.");
                checks.Add(new {name=$"owned-close-during-bind-{cycle}",status="PASS",destroyedDuringBinding,active=source.ActiveCaptureCount});
            }
            source.Dispose();
            Ui(()=>popup=new((nint)target.Handle,new(target.Bounds.X+100,target.Bounds.Y+100)));
            var ephemeralRoot=WindowCatalog.Find(process.ProcessName).Single(w=>w.Handle==popup!.Handle);
            try
            {
                using var rejected=new WgcOwnedNv12Source(ephemeralRoot,1280,720,false,_=>Ui(()=>{popup!.Dispose();popup=null;}));
            }
            catch(Exception e) when(e.InnerException is ArgumentException { HResult:unchecked((int)0x80070057) })
            { rootCloseRejected=true; }
            if(!rootCloseRejected)throw new InvalidDataException("Root lifetime replacement was silently permitted.");
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        finally {root.Invoke((Action)(()=>popup?.Dispose()));source?.Dispose();MediaFactory.MFShutdown();}
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {
            status=failure is null?"PASS":"FAIL",scope="Real Windows WGC/GPU transient owner race ONLY; not NX/TIA, browser or endurance",
            at=DateTimeOffset.Now,buildIdentity=binaries,checks,destroyedDuringBinding,rootCloseRejected,
            transientBindingRetries=source?.TransientBindingRetries,activeAfterDispose=source?.ActiveCaptureCount,
            error=failure?.ToString(),errorHresult=failure is null?null:$"0x{failure.HResult:X8}"
        },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine($"Transient evidence: {output}");return failure is null?0:1;
    }
}
