using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

public class CaptureInputBindingsTests
{
    [Fact] public void RequiresFirstFrameAndCompleteCompositionBeforeVerifying()
    {
        var registry = new CaptureInputBindings(); var root = InputSessionTests.Root();
        using var binding = registry.Register(root, 1);
        Assert.False(registry.Verify(root));
        Assert.Throws<InvalidOperationException>(() => registry.Publish(1, InputSessionTests.Geometry()));
        binding.FrameObserved(); Assert.False(registry.Verify(root));
        registry.Publish(1, InputSessionTests.Geometry()); Assert.True(registry.Verify(root));
        Assert.Equal(1u, registry.Current!.Version);
    }
    [Fact] public void ClosingCaptureImmediatelyFreezesWholeSceneAndRejectsReusedHandle()
    {
        var registry = new CaptureInputBindings(); var root = InputSessionTests.Root();
        var old = registry.Register(root, 1); old.FrameObserved(); registry.Publish(1, InputSessionTests.Geometry());
        old.Dispose(); Assert.Null(registry.Current); Assert.False(registry.Verify(root));
        using var next = registry.Register(root, 2); next.FrameObserved();
        var rebound = root with { BindingGeneration = 2 }; registry.Publish(2, OwnedWindowScene.Arrange([rebound]));
        Assert.False(registry.Verify(root)); Assert.True(registry.Verify(rebound));
        old.Dispose(); Assert.True(registry.Verify(rebound));
        Assert.Throws<InvalidOperationException>(old.FrameObserved);
    }
    [Fact] public void NewNodeCannotReuseOldSceneOrSkipItsFirstFrame()
    {
        var registry = new CaptureInputBindings(); var root = InputSessionTests.Root();
        using var first = registry.Register(root, 1); first.FrameObserved(); registry.Publish(1, InputSessionTests.Geometry());
        var popup = InputSessionTests.Root(2, 1) with { BindingGeneration = 2 };
        using var second = registry.Register(popup, 2);
        Assert.Null(registry.Current); Assert.False(registry.Verify(root));
        Assert.Throws<InvalidOperationException>(() => registry.Publish(2, InputSessionTests.Geometry()));
        var geometry = OwnedWindowScene.Arrange([root, popup]);
        Assert.Throws<InvalidOperationException>(() => registry.Publish(2, geometry));
        second.FrameObserved(); registry.Publish(2, geometry); Assert.True(registry.Verify(popup));
        second.Dispose(); Assert.False(registry.Verify(root)); Assert.Null(registry.Current);
    }
    [Fact] public void WrongProcessSessionAndGenerationCannotBorrowLiveCapture()
    {
        var registry = new CaptureInputBindings(); var root = InputSessionTests.Root();
        using var token = registry.Register(root, 1); token.FrameObserved(); registry.Publish(1, InputSessionTests.Geometry());
        Assert.False(registry.Verify(root with { ProcessId = 101 }));
        Assert.False(registry.Verify(root with { ProcessStartedAtUtc = root.ProcessStartedAtUtc.AddSeconds(1) }));
        Assert.False(registry.Verify(root with { SessionId = 2 }));
        Assert.False(registry.Verify(root with { BindingGeneration = 0 }));
    }
    [Fact] public void FreezeAndStopAreFailClosedAndPublishedListIsImmutable()
    {
        var registry = new CaptureInputBindings(); var root = InputSessionTests.Root();
        using var token = registry.Register(root, 1); token.FrameObserved();
        var nodes = InputSessionTests.Geometry().Nodes.ToList();
        registry.Publish(1, InputSessionTests.Geometry() with { Nodes = nodes }); nodes.Clear();
        Assert.Single(registry.Current!.Geometry.Nodes); Assert.True(registry.Verify(root));
        registry.Freeze(); Assert.False(registry.Verify(root));
        registry.Publish(1, InputSessionTests.Geometry()); registry.Stop();
        Assert.Null(registry.Current); Assert.False(registry.Verify(root));
        Assert.Throws<InvalidOperationException>(() => registry.Register(root, 2));
        Assert.Throws<InvalidOperationException>(() => registry.Publish(2, InputSessionTests.Geometry()));
    }
    [Fact] public void CannotRegisterDuplicateHandleOrNonIncreasingGeneration()
    {
        var registry = new CaptureInputBindings(); var root = InputSessionTests.Root();
        using var token = registry.Register(root, 1);
        Assert.Throws<InvalidOperationException>(() => registry.Register(root, 2));
        token.Dispose(); Assert.Throws<InvalidOperationException>(() => registry.Register(root, 1));
    }
}
