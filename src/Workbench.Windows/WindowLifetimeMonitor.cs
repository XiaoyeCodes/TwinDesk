using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Workbench.Windows;

// One out-of-context destroy hook per captured process, not one WinRT event source per window.
// A dedicated message loop receives callbacks even while the capture thread is stalled.
internal sealed class WindowLifetimeMonitor : IDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<nint,Registration> registrations=[];
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly uint processId;
    private uint threadId;
    private int stopped,failed;
    private long received;
    public long ReceivedDestroyEvents=>Interlocked.Read(ref received);

    public WindowLifetimeMonitor(int processId)
    {
        if(processId<=0)throw new ArgumentOutOfRangeException(nameof(processId));
        this.processId=(uint)processId;
        thread=new Thread(Run){IsBackground=true,Name="capture-window-lifetime"};
        thread.Start();
        try {ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();}
        catch {Dispose();throw;}
    }
    public Registration Register(nint window,Action retired)
    {
        lock(gate)
        {
            ThrowIfFailed();ObjectDisposedException.ThrowIf(stopped!=0,this);
            if(window==0 || registrations.Count>=OwnedWindowScene.MaximumNodes || registrations.ContainsKey(window))
                throw new InvalidOperationException("Invalid monitored window registration.");
            var result=new Registration(this,window,retired);registrations.Add(window,result);return result;
        }
    }
    public void ThrowIfFailed()
    {
        if(Volatile.Read(ref failed)!=0)throw new InvalidOperationException("Window lifetime notification loop failed; capture must stop.");
    }
    private void Run()
    {
        nint hook=0;WinEvent callback=OnEvent;var root=GCHandle.Alloc(callback);
        try
        {
            threadId=GetCurrentThreadId();PeekMessage(out _,0,0,0,0); // Establish queue before advertising readiness.
            hook=SetWinEventHook(0x8001,0x8001,0,callback,processId,0,0); // EVENT_OBJECT_DESTROY, OUTOFCONTEXT.
            if(hook==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            ready.TrySetResult();
            while(Volatile.Read(ref stopped)==0)
            {
                int result=GetMessage(out var message,0,0,0);
                if(result<=0)
                {
                    if(Volatile.Read(ref stopped)==0)throw new Win32Exception("Lifetime message loop ended unexpectedly.");
                    break;
                }
                TranslateMessage(in message);DispatchMessage(in message);
            }
        }
        catch(Exception e){Volatile.Write(ref failed,1);ready.TrySetException(e);}
        finally
        {
            if(hook!=0 && !UnhookWinEvent(hook))Volatile.Write(ref failed,1);
            root.Free();RetireAll();
        }
    }
    private void OnEvent(nint hook,uint kind,nint window,int objectId,int childId,uint eventThread,uint time)
    {
        try
        {
            if(kind!=0x8001 || window==0 || objectId!=0 || childId!=0)return;
            Registration? registration;
            lock(gate)registrations.Remove(window,out registration);
            if(registration is not null){Interlocked.Increment(ref received);registration.Retire();}
        }
        catch {Volatile.Write(ref failed,1);RetireAll();} // No managed exception crosses the callback ABI.
    }
    private void RetireAll()
    {
        Registration[] remaining;
        lock(gate){remaining=registrations.Values.ToArray();registrations.Clear();}
        foreach(var item in remaining)item.Retire();
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        RetireAll();
        if(threadId!=0)PostThreadMessage(threadId,0x0012,0,0);
        if(Thread.CurrentThread!=thread && !thread.Join(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("Window lifetime thread did not stop.");
    }
    internal sealed class Registration(WindowLifetimeMonitor owner,nint window,Action retired) : IDisposable
    {
        private int alive=1;
        public bool Alive=>Volatile.Read(ref alive)!=0;
        internal void Retire()
        {
            if(Interlocked.Exchange(ref alive,0)==0)return;
            try {retired();}catch {Volatile.Write(ref owner.failed,1);}
        }
        public void Dispose()
        {
            lock(owner.gate)
                if(owner.registrations.TryGetValue(window,out var current) && ReferenceEquals(current,this))owner.registrations.Remove(window);
            Retire();
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEvent(nint hook,uint kind,nint window,int objectId,int childId,uint thread,uint time);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message {public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public int X,Y;public uint Private;}
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetWinEventHook(uint min,uint max,nint module,WinEvent callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint remove);
    [DllImport("user32.dll",SetLastError=true)]private static extern int GetMessage(out Message message,nint window,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")]private static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool PostThreadMessage(uint thread,uint message,nuint wparam,nint lparam);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
}
