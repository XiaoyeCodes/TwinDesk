namespace Workbench.Windows;

public sealed record NativeInputCheck(bool Allowed,string Code,WindowBounds VirtualDesktop);
public interface INativeInputEnvironment { NativeInputCheck Check(OwnedWindowScene scene,ScreenPoint? point); }

/// <summary>Serial native backend. Use only from InputSession's sole executor, never concurrent callers.</summary>
public sealed class NativeInputBackend(INativeInputEnvironment environment,INativeInputTransport transport) : IInputBackend
{
    private OwnedWindowScene? prepared;
    private ScreenPoint? preparedPoint;
    private readonly HashSet<ushort> unicodePending=[];
    public string LastCode { get; private set; }="NOT_CHECKED";
    public string? ReadinessCode=>LastCode;
    public bool HasPendingTransient=>unicodePending.Count!=0;
    public bool IsTargetReady(OwnedWindowScene scene,ScreenPoint? point)
    {
        prepared=null;
        var check=environment.Check(scene,point);LastCode=check.Code;
        if(!check.Allowed)return false;
        if(HasPendingTransient){LastCode="NATIVE_RELEASE_PENDING";return false;}
        prepared=scene;preparedPoint=point;return true;
    }
    public bool TrySend(InputCommand command,ScreenPoint? point)
    {
        var scene=prepared;prepared=null;
        if(scene is null || preparedPoint!=point || !InputCommandValidation.IsValid(command))return false;
        ScreenPoint? expectedPoint=command.U is { } u?SceneInputCoordinates.ToScreen(scene,u,command.V!.Value):null;
        if(point!=expectedPoint)return false;
        if(command.Kind is InputKind.KeyUp or InputKind.ButtonUp or InputKind.ReleaseAll)return false;
        if(command.Kind==InputKind.Text)return SendText(scene,command.Text!,checked((nuint)command.Sequence));
        var check=environment.Check(scene,point);LastCode=check.Code;if(!check.Allowed)return false;
        nuint marker=checked((nuint)command.Sequence);
        var events=new List<NativeInputEvent>(3);
        if(point is { } p)events.Add(NativeInputEvents.Move(p,check.VirtualDesktop,marker));
        switch(command.Kind)
        {
            case InputKind.KeyDown: events.Add(NativeInputEvents.PhysicalKey(command.Key!,false,marker));break;
            case InputKind.ButtonDown: events.Add(NativeInputEvents.Button(command.Button!.Value,false,marker));break;
            case InputKind.Wheel:
                if(command.WheelY!=0)events.Add(NativeInputEvent.Mouse(0x800,wheel:command.WheelY,marker:marker));
                if(command.WheelX!=0)events.Add(NativeInputEvent.Mouse(0x1000,wheel:command.WheelX,marker:marker));
                break;
            case InputKind.Move: break;
            default:return false;
        }
        bool complete=transport.Send(events.ToArray())==events.Count;
        LastCode=complete?"SUBMITTED_NOT_APPLICATION_ACK":"NATIVE_PARTIAL_OR_FAILED";
        return complete;
    }
    private bool SendText(OwnedWindowScene scene,string text,nuint marker)
    {
        for(int offset=0;offset<text.Length;)
        {
            var check=environment.Check(scene,null);LastCode=check.Code;if(!check.Allowed)return false;
            int size=Math.Min(64,text.Length-offset);
            if(char.IsHighSurrogate(text[offset+size-1]))size--; // Never split a surrogate pair between native batches.
            var units=text.AsSpan(offset,size);
            var events=NativeInputEvents.Unicode(units,marker);
            foreach(char unit in units)unicodePending.Add(unit); // Before call: partial/throwing sends may have pressed VK_PACKET.
            try
            {
                if(transport.Send(events)!=events.Length){LastCode="UNICODE_PARTIAL_OR_FAILED";return false;}
                unicodePending.Clear();
            }
            catch(Exception){LastCode="UNICODE_SEND_EXCEPTION";throw;}
            offset+=size;
        }
        LastCode="SUBMITTED_NOT_APPLICATION_ACK";return true;
    }
    public bool TryRelease(IReadOnlyList<HeldInput> held)
    {
        prepared=null;
        bool success=true;
        // Release individually so success is known for each transient unit. Never send a move or a new down here.
        foreach(ushort unit in unicodePending.ToArray())
        {
            try { if(transport.Send([NativeInputEvent.Key(unit,6)])==1)unicodePending.Remove(unit);else success=false; }
            catch(Exception){success=false;}
        }
        foreach(var input in held)
        {
            try { if(transport.Send([NativeInputEvents.Release(input)])!=1)success=false; }
            catch(Exception){success=false;}
        }
        if(!success)LastCode="NATIVE_RELEASE_FAILED";
        return success;
    }
}
