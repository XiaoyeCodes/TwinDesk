using System.Collections.Concurrent;
using Workbench.Windows;
using Xunit;

namespace Workbench.Windows.Tests;

// L0 deterministic backend only. Blocking here simulates a hung native call; no real keyboard injection.
public class BoundedInputExecutorTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(long milliseconds) => Interlocked.Add(ref ticks, milliseconds);
    }
    private sealed class Backend : IInputBackend
    {
        public readonly ManualResetEventSlim Entered = new(false), Continue = new(false);
        public readonly ConcurrentQueue<InputCommand> Sent = new();
        public readonly ConcurrentQueue<HeldInput[]> Releases = new();
        public readonly ConcurrentQueue<int> Threads = new();
        public bool Block, FailRelease;
        public int ReleaseCalls;
        public bool IsTargetReady(OwnedWindowScene scene, ScreenPoint? point) => true;
        public bool TrySend(InputCommand command, ScreenPoint? point)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId); Sent.Enqueue(command);
            if (Block) { Entered.Set(); if (!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            return true;
        }
        public bool TryRelease(IReadOnlyList<HeldInput> held)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId); Releases.Enqueue(held.ToArray());
            Interlocked.Increment(ref ReleaseCalls); return !FailRelease;
        }
    }
    private sealed class Case : IAsyncDisposable
    {
        public readonly InputLease Lease = new(Guid.NewGuid(), 1);
        public readonly InputStamp Stamp = new(Guid.NewGuid(), 1, 1, 1);
        public readonly Backend Backend = new();
        public readonly Clock Clock = new();
        public readonly BoundedInputExecutor Executor;
        public Case(int capacity = 256) => Executor = new(Lease, InputSessionTests.Root(), Backend, Clock, capacity);
        public async Task Ready()
        {
            Assert.True(await Executor.UpdateScene(Stamp, InputSessionTests.Geometry()));
            Assert.True(await Executor.FrameSent(Stamp, 1));
            Assert.True(await Executor.Displayed(Lease, Stamp, 1));
        }
        public InputCommand Down(long seq = 1, string key = "ControlLeft") => new(Lease, seq, Stamp, 1, InputKind.KeyDown, Key: key);
        public InputCommand Up(long seq = 2) => new(Lease, seq, Stamp, 1, InputKind.KeyUp, Key: "ControlLeft");
        public async ValueTask DisposeAsync()
        {
            Backend.Continue.Set();
            Assert.True((await Executor.StopAsync(TimeSpan.FromSeconds(2))).Completed);
            Backend.Entered.Dispose(); Backend.Continue.Dispose();
        }
        public async Task Blocked()
        {
            Backend.Block = true; _ = Executor.Submit(Down());
            Assert.True(await Task.Run(() => Backend.Entered.Wait(TimeSpan.FromSeconds(2))));
        }
    }

    [Fact] public async Task FifoAndReleaseUseOneDedicatedThread()
    {
        await using var c = new Case(); await c.Ready();
        var down = c.Executor.Submit(c.Down()); var up = c.Executor.Submit(c.Up());
        Assert.True((await down).Accepted); Assert.True((await up).Accepted);
        Assert.Single(c.Backend.Sent); Assert.Single(c.Backend.Releases);
        var stop = await c.Executor.StopAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.Released); Assert.Equal(0, stop.Session.HeldCount);
        Assert.Single(c.Backend.Threads.Distinct());
    }
    [Fact] public async Task OverflowRevokesAndClearsQueuedUpWithoutConcurrentNativeRelease()
    {
        await using var c = new Case(2); await c.Ready(); await c.Blocked();
        var up = c.Executor.Submit(c.Up()); var next = c.Executor.Submit(c.Down(3, "KeyA"));
        Assert.Equal(2, c.Executor.Status.Queued);
        var overflow = await c.Executor.Submit(c.Down(4, "KeyB"));
        Assert.Equal("INPUT_QUEUE_OVERFLOW", overflow.Code);
        Assert.False((await up).Accepted); Assert.False((await next).Accepted);
        Assert.False(c.Executor.Status.Accepting); Assert.Equal(0, c.Executor.Status.Queued);
        Assert.Empty(c.Backend.Releases);
        c.Backend.Continue.Set();
        var stop = await c.Executor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.Released); Assert.Equal("INPUT_QUEUE_OVERFLOW", stop.Session.Reason);
        Assert.Single(c.Backend.Sent); Assert.Single(c.Backend.Releases);
    }
    [Fact] public async Task QueueAgeExpiresInsteadOfReplayingAnOldEdit()
    {
        await using var c = new Case(); await c.Ready(); await c.Blocked();
        var next = c.Executor.Submit(c.Down(2, "KeyA")); c.Clock.Advance(500); c.Backend.Continue.Set();
        Assert.Equal("INPUT_QUEUE_EXPIRED", (await next).Code);
        var stop = await c.Executor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.Released); Assert.Single(c.Backend.Sent);
    }
    [Fact] public async Task WatchdogTicksWithoutMediaOrMoreInput()
    {
        await using var c = new Case(); await c.Ready(); Assert.True((await c.Executor.Submit(c.Down())).Accepted);
        c.Clock.Advance(5500);
        var stop = await c.Executor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("HEARTBEAT_EXPIRED", stop.Session.Reason); Assert.True(stop.Released);
    }
    [Fact] public async Task HeartbeatCannotExtendAnAlreadyExpiredLease()
    {
        await using var c = new Case(); await c.Ready(); c.Clock.Advance(5500);
        Assert.False(await c.Executor.Heartbeat(c.Lease));
        Assert.True((await c.Executor.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Released);
    }
    [Fact] public async Task StuckCallReportsIncompleteStopAndDoesNotRunSecondExecutor()
    {
        await using var c = new Case(); await c.Ready(); await c.Blocked();
        var queued = c.Executor.Submit(c.Down(2, "KeyA"));
        var stop = await c.Executor.StopAsync(TimeSpan.FromMilliseconds(40));
        Assert.False(stop.Completed); Assert.False(stop.Released); Assert.False((await queued).Accepted);
        Assert.False(c.Executor.Status.Accepting); Assert.Empty(c.Backend.Releases);
        Assert.False((await c.Executor.Submit(c.Down(3, "KeyB"))).Accepted);
        c.Backend.Continue.Set(); Assert.True((await c.Executor.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Released);
        Assert.Single(c.Backend.Sent); Assert.Single(c.Backend.Threads.Distinct());
    }
    [Fact] public async Task FailedReleaseRetainsLedgerAndIsNeverReportedSafe()
    {
        await using var c = new Case(); await c.Ready(); c.Backend.FailRelease = true;
        Assert.True((await c.Executor.Submit(c.Down())).Accepted);
        var stop = await c.Executor.StopAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.Completed); Assert.False(stop.Released); Assert.Equal(1, stop.Session.HeldCount);
        Assert.Equal("INPUT_RELEASE_FAILED", stop.Session.Reason); Assert.InRange(c.Backend.ReleaseCalls, 1, 4);
        Assert.False((await c.Executor.Submit(c.Down(2, "KeyA"))).Accepted);
    }
    [Fact] public async Task WrongLeaseAndMalformedPayloadCannotConsumeQueueOrReleaseCurrentLease()
    {
        await using var c = new Case(1); await c.Ready(); await c.Blocked();
        var queued = c.Executor.Submit(c.Up());
        for (int i = 0; i < 300; i++)
        {
            Assert.Equal("LEASE_STALE", (await c.Executor.Submit(c.Down() with { Lease = new(Guid.NewGuid(), 2) })).Code);
            Assert.Equal("INVALID_MESSAGE", (await c.Executor.Submit(c.Down() with { Key = "unknown" })).Code);
        }
        Assert.True(c.Executor.Status.Accepting); Assert.Equal(1, c.Executor.Status.Queued);
        c.Backend.Continue.Set(); Assert.True((await queued).Accepted);
    }
    [Fact] public async Task SceneSnapshotCopiedBeforeQueuedExecution()
    {
        await using var c = new Case(); await c.Ready(); await c.Blocked();
        var nodes = InputSessionTests.Geometry().Nodes.ToList(); var geometry = InputSessionTests.Geometry() with { Nodes = nodes };
        var changed = c.Executor.UpdateScene(c.Stamp with { Scene = 2 }, geometry); nodes.Clear();
        c.Backend.Continue.Set(); Assert.True(await changed); Assert.Single(c.Backend.Releases);
    }
    [Fact] public async Task SceneExceptionCompletesAllWaitersAndReleasesHeldKey()
    {
        await using var c = new Case(); await c.Ready(); await c.Blocked();
        var invalid = c.Executor.UpdateScene(c.Stamp, InputSessionTests.Geometry());
        var edit = c.Executor.Submit(c.Down(2, "KeyA")); c.Backend.Continue.Set();
        Assert.False(await invalid); Assert.False((await edit).Accepted);
        Assert.True((await c.Executor.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Released);
        Assert.Single(c.Backend.Sent);
    }
    [Fact] public async Task OldSceneUpStillReleasesOwnedKeyThroughQueue()
    {
        await using var c = new Case(); await c.Ready(); Assert.True((await c.Executor.Submit(c.Down())).Accepted);
        Assert.True((await c.Executor.Submit(c.Up() with { Stamp = default, DisplayedFrame = 0 })).Accepted);
        Assert.Single(c.Backend.Releases);
    }
    [Theory] [InlineData(0)] [InlineData(257)]
    public void QueueBudgetCannotBeDisabled(int capacity) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new BoundedInputExecutor(new(Guid.NewGuid(), 1), InputSessionTests.Root(), new Backend(), capacity: capacity));
}
