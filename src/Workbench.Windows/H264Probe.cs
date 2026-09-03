using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Workbench.Windows;

public sealed record EncodedAccessUnit(long TimestampUs, bool KeyFrame, byte[] Data, int[] NalTypes, string CodecString)
{
    public ProbeSceneConfig? Scene { get; init; }
}
public sealed record H264ProbeResult(string Source, string Encoder, bool Hardware, bool Asynchronous,
    int Width, int Height, int Fps, int InputFrames, int OutputFrames, long OutputBytes,
    double DurationSeconds, double FirstOutputMs, string CodecString, string Format, int OutputTypeChanges, DateTimeOffset RecordedAt);

/// <summary>Finite MFT experiment with an explicit generated NV12 or live GPU source.</summary>
public static class H264Probe
{
    private const int NoEvents = unchecked((int)0xC00D3E80);
    private const int NeedInput = unchecked((int)0xC00D6D72);
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    // Run on one worker thread: native event handling and callbacks never concurrently access the MFT.
    public static H264ProbeResult Run(int frameCount, bool hardware, Action<EncodedAccessUnit> onFrame,
        CancellationToken cancellationToken, int width = 1280, int height = 720, int fps = 30,
        Func<IProbeFrameSource>? sourceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (frameCount is < 1 or > 18000 || width is < 128 or > 2560 || height is < 128 or > 1440
            || (width & 1) != 0 || (height & 1) != 0 || fps is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Invalid bounded probe settings.");
        cancellationToken.ThrowIfCancellationRequested();
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var frameSource = sourceFactory is null ? null : sourceFactory()
                ?? throw new InvalidOperationException("Requested live source returned null; generated fallback is forbidden.");
            using var activation = EncoderCatalog.OpenH264Activation(hardware, 0);
            var name = activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            using var transform = activation.ActivateObject<IMFTransform>();
            try
            {
                using var attributes = transform.Attributes;
                var isAsync = attributes.GetUInt32(TransformAttributeKeys.TransformAsync, out var asyncValue).Success && asyncValue != 0;
                if (isAsync) attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
                attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
                if (frameSource is not null)
                {
                    if (!attributes.GetUInt32(TransformAttributeKeys.D3D11Aware, out var aware).Success || aware == 0)
                        throw new NotSupportedException("Selected encoder does not advertise D3D11 input support.");
                    transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)frameSource.DeviceManager.NativePointer);
                }
                transform.GetStreamCount(out var inputs, out var outputs);
                if (inputs != 1 || outputs != 1) throw new NotSupportedException("Probe requires one input/output stream.");
                int[] inputIds = [0], outputIds = [0];
                try { transform.GetStreamIDs(1, inputIds, 1, outputIds); }
                catch (Exception e) when (e.HResult == unchecked((int)0x80004001)) { } // E_NOTIMPL means consecutive IDs.
                using var outputType = CreateType(H264, width, height, fps);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 4_000_000u).CheckError();
                outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u).CheckError(); // Baseline has no B slices.
                if (frameSource is not null) SetColorMetadata(outputType);
                transform.SetOutputType(outputIds[0], outputType, 0);
                using var inputType = CreateType(Nv12, width, height, fps);
                if (frameSource is not null) SetColorMetadata(inputType);
                transform.SetInputType(inputIds[0], inputType, 0);
                using var events = isAsync ? transform.QueryInterface<IMFMediaEventGenerator>() : null;
                var normalizer = new AnnexBAccessUnits();
                var scenes = new FrameSceneLedger(frameSource is null ? 256 : 8);
                var watch = Stopwatch.StartNew();
                var lastProgress = watch.Elapsed;
                int sent = 0, received = 0, credits = 0, outputTypeChanges = 0;
                long bytes = 0;
                double firstMs = -1;
                bool draining = false, drained = false;
                var pixels = frameSource is null ? new byte[checked(width * height * 3 / 2)] : null;
                double nextSourcePoll = 0;
                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
                while (!drained)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frameSource is not null && !draining && watch.Elapsed.TotalSeconds >= (double)frameCount / fps)
                    {
                        if (sent == 0) throw new TimeoutException("WGC produced no sample during the requested observation interval.");
                        BeginDrain();
                    }
                    if ((frameSource is null || draining || sent > received) && watch.Elapsed - lastProgress > TimeSpan.FromSeconds(10))
                        throw new TimeoutException($"Encoder stalled: input={sent}, output={received}, draining={draining}.");
                    if (isAsync)
                    {
                        // NO_WAIT keeps the finite probe cancellable; production switches to event callbacks.
                        for (int n = 0; n < 16; n++)
                        {
                            IMFMediaEvent mediaEvent;
                            try { mediaEvent = events!.GetEvent(1); }
                            catch (Exception e) when (e.HResult == NoEvents) { break; }
                            using (mediaEvent)
                            {
                                mediaEvent.Status.CheckError();
                                switch (mediaEvent.EventType)
                                {
                                    case MediaEventTypes.TransformNeedInput:
                                        if (++credits > 256) throw new InvalidDataException("Unbounded encoder input credits.");
                                        break;
                                    case MediaEventTypes.TransformHaveOutput: Receive(); break;
                                    case MediaEventTypes.TransformDrainComplete: drained = true; break;
                                }
                            }
                        }
                    }
                    else
                    {
                        while (Receive()) { cancellationToken.ThrowIfCancellationRequested(); }
                        if (draining) drained = true;
                    }
                    if (drained) break;
                    if (!draining && sent < frameCount && (!isAsync || credits > 0)
                        && (frameSource is null || sent - received < 8)
                        && watch.Elapsed.TotalSeconds >= (frameSource is null ? (double)sent / fps : nextSourcePoll))
                    {
                        nextSourcePoll = watch.Elapsed.TotalSeconds + 1.0 / fps;
                        using var sample = frameSource is null ? GeneratedSample() : frameSource.TryGetSample();
                        if (sample is null) continue;
                        scenes.Add(sample.SampleTime, frameSource?.LastSampleScene);
                        transform.ProcessInput(inputIds[0], sample, 0);
                        sent++;
                        if (isAsync) credits--;
                        lastProgress = watch.Elapsed;
                        if (sent == frameCount)
                        {
                            BeginDrain();
                        }
                    }
                    if (cancellationToken.WaitHandle.WaitOne(2)) cancellationToken.ThrowIfCancellationRequested();
                }
                transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
                if (sent != received) throw new InvalidDataException($"Frame count mismatch: {sent} input, {received} output.");
                if (scenes.Count != 0) throw new InvalidDataException("Unreleased encoded frame metadata.");
                return new(frameSource?.Description ?? "generated-nv12-moving-pattern (not NX/TIA)", name, hardware, isAsync,
                    width, height, fps, sent, received, bytes, watch.Elapsed.TotalSeconds, firstMs,
                    normalizer.CodecString ?? throw new InvalidDataException("No SPS codec configuration."), "annexb", outputTypeChanges, DateTimeOffset.Now);

                void BeginDrain()
                {
                    transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, (nuint)inputIds[0]);
                    transform.ProcessMessage(TMessageType.MessageCommandDrain, 0);
                    draining = true;
                    lastProgress = watch.Elapsed;
                }

                IMFSample GeneratedSample()
                {
                    FillPattern(pixels!, width, height, sent);
                    using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels!.Length);
                    buffer.Lock(out var data, out _, out _);
                    try { Marshal.Copy(pixels, 0, data, pixels.Length); }
                    finally { buffer.Unlock(); }
                    buffer.CurrentLength = pixels.Length;
                    var sample = MediaFactory.MFCreateSample();
                    try
                    {
                        sample.AddBuffer(buffer);
                        sample.SampleTime = sent * 10_000_000L / fps;
                        sample.SampleDuration = (sent + 1) * 10_000_000L / fps - sample.SampleTime;
                        return sample;
                    }
                    catch { sample.Dispose(); throw; }
                }

                bool Receive()
                {
                    (byte[] Data, long Time)? unit = null;
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        try { unit = ReadOutput(transform, outputIds[0]); break; }
                        catch (COMException e) when (e.HResult == unchecked((int)0xC00D6D61) && attempt < 3)
                        {
                            // Hardware may finalize its output type only after the first input.
                            using var changedType = transform.GetOutputAvailableType(outputIds[0], 0);
                            if (changedType.GetGUID(MediaTypeAttributeKeys.Subtype) != H264)
                                throw new InvalidDataException("Encoder changed to a non-H264 subtype.");
                            MediaFactory.MFGetAttributeSize(changedType, MediaTypeAttributeKeys.FrameSize, out var changedWidth, out var changedHeight).CheckError();
                            if (changedWidth != width || changedHeight != height)
                                throw new InvalidDataException("Unexpected encoder output dimensions.");
                            transform.SetOutputType(outputIds[0], changedType, 0);
                            if (++outputTypeChanges > 4) throw new InvalidDataException("Repeated output type changes in fixed-format probe.");
                            if (isAsync) return false; // Await the next HaveOutput credit after renegotiation.
                        }
                    }
                    if (unit is null) return false;
                    var normalized = normalizer.Normalize(unit.Value.Data, unit.Value.Time / 10) with { Scene = scenes.Take(unit.Value.Time) };
                    if (firstMs < 0) firstMs = watch.Elapsed.TotalMilliseconds;
                    onFrame(normalized);
                    received++;
                    bytes += normalized.Data.Length;
                    lastProgress = watch.Elapsed;
                    return true;
                }
            }
            finally { activation.ShutdownObject(); }
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }

    private static IMFMediaType CreateType(Guid subtype, int width, int height, int fps)
    {
        var type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError(); // Progressive.
            MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    private static void SetColorMetadata(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)VideoTransferMatrix.Bt709).CheckError();
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709).CheckError();
        type.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709).CheckError();
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235).CheckError();
    }

    private static void FillPattern(byte[] pixels, int width, int height, int frame)
    {
        // Gray ramp and moving bright square; UV fixed neutral. No screen or private content is read.
        pixels.AsSpan(width * height).Fill(128);
        int left = frame * 9 % (width - 64);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels[y * width + x] = (byte)(x >= left && x < left + 64 && y >= 64 && y < 128 ? 235 : 16 + x * 180 / width);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOutput { public int StreamId; public nint Sample; public int Status; public nint Events; }

    private static unsafe (byte[] Data, long Time)? ReadOutput(IMFTransform transform, int streamId)
    {
        var info = transform.GetOutputStreamInfo(streamId);
        using var provided = (info.Flags & 0x100) == 0 ? MediaFactory.MFCreateSample() : null;
        if (provided is not null)
        {
            if (info.Size is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid encoder output buffer size.");
            using var buffer = MediaFactory.MFCreateAlignedMemoryBuffer(info.Size, Math.Max(0, info.Alignment - 1));
            provided.AddBuffer(buffer);
        }
        var native = new NativeOutput { StreamId = streamId, Sample = provided?.NativePointer ?? 0 };
        try
        {
            // IMFTransform::ProcessOutput (IUnknown 0..2, IMFTransform 3..25).
            // Raw pointers make caller-supplied vs MFT-owned sample references unambiguous.
            var method = (delegate* unmanaged[Stdcall]<nint, int, int, NativeOutput*, int*, int>)(*(nint**)transform.NativePointer)[25];
            int status = 0;
            int hr = method(transform.NativePointer, 0, 1, &native, &status);
            if (hr == NeedInput) return null;
            Marshal.ThrowExceptionForHR(hr);
            if (native.Sample == 0) throw new InvalidDataException("Encoder output event contained no sample.");
            // Wrapper owns an extra reference; the original is released in finally only when supplied by MFT.
            Marshal.AddRef(native.Sample);
            using var sample = new IMFSample(native.Sample);
            using var output = sample.ConvertToContiguousBuffer();
            output.Lock(out var data, out _, out var length);
            try
            {
                if (length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid encoded access unit length.");
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                return (bytes, sample.SampleTime);
            }
            finally { output.Unlock(); }
        }
        finally
        {
            if (native.Events != 0) Marshal.Release(native.Events);
            if (native.Sample != 0 && native.Sample != (provided?.NativePointer ?? 0)) Marshal.Release(native.Sample);
        }
    }
}
