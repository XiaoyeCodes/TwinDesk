using System.Runtime.InteropServices;

namespace Workbench.Windows;

public sealed record DisplayInfo(string DeviceName, WindowBounds Bounds, WindowBounds WorkArea, bool Primary,
    uint? EffectiveDpiX, uint? EffectiveDpiY);

public static class DisplayCatalog
{
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var displays = new List<DisplayInfo>();
        Exception? failure = null;
        bool success = EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref NativeMethods.Rect rect, nint state) =>
        {
            try
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), DeviceName = string.Empty };
                if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var dpiResult = GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY);
                displays.Add(new(info.DeviceName, Bounds(info.Monitor), Bounds(info.Work), (info.Flags & 1) != 0,
                    dpiResult >= 0 ? dpiX : null, dpiResult >= 0 ? dpiY : null));
                return true;
            }
            catch (Exception exception) { failure = exception; return false; }
        }, 0);
        if (failure is not null) throw new InvalidOperationException("Display enumeration failed.", failure);
        if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return displays;
    }

    private static WindowBounds Bounds(NativeMethods.Rect r) => new(r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top);
    private delegate bool MonitorCallback(nint monitor, nint hdc, ref NativeMethods.Rect rect, nint state);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeMethods.Rect Monitor;
        public NativeMethods.Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string DeviceName;
    }
    [DllImport("user32.dll", SetLastError=true)] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint state);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
