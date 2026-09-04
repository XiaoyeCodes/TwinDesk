namespace Workbench.Windows;

/// <summary>
/// Backend boundary, not a claim of native focus safety. Implementations must make fresh native checks;
/// no default permissive implementation is provided. Calls must be bounded and must not call back into this session.
/// </summary>
public interface IInputBackend
{
    bool HasPendingTransient => false;
    bool IsTargetReady(OwnedWindowScene scene, ScreenPoint? point);
    // Native implementation must additionally account for partial Unicode/text batches and their transient key-ups.
    // This core only tracks persistent physical keys/buttons; a backend without text safety must reject Text.
    bool TrySend(InputCommand command, ScreenPoint? point);
    // Must emit ONLY up events, including without foreground/media readiness. False retains the conservative ledger.
    bool TryRelease(IReadOnlyList<HeldInput> held);
}

public sealed record InputOutcome(bool Accepted,string Code);
public sealed record InputSessionStatus(bool Active,bool Ready,int HeldCount,int PendingFrames,long LastSequence,string Reason);

/// <summary>
/// One immutable lease/target lifetime. Serializes backend calls, gates input on presented frames, and owns a
/// conservative release ledger. Not a Host/Agent watchdog: the caller must schedule Tick independently of media.
/// </summary>
public sealed class InputSession
{
    // Leave 500 ms for the native poll/release path within the external six-second safety target.
    public static readonly TimeSpan ControlExpiry=TimeSpan.FromMilliseconds(5500);
    private readonly object sync=new();
    private readonly InputLease lease;
    private readonly WindowInfo root;
    private readonly IInputBackend backend;
    private readonly TimeProvider time;
    private readonly HashSet<HeldInput> held=[];
    private readonly Queue<(uint Seq,long At)> pending=new();
    private OwnedWindowScene? scene;
    private InputStamp stamp;
    private uint lastSent,lastDisplayed;
    private long lastSequence,lastHeartbeat;
    private bool active=true,ready;
    private string reason="STREAM_NOT_READY";

    public InputSession(InputLease lease,WindowInfo root,IInputBackend backend,TimeProvider? time=null)
    {
        if(lease.Id==Guid.Empty || lease.Generation<1)throw new ArgumentException("Invalid lease.");
        this.lease=lease;this.root=root??throw new ArgumentNullException(nameof(root));
        this.backend=backend??throw new ArgumentNullException(nameof(backend));this.time=time??TimeProvider.System;
        lastHeartbeat=this.time.GetTimestamp();
    }
    public InputSessionStatus Status { get { lock(sync)return new(active,ready,held.Count,pending.Count,lastSequence,reason); } }

    public bool UpdateScene(InputStamp next,OwnedWindowScene geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        lock(sync)
        {
            Expire(); if(!active)return false;
            if(next.Host==Guid.Empty || next.Stream==0 || next.Epoch==0 || next.Scene==0)throw new ArgumentException("Invalid scene stamp.");
            if(scene is not null && (next.Host!=stamp.Host || next.Stream!=stamp.Stream || next.Epoch<stamp.Epoch
                || next.Scene<stamp.Scene || next==stamp))throw new InvalidOperationException("Scene stamps must advance within the bound stream.");
            // Defensively copy the node collection; public callers may hold a mutable list.
            OwnedWindowScene copy;
            try
            {
                copy=new OwnedWindowScene(geometry.Bounds,Array.AsReadOnly(geometry.Nodes.ToArray()));
                if(copy.Nodes.Count is < 1 or > OwnedWindowScene.MaximumNodes)throw new InvalidDataException("Invalid geometry.");
                var admitted=OwnedWindowScene.Select(root,copy.Nodes.Select(node=>node.Window).ToArray());
                if(admitted.Count!=copy.Nodes.Count || !copy.SameGeometry(OwnedWindowScene.Arrange(admitted)))
                    throw new InvalidDataException("Geometry must match the bound root's validated owner scene.");
                _=SceneInputCoordinates.ToScreen(copy,0,0);
            }
            catch(Exception) { RevokeCore("SCENE_INVALID");throw; }
            ready=false;reason="STREAM_NOT_READY";
            if(!Release(held.ToArray())) { RevokeCore("INPUT_RELEASE_FAILED");return false; }
            bool newEpoch=scene is null || next.Epoch!=stamp.Epoch;
            scene=copy;stamp=next;pending.Clear();lastDisplayed=0;
            if(newEpoch)lastSent=0;
            return true;
        }
    }

    public bool FrameSent(InputStamp value,uint frame)
    {
        lock(sync)
        {
            Expire();if(!active || scene is null || value!=stamp)return false;
            if(frame==0 || frame<=lastSent || pending.Count>=256) { RevokeCore("MEDIA_BACKPRESSURE");return false; }
            lastSent=frame;pending.Enqueue((frame,time.GetTimestamp()));return true;
        }
    }

    public bool Displayed(InputLease owner,InputStamp value,uint frame)
    {
        lock(sync)
        {
            Expire();if(!active || owner!=lease || scene is null || value!=stamp || frame<=lastDisplayed
                || !pending.Any(item=>item.Seq==frame))return false;
            lastDisplayed=frame;
            while(pending.TryPeek(out var item) && item.Seq<=frame)pending.Dequeue();
            // Foreground is checked again for every down/move/wheel/text, not inferred from this ACK.
            ready=true;reason="READY";return true;
        }
    }

    public bool Heartbeat(InputLease owner)
    {
        lock(sync){Expire();if(!active || owner!=lease)return false;lastHeartbeat=time.GetTimestamp();return true;}
    }

    public InputOutcome Dispatch(InputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock(sync)
        {
            Expire();
            if(command.Lease!=lease)return Reject("LEASE_STALE");
            if(!active)return Reject(reason);
            if(!InputCommandValidation.IsValid(command))return Reject("INVALID_MESSAGE");
            if(command.Sequence<=lastSequence)return Reject("INPUT_OUT_OF_ORDER");
            lastSequence=command.Sequence; // Rejected native actions are never queued or automatically replayed.
            if(command.Kind==InputKind.ReleaseAll)return ReleaseResult(held.ToArray());
            if(command.Kind==InputKind.KeyUp)return ReleaseResult([HeldInput.ForKey(command.Key!)]);
            if(command.Kind==InputKind.ButtonUp)return ReleaseResult([HeldInput.ForButton(command.Button!.Value)]);
            if(scene is null || !ready)return Reject("STREAM_NOT_READY");
            if(command.Stamp!=stamp)return Reject("SCENE_STALE");
            if(command.DisplayedFrame==0 || command.DisplayedFrame!=lastDisplayed)return Reject("FRAME_STALE");
            if(command.Kind==InputKind.Text && held.Count!=0)return Reject("TEXT_WHILE_HELD");
            HeldInput? down=command.Kind switch { InputKind.KeyDown=>HeldInput.ForKey(command.Key!),
                InputKind.ButtonDown=>HeldInput.ForButton(command.Button!.Value), _=>null };
            if(down is { } identity && (held.Contains(identity)!=command.Repeat))return Reject("INPUT_HELD_STATE_MISMATCH");
            if(down is not null && !command.Repeat && held.Count>=64){RevokeCore("INPUT_HELD_BUDGET");return Reject(reason);}
            ScreenPoint? point=command.U is { } u ? SceneInputCoordinates.ToScreen(scene,u,command.V!.Value) : null;
            try
            {
                if(!backend.IsTargetReady(scene,point)){RevokeCore("FOCUS_OR_DESKTOP_DENIED");return Reject(reason);}
                if(down is { } key)held.Add(key); // Before native call: a short/throwing send may already have pressed it.
                if(!backend.TrySend(command,point)){RevokeCore("INPUT_SEND_FAILED");return Reject(reason);}
                return new(true,"SUBMITTED_NOT_APPLICATION_ACK");
            }
            catch(Exception) { RevokeCore("INPUT_BACKEND_FAILED");return Reject(reason); }
        }
    }

    public void Tick() { lock(sync)Expire(); }
    public void Invalidate(string code="MEDIA_UNAVAILABLE") { lock(sync)RevokeCore(code); }
    public bool RetrySafetyRelease() { lock(sync){if(active)throw new InvalidOperationException("Revoke before safety retry.");return Release(held.ToArray());} }
    private void Expire()
    {
        if(!active)return;
        long now=time.GetTimestamp();
        if(time.GetElapsedTime(lastHeartbeat,now)>=ControlExpiry)RevokeCore("HEARTBEAT_EXPIRED");
        else if(pending.TryPeek(out var first) && time.GetElapsedTime(first.At,now)>=TimeSpan.FromSeconds(3))RevokeCore("DISPLAY_ACK_TIMEOUT");
    }
    private void RevokeCore(string code)
    {
        active=false;ready=false;reason=code;pending.Clear();
        if(!Release(held.ToArray()))reason="INPUT_RELEASE_FAILED";
    }
    private InputOutcome ReleaseResult(HeldInput[] requested)
    {
        if(Release(requested))return new(true,"RELEASED");
        RevokeCore("INPUT_RELEASE_FAILED");return Reject(reason);
    }
    private bool Release(HeldInput[] requested)
    {
        var owned=requested.Where(held.Contains).OrderBy(key=>key.Button is null?1:0).ToArray();
        if(owned.Length==0 && !backend.HasPendingTransient)return true;
        try { if(!backend.TryRelease(Array.AsReadOnly(owned)))return false; }
        catch(Exception){return false;}
        foreach(var key in owned)held.Remove(key);
        return true;
    }
    private static InputOutcome Reject(string code)=>new(false,code);
}
