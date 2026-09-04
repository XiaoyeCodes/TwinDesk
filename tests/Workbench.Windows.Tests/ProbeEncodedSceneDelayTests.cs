using Workbench.Windows;
using Xunit;
namespace Workbench.Windows.Tests;
public class ProbeEncodedSceneDelayTests
{
    private static EncodedAccessUnit F(long time,uint version)=>new(time,time==0,[1,2,3],[5],"avc1.42401F")
        {Scene=new(version,1280,720,new(0,0,1280,720),1)};
    [Fact]public void OldOutputKeepsItsSceneAfterNewSceneHasAlreadyReachedTheEncoderOutput()
    {
        var frames=new List<EncodedAccessUnit>();using var delay=new ProbeEncodedSceneDelay(frames.Add);
        var original=F(0,1);delay.Push(original);original.Data[0]=9;
        delay.Push(F(10,1));Assert.Empty(frames);delay.Push(F(20,2));delay.Push(F(30,2));
        Assert.Equal([1u,1u,2u,2u],frames.Select(f=>f.Scene!.Version));Assert.Equal([0L,10L,20L,30L],frames.Select(f=>f.TimestampUs));
        Assert.Equal(1,frames[0].Data[0]);Assert.Equal(3,delay.PeakFrames);Assert.Equal(0,delay.Count);
        Assert.Equal("new-captured-scene",delay.ReleaseReason);Assert.Equal(2u,delay.ReleaseVersion);
    }
    [Fact]public void CancelClearsQueueWithoutSendingAnythingIntoTheNextRun()
    {
        var frames=new List<EncodedAccessUnit>();var delay=new ProbeEncodedSceneDelay(frames.Add);delay.Push(F(0,1));delay.Dispose();
        Assert.Empty(frames);Assert.Equal(0,delay.Count);Assert.Equal(0,delay.BufferedBytes);
        Assert.Throws<ObjectDisposedException>(()=>delay.Push(F(10,2)));
    }
    [Fact]public void DeadlineAndEndDoNotFalselyClaimSceneTransition()
    {
        using var delay=new ProbeEncodedSceneDelay(_=>{});delay.Push(F(0,1));delay.Push(F(2_000_000,1));
        Assert.Equal("two-second-sample-deadline",delay.ReleaseReason);
        using var staticDelay=new ProbeEncodedSceneDelay(_=>{});staticDelay.Push(F(0,1));staticDelay.Complete();
        Assert.Equal("stream-ended-without-scene-transition",staticDelay.ReleaseReason);
    }
    [Fact]public void FrameAndByteBudgetsFailWithoutDroppingDependentFrames()
    {
        using var delay=new ProbeEncodedSceneDelay(_=>{});for(int i=0;i<16;i++)delay.Push(F(i,1));
        Assert.Throws<InvalidDataException>(()=>delay.Push(F(16,1)));Assert.Equal(16,delay.Count);
        using var bytes=new ProbeEncodedSceneDelay(_=>{});bytes.Push(F(0,1) with {Data=new byte[8*1024*1024]});
        Assert.Throws<InvalidDataException>(()=>bytes.Push(F(10,2)));Assert.Equal(1,bytes.Count);
    }
    [Fact]public void MissingSceneAndRegressingMetadataFail()
    {
        using var delay=new ProbeEncodedSceneDelay(_=>{});Assert.Throws<InvalidDataException>(()=>delay.Push(F(0,1) with {Scene=null}));
        delay.Push(F(0,2));Assert.Throws<InvalidDataException>(()=>delay.Push(F(1,1)));
        Assert.Throws<InvalidDataException>(()=>delay.Push(F(0,2)));
    }
}
