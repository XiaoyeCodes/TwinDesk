using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Workbench.Windows;

public sealed record WindowBounds(int X, int Y, int Width, int Height);
public sealed record WindowInfo(long Handle, int ProcessId, string ProcessName, string? ExecutablePath,
    string Title, string ClassName, WindowBounds Bounds, uint Dpi, bool Minimized, long Owner,
    DateTime ProcessStartedAtUtc, long Parent, int ControlId, WindowBounds ClientBounds, bool Visible)
{
    public WindowBounds CaptureBounds { get; init; } = Bounds;
    public bool Enabled { get; init; } = true;
    public bool Cloaked { get; init; }
    public bool Layered { get; init; }
    public int ZOrder { get; init; }
    public int SessionId { get; init; }
    public long BindingGeneration { get; init; }
}

public static class WindowCatalog
{
    public static IReadOnlyList<WindowInfo> Find(string processName)
    {
        var result = new List<WindowInfo>();
        var normalized = Path.GetFileNameWithoutExtension(processName);
        int zOrder = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            int rank = zOrder++;
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return true;
                result.Add(Describe(hwnd, process) with { ZOrder = rank });
            }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return true;
        }, 0);
        return result;
    }

    public static IReadOnlyList<WindowInfo> Children(nint parent, bool includeHidden = false)
    {
        var result = new List<WindowInfo>();
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            if (!includeHidden && !NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try { using var p = Process.GetProcessById((int)pid); result.Add(Describe(hwnd, p)); }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return true;
        }, 0);
        return result;
    }

    private static WindowInfo Describe(nint hwnd, Process process)
    {
        var title = new StringBuilder(2048);
        var className = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        NativeMethods.GetClientRect(hwnd, out var client);
        var origin = new NativeMethods.Point();
        NativeMethods.ClientToScreen(hwnd, ref origin);
        string? path;
        try { path = process.MainModule?.FileName; } catch { path = null; }
        var bounds = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var captureBounds = bounds;
        if (NativeMethods.DwmGetWindowAttribute(hwnd, 9, out NativeMethods.Rect visibleRect, 16) == 0
            && visibleRect.Right > visibleRect.Left && visibleRect.Bottom > visibleRect.Top)
            captureBounds = new(visibleRect.Left, visibleRect.Top, visibleRect.Right-visibleRect.Left, visibleRect.Bottom-visibleRect.Top);
        _ = NativeMethods.DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4);
        return new(hwnd, process.Id, process.ProcessName, path, title.ToString(), className.ToString(),
            new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            NativeMethods.GetDpiForWindow(hwnd), NativeMethods.IsIconic(hwnd), NativeMethods.GetWindow(hwnd, 4),
            process.StartTime.ToUniversalTime(), NativeMethods.GetAncestor(hwnd, 1), NativeMethods.GetDlgCtrlID(hwnd),
            new(origin.X, origin.Y, client.Right-client.Left, client.Bottom-client.Top), NativeMethods.IsWindowVisible(hwnd))
        {
            CaptureBounds = captureBounds, Enabled = NativeMethods.IsWindowEnabled(hwnd), Cloaked = cloaked != 0,
            Layered = (NativeMethods.GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x80000) != 0, SessionId = process.SessionId
        };
    }

    public static void SetDpiAwareness() => NativeMethods.SetProcessDpiAwarenessContext(-4);
}

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint state);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint state);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
}
