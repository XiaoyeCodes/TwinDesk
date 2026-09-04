using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// Separates real WGC sessions from scene composition, hooks, MF and codecs.
internal static class CaptureSessionVerification
{
    public static int Run(string mode,string output,int settleSeconds=0)
    {
        if(settleSeconds is <0 or >180)throw new ArgumentOutOfRangeException(nameof(settleSeconds));
        if(mode is not ("item" or "item-raw" or "item-factory" or "item-native" or "item-roinit" or "pool" or "session" or "copy"))throw new ArgumentException("Unknown capture session stage.");
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence directory must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var root=new Form {Text="TwinDesk WGC session isolation — NOT NX/TIA",ClientSize=new(480,320),BackColor=Color.Blue};
        int exit=1;bool finished=false;using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(120+settleSeconds));
        root.FormClosing+=(_,e)=>{if(!finished){e.Cancel=true;cancellation.Cancel();}};
        root.Shown+=async(_,_)=>{
            nint window=root.Handle;
            try {exit=await Task.Run(()=>Verify(window,mode,output,cancellation.Token,settleSeconds));}
            finally {finished=true;root.Close();}
        };
        Application.Run(root);return exit;
    }
    private static int Verify(nint window,string mode,string output,CancellationToken token,int settleSeconds)
    {
        var samples=new List<object>();int frames=0;Exception? failure=null;
        var clock=Stopwatch.StartNew();
        var binaries=new[]{typeof(CaptureSessionVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
        void Sample(int cycle)
        {
            using var p=Process.GetCurrentProcess();p.Refresh();var types=OwnHandleTypes.Snapshot();
            samples.Add(new {cycle,elapsedMs=clock.Elapsed.TotalMilliseconds,p.HandleCount,p.PrivateMemorySize64,handleTypes=types});
            Console.WriteLine($"{mode} {cycle}: handles={p.HandleCount} events={types.GetValueOrDefault("Event")} alpc={types.GetValueOrDefault("ALPC Port")} threads={types.GetValueOrDefault("Thread")}");
        }
        bool initialized=false;
        try
        {
            if(mode=="item-roinit"){Marshal.ThrowExceptionForHR(RoInitialize(1));initialized=true;}
            using(var graphics=new CaptureGraphicsDevice())
            using(var dxgi=graphics.Device.QueryInterface<IDXGIDevice>())
            using(var captureDevice=GraphicsCaptureInterop.FromDxgiDevice(dxgi.NativePointer))
            using(var factory=mode is "item-factory" or "item-native"?new GraphicsCaptureInterop.ItemFactory():null)
            {
                Sample(0);
                for(int cycle=1;cycle<=60;cycle++)
                {
                    token.ThrowIfCancellationRequested();
                    frames+=Cycle(window,mode,graphics,captureDevice,token,factory);
                    if(cycle%10==0){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Sample(cycle);}
                }
            }
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Sample(61);
            for(int elapsed=0;elapsed<settleSeconds;)
            {
                int seconds=Math.Min(10,settleSeconds-elapsed);
                if(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))token.ThrowIfCancellationRequested();
                elapsed+=seconds;Sample(61+elapsed); // No forced collection or new items during idle settling.
            }
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        finally {if(initialized)RoUninitialize();}
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {
            status=failure is null?"OBSERVED_NOT_ENDURANCE":"FAIL",scope="Own native window WGC stages only; no scene compositor, window monitor, MF, codec or input",
            at=DateTimeOffset.Now,mode,settleSeconds,buildIdentity=binaries,frames,samples,error=failure?.ToString()
        },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine($"WGC session evidence: {output}");return failure is null?0:1;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Cycle(nint window,string mode,CaptureGraphicsDevice graphics,
        global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice captureDevice,CancellationToken token,GraphicsCaptureInterop.ItemFactory? factory)
    {
        if(mode=="item-native"){var pointer=factory!.CreateNativeItem(window);Marshal.Release(pointer);return 0;}
        using var temporary=mode=="item-raw"?new GraphicsCaptureInterop.ItemFactory():null;
        var item=(factory??temporary)?.ForWindow(window)??GraphicsCaptureInterop.ForWindow(window);
        if(mode.StartsWith("item",StringComparison.Ordinal))return 0;
        using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
        if(mode=="pool")return 0;
        using var session=pool.CreateCaptureSession(item);session.StartCapture();
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(3))
        {
            token.ThrowIfCancellationRequested();using var frame=pool.TryGetNextFrame();
            if(frame is not null)
            {
                if(mode=="copy")
                {
                    using var surface=GraphicsCaptureInterop.GetTexture(frame.Surface);
                    using var copy=graphics.Device.CreateTexture2D(surface.Description with {BindFlags=BindFlags.ShaderResource});
                    graphics.Context.CopyResource(copy,surface);graphics.Context.Flush();
                }
                return 1;
            }
            Thread.Sleep(5);
        }
        throw new TimeoutException("Real WGC session did not produce a frame.");
    }
    [DllImport("combase.dll")]private static extern int RoInitialize(uint initializationType);
    [DllImport("combase.dll")]private static extern void RoUninitialize();
}
