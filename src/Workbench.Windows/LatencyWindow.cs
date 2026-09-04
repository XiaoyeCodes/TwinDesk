namespace Workbench.Windows;

public sealed record LatencySummary(long Total,int Samples,double P50Ms,double P95Ms,double MaximumMs);

// Bounded diagnostic distribution; callers supply durations from ONE monotonic clock domain.
public sealed class LatencyWindow
{
    private readonly object gate=new();
    private readonly double[] values=new double[256];
    private long total;
    public void Record(double milliseconds)
    {
        if(!double.IsFinite(milliseconds)||milliseconds<0)throw new ArgumentOutOfRangeException(nameof(milliseconds));
        lock(gate){values[total%values.Length]=milliseconds;total++;}
    }
    public LatencySummary Snapshot()
    {
        lock(gate)
        {
            int count=(int)Math.Min(total,values.Length);
            if(count==0)return new(total,0,0,0,0);
            var sorted=values.Take(count).Order().ToArray();
            return new(total,count,sorted[(int)Math.Ceiling(count*.5)-1],sorted[(int)Math.Ceiling(count*.95)-1],sorted[^1]);
        }
    }
}
