using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Workbench.Windows;

/// <summary>
/// Read-only checks. Never activates a window or bypasses UIPI. A live binding verifier from the owning
/// window/capture registry is mandatory; PID + HWND alone cannot prove absence of same-process HWND reuse.
/// </summary>
public sealed class WindowsInputEnvironment(WindowInfo root,Func<WindowInfo,bool> verifyLiveBinding) : INativeInputEnvironment
{
    public NativeInputCheck Check(OwnedWindowScene scene,ScreenPoint? point)
    {
        var desktop=new WindowBounds(GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79));
        NativeInputCheck Deny(string code)=>new(false,code,desktop);
        try
        {
            if(IntPtr.Size!=8)return Deny("X64_REQUIRED");
            if(!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),-4))return Deny("DPI_CONTEXT_UNVERIFIED");
            using var current=Process.GetCurrentProcess();
            using var target=Process.GetProcessById(root.ProcessId);
            if(target.StartTime.ToUniversalTime()!=root.ProcessStartedAtUtc || target.SessionId!=root.SessionId
                || target.SessionId!=current.SessionId || string.IsNullOrEmpty(root.ExecutablePath)
                || !string.Equals(target.MainModule?.FileName,root.ExecutablePath,StringComparison.OrdinalIgnoreCase))return Deny("TARGET_IDENTITY_CHANGED");
            if(!InteractiveDesktop())return Deny("DESKTOP_UNAVAILABLE");
            if(Integrity(target.Handle)>Integrity(current.Handle))return Deny("INPUT_PERMISSION_MISMATCH");
            var live=OwnedWindowScene.Select(root,WindowCatalog.Find(root.ProcessName));
            if(live.Count!=scene.Nodes.Count)return Deny("SCENE_CHANGED");
            var matched=new List<WindowInfo>();
            foreach(var window in live)
            {
                var expected=scene.Nodes.SingleOrDefault(node=>OwnedWindowScene.SameIdentity(node.Window,window))?.Window;
                if(expected is null || !verifyLiveBinding(expected))return Deny("WINDOW_BINDING_UNVERIFIED");
                // Generation comes from the independently verified local registry, never from this native lookup or a browser.
                matched.Add(window with {BindingGeneration=expected.BindingGeneration});
            }
            var fresh=OwnedWindowScene.Arrange(matched);
            if(!scene.SameGeometry(fresh))return Deny("SCENE_CHANGED");
            nint foreground=GetForegroundWindow();
            if(!Allowed(fresh,foreground))return Deny("FOCUS_DENIED");
            var gui=new GuiThreadInfo {Size=(uint)Marshal.SizeOf<GuiThreadInfo>()};
            uint thread=NativeMethods.GetWindowThreadProcessId(foreground,out _);
            if(thread==0 || !GetGUIThreadInfo(thread,ref gui))return Deny("FOCUS_UNVERIFIED");
            if(gui.Capture!=0 && !Allowed(fresh,gui.Capture))return Deny("CAPTURE_TARGET_DENIED");
            if(point is { } p)
            {
                if(!SceneInputCoordinates.Contains(desktop,p))return Deny("POINT_OUTSIDE_DESKTOP");
                nint childHit=WindowFromPhysicalPoint(new Point {X=p.X,Y=p.Y});
                if(!Allowed(fresh,childHit))return Deny("POINTER_TARGET_DENIED");
                nint hit=NativeMethods.GetAncestor(childHit,2);
                var node=fresh.Nodes.SingleOrDefault(n=>n.Window.Handle==(long)hit);
                if(node is null || !SceneInputCoordinates.AllowsNativeHit(scene,p,node.Window))return Deny("POINTER_TARGET_DENIED");
            }
            else if(gui.Focus==0 || !Allowed(fresh,gui.Focus))return Deny("KEYBOARD_TARGET_DENIED");
            if(foreground!=GetForegroundWindow() || !InteractiveDesktop())return Deny("FOCUS_OR_DESKTOP_CHANGED");
            return new(true,"NATIVE_TARGET_READY",desktop);
        }
        catch(Exception e) when(e is Win32Exception or ArgumentException or InvalidOperationException or InvalidDataException or OverflowException)
        { return Deny("NATIVE_ENVIRONMENT_UNAVAILABLE"); }
    }
    private static bool Allowed(OwnedWindowScene scene,nint hwnd)
    {
        if(hwnd==0 || !NativeMethods.IsWindowVisible(hwnd) || !NativeMethods.IsWindowEnabled(hwnd))return false;
        NativeMethods.GetWindowThreadProcessId(hwnd,out uint pid);
        if(pid!=scene.Nodes[0].Window.ProcessId)return false;
        nint top=NativeMethods.GetAncestor(hwnd,2);
        return top!=0 && scene.Nodes.Any(n=>n.Window.Handle==(long)top && n.Window.Enabled);
    }
    public static bool InteractiveDesktop()
    {
        nint desktop=OpenInputDesktop(0,false,1);
        if(desktop==0)return false;
        try
        {
            var name=new StringBuilder(256);
            if(!GetUserObjectInformationW(desktop,2,name,512,out _))return false;
            var threadName=new StringBuilder(256);
            return name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase)
                && GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()),2,threadName,512,out _)
                && name.ToString().Equals(threadName.ToString(),StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    private static int Integrity(nint process)
    {
        if(!OpenProcessToken(process,8,out var token))throw new Win32Exception();
        try
        {
            _=GetTokenInformation(token,25,0,0,out uint length);
            if(length is <16 or >4096)throw new Win32Exception();
            nint buffer=Marshal.AllocHGlobal((int)length);
            try
            {
                if(!GetTokenInformation(token,25,buffer,length,out _))throw new Win32Exception();
                nint sid=Marshal.ReadIntPtr(buffer);if(!IsValidSid(sid))throw new Win32Exception();
                byte count=Marshal.ReadByte(GetSidSubAuthorityCount(sid));if(count==0)throw new Win32Exception();
                return Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)count-1));
            }
            finally {Marshal.FreeHGlobal(buffer);}
        }
        finally {CloseHandle(token);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {public uint Size,Flags;public nint Active,Focus,Capture,MenuOwner,MoveSize,Caret;public NativeMethods.Rect CaretRect;}
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint first,nint second);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformationW(nint handle,int index,StringBuilder value,uint length,out uint needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(nint token,int kind,nint data,uint length,out uint needed);
    [DllImport("advapi32.dll")] private static extern bool IsValidSid(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid,uint index);
}
