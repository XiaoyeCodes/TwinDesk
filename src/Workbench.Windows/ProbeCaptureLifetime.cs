using Vortice.MediaFoundation;

namespace Workbench.Windows;

/// <summary>One selected application's capture outlives individual encoder/browser connections.</summary>
public sealed class ProbeCaptureLifetime : IDisposable
{
    private readonly object gate=new();
    private readonly CaptureGraphicsDevice graphics;
    private readonly WindowInfo target;
    private readonly int width,height;
    private readonly bool bgraOnly;
    private WgcOwnedNv12Source? source;
    private Lease? active;
    private bool disposed;
    public ProbeCaptureLifetime(CaptureGraphicsDevice graphics,WindowInfo target,int width,int height,bool bgraOnly=false)
    {
        this.graphics=graphics;this.target=target;this.width=width;this.height=height;this.bgraOnly=bgraOnly;
        MediaFactory.MFStartup().CheckError();
    }
    public Lease Rent()
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(active is not null)throw new InvalidOperationException("Capture already has an active encoder lease.");
            source??=new WgcOwnedNv12Source(graphics,target,width,height,bgraOnly);
            source.RefreshForNewStream();
            return active=new Lease(this,source);
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;
            if(active is not null)throw new InvalidOperationException("End the encoder before disposing application capture.");
            disposed=true;
            try {source?.Dispose();}
            finally {MediaFactory.MFShutdown().CheckError();}
        }
    }
    public sealed class Lease : IProbeFrameSource,IBgraProbeFrameSource
    {
        private readonly ProbeCaptureLifetime owner;
        public WgcOwnedNv12Source Source {get;}
        private bool disposed;
        internal Lease(ProbeCaptureLifetime owner,WgcOwnedNv12Source source){this.owner=owner;Source=source;}
        public string Description=>Source.Description;
        public IMFDXGIDeviceManager DeviceManager=>Source.DeviceManager;
        public ProbeSceneConfig? LastSampleScene=>Source.LastSampleScene;
        public IMFSample? TryGetSample(){lock(owner.gate){RequireActive();return Source.TryGetSample();}}
        public BgraProbeFrame? TryGetBgraFrame(){lock(owner.gate){RequireActive();return Source.TryGetBgraFrame();}}
        private void RequireActive(){ObjectDisposedException.ThrowIf(disposed,this);if(!ReferenceEquals(owner.active,this))throw new InvalidOperationException("Expired encoder lease.");}
        public void Dispose()
        {
            lock(owner.gate)
            {
                if(disposed)return;disposed=true;
                if(ReferenceEquals(owner.active,this)){Source.InputBindings.Freeze();owner.active=null;}
            }
        }
    }
}
