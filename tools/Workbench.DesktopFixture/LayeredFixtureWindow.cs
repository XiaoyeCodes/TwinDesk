using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Workbench.DesktopFixture;

// Own test window only. No target application input, desktop capture or global hooks.
internal sealed class LayeredFixtureWindow : NativeWindow, IWin32Window, IDisposable
{
    public const int PixelWidth = 300, PixelHeight = 150;
    public LayeredFixtureWindow(nint owner, Point position)
    {
        try
        {
            CreateHandle(new CreateParams { Caption = "TwinDesk alpha fixture", Parent = owner,
                Style = unchecked((int)0x80000000), ExStyle = 0x80000 | 0x80,
                X = position.X, Y = position.Y, Width = PixelWidth, Height = PixelHeight });
            PaintPixels(position);
            ShowWindow(Handle, 4); // Show this fixture without activating another application.
        }
        catch { Dispose(); throw; }
    }

    private void PaintPixels(Point position)
    {
        nint dc = CreateCompatibleDC(0), bitmap = 0, old = 0;
        if (dc == 0) throw new Win32Exception();
        try
        {
            var info = new BitmapInfo { Size = 40, Width = PixelWidth, Height = -PixelHeight, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new Win32Exception();
            old = SelectObject(dc, bitmap);
            byte[] bytes = new byte[PixelWidth*PixelHeight*4];
            for (int y = 0; y < PixelHeight; y++) for (int x = 0; x < PixelWidth; x++)
            {
                byte alpha = x < 100 ? (byte)0 : x < 200 ? (byte)128 : (byte)255;
                int offset = (y*PixelWidth+x)*4;
                bytes[offset+2] = alpha; bytes[offset+3] = alpha; // Premultiplied red.
            }
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            var source = new Point(); var size = new Size(PixelWidth, PixelHeight);
            var blend = new BlendFunction { ConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, 0, ref position, ref size, dc, ref source, 0, ref blend, 2)) throw new Win32Exception();
        }
        finally
        {
            if (old != 0) SelectObject(dc, old);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }
    public void Dispose() { if (Handle != 0) DestroyHandle(); }
    [StructLayout(LayoutKind.Sequential)] private struct BlendFunction { public byte Operation, Flags, ConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant;
        public uint Color;
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool UpdateLayeredWindow(nint hwnd,nint destinationDc,
        ref Point destination,ref Size size,nint sourceDc,ref Point source,uint color,ref BlendFunction blend,uint flags);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError=true)] private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
