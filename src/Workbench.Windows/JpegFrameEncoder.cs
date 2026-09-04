using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Workbench.Windows;

public sealed record BgraProbeFrame(int Width, int Height, byte[] Pixels, long TimestampUs, ProbeSceneConfig Scene);

/// <summary>Explicit CPU JPEG compatibility path. Never changes H264 selection or bypasses secure transport.</summary>
public static class JpegFrameEncoder
{
    public const int MaximumPayload = 8 * 1024 * 1024;
    public static async Task<byte[]> Encode(BgraProbeFrame frame, float quality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width is < 128 or > 2560 || frame.Height is < 128 or > 1440
            || frame.Pixels is null || frame.Pixels.Length != checked(frame.Width * frame.Height * 4)
            || !float.IsFinite(quality) || quality is < 0.1f or > 1f || frame.TimestampUs < 0
            || frame.Scene.Width != frame.Width || frame.Scene.Height != frame.Height)
            throw new ArgumentException("Invalid bounded JPEG input.");
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties).AsTask(cancellationToken);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);
        if (stream.Size is 0 or > MaximumPayload) throw new InvalidDataException("JPEG payload exceeded budget.");
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        uint length = checked((uint)stream.Size);
        if (await reader.LoadAsync(length).AsTask(cancellationToken) != length) throw new EndOfStreamException("Incomplete JPEG output.");
        byte[] bytes = new byte[length]; reader.ReadBytes(bytes); return bytes;
    }
}
