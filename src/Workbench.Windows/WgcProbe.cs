using System.Diagnostics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Foundation;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Workbench.Windows;

public sealed record CaptureProbeResult(string Mode, string WindowTitle, int Width, int Height,
    int Frames, double DurationSeconds, double FrameArrivalRate, double FirstFrameMs, string SnapshotPath,
    WindowInfo Target, DateTimeOffset RecordedAt, string MeasurementNote);

public sealed record MinimizedCaptureObservation(string Status, int ItemWidth, int ItemHeight,
    int Frames, double DurationSeconds, double? FirstFrameMs, WindowInfo Target, DateTimeOffset RecordedAt,
    string MeasurementNote)
{ public string? CallbackError {get;init;} }

public static class WgcProbe
{
    // Deliberately separate from RunAsync: this read-only diagnostic never grants a capture
    // binding, saves a frame, or weakens the normal minimized-window rejection.
    public static async Task<MinimizedCaptureObservation> ObserveMinimizedAsync(WindowInfo target,
        TimeSpan duration, CancellationToken cancellationToken)
    {
        if(duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(duration));
        var live=WindowCatalog.Find(target.ProcessName).SingleOrDefault(w=>
            OwnedWindowScene.SameIdentity(w,target))
            ?? throw new InvalidOperationException("Target identity changed before minimized observation.");
        if(!live.Minimized)throw new InvalidOperationException("This diagnostic requires an actually minimized target.");
        if(!GraphicsCaptureSession.IsSupported())throw new NotSupportedException("WGC unavailable in this session.");
        D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
        using(device)
        using(context)
        using(var dxgi=device.QueryInterface<IDXGIDevice>())
        using(var captureDevice=GraphicsCaptureInterop.FromDxgiDevice(dxgi.NativePointer))
        {
            var item=GraphicsCaptureInterop.ForWindow((nint)live.Handle);
            var size=item.Size;
            if(size.Width<1 || size.Height<1 || size.Width>8192 || size.Height>8192)
                throw new InvalidDataException("Minimized capture item has invalid dimensions.");
            using var pool=Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,2,size);
            using var session=pool.CreateCaptureSession(item);
            int frames=0;
            long firstTicks=0;
            Exception? callbackFailure=null;
            var watch=Stopwatch.StartNew();
            TypedEventHandler<Direct3D11CaptureFramePool,object> handler=(sender,_)=>
            {
                try
                {
                    using var frame=sender.TryGetNextFrame();
                    if(frame is null)return;
                    if(Interlocked.Increment(ref frames)==1)Interlocked.Exchange(ref firstTicks,watch.ElapsedTicks);
                }
                catch(Exception e) {Interlocked.CompareExchange(ref callbackFailure,e,null);}
            };
            pool.FrameArrived+=handler;
            try
            {
                session.StartCapture();
                await Task.Delay(duration,cancellationToken);
                var after=WindowCatalog.Find(target.ProcessName).SingleOrDefault(w=>OwnedWindowScene.SameIdentity(w,target));
                var stillMinimized=after?.Minimized==true;
                return new(callbackFailure is not null?"CAPTURE_CALLBACK_FAILED":
                    stillMinimized?(frames==0?"NO_FRAME_WHILE_MINIMIZED":"FRAMES_WHILE_MINIMIZED_NOT_LIVE_VERIFIED")
                    :"INCONCLUSIVE_TARGET_CHANGED",size.Width,size.Height,frames,watch.Elapsed.TotalSeconds,
                    firstTicks==0?null:firstTicks*1000.0/Stopwatch.Frequency,live,DateTimeOffset.Now,
                    "Read-only WGC frame-arrival observation while the real HWND is minimized. No pixels, input, app workflow or dynamic frame validation; a nonzero count alone does not prove usable background capture.")
                    {CallbackError=callbackFailure?.ToString()};
            }
            finally {pool.FrameArrived-=handler;}
        }
    }

    public static async Task<CaptureProbeResult> RunAsync(WindowInfo window, string outputPath, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(duration));
        window = WindowCatalog.Find(window.ProcessName).SingleOrDefault(current => current.Handle == window.Handle
            && current.ProcessId == window.ProcessId && current.ProcessStartedAtUtc == window.ProcessStartedAtUtc)
            ?? throw new InvalidOperationException("The selected window/process no longer exists; enumerate it again.");
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is not available in this session.");
        if (window.Minimized) throw new InvalidOperationException("Target window is minimized. Restore it before capture.");
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
        using (var captureDevice = GraphicsCaptureInterop.FromDxgiDevice(dxgiDevice.NativePointer))
        {
            var item = GraphicsCaptureInterop.ForWindow((nint)window.Handle);
            var initialSize = item.Size;
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, initialSize);
            using var session = pool.CreateCaptureSession(item);
            var snapshot = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            var captureFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelRegistration = cancellationToken.Register(() => snapshot.TrySetCanceled(cancellationToken));
            int frames = 0;
            double firstFrameMs = -1;
            int width = 0, height = 0;
            int stopped = 0;
            var timer = Stopwatch.StartNew();
            TypedEventHandler<Direct3D11CaptureFramePool, object> frameHandler = async (sender, _) =>
            {
                try
                {
                    if (Volatile.Read(ref stopped) != 0) return;
                    using var frame = sender.TryGetNextFrame();
                    if (frame is null) return;
                    if (frame.ContentSize.Width != initialSize.Width || frame.ContentSize.Height != initialSize.Height)
                        throw new InvalidOperationException("Source resized during this fixed-size probe. Repeat capture at the new size; dynamic recovery is not yet implemented.");
                    var count = Interlocked.Increment(ref frames);
                    if (count != 1) return;
                    firstFrameMs = timer.Elapsed.TotalMilliseconds;
                    width = frame.ContentSize.Width;
                    height = frame.ContentSize.Height;
                    // Probe only: a single CPU readback for its evidence snapshot.
                    var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                    if (!snapshot.TrySetResult(bitmap)) bitmap.Dispose();
                }
                catch (Exception exception)
                {
                    if (Volatile.Read(ref stopped) != 0) return;
                    if (!snapshot.TrySetException(exception)) captureFault.TrySetException(exception);
                }
            };
            pool.FrameArrived += frameHandler;
            bool snapshotOwned = false;
            try
            {
                session.StartCapture();
                using var bitmap = await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                snapshotOwned = true;
                outputPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputPath)!);
                var file = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.FailIfExists);
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetSoftwareBitmap(bitmap);
                    await encoder.FlushAsync();
                }
                await await Task.WhenAny(Task.Delay(duration, cancellationToken), captureFault.Task);
                if (captureFault.Task.IsFaulted) await captureFault.Task;
                return new("wgc", window.Title, width, height, frames, timer.Elapsed.TotalSeconds,
                    frames / timer.Elapsed.TotalSeconds, firstFrameMs, outputPath, window, DateTimeOffset.Now,
                    "FrameArrivalRate is not browser FPS. Static WGC sources may produce only one frame. FirstFrameMs starts at capture start; this is not end-to-end latency. Snapshot readback is probe-only.");
            }
            finally
            {
                Volatile.Write(ref stopped, 1);
                pool.FrameArrived -= frameHandler;
                snapshot.TrySetCanceled();
                if (!snapshotOwned && snapshot.Task.IsCompletedSuccessfully) snapshot.Task.Result.Dispose();
            }
        }
    }
}
