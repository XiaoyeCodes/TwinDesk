using Workbench.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace Workbench.Windows.Tests;

public class JpegFrameEncoderTests
{
    private static BgraProbeFrame Frame(int width=128,int height=128)
    {
        byte[] pixels=new byte[width*height*4];
        for(int i=0;i<pixels.Length;i+=4){pixels[i]=20;pixels[i+1]=80;pixels[i+2]=210;pixels[i+3]=255;}
        return new(width,height,pixels,1,new(1,width,height,new(0,0,width,height),1));
    }
    [Theory] [InlineData(128,128)] [InlineData(1920,1080)]
    public async Task RealWindowsCodecRoundTripPreservesDimensionsAndApproximateColor(int width,int height)
    {
        var bytes=await JpegFrameEncoder.Encode(Frame(width,height),0.85f,CancellationToken.None);
        Assert.Equal(0xff,bytes[0]);Assert.Equal(0xd8,bytes[1]);Assert.Equal(0xff,bytes[^2]);Assert.Equal(0xd9,bytes[^1]);
        using var stream=new InMemoryRandomAccessStream();
        using(var writer=new DataWriter(stream)){writer.WriteBytes(bytes);await writer.StoreAsync();writer.DetachStream();}
        stream.Seek(0);var decoder=await BitmapDecoder.CreateAsync(stream);
        Assert.Equal((uint)width,decoder.PixelWidth);Assert.Equal((uint)height,decoder.PixelHeight);
        var data=await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,new BitmapTransform(),ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage);
        var pixels=data.DetachPixelData();Assert.InRange((int)pixels[0],15,25);Assert.InRange((int)pixels[1],75,85);Assert.InRange((int)pixels[2],205,215);
    }
    [Theory] [InlineData(float.NaN)] [InlineData(0f)] [InlineData(1.1f)]
    public async Task InvalidQualityRejected(float quality)=>await Assert.ThrowsAsync<ArgumentException>(()=>JpegFrameEncoder.Encode(Frame(),quality,CancellationToken.None));
    [Fact] public async Task InvalidLayoutRejectedBeforeNativeEncoder()
    {
        await Assert.ThrowsAsync<ArgumentException>(()=>JpegFrameEncoder.Encode(Frame() with {Pixels=[]},0.8f,CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(()=>JpegFrameEncoder.Encode(Frame() with {Width=4096},0.8f,CancellationToken.None));
    }
    [Fact] public async Task CancelledWorkDoesNotEncode()=>await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>JpegFrameEncoder.Encode(Frame(),0.8f,new CancellationToken(true)));
}
