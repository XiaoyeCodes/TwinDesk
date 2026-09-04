using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Vortice.MediaFoundation;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

// Separate capture-only diagnostic: no pixel readback, encoder, browser, input or user application.
internal static class CaptureResourceVerification
{
    public static int Run(string output,bool windowsOnly=false,bool itemsOnly=false,bool itemEvents=false,bool nativeEvents=false,bool afterClosed=false,bool rawDelegate=false,int cycles=40,int roundCount=3,bool sharedDevice=false)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output))throw new IOException("Evidence directory must be new.");
        Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var root=new Form {Text="TwinDesk SC02 isolated resources — NOT NX / TIA",ClientSize=new(900,540),
            StartPosition=FormStartPosition.CenterScreen,BackColor=Color.DarkBlue};
        root.Controls.Add(new Label {AutoSize=true,ForeColor=Color.White,Location=new(20,20),
            Text=$"Finite capture-only resource diagnostic · {roundCount} × {cycles} owner cycles · no input or CPU pixel readback"});
        using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(cycles>120?5:3));
        bool finished=false;int exit=1;
        root.FormClosing+=(_,e)=>{if(!finished){cancellation.Cancel();e.Cancel=true;}};
        root.Shown+=async(_,_)=>{
            try {exit=await Task.Run(()=>Verify(root,output,cancellation.Token,windowsOnly,itemsOnly,itemEvents,nativeEvents,afterClosed,rawDelegate,cycles,roundCount,sharedDevice));}
            finally {finished=true;root.Close();}
        };
        Application.Run(root);return exit;
    }

    private static int Verify(Form root,string output,CancellationToken token,bool windowsOnly,bool itemsOnly,bool itemEvents,bool nativeEvents,bool afterClosed,bool rawDelegate,int cycles,int roundCount,bool sharedDevice)
    {
        var samples=new List<object>();var rounds=new List<object>();var clock=Stopwatch.StartNew();Exception? failure=null;
        object? rootClosure=null;
        CaptureGraphicsDevice? graphics=null;
        var sources=new[]{typeof(CaptureResourceVerification).Assembly.Location,typeof(WgcOwnedNv12Source).Assembly.Location}
            .Select(path=>new {file=Path.GetFileName(path),sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}).ToArray();
        void Sample(string stage,int round,int cycle,int? active=null)
        {
            using var process=Process.GetCurrentProcess();process.Refresh();var gc=GC.GetGCMemoryInfo();
            samples.Add(new {stage,round,cycle,elapsedMs=clock.Elapsed.TotalMilliseconds,process.WorkingSet64,process.PrivateMemorySize64,
                process.HandleCount,gdi=GetGuiResources(process.Handle,0),user=GetGuiResources(process.Handle,1),activeCaptures=active,
                managedBytes=GC.GetTotalMemory(false),heapBytes=gc.HeapSizeBytes,gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2),
                handleTypes=OwnHandleTypes.Snapshot(),liveNativeCallbacks=NativeClosedCallback.LiveInstances});
            Console.WriteLine($"Resource {stage} r{round}/c{cycle}: handles={process.HandleCount}, private={process.PrivateMemorySize64}, active={active}");
        }
        bool mf=false;
        try
        {
            MediaFactory.MFStartup(false).CheckError();mf=true;
            if(sharedDevice)graphics=new CaptureGraphicsDevice();
            nint handle=0;root.Invoke((Action)(()=>handle=root.Handle));
            using var process=Process.GetCurrentProcess();
            var target=WindowCatalog.Find(process.ProcessName).Single(w=>w.Handle==handle&&w.ProcessId==process.Id);
            Sample("before-any-source",0,0);
            for(int round=1;round<=roundCount;round++)
            {
                token.ThrowIfCancellationRequested();
                rounds.Add(windowsOnly||itemsOnly?WindowsOnly(root,target,round,token,Sample,itemsOnly,itemEvents,nativeEvents,afterClosed,rawDelegate):Round(root,target,round,token,Sample,cycles,graphics));
                // Record production-like disposal first. Forced collection is diagnostic only and never a product fix.
                Thread.Sleep(500);Sample("disposed-before-diagnostic-gc",round,cycles,0);
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                Thread.Sleep(500);Sample("after-diagnostic-gc",round,cycles,0);
            }
            if(!windowsOnly && !itemsOnly)rootClosure=VerifyRootClose(root,target,token,graphics);
        }
        catch(Exception e){failure=e;Console.Error.WriteLine(e);}
        finally{graphics?.Dispose();if(mf)MediaFactory.MFShutdown();}
        using var report=new FileStream(Path.Combine(output,"report.json"),FileMode.CreateNew);
        JsonSerializer.Serialize(report,new {status=failure is null?"OBSERVED_NOT_ENDURANCE":"FAIL",
            scope=windowsOnly?"Native fixture allocation ONLY; no WGC/GPU capture":itemsOnly?"Native fixture plus capture ITEM creation only; no frame pool, capture session or GPU":"SC02 isolated actual WGC/GPU/NV12 allocation and disposal; no CPU readback, encoding, browser, NX/TIA or input",
            windowsOnly,itemsOnly,itemEvents,nativeEvents,afterClosed,rawDelegate,cyclesPerRound=cycles,roundCount,sharedDevice,
            sharedActiveAfterDispose=graphics?.ActiveSources,graphicsDeviceIdentity=graphics?.Identity,
            at=DateTimeOffset.Now,buildIdentity=sources,rounds,samples,rootClosure,error=failure?.ToString(),
            forcedGc="Only after each source is disposed; not in capture loops; diagnostic comparison, not a runtime workaround"},
            new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true});
        Console.WriteLine($"Resource evidence: {output}");return failure is null?0:1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object WindowsOnly(Form root,WindowInfo target,int round,CancellationToken token,Action<string,int,int,int?> sample,bool itemsOnly,bool itemEvents,bool nativeEvents,bool afterClosed,bool rawDelegate)
    {
        int callbacks=0;
        for(int cycle=1;cycle<=40;cycle++)
        {
            token.ThrowIfCancellationRequested();LayeredFixtureWindow? popup=null;
            try
            {
                root.Invoke((Action)(()=>popup=new((nint)target.Handle,new(target.Bounds.X+160,target.Bounds.Y+180))));
                if(itemsOnly)callbacks+=CreateItemOnly(popup!.Handle,itemEvents,nativeEvents,afterClosed,()=>root.Invoke((Action)(()=>popup.Dispose())),token,rawDelegate);
                Thread.Sleep(100);
            }
            finally{root.Invoke((Action)(()=>popup?.Dispose()));}
            if(cycle%10==0)sample(itemsOnly?"items-only-cycle":"windows-only-cycle",round,cycle,0);
        }
        return new {round,cycles=40,noCapture=true,callbacks};
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CreateItemOnly(nint handle,bool itemEvents,bool nativeEvents,bool afterClosed,Action closeWindow,CancellationToken token,bool rawDelegate)
    {
        var item=GraphicsCaptureInterop.ForWindow(handle);
        if(item.Size.Width<1)throw new InvalidDataException("Invalid actual capture item.");
        int callbacks=0;
        if(nativeEvents)
        {
            using var subscription=new CaptureClosedSubscription(item,()=>Interlocked.Increment(ref callbacks),rawDelegate);
            if(afterClosed)
            {
                closeWindow();var wait=Stopwatch.StartNew();
                while(Volatile.Read(ref callbacks)==0 && wait.Elapsed<TimeSpan.FromSeconds(3)) {token.ThrowIfCancellationRequested();Thread.Sleep(10);}
                if(Volatile.Read(ref callbacks)!=1)throw new InvalidDataException("Expected one real Closed callback before revocation.");
            }
        }
        else if(itemEvents){item.Closed+=Closed;item.Closed-=Closed;}
        // No frame pool: isolate creation/event registration from the full capture source.
        static void Closed(global::Windows.Graphics.Capture.GraphicsCaptureItem sender,object args){}
        return callbacks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object VerifyRootClose(Form root,WindowInfo owner,CancellationToken token,CaptureGraphicsDevice? graphics)
    {
        LayeredFixtureWindow? window=null;
        try
        {
            root.Invoke((Action)(()=>window=new((nint)owner.Handle,new(owner.Bounds.X+160,owner.Bounds.Y+180))));
            var target=WindowCatalog.Find(owner.ProcessName).Single(w=>w.Handle==window!.Handle);
            using var source=graphics is null?new WgcOwnedNv12Source(target,1280,720):new WgcOwnedNv12Source(graphics,target,1280,720);
            var wait=Stopwatch.StartNew();
            while(source.InputBindings.Current is null)
            {
                token.ThrowIfCancellationRequested();using var frame=source.TryGetSample();
                if(wait.Elapsed>TimeSpan.FromSeconds(3))throw new TimeoutException("Root-close fixture did not capture first frame.");
                Thread.Sleep(10);
            }
            var binding=source.InputBindings.Current.Geometry.Nodes.Single().Window;
            wait.Restart();root.Invoke((Action)(()=>window!.Dispose()));
            while(source.ReceivedDestroyEvents==0 || source.InputBindings.Verify(binding))
            {
                token.ThrowIfCancellationRequested();
                if(wait.Elapsed>TimeSpan.FromSeconds(1))throw new TimeoutException("Closed root did not asynchronously revoke input.");
                Thread.Sleep(1);
            }
            double notificationMs=wait.Elapsed.TotalMilliseconds;bool rejected=false;
            try {using var frame=source.TryGetSample();}
            catch(InvalidOperationException e) when(e.Message.Contains("Bound root capture closed",StringComparison.Ordinal)){rejected=true;}
            if(!rejected)throw new InvalidDataException("Closed root allowed old stream to continue.");
            source.Dispose();
            return new {status="PASS",actualDestroyNotifications=source.ReceivedDestroyEvents,notificationMs,
                rejectedOldStream=rejected,activeAfterDispose=source.ActiveCaptureCount};
        }
        finally {root.Invoke((Action)(()=>window?.Dispose()));}
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Round(Form root,WindowInfo target,int round,CancellationToken token,Action<string,int,int,int?> sample,int cycles,CaptureGraphicsDevice? graphics)
    {
        using var source=graphics is null?new WgcOwnedNv12Source(target,1280,720):new WgcOwnedNv12Source(graphics,target,1280,720);
        int frames=0,changes=0;uint lastVersion=0;
        double maxCloseNotificationMs=0;
        LayeredFixtureWindow? popup=null;
        void Ui(Action action)
        {
            token.ThrowIfCancellationRequested();var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            root.BeginInvoke((Action)(()=>{try{action();done.SetResult();}catch(Exception e){done.SetException(e);}}));
            done.Task.WaitAsync(TimeSpan.FromSeconds(3),token).GetAwaiter().GetResult();
        }
        void WaitNodes(int count)
        {
            var wait=Stopwatch.StartNew();
            while(wait.Elapsed<TimeSpan.FromSeconds(3))
            {
                token.ThrowIfCancellationRequested();using var frame=source.TryGetSample();
                if(frame is not null)
                {
                    frames++;
                    if(source.LastSampleScene?.NodeCount==count && source.ActiveCaptureCount==count && source.LastSampleScene.Version>lastVersion)
                    {
                        lastVersion=source.LastSampleScene.Version;changes++;
                        var current=source.InputBindings.Current??throw new InvalidDataException("Missing capture binding.");
                        if(current.Geometry.Nodes.Any(n=>!source.InputBindings.Verify(n.Window)))throw new InvalidDataException("Dead capture binding.");
                        return;
                    }
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException("Did not observe complete new scene with expected live capture count.");
        }
        try
        {
            WaitNodes(1);sample("source-first-frame",round,0,source.ActiveCaptureCount);
            for(int cycle=1;cycle<=cycles;cycle++)
            {
                Ui(()=>popup=new LayeredFixtureWindow((nint)target.Handle,new(target.Bounds.X+160,target.Bounds.Y+180)));
                WaitNodes(2);
                var oldBinding=source.InputBindings.Current!.Geometry.Nodes.Single(n=>n.Window.Handle!=target.Handle).Window;
                long previousEvents=source.ReceivedDestroyEvents;
                var closing=Stopwatch.StartNew();Ui(()=>{popup!.Dispose();popup=null;});
                // Do not call capture or reconcile: the independent native event must retire the old input binding.
                while(source.ReceivedDestroyEvents==previousEvents || source.InputBindings.Verify(oldBinding))
                {
                    token.ThrowIfCancellationRequested();
                    if(closing.Elapsed>TimeSpan.FromSeconds(1))throw new TimeoutException("Destroy notification failed to retire input without capture polling.");
                    Thread.Sleep(1);
                }
                maxCloseNotificationMs=Math.Max(maxCloseNotificationMs,closing.Elapsed.TotalMilliseconds);
                WaitNodes(1);
                if(cycle%10==0)sample("capture-only-cycle",round,cycle,source.ActiveCaptureCount);
            }
        }
        finally
        {
            root.Invoke((Action)(()=>popup?.Dispose()));source.Dispose();
            sample("source-disposed-immediate",round,cycles,source.ActiveCaptureCount);
        }
        if(source.ActiveCaptureCount!=0)throw new InvalidDataException("Active captures survived disposal.");
        if(source.SceneHistory.Count!=Math.Min(changes,WgcOwnedNv12Source.MaximumHistoryEntries) ||
            source.SceneHistoryDropped!=Math.Max(0,changes-WgcOwnedNv12Source.MaximumHistoryEntries))
            throw new InvalidDataException("Bounded scene history lost its accounting.");
        return new {round,cycles,frames,sceneTransitions=changes,bindingsCreated=source.CaptureBindingsCreated,source.GraphicsDeviceIdentity,
            historyRetained=source.SceneHistory.Count,historyDropped=source.SceneHistoryDropped,
            activeAfterDispose=source.ActiveCaptureCount,captureGeometryRetries=source.CaptureGeometryRetries,
            actualDestroyNotifications=source.ReceivedDestroyEvents,maxCloseNotificationMs};
    }

    [DllImport("user32.dll")]private static extern uint GetGuiResources(nint process,uint flags);
}
