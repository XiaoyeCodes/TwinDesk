using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class OwnedWindowSceneTests
{
    private static WindowInfo W(long handle, long owner = 0, int x = 0, int y = 0, int width = 200, int height = 100) =>
        new(handle, 99, "fixture", "fixture.exe", "title", "fixture", new(x,y,width,height), 96, false, owner,
            new DateTime(2026,9,3,0,0,0,DateTimeKind.Utc), 0, 0, new(x,y,width,height), true) { SessionId=1, ZOrder=(int)(10-handle) };

    [Fact]
    public void SelectsTransitiveOwnersNotUnrelatedSameProcess()
    {
        var root=W(1); var popup=W(2,1); var child=W(3,2); var unrelated=W(4);
        Assert.Equal([1L,2L,3L],OwnedWindowScene.Select(root,[root,popup,child,unrelated]).Select(w=>w.Handle));
    }
    [Fact]
    public void DifferentProcessOrSessionIsNotSilentlyTrusted()
    {
        var root=W(1);
        Assert.Single(OwnedWindowScene.Select(root,[root,W(2,1) with {ProcessId=100},W(3,1) with {SessionId=2}]));
    }
    [Fact]
    public void DecorationsAndHiddenCloakedNodesAreNotContent()
    {
        var root=W(1);
        Assert.Single(OwnedWindowScene.Select(root,[root,W(2,1) with {ClassName="SysShadow"},W(3,1) with {Visible=false},W(4,1) with {Cloaked=true}]));
    }
    [Fact]
    public void CyclicOwnerGraphFailsClosed()
    {
        var root=W(1);
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Select(root,[root,W(2,3),W(3,2)]));
    }
    [Fact]
    public void MissingOwnerDoesNotGrantRootMembership()
    {
        var root=W(1);Assert.Single(OwnedWindowScene.Select(root,[root,W(2,8)]));
    }
    [Fact]
    public void ReusedProcessIdentityIsRejected()
    {
        var root=W(1);
        Assert.Throws<InvalidOperationException>(()=>OwnedWindowScene.Select(root,[root with {ProcessStartedAtUtc=root.ProcessStartedAtUtc.AddSeconds(1)}]));
    }
    [Fact]
    public void UnavailableRootIsRejected()
    {
        var root=W(1);
        Assert.Throws<InvalidOperationException>(()=>OwnedWindowScene.Select(root,[root with {Minimized=true}]));
        Assert.Throws<InvalidOperationException>(()=>OwnedWindowScene.Select(root,[root with {Cloaked=true}]));
    }
    [Fact]
    public void DuplicateHandleAndNodeBudgetAreRejected()
    {
        var root=W(1);
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Select(root,[root,root]));
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Select(root,[root,..Enumerable.Range(2,8).Select(i=>W(i,1))]));
    }
    [Fact]
    public void PopupOutsideRootAndNegativeCoordinatesExpandUnion()
    {
        var scene=OwnedWindowScene.Arrange([W(1,x:-100,y:-50),W(2,1,x:90,y:30)]);
        Assert.Equal(new WindowBounds(-100,-50,390,180),scene.Bounds);
        Assert.Equal(new WindowBounds(190,80,200,100),scene.Nodes[1].Destination);
    }
    [Theory]
    [InlineData(0,100)] [InlineData(8193,10)] [InlineData(8192,8192)]
    public void InvalidDimensionsRejected(int width,int height) =>
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Arrange([W(1,width:width,height:height)]));
    [Fact]
    public void UnionOverflowAndTotalSourceBudgetRejected()
    {
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Arrange([W(1,x:int.MaxValue-1)]));
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Arrange([W(1,x:int.MinValue),W(2,x:int.MaxValue-500)]));
        Assert.Throws<InvalidDataException>(()=>OwnedWindowScene.Arrange([W(1,width:4096,height:4096),W(2,1)]));
    }
    [Fact]
    public void EquivalentGeometryIgnoresTitleAndAbsoluteRank()
    {
        var first=W(1); var before=OwnedWindowScene.Arrange([first]);
        Assert.True(before.SameGeometry(OwnedWindowScene.Arrange([first with {Title="new title",ZOrder=999}])));
        Assert.False(before.SameGeometry(OwnedWindowScene.Arrange([first with {Enabled=false}])));
        Assert.False(before.SameGeometry(OwnedWindowScene.Arrange([first with {BindingGeneration=2}])));
        Assert.False(before.SameGeometry(OwnedWindowScene.Arrange([first with {CaptureBounds=new(1,0,200,100)}])));
    }
    [Fact]
    public void RelativeZOrderChangesScene()
    {
        var root=W(1);var a=W(2,1);var b=W(3,1);
        Assert.False(OwnedWindowScene.Arrange([root,a,b]).SameGeometry(OwnedWindowScene.Arrange([root,b,a])));
    }
    [Fact] public void ArrangedSceneCanBeReselectedWithoutReversingNativeZOrder()
    {
        var root=W(1);var geometry=OwnedWindowScene.Arrange(OwnedWindowScene.Select(root,[W(3,2),root,W(2,1)]));
        var again=OwnedWindowScene.Arrange(OwnedWindowScene.Select(root,geometry.Nodes.Select(node=>node.Window).ToArray()));
        Assert.True(geometry.SameGeometry(again));Assert.Equal([2,1,0],geometry.Nodes.Select(node=>node.Window.ZOrder));
    }
    [Fact]
    public void NodesCannotBeMutatedThroughInputArray()
    {
        WindowInfo[] source=[W(1)];var scene=OwnedWindowScene.Arrange(source);source[0]=W(2);
        Assert.Equal(1,scene.Nodes[0].Window.Handle);
        Assert.Throws<NotSupportedException>(()=>((IList<SceneNode>)scene.Nodes).Clear());
    }
    [Fact]
    public void LetterboxPreservesAspectAndDefinesRealContent()
    {
        Assert.Equal(new WindowBounds(280,0,720,720),OwnedWindowScene.Letterbox(new(0,0,100,100),1280,720));
        Assert.Equal(new WindowBounds(0,200,1280,320),OwnedWindowScene.Letterbox(new(0,0,800,200),1280,720));
    }
    private static ProbeSceneConfig Config(uint version) => new(version,1280,720,new(0,0,1280,720),1);
    [Fact]
    public void DelayedEncoderOutputKeepsOriginalScene()
    {
        var ledger=new FrameSceneLedger(8);var old=Config(1);ledger.Add(0,old);ledger.Add(333330,Config(2));
        Assert.Same(old,ledger.Take(0));Assert.Equal(2u,ledger.Take(333330)!.Version);Assert.Equal(0,ledger.Count);
    }
    [Fact]
    public void OutOfOrderLookupDoesNotTakeLatestMetadata()
    {
        var ledger=new FrameSceneLedger(8);ledger.Add(0,Config(1));ledger.Add(10,Config(2));
        Assert.Equal(2u,ledger.Take(10)!.Version);Assert.Equal(1u,ledger.Take(0)!.Version);
    }
    [Fact]
    public void MissingRepeatedAndOverBudgetMetadataFail()
    {
        var ledger=new FrameSceneLedger(1);ledger.Add(0,Config(1));
        Assert.Throws<InvalidDataException>(()=>ledger.Add(10,Config(2)));
        Assert.Throws<InvalidDataException>(()=>ledger.Take(1));ledger.Take(0);
        Assert.Throws<InvalidDataException>(()=>ledger.Take(0));
        Assert.Throws<InvalidDataException>(()=>ledger.Add(0,Config(1)));
        Assert.Throws<InvalidDataException>(()=>ledger.Add(9,Config(1)));
    }
}
