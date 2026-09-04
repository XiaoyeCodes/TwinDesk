namespace Workbench.Windows;

/// <summary>M1-only shared admission. A new target cannot acquire until the previous executor confirms release.</summary>
public sealed class ProbeControlAdmission
{
    private readonly object gate=new();
    private Guid? owner;
    public bool TryAcquire(Guid candidate)
    {
        if(candidate==Guid.Empty)throw new ArgumentException("A unique controller identity is required.",nameof(candidate));
        lock(gate){if(owner is not null)return false;owner=candidate;return true;}
    }
    public bool Release(Guid candidate,bool stopped,bool released)
    {
        lock(gate)
        {
            if(owner!=candidate||!stopped||!released)return false;
            owner=null;return true;
        }
    }
}
