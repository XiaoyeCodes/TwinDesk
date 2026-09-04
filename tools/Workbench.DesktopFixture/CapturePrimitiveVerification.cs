using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// No UI, capture session, frame pool, input or user application; isolates source-owned primitives.
internal static class CapturePrimitiveVerification
{
    public static int Run(string mode,string output)
    {
        if(mode is not ("hook" or "d3d" or "manager" or "manager-shared" or "nv12-shared" or "winrt-device" or "compositor" or "compositor-clear" or "compositor-wait" or "compositor-warp" or "compositor-no-workers" or "compositor-no-video" or "compositor-shared" or "compositor-init" or "gpu-clear"))throw new ArgumentException("Unknown primitive.");
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence path must be new.");
        Directory.CreateDirectory(output);
        var samples=new List<object>();var finalDeviceReferences=new List<int>();Exception? failure=null;
        ID3D11Device? sharedDevice=null;ID3D11DeviceContext? sharedContext=null;
        var binaries=new[]{typeof(CapturePrimitiveVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(p=>new {file=Path.GetFileName(p),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
        void Sample(string stage,int round)
        {
            using var p=Process.GetCurrentProcess();p.Refresh();var types=OwnHandleTypes.Snapshot();
            samples.Add(new {stage,round,p.HandleCount,p.PrivateMemorySize64,handleTypes=types});
            Console.WriteLine($"{mode} {stage} round={round} handles={p.HandleCount} events={types.GetValueOrDefault("Event")} alpc={types.GetValueOrDefault("ALPC Port")}");
        }
        try
        {
            MediaFactory.MFStartup(false).CheckError();Sample("before",0);
            if(mode.EndsWith("-shared",StringComparison.Ordinal))
                D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport|DeviceCreationFlags.VideoSupport,
                    [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0],out sharedDevice,out sharedContext).CheckError();
            for(int round=1;round<=3;round++)
            {
                for(int cycle=0;cycle<20;cycle++)
                    if(sharedDevice is not null)Exercise(sharedDevice,sharedContext!,mode[..^7]);
                    else finalDeviceReferences.Add(Allocate(mode));
                Sample("disposed",round);
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Thread.Sleep(200);
                Sample("after-diagnostic-gc",round);
            }
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        finally {sharedContext?.ClearState();sharedContext?.Flush();sharedContext?.Dispose();sharedDevice?.Dispose();MediaFactory.MFShutdown();}
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new {
            status=failure is null?"OBSERVED_NOT_ENDURANCE":"FAIL",scope="Own process primitive allocation only; no capture windows/frames/input/NX/TIA",
            mode,rounds=3,cyclesPerRound=20,at=DateTimeOffset.Now,buildIdentity=binaries,samples,finalDeviceReferences,error=failure?.ToString()
        },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
        Console.WriteLine($"Primitive evidence: {output}");return failure is null?0:1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Allocate(string mode)
    {
        if(mode=="hook") {using var monitor=new WindowLifetimeMonitor(Environment.ProcessId);return -1;}
        D3D11.D3D11CreateDevice(null,mode=="compositor-warp"?DriverType.Warp:DriverType.Hardware,
            (mode is "compositor-warp" or "compositor-no-video"?DeviceCreationFlags.BgraSupport:DeviceCreationFlags.BgraSupport|DeviceCreationFlags.VideoSupport) | (mode=="compositor-no-workers"?DeviceCreationFlags.PreventInternalThreadingOptimizations:0),
            [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
        nint pointer=device.NativePointer;Marshal.AddRef(pointer);int finalReferences=-1;
        try {
        using(device)using(context)
            Exercise(device,context,mode);
        } finally {finalReferences=Marshal.Release(pointer);}
        return finalReferences;
    }

    private static void Exercise(ID3D11Device device,ID3D11DeviceContext context,string mode)
    {
            using(var mt=context.QueryInterface<ID3D11Multithread>())mt.SetMultithreadProtected(true);
            if(mode=="nv12")
            {
                using var videoDevice=device.QueryInterface<ID3D11VideoDevice>();
                using var videoContext=context.QueryInterface<ID3D11VideoContext>();
                using var enumerator=videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription {
                    InputFrameFormat=VideoFrameFormat.Progressive,InputWidth=320,InputHeight=240,OutputWidth=320,OutputHeight=240,
                    InputFrameRate=new(30,1),OutputFrameRate=new(30,1),Usage=VideoUsage.PlaybackNormal});
                using var processor=videoDevice.CreateVideoProcessor(enumerator,0);
                using var input=device.CreateTexture2D(new Texture2DDescription {Width=320,Height=240,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),BindFlags=BindFlags.RenderTarget});
                using var target=device.CreateRenderTargetView(input);context.ClearRenderTargetView(target,new Color4(0,0,1,1));
                using var output=device.CreateTexture2D(input.Description with {Format=Format.NV12});
                using var inputView=videoDevice.CreateVideoProcessorInputView(input,enumerator,new VideoProcessorInputViewDescription {ViewDimension=VideoProcessorInputViewDimension.Texture2D});
                using var outputView=videoDevice.CreateVideoProcessorOutputView(output,enumerator,new VideoProcessorOutputViewDescription {ViewDimension=VideoProcessorOutputViewDimension.Texture2D});
                videoContext.VideoProcessorBlt(processor,outputView,0,[new VideoProcessorStream {Enable=true,InputSurface=inputView}]).CheckError();
                context.Flush();
            }
            if(mode=="manager")
            {
                using var manager=MediaFactory.MFCreateDXGIDeviceManager();manager.ResetDevice(device).CheckError();
            }
            if(mode=="winrt-device")
            {
                using var dxgi=device.QueryInterface<IDXGIDevice>();
                using var wrapped=GraphicsCaptureInterop.FromDxgiDevice(dxgi.NativePointer);
            }
            if(mode=="compositor-init") {using var compositor=new GpuSceneCompositor(device,context);}
            if(mode=="gpu-clear")
            {
                using var texture=device.CreateTexture2D(new Texture2DDescription {Width=320,Height=240,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),BindFlags=BindFlags.RenderTarget});
                using var target=device.CreateRenderTargetView(texture);
                context.ClearRenderTargetView(target,new Color4(0,0,1,1));context.Flush();
            }
            if(mode is "compositor" or "compositor-clear" or "compositor-wait" or "compositor-warp" or "compositor-no-workers" or "compositor-no-video")
            {
                using(var compositor=new GpuSceneCompositor(device,context))
                using(var texture=device.CreateTexture2D(new Texture2DDescription {Width=320,Height=240,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource}))
                using(var output=device.CreateTexture2D(texture.Description))
                using(var view=device.CreateShaderResourceView(texture))
                using(var target=device.CreateRenderTargetView(output))
                using(var sourceTarget=device.CreateRenderTargetView(texture))
                {
                    context.ClearRenderTargetView(sourceTarget,new Color4(0,0,1,1));
                    compositor.Begin(target,320,240,new Color4(0,0,0,1));
                    compositor.Draw(view,new(0,0,320,240));compositor.End();context.Flush();
                    if(mode=="compositor-wait")
                    {
                        using var query=device.CreateQuery(new QueryDescription(QueryType.Event));
                        context.End(query);context.Flush();var wait=Stopwatch.StartNew();
                        while(true)
                        {
                            var result=context.GetData(query,0,0,AsyncGetDataFlags.DoNotFlush);result.CheckError();
                            if(result.Code==0)break;
                            if(wait.Elapsed>TimeSpan.FromSeconds(2))throw new TimeoutException("GPU event did not complete.");
                            Thread.Sleep(1);
                        }
                    }
                }
                if(mode is "compositor-clear" or "compositor-wait") {context.ClearState();context.Flush();}
            }
    }
}
