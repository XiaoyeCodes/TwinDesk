using System.Runtime.InteropServices;

namespace Workbench.Windows;

// Server-side native representation only. Never accept these structs directly from a network client.
[StructLayout(LayoutKind.Sequential)]
public struct NativeInputEvent
{
    public uint Type;
    public NativeInputUnion Data;
    public static NativeInputEvent Key(ushort scan,uint flags,nuint marker=0)=>new()
        { Type=1,Data=new() { Keyboard=new() { Scan=scan,Flags=flags,ExtraInfo=marker } } };
    public static NativeInputEvent Mouse(uint flags,int x=0,int y=0,int wheel=0,nuint marker=0)=>new()
        { Type=0,Data=new() { Mouse=new() { X=x,Y=y,Flags=flags,MouseData=unchecked((uint)wheel),ExtraInfo=marker } } };
}
[StructLayout(LayoutKind.Explicit)]
public struct NativeInputUnion
{
    [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    [FieldOffset(0)] public NativeMouseInput Mouse;
}
[StructLayout(LayoutKind.Sequential)]
public struct NativeKeyboardInput { public ushort VirtualKey,Scan;public uint Flags,Time;public nuint ExtraInfo; }
[StructLayout(LayoutKind.Sequential)]
public struct NativeMouseInput { public int X,Y;public uint MouseData,Flags,Time;public nuint ExtraInfo; }

public static class NativeInputEvents
{
    public static NativeInputEvent PhysicalKey(string code,bool up,nuint marker=0)
    {
        if(!PhysicalKeyMap.TryGet(code,out var key))throw new ArgumentException("Unsupported physical key.");
        return NativeInputEvent.Key(key.ScanCode,8u|(key.Extended?1u:0u)|(up?2u:0u),marker);
    }
    public static NativeInputEvent Button(InputButton button,bool up,nuint marker=0) => NativeInputEvent.Mouse(button switch
        { InputButton.Left=>up?4u:2u,InputButton.Right=>up?16u:8u,InputButton.Middle=>up?64u:32u,_=>throw new ArgumentOutOfRangeException(nameof(button)) },marker:marker);
    public static NativeInputEvent Move(ScreenPoint physical,WindowBounds desktop,nuint marker=0)
    {
        var point=SceneInputCoordinates.ToAbsolute(physical,desktop);
        return NativeInputEvent.Mouse(0x8000|0x4000|1,point.X,point.Y,marker:marker);
    }
    public static NativeInputEvent Release(HeldInput input) => (input.Key,input.Button) switch
        { ({ } key,null)=>PhysicalKey(key,true),(null,{ } button)=>Button(button,true),_=>throw new ArgumentException("Invalid release identity.") };
    public static NativeInputEvent[] Unicode(ReadOnlySpan<char> text,nuint marker=0)
    {
        if(text.Length is <1 or >64 || !InputCommandValidation.ValidText(text.ToString()))throw new ArgumentException("Invalid bounded Unicode batch.");
        var result=new NativeInputEvent[text.Length*2];
        for(int i=0;i<text.Length;i++){result[2*i]=NativeInputEvent.Key(text[i],4,marker);result[2*i+1]=NativeInputEvent.Key(text[i],6,marker);}
        return result;
    }
}

public interface INativeInputTransport { uint Send(NativeInputEvent[] events); }
public sealed class WindowsInputTransport : INativeInputTransport
{
    public uint Send(NativeInputEvent[] events)
    {
        if(events.Length is <1 or >128)throw new ArgumentOutOfRangeException(nameof(events));
        return SendInput((uint)events.Length,events,Marshal.SizeOf<NativeInputEvent>());
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,[In] NativeInputEvent[] inputs,int size);
}
