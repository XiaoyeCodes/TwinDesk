using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

// Deliberately no SendInput here. These tests prove L0 decisions/ledger behavior, not native application response.
public class InputSessionTests
{
    internal static WindowInfo Root(long handle=1,long owner=0) => new(handle,99,"fixture","fixture.exe","fixture","fixture",
        new(-1920,50,1000,700),120,false,owner,new DateTime(2026,9,4,0,0,0,DateTimeKind.Utc),0,0,
        new(-1912,81,984,661),true) { SessionId=1,BindingGeneration=1,ZOrder=(int)handle-1 };
    internal static OwnedWindowScene Geometry()=>OwnedWindowScene.Arrange([Root()]);
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>ticks;
        public void Advance(int milliseconds)=>ticks+=milliseconds;
    }
    private sealed class Backend : IInputBackend
    {
        public bool Focus=true,Send=true,Release=true,ThrowSend,ThrowRelease;
        public readonly List<InputCommand> Commands=[];
        public readonly List<HeldInput[]> Releases=[];
        public bool IsTargetReady(OwnedWindowScene scene,ScreenPoint? point)=>Focus;
        public bool TrySend(InputCommand command,ScreenPoint? point)
        {
            Commands.Add(command);if(ThrowSend)throw new InvalidOperationException("Synthetic partial failure");return Send;
        }
        public bool TryRelease(IReadOnlyList<HeldInput> held)
        {
            Releases.Add(held.ToArray());if(ThrowRelease)throw new InvalidOperationException("Synthetic release failure");return Release;
        }
    }
    private sealed class Case
    {
        public readonly Clock Clock=new();
        public readonly Backend Backend=new();
        public readonly InputLease Lease=new(Guid.NewGuid(),1);
        public InputStamp Stamp=new(Guid.NewGuid(),1,1,1);
        public readonly InputSession Session;
        public Case(bool ready=true)
        {
            Session=new(Lease,Root(),Backend,Clock);
            Session.UpdateScene(Stamp,Geometry());
            if(ready){Session.FrameSent(Stamp,1);Session.Displayed(Lease,Stamp,1);}
        }
        public InputCommand Down(long seq=1,string key="ControlLeft")=>new(Lease,seq,Stamp,1,InputKind.KeyDown,Key:key);
        public InputCommand Up(long seq=2,string key="ControlLeft")=>new(Lease,seq,Stamp,1,InputKind.KeyUp,Key:key);
    }

    [Fact] public void NoPressBeforeExactSentFrameIsDisplayed()
    {
        var c=new Case(false);
        Assert.Equal("STREAM_NOT_READY",c.Session.Dispatch(c.Down()).Code);
        Assert.False(c.Session.Displayed(c.Lease,c.Stamp,1));
        Assert.True(c.Session.FrameSent(c.Stamp,1));
        Assert.False(c.Session.Displayed(c.Lease,c.Stamp with {Epoch=2},1));
        Assert.False(c.Session.Displayed(c.Lease,c.Stamp,2));
        Assert.True(c.Session.Displayed(c.Lease,c.Stamp,1));
        Assert.True(c.Session.Dispatch(c.Down(2)).Accepted);
        Assert.Single(c.Backend.Commands);
    }
    [Fact] public void NewSceneReleasesAndRejectsOldAckAndInput()
    {
        var c=new Case();Assert.True(c.Session.Dispatch(c.Down()).Accepted);
        var old=c.Stamp;c.Stamp=c.Stamp with {Scene=2};
        Assert.True(c.Session.UpdateScene(c.Stamp,Geometry()));
        Assert.Equal(0,c.Session.Status.HeldCount);Assert.False(c.Session.Status.Ready);
        Assert.False(c.Session.Displayed(c.Lease,old,1));
        Assert.True(c.Session.FrameSent(c.Stamp,2));Assert.True(c.Session.Displayed(c.Lease,c.Stamp,2));
        Assert.Equal("SCENE_STALE",c.Session.Dispatch(c.Down(2) with {Stamp=old,DisplayedFrame=2}).Code);
        Assert.True(c.Session.Dispatch(c.Down(3) with {DisplayedFrame=2}).Accepted);
    }
    [Fact] public void OldSceneReleaseAllowedButOldLeaseCannotReleaseCurrentKey()
    {
        var c=new Case();c.Session.Dispatch(c.Down());
        Assert.Equal("LEASE_STALE",c.Session.Dispatch(c.Up() with {Lease=c.Lease with {Generation=2}}).Code);
        Assert.Equal(1,c.Session.Status.HeldCount);Assert.Empty(c.Backend.Releases);
        Assert.True(c.Session.Dispatch(c.Up() with {Stamp=default,DisplayedFrame=0}).Accepted);
        Assert.Equal(0,c.Session.Status.HeldCount);Assert.Single(c.Backend.Releases);
    }
    [Fact] public void DuplicateOrOutOfOrderActionsAreNotReplayed()
    {
        var c=new Case();Assert.True(c.Session.Dispatch(c.Down(5)).Accepted);
        Assert.Equal("INPUT_OUT_OF_ORDER",c.Session.Dispatch(c.Down(5)).Code);
        Assert.Equal("INPUT_OUT_OF_ORDER",c.Session.Dispatch(c.Up(4)).Code);
        Assert.Equal(1,c.Session.Status.HeldCount);Assert.Single(c.Backend.Commands);
        Assert.True(c.Session.Dispatch(c.Up(6)).Accepted);
    }
    [Fact] public void RepeatsRequireHeldKeyAndDoNotExpandLedger()
    {
        var c=new Case();
        Assert.False(c.Session.Dispatch(c.Down() with {Repeat=true}).Accepted);
        Assert.True(c.Session.Dispatch(c.Down(2)).Accepted);
        Assert.False(c.Session.Dispatch(c.Down(3)).Accepted);
        Assert.True(c.Session.Dispatch(c.Down(4) with {Repeat=true}).Accepted);
        Assert.Equal(1,c.Session.Status.HeldCount);
        Assert.True(c.Session.Dispatch(c.Up(5)).Accepted);
    }
    [Theory] [InlineData(InputButton.Left)] [InlineData(InputButton.Right)] [InlineData(InputButton.Middle)]
    public void MouseReleaseDoesNotMoveCursorOrRequireCurrentGeometry(InputButton button)
    {
        var c=new Case();
        Assert.True(c.Session.Dispatch(new(c.Lease,1,c.Stamp,1,InputKind.ButtonDown,Button:button,U:0.5,V:0.5)).Accepted);
        Assert.True(c.Session.Dispatch(new(c.Lease,2,default,0,InputKind.ButtonUp,Button:button)).Accepted);
        Assert.Equal(HeldInput.ForButton(button),Assert.Single(Assert.Single(c.Backend.Releases)));
        Assert.Single(c.Backend.Commands);
    }
    [Fact] public void HeartbeatExpiryLeavesMarginInsideSixSecondExternalTarget()
    {
        var c=new Case();c.Session.Dispatch(c.Down());c.Clock.Advance(5499);c.Session.Tick();Assert.True(c.Session.Status.Active);
        c.Clock.Advance(1);c.Session.Tick();Assert.False(c.Session.Status.Active);
        Assert.Equal("HEARTBEAT_EXPIRED",c.Session.Status.Reason);Assert.Equal(0,c.Session.Status.HeldCount);
        Assert.False(c.Session.Heartbeat(c.Lease));Assert.False(c.Session.Dispatch(c.Down(2)).Accepted);
    }
    [Fact] public void StaticFiveMinutesWithHeartbeatDoesNotNeedFakeNewFrames()
    {
        var c=new Case();
        for(int i=0;i<150;i++){c.Clock.Advance(2000);Assert.True(c.Session.Heartbeat(c.Lease));c.Session.Tick();}
        Assert.True(c.Session.Dispatch(c.Down()).Accepted);Assert.True(c.Session.Status.Ready);
    }
    [Fact] public void UnconfirmedActualUpdateTimesOutDespiteControlHeartbeat()
    {
        var c=new Case();c.Session.Dispatch(c.Down());c.Session.FrameSent(c.Stamp,2);
        c.Clock.Advance(2000);c.Session.Heartbeat(c.Lease);c.Clock.Advance(1000);c.Session.Tick();
        Assert.Equal("DISPLAY_ACK_TIMEOUT",c.Session.Status.Reason);Assert.Equal(0,c.Session.Status.HeldCount);
    }
    [Fact] public void SlowAcksDoNotResetAgeOfOlderOutstandingFrames()
    {
        var c=new Case();c.Session.FrameSent(c.Stamp,2);c.Clock.Advance(500);c.Session.FrameSent(c.Stamp,3);
        c.Clock.Advance(2000);Assert.True(c.Session.Displayed(c.Lease,c.Stamp,2));c.Session.Heartbeat(c.Lease);
        c.Clock.Advance(1000);c.Session.Tick();Assert.Equal("DISPLAY_ACK_TIMEOUT",c.Session.Status.Reason);
    }
    [Fact] public void FrameHistoryIsBoundedAndFailsClosed()
    {
        var c=new Case();c.Session.Dispatch(c.Down());
        for(uint i=2;i<=257;i++)Assert.True(c.Session.FrameSent(c.Stamp,i));
        Assert.False(c.Session.FrameSent(c.Stamp,258));Assert.Equal("MEDIA_BACKPRESSURE",c.Session.Status.Reason);
        Assert.Equal(0,c.Session.Status.HeldCount);Assert.Equal(0,c.Session.Status.PendingFrames);
    }
    [Fact] public void FocusFailureReleasesGestureWithoutSendingNewAction()
    {
        var c=new Case();c.Session.Dispatch(c.Down());c.Backend.Focus=false;
        Assert.False(c.Session.Dispatch(c.Down(2,"KeyA")).Accepted);Assert.Single(c.Backend.Commands);
        Assert.Equal(0,c.Session.Status.HeldCount);Assert.False(c.Session.Status.Active);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FailedOrThrowingDownIsConservativelyReleased(bool throws)
    {
        var c=new Case();c.Backend.Send=false;c.Backend.ThrowSend=throws;
        Assert.False(c.Session.Dispatch(c.Down()).Accepted);
        Assert.Equal(HeldInput.ForKey("ControlLeft"),Assert.Single(Assert.Single(c.Backend.Releases)));
        Assert.False(c.Session.Status.Active);Assert.Equal(0,c.Session.Status.HeldCount);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FailedReleaseRetainsLedgerForExplicitSafetyRetry(bool throws)
    {
        var c=new Case();c.Session.Dispatch(c.Down());c.Backend.Release=false;c.Backend.ThrowRelease=throws;
        Assert.False(c.Session.Dispatch(c.Up()).Accepted);
        Assert.Equal("INPUT_RELEASE_FAILED",c.Session.Status.Reason);Assert.Equal(1,c.Session.Status.HeldCount);
        c.Backend.Release=true;c.Backend.ThrowRelease=false;Assert.True(c.Session.RetrySafetyRelease());Assert.Equal(0,c.Session.Status.HeldCount);
        Assert.False(c.Session.Status.Active);
    }
    [Fact] public void DisconnectAndMediaLossUseSameReleaseOnlyPath()
    {
        var c=new Case();c.Session.Dispatch(c.Down());
        c.Session.Dispatch(new(c.Lease,2,c.Stamp,1,InputKind.ButtonDown,Button:InputButton.Middle,U:0.4,V:0.5));
        c.Session.Invalidate("VIDEO_DISCONNECTED");
        Assert.Equal(2,Assert.Single(c.Backend.Releases).Length);Assert.Equal(0,c.Session.Status.HeldCount);
        Assert.False(c.Session.Dispatch(c.Down(3)).Accepted);
    }
    [Fact] public void TextRequiresNoHeldGestureAndIsNotRetried()
    {
        var c=new Case();c.Session.Dispatch(c.Down());
        var text=new InputCommand(c.Lease,2,c.Stamp,1,InputKind.Text,Text:"测试 NX / TIA，123 🧪");
        Assert.Equal("TEXT_WHILE_HELD",c.Session.Dispatch(text).Code);c.Session.Dispatch(c.Up(3));
        Assert.True(c.Session.Dispatch(text with {Sequence=4}).Accepted);
        Assert.False(c.Session.Dispatch(text with {Sequence=4}).Accepted);
        Assert.Equal(2,c.Backend.Commands.Count);Assert.DoesNotContain("测试",text.ToString());
    }
    [Fact] public void CannotRebindSessionToUnrelatedRootOrReuseSceneStamp()
    {
        var c=new Case();
        Assert.Throws<InvalidOperationException>(()=>c.Session.UpdateScene(c.Stamp,Geometry()));
        Assert.True(c.Session.Dispatch(c.Down()).Accepted);
        Assert.Throws<InvalidOperationException>(()=>c.Session.UpdateScene(c.Stamp with {Scene=2},OwnedWindowScene.Arrange([Root(9)])));
        Assert.False(c.Session.Status.Active);Assert.Equal(0,c.Session.Status.HeldCount);Assert.Equal("SCENE_INVALID",c.Session.Status.Reason);
    }
    [Fact] public void EpochChangeRequiresNewFrameAndCannotAcceptOldFrameAck()
    {
        var c=new Case();var old=c.Stamp;c.Stamp=c.Stamp with {Epoch=2};
        c.Session.UpdateScene(c.Stamp,Geometry());Assert.True(c.Session.FrameSent(c.Stamp,1));
        Assert.False(c.Session.Displayed(c.Lease,old,1));Assert.True(c.Session.Displayed(c.Lease,c.Stamp,1));
    }
    [Fact] public void OneThousandLogicalPairsLeaveNoLedgerEntries_NotNativeReliabilityProof()
    {
        var c=new Case();for(int i=0;i<1000;i++)
        {
            Assert.True(c.Session.Dispatch(c.Down(2*i+1)).Accepted);Assert.True(c.Session.Dispatch(c.Up(2*i+2)).Accepted);
        }
        Assert.Equal(0,c.Session.Status.HeldCount);Assert.Equal(1000,c.Backend.Commands.Count);Assert.Equal(1000,c.Backend.Releases.Count);
    }
    [Fact] public void ReleaseOnlyRequestsNeverInventUnheldKeysOrButtons()
    {
        var c=new Case();Assert.True(c.Session.Dispatch(c.Up(1,"KeyA")).Accepted);
        Assert.Empty(c.Backend.Releases);
        c.Session.Dispatch(c.Down(2));
        c.Session.Dispatch(new(c.Lease,3,c.Stamp,1,InputKind.ButtonDown,Button:InputButton.Right,U:0.5,V:0.5));
        c.Session.Dispatch(new(c.Lease,4,default,0,InputKind.ReleaseAll));
        var release=Assert.Single(c.Backend.Releases);
        Assert.Equal(HeldInput.ForButton(InputButton.Right),release[0]);Assert.Equal(HeldInput.ForKey("ControlLeft"),release[1]);
    }
    [Fact] public void ChangingCallerListAfterScenePublishCannotChangeInputGeometry()
    {
        var c=new Case();var list=Geometry().Nodes.ToList();
        c.Stamp=c.Stamp with {Scene=2};c.Session.UpdateScene(c.Stamp,new(Geometry().Bounds,list));list.Clear();
        Assert.True(c.Session.FrameSent(c.Stamp,2));Assert.True(c.Session.Displayed(c.Lease,c.Stamp,2));
        Assert.True(c.Session.Dispatch(c.Down() with {DisplayedFrame=2}).Accepted);
    }
}
