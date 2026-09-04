using System.Diagnostics;
using Vortice.MediaFoundation;

namespace Workbench.Windows;

public sealed record JpegProbeFrame(long TimestampUs, byte[] Data, ProbeSceneConfig Scene);
public sealed record JpegProbeResult(string Source, string Encoder, bool Hardware, int Width, int Height,
    int Fps, float Quality, int InputFrames, int OutputFrames, long OutputBytes, long ReadbackBytes,
    double DurationSeconds, double FirstOutputMs, string Format, DateTimeOffset RecordedAt);

public static class JpegProbe
{
    // Single bounded worker; newest WGC frames are polled at at most 10Hz, no stale JPEG replay or unbounded queue.
    public static JpegProbeResult Run(TimeSpan duration, Func<IBgraProbeFrameSource> sourceFactory,
        Action<JpegProbeFrame> onFrame, CancellationToken cancellationToken, ProbeVideoProfile? profile = null,bool continuous=false)
    {
        if(duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(10))throw new ArgumentOutOfRangeException(nameof(duration));
        if(continuous && !cancellationToken.CanBeCanceled)throw new ArgumentException("Continuous mode requires cancellation.");
        ArgumentNullException.ThrowIfNull(sourceFactory); ArgumentNullException.ThrowIfNull(onFrame);
        profile ??= ProbeVideoProfile.Hd;
        cancellationToken.ThrowIfCancellationRequested(); MediaFactory.MFStartup().CheckError();
        try
        {
            using var source = sourceFactory() ?? throw new InvalidOperationException("A real window source is required for this probe.");
            var watch=Stopwatch.StartNew(); TimeSpan next=TimeSpan.Zero; int count=0; long bytes=0,readback=0; double first=-1;
            while(continuous || watch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(watch.Elapsed < next){cancellationToken.WaitHandle.WaitOne(5);continue;}
                next=watch.Elapsed+TimeSpan.FromMilliseconds(100);
                var frame=source.TryGetBgraFrame(); if(frame is null)continue;
                profile.RequireFrame(frame.Width,frame.Height);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var encoded=JpegFrameEncoder.Encode(frame,0.85f,deadline.Token).GetAwaiter().GetResult();
                onFrame(new(frame.TimestampUs,encoded,frame.Scene));
                if(count==int.MaxValue)throw new InvalidOperationException("Frame counter exhausted; reconnect explicitly.");
                if(count++==0)first=watch.Elapsed.TotalMilliseconds;
                bytes+=encoded.Length;readback+=frame.Pixels.Length;
            }
            if(count==0)throw new TimeoutException("No complete live JPEG frame; no synthetic fallback.");
            return new("live-wgc-owned-scene/gpu-bgra-scale/explicit-cpu-readback", "Windows BitmapEncoder JPEG", false,
                profile.Width,profile.Height,10,0.85f,count,count,bytes,readback,watch.Elapsed.TotalSeconds,first,"jpeg",DateTimeOffset.Now);
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }
}
