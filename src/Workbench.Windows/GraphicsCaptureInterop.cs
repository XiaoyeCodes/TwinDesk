using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Workbench.Windows;

internal static class GraphicsCaptureInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, in Guid iid, out nint result);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string text, uint length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint inspectable);

    public static GraphicsCaptureItem ForWindow(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var name));
        nint factoryPtr = 0;
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, iid, out factoryPtr));
            var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Marshal.ThrowExceptionForHR(factory.CreateForWindow(hwnd, itemIid, out var itemPtr));
                try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        finally
        {
            if (factoryPtr != 0) Marshal.Release(factoryPtr);
            WindowsDeleteString(name);
        }
    }

    public static IDirect3DDevice FromDxgiDevice(nint dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var pointer));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    public static unsafe Vortice.Direct3D11.ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0;
        try
        {
            var accessId = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, in accessId, out access));
            var textureId = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            nint texture = 0;
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            return new(texture);
        }
        finally
        {
            if (access != 0) Marshal.Release(access);
            Marshal.Release(surfacePointer);
        }
    }
}
