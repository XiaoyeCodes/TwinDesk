namespace Workbench.Windows;

// One producer (the hook message thread), one consumer (control monitor).
// Published slots are immutable. Consumer never locks the producer or edits its tail slot.
public sealed class LocalDeviceQueue(TimeProvider? time=null)
{
    private readonly TimeProvider clock=time??TimeProvider.System;
    public const int Capacity=128;
    private readonly Entry?[] entries=new Entry[Capacity];
    private long head,tail;
    private int maximumDepth;
    public int MaximumDepth=>Volatile.Read(ref maximumDepth);
    public LatencyWindow QueueAge {get;}=new();
    public bool TryWrite(LocalDeviceEvent value)
    {
        long position=tail,read=Volatile.Read(ref head);
        if(position-read>=Capacity)return false;
        entries[position%Capacity]=new(value,clock.GetTimestamp());
        int depth=(int)(position-read+1);
        if(depth>maximumDepth)Volatile.Write(ref maximumDepth,depth);
        Volatile.Write(ref tail,position+1);
        return true;
    }
    public LocalDeviceEvent[] Drain()
    {
        long limit=Volatile.Read(ref tail);var result=new List<LocalDeviceEvent>();
        while(head<limit)
        {
            long position=head;
            var entry=entries[position%Capacity]!;entries[position%Capacity]=null;
            Volatile.Write(ref head,position+1);
            double age=clock.GetElapsedTime(entry.At).TotalMilliseconds;QueueAge.Record(age);
            if(age>=250)throw new TimeoutException("LOCAL_DEVICE_QUEUE_EXPIRED");
            var value=entry.Value;
            // Merge only adjacent relative moves in this drained batch and capture generation.
            if(value.Kind=="Move" && result.LastOrDefault() is {Kind:"Move"} previous && value.Scene==previous.Scene
                && Math.Abs((long)previous.Dx+value.Dx)<=100000 && Math.Abs((long)previous.Dy+value.Dy)<=100000)
                result[^1]=previous with {Dx=previous.Dx+value.Dx,Dy=previous.Dy+value.Dy};
            else result.Add(value);
        }
        return result.ToArray();
    }
    private sealed record Entry(LocalDeviceEvent Value,long At);
}
