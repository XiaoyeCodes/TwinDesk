using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// Real WGC refresh regression on this fixture only. Does not inject input or touch NX.
internal static class SceneRefreshVerification
{
    public static int Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence path must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var form=new Form {Text="Scene refresh verification — NOT NX",ClientSize=new(480,320),BackColor=Color.Blue};
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        bool finished=false;int exit=1;
        form.FormClosing+=(_,e)=>{if(!finished){cancel.Cancel();e.Cancel=true;}};
        form.Shown+=async(_,_)=>{
            long hwnd=form.Handle;
            try {exit=await Task.Run(()=>Verify(hwnd,output,cancel.Token));}
            finally {finished=true;form.Close();}
        };
        Application.Run(form);return exit;
    }
    private static int Verify(long hwnd,string output,CancellationToken token)
    {
        Exception? failure=null;var versions=new List<uint>();
        try
        {
            using var process=Process.GetCurrentProcess();
            var root=WindowCatalog.Find(process.ProcessName).Single(w=>w.Handle==hwnd);
            using var source=new WgcOwnedNv12Source(root,1280,720);
            void Frame()
            {
                var timer=Stopwatch.StartNew();
                while(timer.Elapsed<TimeSpan.FromSeconds(3))
                {
                    token.ThrowIfCancellationRequested();using var sample=source.TryGetSample();
                    if(sample is not null)return;Thread.Sleep(10);
                }
                throw new TimeoutException("Fresh WGC composition missing.");
            }
            Frame();versions.Add(source.InputBindings.Current!.Version);
            var geometry=source.InputBindings.Current!.Geometry;
            for(int i=0;i<10;i++)
            {
                source.RequestInputSceneRefresh();Frame();
                var current=source.InputBindings.Current!;
                if(current.Version<=versions[^1] || !geometry.SameGeometry(current.Geometry))
                    throw new InvalidDataException("Refresh must advance the composed version without inventing geometry.");
                versions.Add(current.Version);
            }
        }
        catch(Exception e){failure=e;}
        var binaries=new[]{typeof(SceneRefreshVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))});
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {
            status=failure is null?"PASS":"FAIL",scope="Own Windows fixture: ten actual WGC/NV12 scene refreshes with unchanged geometry. Not NX workflow, input, latency or endurance acceptance.",
            at=DateTimeOffset.Now,versions,buildIdentity=binaries,error=failure?.ToString()
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"Scene refresh: {(failure is null?"PASS":"FAIL")} {output}");
        return failure is null?0:1;
    }
}
