namespace Workbench.Windows;

/// <summary>Explicit finite fault injection after real MFT output, before transport. Never reorders access units.</summary>
public sealed class ProbeEncodedSceneDelay(Action<EncodedAccessUnit> output) : IDisposable
{
    private readonly Queue<EncodedAccessUnit> pending=new();
    private bool released,disposed;
    private long firstTimestamp=-1,lastTimestamp=-1;
    private uint lastVersion;
    public uint FirstVersion {get;private set;}
    public uint ReleaseVersion {get;private set;}
    public string? ReleaseReason {get;private set;}
    public int BufferedBytes {get;private set;}
    public int PeakFrames {get;private set;}
    public int PeakBytes {get;private set;}
    public int DelayedFrames {get;private set;}
    public int Count=>pending.Count;

    public void Push(EncodedAccessUnit frame)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(frame.Scene is null || frame.Scene.Version==0 || frame.TimestampUs<=lastTimestamp || frame.Scene.Version<lastVersion)
            throw new InvalidDataException("Encoded delay requires increasing real scene metadata.");
        lastTimestamp=frame.TimestampUs;lastVersion=frame.Scene.Version;
        if(released){output(frame);return;}
        if(pending.Count>=16 || frame.Data.Length is <1 or >8*1024*1024 || BufferedBytes>8*1024*1024-frame.Data.Length)
            throw new InvalidDataException("Encoded fault injection exceeded its finite queue; no dependent frames dropped.");
        if(firstTimestamp<0){firstTimestamp=frame.TimestampUs;FirstVersion=frame.Scene.Version;}
        pending.Enqueue(frame with {Data=frame.Data.ToArray(),NalTypes=frame.NalTypes.ToArray()});
        BufferedBytes+=frame.Data.Length;PeakFrames=Math.Max(PeakFrames,pending.Count);PeakBytes=Math.Max(PeakBytes,BufferedBytes);
        if(frame.Scene.Version!=FirstVersion)Flush("new-captured-scene",frame.Scene.Version);
        else if(frame.TimestampUs-firstTimestamp>=2_000_000)Flush("two-second-sample-deadline",frame.Scene.Version);
    }
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!released && pending.Count>0)Flush("stream-ended-without-scene-transition",lastVersion);
    }
    private void Flush(string reason,uint version)
    {
        released=true;ReleaseReason=reason;ReleaseVersion=version;DelayedFrames=pending.Count;
        while(pending.TryDequeue(out var frame)){BufferedBytes-=frame.Data.Length;output(frame);}
    }
    public void Dispose(){disposed=true;pending.Clear();BufferedBytes=0;}
}
