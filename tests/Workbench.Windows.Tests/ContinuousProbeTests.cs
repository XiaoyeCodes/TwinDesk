using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class ContinuousProbeTests
{
    [Fact] public void ContinuousH264CannotStartGeneratedOrUncancellableSource()
    {
        using var cancel=new CancellationTokenSource();
        Assert.Throws<ArgumentException>(()=>H264Probe.Run(30,true,_=>{},cancel.Token,continuous:true));
        Assert.Throws<ArgumentException>(()=>H264Probe.Run(30,true,_=>{},default,sourceFactory:()=>throw new Exception("must not create"),continuous:true));
    }
    [Fact] public void CancelledContinuousH264NeverCreatesSourceOrStartsEncoder()
    {
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>H264Probe.Run(30,true,_=>{},cancel.Token,sourceFactory:()=>throw new Exception("must not create"),continuous:true));
    }
    [Fact] public void ContinuousJpegCannotStartWithoutCancellation()
    {
        Assert.Throws<ArgumentException>(()=>JpegProbe.Run(TimeSpan.FromSeconds(1),()=>throw new Exception("must not create"),_=>{},default,continuous:true));
    }
    [Fact] public void CancelledContinuousJpegNeverCreatesSource()
    {
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>JpegProbe.Run(TimeSpan.FromSeconds(1),()=>throw new Exception("must not create"),_=>{},cancel.Token,continuous:true));
    }
}
