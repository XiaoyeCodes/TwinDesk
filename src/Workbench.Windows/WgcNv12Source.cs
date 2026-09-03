using System.Diagnostics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Workbench.Windows;

/// <summary>Fixed-size WGC -> D3D11 video processor -> NV12 GPU sample. No CPU pixel readback.</summary>
public sealed class WgcNv12Source : IProbeFrameSource
{
    private ID3D11Device device = null!;
    private ID3D11DeviceContext context = null!;
    private ID3D11VideoDevice videoDevice = null!;
    private ID3D11VideoContext videoContext = null!;
    private ID3D11VideoProcessorEnumerator enumerator = null!;
    private ID3D11VideoProcessor processor = null!;
    private global::Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice captureDevice = null!;
    private Direct3D11CaptureFramePool pool = null!;
    private GraphicsCaptureSession session = null!;
    private readonly WindowInfo target;
    private readonly int width, height, sourceWidth, sourceHeight;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private TimeSpan lastIdentityCheck = TimeSpan.FromSeconds(-1);
    private long? firstCaptureTime;
    private long lastCaptureTime = -1;
    private uint outputIndex;
    private bool disposed;
    public IMFDXGIDeviceManager DeviceManager { get; private set; } = null!;
    public string Description => "live-wgc-window/d3d11-bgra-to-nv12/gpu-sample (no CPU pixel readback)";
    public int CapturedFrames { get; private set; }
    public int SupersededFrames { get; private set; }

    // MFStartup must be alive during construction and through disposal.
    public WgcNv12Source(WindowInfo window, int outputWidth, int outputHeight)
    {
        target = window;
        width = outputWidth; height = outputHeight;
        if (width is < 128 or > 2560 || height is < 128 or > 1440 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth));
        ValidateWindow();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("WGC unsupported in current session.");
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device, out context).CheckError();
            using (var multithread = context.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
            DeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            DeviceManager.ResetDevice(device).CheckError();
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();
            using (var dxgi = device.QueryInterface<IDXGIDevice>()) captureDevice = GraphicsCaptureInterop.FromDxgiDevice(dxgi.NativePointer);
            var item = GraphicsCaptureInterop.ForWindow((nint)target.Handle);
            sourceWidth = item.Size.Width; sourceHeight = item.Size.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > 8192 || sourceHeight > 8192
                || (long)sourceWidth * sourceHeight > 16_777_216)
                throw new InvalidDataException("Invalid capture dimensions.");
            enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)sourceWidth, InputHeight = (uint)sourceHeight,
                InputFrameRate = new Rational(30, 1), OutputFrameRate = new Rational(30, 1),
                OutputWidth = (uint)width, OutputHeight = (uint)height, Usage = VideoUsage.PlaybackNormal
            });
            enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm, out var inputSupport).CheckError();
            enumerator.CheckVideoProcessorFormat(Format.NV12, out var outputSupport).CheckError();
            if (((int)inputSupport & 1) == 0 || ((int)outputSupport & 2) == 0)
                throw new NotSupportedException("GPU does not support BGRA input and NV12 output video processing.");
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            videoContext.VideoProcessorSetStreamColorSpace(processor, 0,
                new VideoProcessorColorSpace { Usage = 1, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 2 });
            videoContext.VideoProcessorSetOutputColorSpace(processor,
                new VideoProcessorColorSpace { Usage = 1, RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 });
            videoContext.VideoProcessorSetOutputBackgroundColor(processor, false,
                new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
            // Letterbox instead of stretching. Output target is cleared by the video processor.
            double scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
            int destinationWidth = (int)Math.Round(sourceWidth * scale), destinationHeight = (int)Math.Round(sourceHeight * scale);
            int left = (width - destinationWidth) / 2, top = (height - destinationHeight) / 2;
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(left, top, left + destinationWidth, top + destinationHeight));
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, width, height));
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    public IMFSample? TryGetSample()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (clock.Elapsed - lastIdentityCheck >= TimeSpan.FromMilliseconds(250)) ValidateWindow();
        Direct3D11CaptureFrame? latest = null;
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var next = pool.TryGetNextFrame();
                if (next is null) break;
                if (latest is not null) { latest.Dispose(); SupersededFrames++; }
                latest = next;
                CapturedFrames++;
            }
            if (latest is null) return null;
            if (latest.ContentSize.Width != sourceWidth || latest.ContentSize.Height != sourceHeight)
                throw new InvalidOperationException("Window resized; fixed-size probe requires rebind. No stale geometry accepted.");
            long captureTime = latest.SystemRelativeTime.Ticks;
            if (captureTime <= lastCaptureTime) return null;
            firstCaptureTime ??= captureTime;
            lastCaptureTime = captureTime;
            using var inputTexture = GraphicsCaptureInterop.GetTexture(latest.Surface);
            using var inputView = videoDevice.CreateVideoProcessorInputView(inputTexture, enumerator,
                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            // Each submitted sample owns a distinct texture. Reuse requires tracked-sample release, not ProcessInput return.
            using var outputTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
            });
            using var outputView = videoDevice.CreateVideoProcessorOutputView(outputTexture, enumerator,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
            videoContext.VideoProcessorBlt(processor, outputView, outputIndex++,
                [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
            context.Flush();
            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, outputTexture, 0, false);
            var sample = MediaFactory.MFCreateSample();
            try
            {
                sample.AddBuffer(buffer);
                sample.SampleTime = captureTime - firstCaptureTime.Value;
                sample.SampleDuration = 10_000_000 / 30;
                return sample;
            }
            catch { sample.Dispose(); throw; }
        }
        finally { latest?.Dispose(); }
    }

    private void ValidateWindow()
    {
        var current = WindowCatalog.Find(target.ProcessName).SingleOrDefault(w => w.Handle == target.Handle
            && w.ProcessId == target.ProcessId && w.ProcessStartedAtUtc == target.ProcessStartedAtUtc);
        if (current is null) throw new InvalidOperationException("Captured window identity changed or closed.");
        if (current.Minimized) throw new InvalidOperationException("Captured window is minimized; restore locally before reconnecting.");
        lastIdentityCheck = clock.Elapsed;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session?.Dispose(); pool?.Dispose();
        processor?.Dispose(); enumerator?.Dispose(); videoContext?.Dispose(); videoDevice?.Dispose();
        DeviceManager?.Dispose(); captureDevice?.Dispose(); context?.Dispose(); device?.Dispose();
    }
}
