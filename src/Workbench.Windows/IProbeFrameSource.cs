using Vortice.MediaFoundation;

namespace Workbench.Windows;

public interface IProbeFrameSource : IDisposable
{
    string Description { get; }
    IMFDXGIDeviceManager DeviceManager { get; }
    // Read synchronously immediately after TryGetSample; encoder output must use a timestamp ledger.
    ProbeSceneConfig? LastSampleScene => null;
    // Ownership of a returned sample transfers to caller; null means no new capture, not source failure.
    IMFSample? TryGetSample();
}

public interface IBgraProbeFrameSource : IDisposable
{
    BgraProbeFrame? TryGetBgraFrame();
}
