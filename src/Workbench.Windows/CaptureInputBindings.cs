namespace Workbench.Windows;

public sealed record CapturedInputScene(uint Version, OwnedWindowScene Geometry);

/// <summary>
/// Thread-safe bridge from the capture owner to the input executor. Only complete composed geometry is
/// published. Lifetime tokens are invalidated by native window destruction/disposal before resources can be rebound.
/// Does not activate windows, acknowledge browser frames, or prove that the native application acted.
/// </summary>
public sealed class CaptureInputBindings
{
    private readonly object gate = new();
    private readonly Dictionary<long, Lifetime> live = new();
    private long lastGeneration;
    private CapturedInputScene? current;
    private bool stopped;

    public Lifetime Register(WindowInfo window, long generation)
    {
        lock (gate)
        {
            if (stopped || generation <= lastGeneration || live.ContainsKey(window.Handle)
                || live.Count >= OwnedWindowScene.MaximumNodes) throw new InvalidOperationException("Invalid capture binding registration.");
            lastGeneration = generation; current = null;
            var binding = new Lifetime(this, window with { BindingGeneration = generation });
            live.Add(window.Handle, binding); return binding;
        }
    }
    public void Freeze() { lock (gate) current = null; }
    public void Stop()
    {
        lock (gate) { stopped = true; current = null; foreach (var entry in live.Values) entry.Alive = false; live.Clear(); }
    }
    public CapturedInputScene? Current { get { lock (gate) return current; } }
    public bool Verify(WindowInfo window)
    {
        lock (gate) return !stopped && current is not null && Matches(window)
            && current.Geometry.Nodes.Any(node => SameBinding(node.Window, window));
    }
    public void Publish(uint version, OwnedWindowScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (gate)
        {
            if (stopped || version == 0 || scene.Nodes.Count != live.Count || scene.Nodes.Count == 0
                || scene.Nodes.Any(node => !Matches(node.Window) || !live[node.Window.Handle].HasFrame)
                || scene.Nodes.Select(node => node.Window.Handle).Distinct().Count() != scene.Nodes.Count)
                throw new InvalidOperationException("Incomplete or expired capture scene cannot permit input.");
            current = new(version, new(scene.Bounds, Array.AsReadOnly(scene.Nodes.ToArray())));
        }
    }
    private bool Matches(WindowInfo window) => live.TryGetValue(window.Handle, out var binding) && binding.Alive
        && SameBinding(binding.Identity, window);
    private static bool SameBinding(WindowInfo left, WindowInfo right) => OwnedWindowScene.SameIdentity(left, right)
        && left.BindingGeneration > 0 && left.BindingGeneration == right.BindingGeneration;

    public sealed class Lifetime : IDisposable
    {
        private readonly CaptureInputBindings registry;
        public WindowInfo Identity { get; }
        internal bool Alive = true, HasFrame;
        internal Lifetime(CaptureInputBindings registry, WindowInfo identity) { this.registry = registry; Identity = identity; }
        public void FrameObserved()
        {
            lock (registry.gate)
            {
                if (!Alive) throw new InvalidOperationException("Frame from retired capture binding.");
                HasFrame = true;
            }
        }
        public void Dispose()
        {
            lock (registry.gate)
            {
                if (!Alive) return;
                Alive = false; registry.current = null;
                if (registry.live.TryGetValue(Identity.Handle, out var entry) && ReferenceEquals(entry, this)) registry.live.Remove(Identity.Handle);
            }
        }
    }
}
