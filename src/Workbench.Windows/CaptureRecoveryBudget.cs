namespace Workbench.Windows;

/// <summary>Bounded geometry recovery, reset only after a complete fresh scene is composed.</summary>
public sealed class CaptureRecoveryBudget
{
    public const int MaximumAttempts = 8;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private TimeSpan? started;
    public int Attempts { get; private set; }
    public void Record(TimeSpan now)
    {
        ThrowIfExpired(now);
        started ??= now;
        if (++Attempts > MaximumAttempts) throw new TimeoutException("Capture geometry recovery attempt budget exceeded.");
    }
    public void ThrowIfExpired(TimeSpan now)
    {
        if (now < TimeSpan.Zero || (started is { } first && now < first))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (started is { } at && now - at >= Deadline)
            throw new TimeoutException("Capture geometry did not stabilize within two seconds.");
    }
    public void Complete() { started = null; Attempts = 0; }
}
