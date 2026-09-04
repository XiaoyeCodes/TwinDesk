using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class LocalDeviceQueueTests
{
    private sealed class Clock:TimeProvider
    {public long Now;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>Now;}
    [Fact] public void CapacityAndWraparoundDoNotOverwriteUnreadEdges()
    {
        var q=new LocalDeviceQueue();
        for(int round=0;round<10;round++)
        {
            for(int i=0;i<LocalDeviceQueue.Capacity;i++)Assert.True(q.TryWrite(new("Button",Button:"Left",Up:(i%2)==1)));
            Assert.False(q.TryWrite(new("Wheel",WheelY:120)));
            var items=q.Drain();Assert.Equal(128,items.Length);
            for(int i=0;i<items.Length;i++)Assert.Equal((i%2)==1,items[i].Up);
        }
        Assert.Empty(q.Drain());Assert.Equal(128,q.MaximumDepth);
    }
    [Fact] public void RelativeMovesRespectButtonKeyWheelAndSceneBoundaries()
    {
        var q=new LocalDeviceQueue();
        LocalDeviceEvent[] items=[new("Move",1,2,Scene:1),new("Move",3,4,Scene:1),new("Button",Button:"Left"),
            new("Move",5,6,Scene:1),new("Move",7,8,Scene:2),new("Key",Code:"ShiftLeft"),new("Wheel",WheelY:120)];
        foreach(var item in items)Assert.True(q.TryWrite(item));
        var output=q.Drain();Assert.Equal(6,output.Length);Assert.Equal(new("Move",4,6,Scene:1),output[0]);
        Assert.Equal(items[2..],output[1..]);
    }
    [Fact] public void DeltaOverflowDoesNotWrapOrLoseMotion()
    {
        var q=new LocalDeviceQueue();q.TryWrite(new("Move",int.MaxValue,0));q.TryWrite(new("Move",1,0));
        var output=q.Drain();Assert.Equal(2,output.Length);Assert.Equal(int.MaxValue,output[0].Dx);
    }
    [Fact] public void ExpiredInputIsReportedInsteadOfReplayed()
    {
        var clock=new Clock();var q=new LocalDeviceQueue(clock);q.TryWrite(new("Key",Code:"KeyA"));clock.Now=250;
        Assert.Throws<TimeoutException>(()=>q.Drain());Assert.Empty(q.Drain());
        Assert.Equal(250,q.QueueAge.Snapshot().P95Ms);
    }
    [Fact] public async Task ConcurrentProducerConsumerPreservesOneHundredThousandEdges()
    {
        var q=new LocalDeviceQueue(new Clock());const int count=100000;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writer=Task.Run(()=>{
            for(int i=0;i<count;i++)while(!q.TryWrite(new("Wheel",WheelY:i))){deadline.Token.ThrowIfCancellationRequested();Thread.Yield();}
        });
        int received=0;
        while(received<count)
        {
            deadline.Token.ThrowIfCancellationRequested();
            foreach(var item in q.Drain())Assert.Equal(received++,item.WheelY);
            Thread.Yield();
        }
        await writer;Assert.Empty(q.Drain());Assert.InRange(q.MaximumDepth,1,128);
    }
    [Fact] public void DiagnosticPercentilesUseOnlyBoundedWindow()
    {
        var sample=new LatencyWindow();for(int i=1;i<=300;i++)sample.Record(i);
        var stats=sample.Snapshot();Assert.Equal(300,stats.Total);Assert.Equal(256,stats.Samples);
        Assert.Equal(172,stats.P50Ms);Assert.Equal(288,stats.P95Ms);Assert.Equal(300,stats.MaximumMs);
        Assert.Throws<ArgumentOutOfRangeException>(()=>sample.Record(double.NaN));
    }
}
