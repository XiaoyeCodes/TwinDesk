using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Workbench.Windows;

// Reversible, bounded visual-overlay feasibility test. It never injects input.
// The browser remains opaque and topmost but mouse hit-testing passes through.
// Run only with a disposable Chrome/Edge app window, never the Codex window.
WindowCatalog.SetDpiAwareness();
if(args.Length!=8||args[0]!="--browser"||args[2]!="--left"||args[4]!="--right"||args[6]!="--seconds"||
   !long.TryParse(args[1],out long browserHandle)||!long.TryParse(args[3],out long leftHandle)||
   !long.TryParse(args[5],out long rightHandle)||!int.TryParse(args[7],out int seconds)||seconds is <1 or >120)
    throw new ArgumentException("Use --browser <dedicated Chrome/Edge app HWND> --left <NX HWND> --right <NX HWND> --seconds 1..120.");

var roots=WindowCatalog.Find("ugraf");
var left=roots.SingleOrDefault(w=>w.Handle==leftHandle);
var right=roots.SingleOrDefault(w=>w.Handle==rightHandle);
if(left is null||right is null||left.ProcessId==right.ProcessId||left.Minimized||right.Minimized||
   !left.Visible||!right.Visible||left.SessionId!=right.SessionId)
    throw new InvalidOperationException("Two visible, distinct NX instances in one session are required.");
if(!WindowsInputEnvironment.InteractiveDesktop())throw new InvalidOperationException("Interactive desktop unavailable.");

nint browser=(nint)browserHandle;
if(!IsWindow(browser)||GetAncestor(browser,2)!=browser||GetWindowThreadProcessId(browser,out uint browserPid)==0)
    throw new InvalidOperationException("Invalid dedicated browser root window.");
using var process=Process.GetProcessById(checked((int)browserPid));
if(process.ProcessName is not ("chrome" or "msedge")||process.SessionId!=left.SessionId)
    throw new InvalidOperationException("The overlay must be a dedicated Chrome/Edge window in the NX session.");
var title=new StringBuilder(256);
GetWindowTextW(browser,title,title.Capacity);
if(!title.ToString().Contains("双 NX 真实画面试验",StringComparison.Ordinal))
    throw new InvalidOperationException("Browser window title does not match this local trial.");

nint original=GetWindowLongPtrW(browser,-20);
bool wasTopmost=((long)original&8)!=0;
nint overlay=(nint)((long)original|0x80000|0x20);
Console.WriteLine($"PREPARED browser={browserHandle} pid={browserPid} left={left.ProcessId}/{leftHandle} right={right.ProcessId}/{rightHandle} seconds={seconds} originalExStyle=0x{(long)original:X}");
try
{
    SetLastError(0);
    nint previous=SetWindowLongPtrW(browser,-20,overlay);
    if(previous==0&&Marshal.GetLastWin32Error()!=0)throw new Win32Exception(Marshal.GetLastWin32Error());
    if(!SetLayeredWindowAttributes(browser,0,255,2))throw new Win32Exception(Marshal.GetLastWin32Error());
    if(!SetWindowPos(browser,-1,0,0,0,0,0x13|0x20))throw new Win32Exception(Marshal.GetLastWin32Error());
    Console.WriteLine("ACTIVE: browser stays visible; mouse hit tests pass through it. F12 exits immediately.");
    var until=Stopwatch.StartNew();
    while(until.Elapsed<TimeSpan.FromSeconds(seconds))
    {
        if(!IsWindow(browser)||!IsWindow((nint)leftHandle)||!IsWindow((nint)rightHandle))
        {Console.WriteLine("A bound window closed; stopping.");break;}
        if((GetAsyncKeyState(0x7B)&0x8000)!=0){Console.WriteLine("F12 exit.");break;}
        Thread.Sleep(40);
    }
}
finally
{
    if(IsWindow(browser))
    {
        SetWindowLongPtrW(browser,-20,original);
        SetWindowPos(browser,wasTopmost?-1:-2,0,0,0,0,0x13|0x20);
        Console.WriteLine("RESTORED original browser hit testing and topmost state.");
    }
}

[DllImport("user32.dll")]static extern bool IsWindow(nint hwnd);
[DllImport("user32.dll")]static extern nint GetAncestor(nint hwnd,uint flags);
[DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
[DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowTextW(nint hwnd,StringBuilder text,int max);
[DllImport("user32.dll",SetLastError=true)]static extern nint GetWindowLongPtrW(nint hwnd,int index);
[DllImport("user32.dll",SetLastError=true)]static extern nint SetWindowLongPtrW(nint hwnd,int index,nint value);
[DllImport("user32.dll",SetLastError=true)]static extern bool SetLayeredWindowAttributes(nint hwnd,uint key,byte alpha,uint flags);
[DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int cx,int cy,uint flags);
[DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
[DllImport("kernel32.dll")]static extern void SetLastError(uint code);
