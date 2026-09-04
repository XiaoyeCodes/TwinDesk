using Workbench.Windows;
using Xunit;

public sealed class ProbeControlAdmissionTests
{
    [Fact]
    public void ParallelTargetsHaveOneOwnerAndUnconfirmedReleaseCannotReplaceIt()
    {
        var admission=new ProbeControlAdmission();var first=Guid.NewGuid();var second=Guid.NewGuid();
        Assert.True(admission.TryAcquire(first));Assert.False(admission.TryAcquire(second));
        Assert.False(admission.Release(second,true,true));
        Assert.False(admission.Release(first,false,true));Assert.False(admission.Release(first,true,false));
        Assert.False(admission.TryAcquire(second));Assert.True(admission.Release(first,true,true));
        Assert.True(admission.TryAcquire(second));Assert.False(admission.Release(first,true,true));
    }
    [Fact]
    public void ConcurrentAcquisitionsAdmitExactlyOne()
    {
        var admission=new ProbeControlAdmission();int accepted=0;
        Parallel.For(0,32,_=>{if(admission.TryAcquire(Guid.NewGuid()))Interlocked.Increment(ref accepted);});
        Assert.Equal(1,accepted);
    }
}
