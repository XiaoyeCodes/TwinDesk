using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class GpuSceneCompositorTests
{
    [Fact]
    public void ActualGpuPremultipliedAlphaAndPlacementMatchPixels()
    {
        D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
        using(device) using(context) using(var compositor=new GpuSceneCompositor(device,context))
        {
            // Synthetic BGRA only: transparent / half-premultiplied-red / opaque-red.
            byte[] bytes=[0,0,0,0,0,0,128,128,0,0,255,255];
            var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            try
            {
                var sourceDescription=Description(3,1,BindFlags.ShaderResource);
                using var source=device.CreateTexture2D(sourceDescription,new SubresourceData(pin.AddrOfPinnedObject(),12,12));
                using var sourceView=device.CreateShaderResourceView(source);
                using var output=device.CreateTexture2D(Description(5,3,BindFlags.RenderTarget));
                using var outputView=device.CreateRenderTargetView(output);
                compositor.Begin(outputView,5,3,new Color4(0,0,1,1));
                compositor.Draw(sourceView,new(1,1,3,1));
                Assert.Throws<InvalidDataException>(()=>compositor.Draw(sourceView,new(-1,0,1,1)));
                Assert.Throws<InvalidDataException>(()=>compositor.Draw(sourceView,new(4,0,2,1)));
                compositor.End();
                var stagingDescription=Description(5,3,BindFlags.None);
                stagingDescription.Usage=ResourceUsage.Staging;stagingDescription.CPUAccessFlags=CpuAccessFlags.Read;
                using var staging=device.CreateTexture2D(stagingDescription);
                context.CopyResource(staging,output);
                var mapped=context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                try
                {
                    // CPU readback occurs only in this synthetic verification, never the live media path.
                    for(int y=0;y<3;y++)for(int x=0;x<5;x++)
                    {
                        int red=y==1 && x==2?128:y==1 && x==3?255:0;
                        int blue=255-red;
                        nint pointer=mapped.DataPointer+checked((int)mapped.RowPitch*y+x*4);
                        Assert.InRange((int)Marshal.ReadByte(pointer),Math.Max(0,blue-1),Math.Min(255,blue+1));
                        Assert.Equal(0,Marshal.ReadByte(pointer,1));
                        Assert.InRange((int)Marshal.ReadByte(pointer,2),Math.Max(0,red-1),Math.Min(255,red+1));
                        Assert.Equal(255,Marshal.ReadByte(pointer,3));
                    }
                }
                finally { context.Unmap(staging,0); }
            }
            finally { pin.Free(); }
        }
    }
    private static Texture2DDescription Description(int w,int h,BindFlags bind) => new()
    { Width=(uint)w,Height=(uint)h,MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=bind };
}
