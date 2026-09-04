using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Workbench.Windows;

/// <summary>Explicit service-owned GPU lifetime. At most two sources; never a hidden process-global cache.</summary>
public sealed class CaptureGraphicsDevice : IDisposable
{
    private readonly object gate=new();
    private readonly HashSet<Lease> leases=[];
    private ID3D11Multithread multithread=null!;
    private bool disposed,retired;
    internal ID3D11Device Device {get;private set;}=null!;
    internal ID3D11DeviceContext Context {get;private set;}=null!;
    public Guid Identity {get;}=Guid.NewGuid();
    public int ActiveSources {get {lock(gate)return leases.Count;}}
    public bool IsRetired {get {lock(gate)return retired;}}

    public CaptureGraphicsDevice()
    {
        try
        {
            D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport|DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
            Device=device;Context=context;
            multithread=context.QueryInterface<ID3D11Multithread>();multithread.SetMultithreadProtected(true);
        }
        catch {Dispose();throw;}
    }

    internal Lease Acquire(Action revokeInput)
    {
        ArgumentNullException.ThrowIfNull(revokeInput);
        lock(gate)
        {
            ThrowIfUnavailable();
            if(leases.Count>=2)throw new InvalidOperationException("Capture device has reached its two-source budget.");
            var lease=new Lease(this,revokeInput);leases.Add(lease);return lease;
        }
    }

    // Explicit fail-closed operation, also used when GetDeviceRemovedReason reports native failure.
    // Recovery must retire all streams before creating a replacement device; never replace it under an MFT.
    public void Retire()
    {
        lock(gate)
        {
            if(retired)return;retired=true;
            foreach(var lease in leases)lease.RevokeInput();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(retired)throw new InvalidOperationException("Shared capture device retired; end all dependent streams before recovery.");
        var reason=Device.DeviceRemovedReason;
        if(reason.Failure){Retire();reason.CheckError();}
    }

    private IDisposable Enter(Lease lease,bool cleanup)
    {
        if(!Monitor.TryEnter(gate,TimeSpan.FromSeconds(3)))throw new TimeoutException("Shared GPU operation did not acquire its bounded gate.");
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!leases.Contains(lease))throw new ObjectDisposedException(nameof(Lease));
            if(!cleanup)ThrowIfUnavailable();
            return new Operation(this);
        }
        catch {Monitor.Exit(gate);throw;}
    }

    internal IDisposable EnterRender()
    {
        if(!Monitor.IsEntered(gate))throw new InvalidOperationException("GPU rendering requires the source operation gate.");
        // Only hold the native context lock across render commands. StartCapture can synchronously
        // wait for another WGC thread to acquire this lock, so native capture setup must be outside it.
        multithread.Enter();return new RenderOperation(multithread);
    }

    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;
            if(leases.Count!=0)throw new InvalidOperationException("Dispose all capture sources before their shared graphics device.");
            disposed=true;retired=true;
            Context?.ClearState();Context?.Flush();
            multithread?.Dispose();Context?.Dispose();Device?.Dispose();
        }
    }

    internal sealed class Lease(CaptureGraphicsDevice owner,Action revokeInput) : IDisposable
    {
        internal void RevokeInput()=>revokeInput();
        internal IDisposable Enter(bool cleanup=false)=>owner.Enter(this,cleanup);
        public void Dispose(){lock(owner.gate){if(owner.leases.Remove(this))RevokeInput();}}
    }
    private sealed class Operation(CaptureGraphicsDevice owner) : IDisposable
    {
        private bool disposed;
        public void Dispose(){if(disposed)return;disposed=true;Monitor.Exit(owner.gate);}
    }
    private sealed class RenderOperation(ID3D11Multithread multithread) : IDisposable
    {
        private bool disposed;
        public void Dispose(){if(disposed)return;disposed=true;multithread.Leave();}
    }
}
