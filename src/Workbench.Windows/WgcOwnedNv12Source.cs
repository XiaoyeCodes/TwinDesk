using System.Diagnostics;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Workbench.Windows;

/// <summary>Finite same-process owned-window GPU experiment; no input or desktop fallback.</summary>
public sealed class WgcOwnedNv12Source : IProbeFrameSource, IBgraProbeFrameSource
{
    private ID3D11Device device = null!;
    private ID3D11DeviceContext context = null!;
    private ID3D11VideoDevice videoDevice = null!;
    private ID3D11VideoContext videoContext = null!;
    private ID3D11VideoProcessorEnumerator enumerator = null!;
    private ID3D11VideoProcessor processor = null!;
    private ID3D11Texture2D canvas = null!;
    private ID3D11RenderTargetView canvasView = null!;
    private GpuSceneCompositor compositor = null!;
    private global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice captureDevice = null!;
    private WindowLifetimeMonitor lifetimes = null!;
    private CaptureGraphicsDevice graphics = null!;
    private CaptureGraphicsDevice.Lease? graphicsLease;
    private readonly bool ownsGraphics;
    public Guid GraphicsDeviceIdentity => graphics.Identity;
    private readonly Dictionary<long, CaptureNode> captures = new();
    private readonly Queue<object> history = new();
    public const int MaximumHistoryEntries = 256;
    public long SceneHistoryDropped { get; private set; }
    private readonly WindowInfo root;
    private readonly int width, height;
    private readonly bool bgraOnly;
    private readonly Action<WindowInfo>? beforeNodeCreation;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan nextSnapshot;
    private long lastTime = -10, generation;
    private uint sceneVersion, composedVersion, outputIndex;
    private OwnedWindowScene? scene;
    private bool dirty, disposed, reconciliationPending;
    private int inputRefreshRequested;
    private bool forceSceneVersion;
    private readonly CaptureRecoveryBudget recovery = new();
    public int CaptureGeometryRetries { get; private set; }
    public int TransientBindingRetries { get; private set; }
    public IMFDXGIDeviceManager DeviceManager { get; private set; } = null!;
    public ProbeSceneConfig? LastSampleScene { get; private set; }
    public string Description => "live-wgc-owned-scene/premultiplied-gpu-composition/gpu-nv12 (same-process probe; no input; no CPU pixel readback)";
    public IReadOnlyList<object> SceneHistory => Array.AsReadOnly(history.ToArray());
    public int CapturedFrames { get; private set; }
    public int SupersededFrames { get; private set; }
    public int ActiveCaptureCount => captures.Count;
    public long CaptureBindingsCreated => generation;
    public long ReceivedDestroyEvents => lifetimes?.ReceivedDestroyEvents ?? 0;
    public CaptureInputBindings InputBindings { get; } = new();
    // Cross-thread request only. Capture resources and version changes stay on their owning thread.
    public void RequestInputSceneRefresh()=>Interlocked.Exchange(ref inputRefreshRequested,1);

    public WgcOwnedNv12Source(WindowInfo root, int width, int height, bool bgraOnly = false)
        : this(root, width, height, bgraOnly, null) { }

    public WgcOwnedNv12Source(CaptureGraphicsDevice graphics, WindowInfo root, int width, int height, bool bgraOnly=false)
        : this(root,width,height,bgraOnly,null,graphics ?? throw new ArgumentNullException(nameof(graphics))) { }

    // Internal scheduling seam for real native-window race tests; unavailable to network clients.
    internal WgcOwnedNv12Source(WindowInfo root, int width, int height, bool bgraOnly, Action<WindowInfo>? beforeNodeCreation,
        CaptureGraphicsDevice? sharedGraphics=null)
    {
        ownsGraphics=sharedGraphics is null;
        this.beforeNodeCreation = beforeNodeCreation;
        this.root = root; this.width = width; this.height = height; this.bgraOnly = bgraOnly;
        if (width is < 128 or > 2560 || height is < 128 or > 1440 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("WGC unsupported.");
        try
        {
            graphics=sharedGraphics??new CaptureGraphicsDevice();
            graphicsLease=graphics.Acquire(InputBindings.Stop);
            using var operation=graphicsLease.Enter();
            lifetimes=new(root.ProcessId);
            device=graphics.Device;context=graphics.Context;
            using(var dxgi=device.QueryInterface<IDXGIDevice>())captureDevice=GraphicsCaptureInterop.FromDxgiDevice(dxgi.NativePointer);
            DeviceManager = MediaFactory.MFCreateDXGIDeviceManager(); DeviceManager.ResetDevice(device).CheckError();
            if (!bgraOnly) { videoDevice = device.QueryInterface<ID3D11VideoDevice>(); videoContext = context.QueryInterface<ID3D11VideoContext>(); }
            compositor = new(device,context);
            TryReconcile();
        }
        catch { Dispose(); throw; }
    }

    public IMFSample? TryGetSample()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        using var operation=graphicsLease!.Enter();
        if (bgraOnly) throw new InvalidOperationException("BGRA compatibility source cannot produce NV12 samples.");
        if (!TryCompose()) return null;
        using var inputView = videoDevice.CreateVideoProcessorInputView(canvas, enumerator,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        using var output = device.CreateTexture2D(TextureDescription(width, height, Format.NV12, BindFlags.RenderTarget));
        using var outputView = videoDevice.CreateVideoProcessorOutputView(output, enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        videoContext.VideoProcessorBlt(processor, outputView, outputIndex++,
            [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
        context.Flush();
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, output, 0, false);
        var sample = MediaFactory.MFCreateSample();
        try
        {
            sample.AddBuffer(buffer);
            sample.SampleTime = lastTime; sample.SampleDuration = 10_000_000 / 30;
            return sample;
        }
        catch { sample.Dispose(); throw; }
    }

    // JPEG compatibility alone performs GPU -> CPU readback. H264 never invokes this method.
    public BgraProbeFrame? TryGetBgraFrame()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        using var operation=graphicsLease!.Enter();
        if (!TryCompose()) return null;
        using var output = device.CreateTexture2D(TextureDescription(width, height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget));
        using var outputView = device.CreateRenderTargetView(output);
        using var sourceView = device.CreateShaderResourceView(canvas);
        using(graphics.EnterRender())
        {
            compositor.Begin(outputView, width, height, new Color4(0,0,0,1));
            compositor.Draw(sourceView, LastSampleScene!.ContentRect); compositor.End();
        }
        var description = output.Description;
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(description);
        context.CopyResource(staging, output);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = checked(width * 4); byte[] bytes = new byte[checked(stride * height)];
            for (int y=0; y<height; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch*y), bytes, stride*y, stride);
            return new(width, height, bytes, lastTime / 10, LastSampleScene);
        }
        finally { context.Unmap(staging, 0); }
    }

    internal void RefreshForNewStream()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        using var operation=graphicsLease!.Enter();
        InputBindings.Freeze();dirty=true;nextSnapshot=TimeSpan.Zero;
    }

    private bool TryCompose()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if(Interlocked.Exchange(ref inputRefreshRequested,0)!=0)
        { InputBindings.Freeze();forceSceneVersion=true;nextSnapshot=TimeSpan.Zero; }
        lifetimes.ThrowIfFailed();
        recovery.ThrowIfExpired(clock.Elapsed);
        if (clock.Elapsed >= nextSnapshot || captures.Values.Any(c=>c.Closed))
        { if (!TryReconcile()) return false; }
        // A failed initial reconciliation has no canvas yet. Never publish a partial/old scene.
        if (reconciliationPending || scene is null || captures.Values.Any(c=>c.RequiresRebind)) return false;
        foreach (var node in captures.Values)
        {
            var (count, skipped) = node.Poll(context);
            CapturedFrames += count; SupersededFrames += skipped; dirty |= count > 0;
            if (node.RequiresRebind)
            {
                GeometryRetry();
                return false;
            }
            if (!node.HasFrame && clock.Elapsed - node.CreatedAt > TimeSpan.FromSeconds(2))
                throw new TimeoutException("Owned window did not produce its first frame; incomplete scene forbidden.");
        }
        if (!dirty || captures.Values.Any(c => !c.HasFrame)) return false;
        using(graphics.EnterRender())
        {
            compositor.Begin(canvasView,scene!.Bounds.Width,scene.Bounds.Height,new Color4(0,0,0,1));
            foreach (var node in scene!.Nodes)
            {
                var source = captures[node.Window.Handle];
                compositor.Draw(source.View,node.Destination);
            }
            compositor.End();
        }
        composedVersion = sceneVersion;
        lastTime = Math.Max(lastTime + 10, clock.Elapsed.Ticks / 10 * 10);
        LastSampleScene = new(sceneVersion, width, height, OwnedWindowScene.Letterbox(scene.Bounds, width, height), scene.Nodes.Count)
            {SourceWidth=scene.Bounds.Width,SourceHeight=scene.Bounds.Height};
        InputBindings.Publish(sceneVersion, scene);
        recovery.Complete();
        dirty = false;
        return true;
    }

    private void GeometryRetry()
    {
        InputBindings.Freeze();
        composedVersion = 0; LastSampleScene = null; dirty = true;
        nextSnapshot = clock.Elapsed + TimeSpan.FromMilliseconds(100);
        CaptureGeometryRetries++;
        recovery.Record(clock.Elapsed);
    }

    private bool TryReconcile()
    {
        reconciliationPending = true;
        try { Reconcile(); reconciliationPending = false; return true; }
        catch (CaptureGeometryChangedException) { GeometryRetry(); return false; }
        catch (CaptureWindowDisappearedException e) when (e.Window.Handle != root.Handle)
        {
            // Only a failed native CreateForWindow AND a now-invalid HWND is recoverable.
            // Never mask E_INVALIDARG for a still-live window or silently replace a root lifetime.
            TransientBindingRetries++;
            GeometryRetry();
            return false;
        }
    }

    private void Reconcile()
    {
        if (captures.TryGetValue(root.Handle, out var boundRoot) && boundRoot.Closed)
            throw new InvalidOperationException("Bound root capture closed; a new app/stream lifetime is required.");
        var selected = OwnedWindowScene.Select(root, WindowCatalog.Find(root.ProcessName));
        _ = OwnedWindowScene.Arrange(selected); // Validate aggregate allocation before constructing any new frame pools.
        var retained = selected.Select(w => w.Handle).ToHashSet();
        foreach (var id in captures.Keys.Where(id => !retained.Contains(id)).ToArray())
        { captures[id].Dispose(); captures.Remove(id); }
        var resolved = new List<WindowInfo>();
        foreach (var window in selected)
        {
            if (captures.TryGetValue(window.Handle, out var old) && (!OwnedWindowScene.SameIdentity(old.Identity, window)
                || old.Closed || old.RequiresRebind || !old.CanMap(window)))
            { old.Dispose(); captures.Remove(window.Handle); }
            if (!captures.TryGetValue(window.Handle, out var capture))
            {
                beforeNodeCreation?.Invoke(window);
                capture = new(device, captureDevice, window, clock.Elapsed, checked(++generation), InputBindings, lifetimes);
                captures.Add(window.Handle, capture);
            }
            resolved.Add(window with { CaptureBounds = capture.MapBounds(window), BindingGeneration = capture.Generation });
        }
        var next = OwnedWindowScene.Arrange(resolved);
        if (scene is null || !scene.SameGeometry(next) || forceSceneVersion)
        {
            InputBindings.Freeze();
            if (sceneVersion == uint.MaxValue) throw new InvalidOperationException("Scene version rollover requires a new stream.");
            bool resized = scene is null || scene.Bounds.Width != next.Bounds.Width || scene.Bounds.Height != next.Bounds.Height;
            scene = next; sceneVersion++; dirty = true;
            forceSceneVersion=false;
            if (resized) PrepareCanvas(next.Bounds);
            if (history.Count == MaximumHistoryEntries) { history.Dequeue(); SceneHistoryDropped++; }
            history.Enqueue(new { version = sceneVersion, atMs = clock.Elapsed.TotalMilliseconds, scene.Bounds,
                nodes = scene.Nodes.Select(n => new { n.Window.Handle, n.Window.Owner, n.Window.ClassName,
                    n.Window.BindingGeneration, n.Window.CaptureBounds, n.Window.Bounds, n.Window.Enabled, n.Window.ZOrder, n.Destination }).ToArray() });
        }
        nextSnapshot = clock.Elapsed + TimeSpan.FromMilliseconds(100);
    }

    private void PrepareCanvas(WindowBounds bounds)
    {
        processor?.Dispose(); enumerator?.Dispose(); canvasView?.Dispose(); canvas?.Dispose();
        canvas = device.CreateTexture2D(TextureDescription(bounds.Width, bounds.Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource));
        canvasView = device.CreateRenderTargetView(canvas);
        if (bgraOnly) return;
        enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = (uint)bounds.Width, InputHeight = (uint)bounds.Height,
            InputFrameRate = new Rational(30,1), OutputFrameRate = new Rational(30,1),
            OutputWidth = (uint)width, OutputHeight = (uint)height, Usage = VideoUsage.PlaybackNormal
        });
        enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm, out var inputSupport).CheckError();
        enumerator.CheckVideoProcessorFormat(Format.NV12, out var outputSupport).CheckError();
        if (((int)inputSupport & 1) == 0 || ((int)outputSupport & 2) == 0) throw new NotSupportedException("GPU format conversion unavailable.");
        processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, new VideoProcessorColorSpace { Usage=1, RGB_Range=0, YCbCr_Matrix=1, Nominal_Range=2 });
        videoContext.VideoProcessorSetOutputColorSpace(processor, new VideoProcessorColorSpace { Usage=1, RGB_Range=0, YCbCr_Matrix=1, Nominal_Range=1 });
        videoContext.VideoProcessorSetOutputBackgroundColor(processor, false, new VideoColor { Rgba = new VideoColorRgba { A=1 } });
        videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0,0,bounds.Width,bounds.Height));
        var d = OwnedWindowScene.Letterbox(bounds, width, height);
        videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(d.X,d.Y,d.X+d.Width,d.Y+d.Height));
        videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0,0,width,height));
    }

    private static Texture2DDescription TextureDescription(int w, int h, Format format, BindFlags bind) => new()
    { Width=(uint)w, Height=(uint)h, MipLevels=1, ArraySize=1, Format=format, SampleDescription=new SampleDescription(1,0), Usage=ResourceUsage.Default, BindFlags=bind };

    /// <summary>Explicit local fixture diagnostic only; never called by the streaming path.</summary>
    public ScenePixelSnapshot ReadbackForDiagnostics()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var operation=graphicsLease!.Enter();
        if (scene is null || composedVersion == 0 || composedVersion != sceneVersion)
            throw new InvalidOperationException("No complete composed scene for this version.");
        var description = canvas.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(description);
        context.CopyResource(staging, canvas);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = checked(scene.Bounds.Width * 4);
            byte[] bytes = new byte[checked(stride * scene.Bounds.Height)];
            for (int y = 0; y < scene.Bounds.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch*y), bytes, stride*y, stride);
            return new(scene, composedVersion, stride, bytes);
        }
        finally { context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        InputBindings.Stop();
        using(var operation=graphicsLease?.Enter(cleanup:true))
        {
        foreach (var capture in captures.Values) capture.Dispose(); captures.Clear();
        lifetimes?.Dispose();
        processor?.Dispose(); enumerator?.Dispose(); canvasView?.Dispose(); canvas?.Dispose();
        compositor?.Dispose();videoContext?.Dispose(); videoDevice?.Dispose(); DeviceManager?.Dispose();captureDevice?.Dispose();
        }
        graphicsLease?.Dispose();
        if(ownsGraphics)graphics?.Dispose();
    }

    private sealed class CaptureNode : IDisposable
    {
        private GraphicsCaptureItem item = null!;
        private Direct3D11CaptureFramePool pool = null!;
        private GraphicsCaptureSession session = null!;
        private readonly int width, height;
        private int closed;
        private bool disposed;
        private CaptureInputBindings.Lifetime? inputLifetime;
        private WindowLifetimeMonitor.Registration? notification;
        public WindowInfo Identity { get; }
        public long Generation { get; }
        public TimeSpan CreatedAt { get; }
        public ID3D11Texture2D Texture { get; private set; } = null!;
        public ID3D11ShaderResourceView View { get; private set; } = null!;
        public bool HasFrame { get; private set; }
        public bool RequiresRebind { get; private set; }
        public bool Closed
        {
            get
            {
                // Independent destroy notifications also cover rapid HWND reuse. The synchronous PID check
                // is a fail-closed backup; exact process/start/session and geometry are reconciled every 100 ms.
                if(Volatile.Read(ref closed)!=0)return true;
                if(notification is {Alive:false}){Interlocked.Exchange(ref closed,1);inputLifetime?.Dispose();return true;}
                uint thread=NativeMethods.GetWindowThreadProcessId((nint)Identity.Handle,out uint processId);
                if(thread!=0 && processId==(uint)Identity.ProcessId)return false;
                Interlocked.Exchange(ref closed,1);inputLifetime?.Dispose();return true;
            }
        }

        public CaptureNode(ID3D11Device device, global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice captureDevice,
            WindowInfo window, TimeSpan createdAt, long generation, CaptureInputBindings bindings, WindowLifetimeMonitor lifetimes)
        {
            Identity = window; CreatedAt = createdAt; Generation = generation;
            try
            {
                inputLifetime = bindings.Register(window, generation);
                notification=lifetimes.Register((nint)window.Handle,()=>{Interlocked.Exchange(ref closed,1);inputLifetime.Dispose();});
                try { item = GraphicsCaptureInterop.ForWindow((nint)window.Handle); }
                catch (ArgumentException e) when (e.HResult == unchecked((int)0x80070057) &&
                    NativeMethods.GetWindowThreadProcessId((nint)window.Handle, out _) == 0)
                { throw new CaptureWindowDisappearedException(window, e); }
                width = item.Size.Width; height = item.Size.Height;
                _ = MapBounds(window);
                Texture = device.CreateTexture2D(TextureDescription(width, height, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource));
                View = device.CreateShaderResourceView(Texture);
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                session = pool.CreateCaptureSession(item); session.StartCapture();
            }
            catch { Dispose(); throw; }
        }
        public bool CanMap(WindowInfo window) => Fits(window.CaptureBounds) || Fits(window.Bounds);
        private bool Fits(WindowBounds b) => b.Width == width && b.Height == height && width > 0 && height > 0;
        public WindowBounds MapBounds(WindowInfo window) => Fits(window.CaptureBounds) ? window.CaptureBounds : Fits(window.Bounds) ? window.Bounds
            : throw new CaptureGeometryChangedException("WGC dimensions match neither current DWM nor window bounds.");

        public (int Count, int Skipped) Poll(ID3D11DeviceContext context)
        {
            if (Closed || RequiresRebind) return (0,0);
            Direct3D11CaptureFrame? frame = null;
            int count = 0, skipped = 0;
            try
            {
                for (int n = 0; n < 2; n++)
                {
                    var next = pool.TryGetNextFrame(); if (next is null) break;
                    if (frame is not null) { frame.Dispose(); skipped++; }
                    frame = next; count++;
                }
                if (frame is null) return (0,0);
                if (frame.ContentSize.Width != width || frame.ContentSize.Height != height)
                {
                    // Retire the input lifetime immediately, then the pool on the capture thread.
                    // The resized surface is never copied into the old-size texture or presented.
                    HasFrame = false; RequiresRebind = true; inputLifetime?.Dispose();
                    return (count, skipped + 1);
                }
                using var texture = GraphicsCaptureInterop.GetTexture(frame.Surface);
                var description = texture.Description;
                if (description.Width < width || description.Height < height || description.Format != Format.B8G8R8A8_UNorm)
                    throw new InvalidDataException("Capture texture does not contain the promised content rectangle.");
                context.CopySubresourceRegion(Texture, 0, 0, 0, 0, texture, 0, new Box(0,0,0,width,height,1));
                inputLifetime!.FrameObserved();
                HasFrame = true; return (count,skipped);
            }
            finally { frame?.Dispose(); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            inputLifetime?.Dispose();
            notification?.Dispose();
            session?.Dispose(); pool?.Dispose(); View?.Dispose();Texture?.Dispose();
        }
    }

    private sealed class CaptureGeometryChangedException(string message) : Exception(message);
    private sealed class CaptureWindowDisappearedException(WindowInfo window, Exception inner)
        : Exception("WGC CreateForWindow failed after the selected native window disappeared.", inner)
    { public WindowInfo Window { get; } = window; }
}

public sealed record ScenePixelSnapshot(OwnedWindowScene Scene, uint Version, int Stride, byte[] Bgra);
