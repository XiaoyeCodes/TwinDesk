namespace Workbench.Windows;

public sealed record InputExecutorStatus(bool Accepting, int Queued, InputSessionStatus Session);
public sealed record InputExecutorStop(bool Completed, bool Released, InputSessionStatus Session);

/// <summary>
/// Sole owner of one InputSession and native backend. Dedicated thread, bounded FIFO, no replay/coalescing.
/// Media must never execute on this thread. Stop timeout is NOT permission to replace a stuck executor;
/// the future Host/Agent supervisor must confirm the old process has exited before its release-only guard runs.
/// </summary>
public sealed class BoundedInputExecutor
{
    public const int MaximumQueued = 256;
    public static readonly TimeSpan MaximumQueueAge = TimeSpan.FromMilliseconds(500);
    private readonly object gate = new();
    private readonly Queue<Work> queue = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly TaskCompletionSource<InputExecutorStop> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly InputLease lease;
    private readonly InputSession session;
    private readonly IInputBackend backend;
    private readonly TimeProvider time;
    private readonly int capacity;
    private InputSessionStatus snapshot;
    private string? stopping;
    private bool finished;

    public BoundedInputExecutor(InputLease lease, WindowInfo root, IInputBackend backend,
        TimeProvider? time = null, int capacity = MaximumQueued)
    {
        if (capacity is < 1 or > MaximumQueued) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.lease = lease; this.backend = backend; this.time = time ?? TimeProvider.System; this.capacity = capacity;
        session = new(lease, root, backend, this.time); snapshot = session.Status;
        new Thread(Run) { IsBackground = true, Name = "Workbench input executor" }.Start();
    }

    // Nonblocking cached status: reading diagnostics must not wait on a stuck native call/session lock.
    public InputExecutorStatus Status { get { lock (gate) return new(stopping is null && !finished, queue.Count, snapshot); } }
    public Task<InputExecutorStop> Completion => completion.Task;

    public Task<InputOutcome> Submit(InputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Lease != lease) return Task.FromResult(new InputOutcome(false, "LEASE_STALE"));
        if (!InputCommandValidation.IsValid(command)) return Task.FromResult(new InputOutcome(false, "INVALID_MESSAGE"));
        return Enqueue(() => session.Dispatch(command), code => new InputOutcome(false, code));
    }

    public Task<bool> UpdateScene(InputStamp stamp, OwnedWindowScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Nodes.Count is < 1 or > OwnedWindowScene.MaximumNodes) return Task.FromResult(false);
        // Copy at admission, not at execution: callers cannot change pending input geometry.
        var copy = new OwnedWindowScene(scene.Bounds, Array.AsReadOnly(scene.Nodes.ToArray()));
        return Enqueue(() => session.UpdateScene(stamp, copy), _ => false);
    }
    public Task<bool> FrameSent(InputStamp stamp, uint sequence) => Enqueue(() => session.FrameSent(stamp, sequence), _ => false);
    public Task<bool> PauseScene() => Enqueue(session.PauseScene, _ => false);
    public Task<bool> Displayed(InputLease owner, InputStamp stamp, uint sequence) => owner != lease ? Task.FromResult(false)
        : Enqueue(() => session.Displayed(owner, stamp, sequence), _ => false);
    public Task<bool> Heartbeat(InputLease owner) => owner != lease ? Task.FromResult(false)
        : Enqueue(() => session.Heartbeat(owner), _ => false);

    // Out-of-band local safety signal, not a browser-supplied arbitrary action. Never waits for a native call.
    public void Invalidate(string code = "INPUT_EXECUTOR_STOPPED")
    {
        lock (gate)
        {
            if (finished) return;
            StopAdmission(code); wake.Set();
        }
    }
    public async Task<InputExecutorStop> StopAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(6)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Invalidate();
        try { return await completion.Task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return new(false, false, Status.Session); }
    }

    private Task<T> Enqueue<T>(Func<T> action, Func<string, T> rejected)
    {
        lock (gate)
        {
            if (stopping is not null || finished) return Task.FromResult(rejected(stopping ?? "INPUT_EXECUTOR_STOPPED"));
            if (queue.Count >= capacity)
            {
                StopAdmission("INPUT_QUEUE_OVERFLOW"); wake.Set();
                return Task.FromResult(rejected(stopping!));
            }
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(new(time.GetTimestamp(), () => result.TrySetResult(action()), code => result.TrySetResult(rejected(code))));
            wake.Set(); return result.Task;
        }
    }
    private void StopAdmission(string code)
    {
        stopping ??= code;
        while (queue.TryDequeue(out var pending)) pending.Reject(stopping);
    }
    private void Run()
    {
        Work? executing = null;
        bool released = false;
        try
        {
            while (true)
            {
                session.Tick(); Publish();
                lock (gate)
                {
                    if (!snapshot.Active) StopAdmission(snapshot.Reason);
                    if (stopping is not null) break;
                    executing = queue.TryDequeue(out var next) ? next : null;
                    if (executing is not null && time.GetElapsedTime(executing.At) >= MaximumQueueAge)
                    {
                        StopAdmission("INPUT_QUEUE_EXPIRED"); executing.Reject(stopping!); executing = null; break;
                    }
                }
                // Invalidation racing an already-dequeued operation allows only this one in-flight call;
                // no release executes concurrently with it, and no later queued action can run.
                if (executing is null) wake.WaitOne(25);
                else { executing.Execute(); executing = null; Publish(); }
            }
        }
        catch (Exception)
        {
            var failed=session.Status;
            lock (gate) { StopAdmission(failed.Active?"INPUT_EXECUTOR_FAILED":failed.Reason); executing?.Reject(stopping!); }
        }
        finally
        {
            lock (gate) StopAdmission(stopping ?? "INPUT_EXECUTOR_STOPPED");
            session.Invalidate(stopping!);
            // Bounded release-only retries; failure remains explicit and never restarts this lease.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                released = session.RetrySafetyRelease() && !backend.HasPendingTransient;
                if (released) break;
                wake.WaitOne(50);
            }
            Publish();
            lock (gate)
            {
                finished = true; wake.Dispose();
                completion.TrySetResult(new(true, released && snapshot.HeldCount == 0, snapshot));
            }
        }
    }
    private void Publish() { var state = session.Status; lock (gate) snapshot = state; }
    private sealed record Work(long At, Action Execute, Action<string> Reject);
}
