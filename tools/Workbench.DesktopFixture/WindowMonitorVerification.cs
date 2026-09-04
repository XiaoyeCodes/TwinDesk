using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// Own native windows + actual destroy notifications only. No WGC/Direct3D/media/input.
internal static class WindowMonitorVerification
{
    public static int Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence directory must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var root=new Form {Text="TwinDesk window monitor resources — NOT NX/TIA",ClientSize=new(480,320)};
        int exit=1;bool finished=false;using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        root.FormClosing+=(_,e)=>{if(!finished){e.Cancel=true;cancellation.Cancel();}};
        root.Shown+=async(_,_)=>{
            try {exit=await Task.Run(()=>Verify(root,output,cancellation.Token));}
            finally {finished=true;root.Close();}
        };
        Application.Run(root);return exit;
    }
    private static int Verify(Form root,string output,CancellationToken token)
    {
        var samples=new List<object>();int callbacks=0;Exception? failure=null;
        var binaries=new[]{typeof(WindowMonitorVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
        void Sample(int cycle)
        {
            using var p=Process.GetCurrentProcess();p.Refresh();var types=OwnHandleTypes.Snapshot();
            samples.Add(new {cycle,p.HandleCount,p.PrivateMemorySize64,handleTypes=types});
            Console.WriteLine($"monitor {cycle}: handles={p.HandleCount} events={types.GetValueOrDefault("Event")} alpc={types.GetValueOrDefault("ALPC Port")} threads={types.GetValueOrDefault("Thread")}");
        }
        try
        {
            Sample(0);
            for(int cycle=1;cycle<=60;cycle++)
            {
                token.ThrowIfCancellationRequested();Form? popup=null;
                try
                {
                    using var monitor=new WindowLifetimeMonitor(Environment.ProcessId);
                    nint handle=0;
                    root.Invoke((Action)(()=>{popup=new Form {Text="Own destroy notification",ClientSize=new(180,100)};popup.Show(root);handle=popup.Handle;}));
                    using var closed=new ManualResetEventSlim();
                    using var registration=monitor.Register(handle,()=>{Interlocked.Increment(ref callbacks);closed.Set();});
                    root.Invoke((Action)(()=>popup!.Dispose()));
                    if(!closed.Wait(TimeSpan.FromSeconds(2),token)||registration.Alive||monitor.ReceivedDestroyEvents!=1)
                        throw new InvalidDataException("Expected one actual native destroy callback and retired registration.");
                }
                finally {root.Invoke((Action)(()=>popup?.Dispose()));}
                if(cycle%10==0){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Sample(cycle);}
            }
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {
            status=failure is null?"OBSERVED_NOT_ENDURANCE":"FAIL",scope="60 real own window destroy callbacks / separate monitors; no WGC/GPU/media/input",
            at=DateTimeOffset.Now,buildIdentity=binaries,callbacks,samples,error=failure?.ToString()
        },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine($"Monitor evidence: {output}");return failure is null?0:1;
    }
}
