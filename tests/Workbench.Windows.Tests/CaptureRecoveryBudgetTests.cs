using Workbench.Windows;
using Xunit;

public sealed class CaptureRecoveryBudgetTests
{
    [Fact] public void AttemptsAreFinite()
    {
        var budget = new CaptureRecoveryBudget();
        for (int i=0;i<8;i++) budget.Record(TimeSpan.FromMilliseconds(i*100));
        Assert.Throws<TimeoutException>(()=>budget.Record(TimeSpan.FromMilliseconds(800)));
    }
    [Fact] public void DeadlineAppliesEvenWithoutAnotherFailure()
    {
        var budget = new CaptureRecoveryBudget(); budget.Record(TimeSpan.FromSeconds(10));
        budget.ThrowIfExpired(TimeSpan.FromMilliseconds(11999));
        Assert.Throws<TimeoutException>(()=>budget.ThrowIfExpired(TimeSpan.FromSeconds(12)));
    }
    [Fact] public void CompleteFreshCompositionResetsTheBudget()
    {
        var budget = new CaptureRecoveryBudget(); budget.Record(TimeSpan.FromSeconds(10));
        budget.Complete(); budget.ThrowIfExpired(TimeSpan.FromSeconds(100));
        Assert.Equal(0,budget.Attempts); budget.Record(TimeSpan.FromSeconds(100));
        Assert.Equal(1,budget.Attempts);
    }
    [Fact] public void BackwardsTimeIsRejected()
    {
        var budget = new CaptureRecoveryBudget(); budget.Record(TimeSpan.FromSeconds(10));
        Assert.Throws<ArgumentOutOfRangeException>(()=>budget.Record(TimeSpan.FromSeconds(9)));
    }
}
