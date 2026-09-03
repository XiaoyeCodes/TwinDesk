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
public sealed class WgcOwnedNv12Source : IProbeFrameSource
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
    private readonly Dictionary<long, CaptureNode> captures = new();
    private readonly List<object> history = new();
    private readonly WindowInfo root;
    private readonly int width, height;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan nextSnapshot;
    private long lastTime = -10, generation;
    private uint sceneVersion, outputIndex;
    private OwnedWindowScene? scene;
    private bool dirty, disposed;
    public IMFDXGIDeviceManager DeviceManager { get; private set; } = null!;
    public ProbeSceneConfig? LastSampleScene { get; private set; }
    public string Description => "live-wgc-owned-scene/premultiplied-gpu-composition/gpu-nv12 (same-process probe; no input; no CPU pixel readback)";
    public IReadOnlyList<object> SceneHistory => history.AsReadOnly();
    public int CapturedFrames { get; private set; }
    public int SupersededFrames { get; private set; }

    public WgcOwnedNv12Source(WindowInfo root, int width, int height)
    {
        this.root = root; this.width = width; this.height = height;
        if (width is < 128 or > 2560 || height is < 128 or > 1440 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("WGC unsupported.");
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device, out context).CheckError();
            using (var mt = context.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
            DeviceManager = MediaFactory.MFCreateDXGIDeviceManager(); DeviceManager.ResetDevice(device).CheckError();
            videoDevice = device.QueryInterface<ID3D11VideoDevice>(); videoContext = context.QueryInterface<ID3D11VideoContext>();
            compositor = new(device,context);
            using (var dxgi = device.QueryInterface<IDXGIDevice>()) captureDevice = GraphicsCaptureInterop.FromDxgiDevice(dxgi.NativePointer);
            Reconcile();
        }
        catch { Dispose(); throw; }
    }

    public IMFSample? TryGetSample()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (clock.Elapsed >= nextSnapshot || captures.Values.Any(c=>c.Closed)) Reconcile();
        foreach (var node in captures.Values)
        {
            var (count, skipped) = node.Poll(context);
            CapturedFrames += count; SupersededFrames += skipped; dirty |= count > 0;
            if (!node.HasFrame && clock.Elapsed - node.CreatedAt > TimeSpan.FromSeconds(2))
                throw new TimeoutException("Owned window did not produce its first frame; incomplete scene forbidden.");
        }
        if (!dirty || captures.Values.Any(c => !c.HasFrame)) return null;
        compositor.Begin(canvasView,scene!.Bounds.Width,scene.Bounds.Height,new Color4(0,0,0,1));
        foreach (var node in scene!.Nodes)
        {
            var source = captures[node.Window.Handle];
            compositor.Draw(source.View,node.Destination);
        }
        compositor.End();
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
            lastTime = Math.Max(lastTime + 10, clock.Elapsed.Ticks / 10 * 10);
            sample.SampleTime = lastTime; sample.SampleDuration = 10_000_000 / 30;
            LastSampleScene = new(sceneVersion, width, height, OwnedWindowScene.Letterbox(scene.Bounds, width, height), scene.Nodes.Count);
            dirty = false;
            return sample;
        }
        catch { sample.Dispose(); throw; }
    }

    private void Reconcile()
    {
        var selected = OwnedWindowScene.Select(root, WindowCatalog.Find(root.ProcessName));
        _ = OwnedWindowScene.Arrange(selected); // Validate aggregate allocation before constructing any new frame pools.
        var retained = selected.Select(w => w.Handle).ToHashSet();
        foreach (var id in captures.Keys.Where(id => !retained.Contains(id)).ToArray())
        { captures[id].Dispose(); captures.Remove(id); }
        var resolved = new List<WindowInfo>();
        foreach (var window in selected)
        {
            if (captures.TryGetValue(window.Handle, out var old) && (!OwnedWindowScene.SameIdentity(old.Identity, window)
                || old.Closed || !old.CanMap(window)))
            { old.Dispose(); captures.Remove(window.Handle); }
            if (!captures.TryGetValue(window.Handle, out var capture))
            {
                capture = new(device, captureDevice, window, clock.Elapsed, checked(++generation));
                captures.Add(window.Handle, capture);
            }
            resolved.Add(window with { CaptureBounds = capture.MapBounds(window), BindingGeneration = capture.Generation });
        }
        var next = OwnedWindowScene.Arrange(resolved);
        if (scene is null || !scene.SameGeometry(next))
        {
            if (sceneVersion == uint.MaxValue) throw new InvalidOperationException("Scene version rollover requires a new stream.");
            bool resized = scene is null || scene.Bounds.Width != next.Bounds.Width || scene.Bounds.Height != next.Bounds.Height;
            scene = next; sceneVersion++; dirty = true;
            if (resized) PrepareCanvas(next.Bounds);
            if (history.Count >= 256) throw new InvalidOperationException("Finite scene history budget exceeded; end this probe.");
            history.Add(new { version = sceneVersion, atMs = clock.Elapsed.TotalMilliseconds, scene.Bounds,
                nodes = scene.Nodes.Select(n => new { n.Window.Handle, n.Window.Owner, n.Window.ClassName,
                    n.Window.BindingGeneration, n.Window.CaptureBounds, n.Window.Bounds, n.Window.Enabled, n.Window.ZOrder, n.Destination }).ToArray() });
        }
        nextSnapshot = clock.Elapsed + TimeSpan.FromMilliseconds(100);
    }

    private void PrepareCanvas(WindowBounds bounds)
    {
        processor?.Dispose(); enumerator?.Dispose(); canvasView?.Dispose(); canvas?.Dispose();
        canvas = device.CreateTexture2D(TextureDescription(bounds.Width, bounds.Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget));
        canvasView = device.CreateRenderTargetView(canvas);
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

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var capture in captures.Values) capture.Dispose(); captures.Clear();
        processor?.Dispose(); enumerator?.Dispose(); canvasView?.Dispose(); canvas?.Dispose();
        compositor?.Dispose();videoContext?.Dispose(); videoDevice?.Dispose(); DeviceManager?.Dispose(); captureDevice?.Dispose(); context?.Dispose(); device?.Dispose();
    }

    private sealed class CaptureNode : IDisposable
    {
        private GraphicsCaptureItem item = null!;
        private Direct3D11CaptureFramePool pool = null!;
        private GraphicsCaptureSession session = null!;
        private readonly int width, height;
        private int closed;
        private bool disposed;
        public WindowInfo Identity { get; }
        public long Generation { get; }
        public TimeSpan CreatedAt { get; }
        public ID3D11Texture2D Texture { get; private set; } = null!;
        public ID3D11ShaderResourceView View { get; private set; } = null!;
        public bool HasFrame { get; private set; }
        public bool Closed => Volatile.Read(ref closed) != 0;

        public CaptureNode(ID3D11Device device, global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice captureDevice,
            WindowInfo window, TimeSpan createdAt, long generation)
        {
            Identity = window; CreatedAt = createdAt; Generation = generation;
            try
            {
                item = GraphicsCaptureInterop.ForWindow((nint)window.Handle);
                width = item.Size.Width; height = item.Size.Height;
                _ = MapBounds(window);
                Texture = device.CreateTexture2D(TextureDescription(width, height, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource));
                View = device.CreateShaderResourceView(Texture);
                item.Closed += OnClosed;
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                session = pool.CreateCaptureSession(item); session.StartCapture();
            }
            catch { Dispose(); throw; }
        }
        private void OnClosed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref closed, 1);
        public bool CanMap(WindowInfo window) => Fits(window.CaptureBounds) || Fits(window.Bounds);
        private bool Fits(WindowBounds b) => b.Width == width && b.Height == height && width > 0 && height > 0;
        public WindowBounds MapBounds(WindowInfo window) => Fits(window.CaptureBounds) ? window.CaptureBounds : Fits(window.Bounds) ? window.Bounds
            : throw new InvalidDataException("WGC dimensions match neither physical DWM nor window bounds; explicit rebind/calibration required.");

        public (int Count, int Skipped) Poll(ID3D11DeviceContext context)
        {
            if (Closed) return (0,0);
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
                    throw new InvalidOperationException("Capture changed size during snapshot; reconnect this bounded probe.");
                using var texture = GraphicsCaptureInterop.GetTexture(frame.Surface);
                var description = texture.Description;
                if (description.Width < width || description.Height < height || description.Format != Format.B8G8R8A8_UNorm)
                    throw new InvalidDataException("Capture texture does not contain the promised content rectangle.");
                context.CopySubresourceRegion(Texture, 0, 0, 0, 0, texture, 0, new Box(0,0,0,width,height,1));
                HasFrame = true; return (count,skipped);
            }
            finally { frame?.Dispose(); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (item is not null) item.Closed -= OnClosed;
            session?.Dispose(); pool?.Dispose(); View?.Dispose();Texture?.Dispose();
        }
    }
}
