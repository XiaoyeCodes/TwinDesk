using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Workbench.Windows;

public sealed record LocalDeviceEvent(string Kind,int Dx=0,int Dy=0,string? Code=null,
    string? Button=null,bool Up=false,int WheelX=0,int WheelY=0);

// Explicit finite experiment: physical devices -> browser -> normal InputSession.
// No text logging, direct target input, automatic activation or background startup.
public sealed class LocalConsoleBridge : IDisposable
{
    private readonly object gate=new();
    private readonly List<LocalDeviceEvent> queue=[];
    private readonly Action<string> stopInput;
    private readonly Thread thread;
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint[] targets=[];
    private long deadline;
    private uint threadId;
    private int stopped;
    private int detachFailed;
    private static readonly System.Collections.Concurrent.ConcurrentBag<Delegate> uncertainCallbacks=new();
    private string reason="LOCAL_CONSOLE_ACTIVE";
    public string Reason=>Volatile.Read(ref reason);
    public bool Active=>Volatile.Read(ref stopped)==0;
    public long PhysicalEvents {get;private set;}
    public long IgnoredInjected {get;private set;}

    public LocalConsoleBridge(IEnumerable<long> windows,Action<string> stopInput)
    {
        this.stopInput=stopInput;
        Refresh(windows);
        if(!WindowsInputEnvironment.InteractiveDesktop() || !TargetAllowed())throw new InvalidOperationException("LOCAL_TARGET_NOT_FOREGROUND");
        for(int key=1;key<255;key++)
        {
            uint scan=MapVirtualKey((uint)key,4);
            bool supported=key<=6 || key is 0x5b or 0x5c || PhysicalKeyMap.FromScanCode(scan&0xff,(scan&0xff00)==0xe000) is not null;
            if(supported && (GetAsyncKeyState(key)&0x8000)!=0)
                throw new InvalidOperationException($"LOCAL_RELEASE_PHYSICAL_KEYS_FIRST (VK {key:X2})");
        }
        thread=new Thread(Run){IsBackground=true,Name="explicit-local-console-devices"};thread.Start();
        try{ready.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();}
        catch{Dispose();throw;}
    }
    public void Refresh(IEnumerable<long> windows)
    {
        var next=windows.Select(w=>(nint)w).ToArray();
        if(next.Length is <1 or >8 || next.Any(w=>w==0))throw new ArgumentException("Invalid local target set.");
        Volatile.Write(ref targets,next);
        Volatile.Write(ref deadline,Stopwatch.GetTimestamp()+Stopwatch.Frequency/2);
    }
    private bool TargetAllowed()=>Volatile.Read(ref targets).Contains(GetForegroundWindow());
    private bool Safe()
    {
        if(!Active)return false;
        if(Stopwatch.GetTimestamp()>Volatile.Read(ref deadline) || !TargetAllowed())
        {Stop("LOCAL_TARGET_OR_HEARTBEAT_LOST");return false;}
        return true;
    }
    public LocalDeviceEvent[] Drain()
    {
        lock(gate){if(!Active){queue.Clear();return [];}var result=queue.ToArray();queue.Clear();return result;}
    }
    private void Enqueue(LocalDeviceEvent value)
    {
        // Hooks cannot block on network/UI work. Only adjacent moves may be coalesced.
        if(!Monitor.TryEnter(gate)){Stop("LOCAL_DEVICE_QUEUE_BUSY");return;}
        try
        {
            if(!Active)return;
            PhysicalEvents++;
            if(value.Kind=="Move" && queue.LastOrDefault() is {Kind:"Move"} last)
                queue[^1]=last with {Dx=last.Dx+value.Dx,Dy=last.Dy+value.Dy};
            else if(queue.Count>=128)Stop("LOCAL_DEVICE_QUEUE_OVERFLOW");
            else queue.Add(value);
        }
        finally{Monitor.Exit(gate);}
    }
    public void Stop(string code="LOCAL_CONSOLE_STOPPED")
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        Volatile.Write(ref reason,code);
        stopInput(code); // Nonblocking executor invalidation only.
        if(threadId!=0)PostThreadMessage(threadId,0x12,0,0);
    }
    private nint Mouse(int code,nuint message,nint data)
    {
        if(code<0)return CallNextHookEx(0,code,message,data);
        try
        {
            var item=Marshal.PtrToStructure<MouseData>(data);
            if((item.Flags&3)!=0){IgnoredInjected++;return CallNextHookEx(0,code,message,data);}
            if(!Safe())return CallNextHookEx(0,code,message,data);
            LocalDeviceEvent? value=(uint)message switch
            {
                0x201=>new("Button",Button:"Left"),0x202=>new("Button",Button:"Left",Up:true),
                0x204=>new("Button",Button:"Right"),0x205=>new("Button",Button:"Right",Up:true),
                0x207=>new("Button",Button:"Middle"),0x208=>new("Button",Button:"Middle",Up:true),
                0x20A=>new("Wheel",WheelY:unchecked((short)(item.Data>>16))),
                0x20E=>new("Wheel",WheelX:unchecked((short)(item.Data>>16))),_=>null
            };
            if((uint)message==0x200)
            {
                if(!GetCursorPos(out var current)){Stop("LOCAL_CURSOR_UNAVAILABLE");return 1;}
                value=new("Move",item.Point.X-current.X,item.Point.Y-current.Y);
            }
            if(value is null){Stop("LOCAL_UNSUPPORTED_MOUSE");return 1;}
            Enqueue(value);return 1;
        }
        catch{Stop("LOCAL_HOOK_FAILED");return CallNextHookEx(0,code,message,data);}
    }
    private nint Keyboard(int code,nuint message,nint data)
    {
        if(code<0)return CallNextHookEx(0,code,message,data);
        try
        {
            var item=Marshal.PtrToStructure<KeyData>(data);
            if((item.Flags&0x12)!=0){IgnoredInjected++;return CallNextHookEx(0,code,message,data);}
            if(!Safe())return CallNextHookEx(0,code,message,data);
            if(item.VirtualKey==0x7b){Stop("LOCAL_F12_STOPPED");return 1;}
            string? mapped=PhysicalKeyMap.FromScanCode(item.Scan,(item.Flags&1)!=0);
            if(mapped is null){Stop("LOCAL_UNSUPPORTED_KEY");return 1;}
            Enqueue(new("Key",Code:mapped,Up:((uint)message is 0x101 or 0x105)));return 1;
        }
        catch{Stop("LOCAL_HOOK_FAILED");return CallNextHookEx(0,code,message,data);}
    }
    private void Run()
    {
        nint mouse=0,keyboard=0;Hook mouseCallback=Mouse,keyCallback=Keyboard;
        try
        {
            WindowCatalog.SetDpiAwareness();
            threadId=GetCurrentThreadId();PeekMessage(out _,0,0,0,0);
            mouse=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
            keyboard=SetWindowsHookEx(13,keyCallback,GetModuleHandle(null),0);
            if(mouse==0||keyboard==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            using var watchdog=new Timer(_=>{if(Active)Safe();},null,100,100);
            ready.TrySetResult();
            while(Active && GetMessage(out var message,0,0,0)>0){TranslateMessage(in message);DispatchMessage(in message);}
        }
        catch(Exception e){ready.TrySetException(e);Stop("LOCAL_HOOK_UNAVAILABLE");}
        finally
        {
            if(mouse!=0 && !UnhookWindowsHookEx(mouse)){uncertainCallbacks.Add(mouseCallback);Volatile.Write(ref detachFailed,1);}
            if(keyboard!=0 && !UnhookWindowsHookEx(keyboard)){uncertainCallbacks.Add(keyCallback);Volatile.Write(ref detachFailed,1);}
            GC.KeepAlive(mouseCallback);GC.KeepAlive(keyCallback);
            Stop("LOCAL_HOOK_ENDED");
        }
    }
    public void Dispose()
    {
        Stop();
        if(Thread.CurrentThread!=thread && !thread.Join(TimeSpan.FromSeconds(2)))throw new TimeoutException("Local console thread did not stop.");
        lock(gate)queue.Clear();
        if(Volatile.Read(ref detachFailed)!=0)throw new InvalidOperationException("Local hook removal unconfirmed; stop the diagnostic process.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate nint Hook(int code,nuint message,nint data);
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseData{public Point Point;public uint Data,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct KeyData{public uint VirtualKey,Scan,Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public Point Point;public uint Private;}
    [DllImport("user32.dll",SetLastError=true,EntryPoint="SetWindowsHookExW")]private static extern nint SetWindowsHookEx(int kind,Hook callback,nint module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]private static extern nint CallNextHookEx(nint hook,int code,nuint message,nint data);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",EntryPoint="MapVirtualKeyW")]private static extern uint MapVirtualKey(uint code,uint mode);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint remove);
    [DllImport("user32.dll")]private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")]private static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,uint message,nuint wparam,nint lparam);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern nint GetModuleHandle(string? name);
}
