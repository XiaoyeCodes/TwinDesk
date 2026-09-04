namespace Workbench.Windows;

public sealed record SceneNode(WindowInfo Window, WindowBounds Destination);

/// <summary>Immutable, bounded layout for the locally selected root's same-process owned windows.</summary>
public sealed record OwnedWindowScene(WindowBounds Bounds, IReadOnlyList<SceneNode> Nodes)
{
    public const int MaximumNodes = 8;
    public const long MaximumPixels = 16_777_216;

    public static bool SameIdentity(WindowInfo a, WindowInfo b) => a.Handle == b.Handle && a.ProcessId == b.ProcessId
        && a.ProcessStartedAtUtc == b.ProcessStartedAtUtc && a.SessionId == b.SessionId;

    public static IReadOnlyList<WindowInfo> Select(WindowInfo root, IReadOnlyList<WindowInfo> windows)
    {
        var candidates = windows.Where(w => w.ProcessId == root.ProcessId && w.ProcessStartedAtUtc == root.ProcessStartedAtUtc
            && w.SessionId == root.SessionId).ToArray();
        var byHandle = new Dictionary<long, WindowInfo>();
        foreach (var window in candidates)
            if (!byHandle.TryAdd(window.Handle, window)) throw new InvalidDataException("Duplicate native window identity.");
        if (!byHandle.TryGetValue(root.Handle, out var current) || !SameIdentity(root, current)
            || !current.Visible || current.Minimized || current.Cloaked)
            throw new InvalidOperationException("Root window is closed, minimized, cloaked or has changed identity.");
        var selected = new List<WindowInfo> { current };
        foreach (var window in candidates)
        {
            if (window.Handle == root.Handle || !window.Visible || window.Minimized || window.Cloaked
                || window.ClassName.Equals("SysShadow", StringComparison.OrdinalIgnoreCase)) continue;
            var visited = new HashSet<long> { window.Handle };
            long owner = window.Owner;
            while (owner != 0)
            {
                if (!visited.Add(owner)) throw new InvalidDataException("Cyclic owner chain; scene ownership uncertain.");
                if (owner == root.Handle) { selected.Add(window); break; }
                if (!byHandle.TryGetValue(owner, out var parent)) break;
                owner = parent.Owner;
            }
        }
        if (selected.Count > MaximumNodes) throw new InvalidDataException("Owned scene exceeds node budget; no nodes silently omitted.");
        // EnumWindows provides top-level order. Store only relative order, not unrelated windows' ranks.
        return Array.AsReadOnly(selected.OrderByDescending(w => w.ZOrder).ThenBy(w => w.Handle).ToArray());
    }

    public static OwnedWindowScene Arrange(IReadOnlyList<WindowInfo> backToFront)
    {
        if (backToFront.Count is < 1 or > MaximumNodes) throw new InvalidDataException("Invalid scene node count.");
        long area = 0, left = long.MaxValue, top = long.MaxValue, right = long.MinValue, bottom = long.MinValue;
        foreach (var window in backToFront)
        {
            var b = window.CaptureBounds;
            ValidateSize(b.Width, b.Height);
            area = checked(area + (long)b.Width * b.Height);
            left = Math.Min(left, b.X); top = Math.Min(top, b.Y);
            right = Math.Max(right, (long)b.X + b.Width); bottom = Math.Max(bottom, (long)b.Y + b.Height);
        }
        if (area > MaximumPixels) throw new InvalidDataException("Total source texture pixel budget exceeded.");
        if (right > int.MaxValue || bottom > int.MaxValue) throw new InvalidDataException("Native coordinates overflow.");
        ValidateSize(right-left, bottom-top);
        var bounds = new WindowBounds((int)left, (int)top, (int)(right-left), (int)(bottom-top));
        // Keep the native convention: rank 0 is frontmost, although the node array is back-to-front.
        // Otherwise Select(Arrange(...).Nodes) reverses every multi-window scene during input validation.
        var nodes = backToFront.Select((w, index) => new SceneNode(w with { ZOrder = backToFront.Count - 1 - index },
            new(w.CaptureBounds.X-bounds.X, w.CaptureBounds.Y-bounds.Y, w.CaptureBounds.Width, w.CaptureBounds.Height))).ToArray();
        return new(bounds, Array.AsReadOnly(nodes));
    }

    public bool SameGeometry(OwnedWindowScene other) => Bounds == other.Bounds && Nodes.Count == other.Nodes.Count
        && Nodes.Zip(other.Nodes).All(pair => SameIdentity(pair.First.Window, pair.Second.Window)
            && pair.First.Destination == pair.Second.Destination && pair.First.Window.Enabled == pair.Second.Window.Enabled
            && pair.First.Window.Owner == pair.Second.Window.Owner && pair.First.Window.Dpi == pair.Second.Window.Dpi
            && pair.First.Window.BindingGeneration == pair.Second.Window.BindingGeneration
            && pair.First.Window.Layered == pair.Second.Window.Layered);

    public static WindowBounds Letterbox(WindowBounds scene, int outputWidth, int outputHeight)
    {
        ValidateSize(scene.Width, scene.Height); ValidateSize(outputWidth, outputHeight);
        double scale = Math.Min((double)outputWidth/scene.Width, (double)outputHeight/scene.Height);
        int width = Math.Max(1, (int)Math.Round(scene.Width*scale)), height = Math.Max(1, (int)Math.Round(scene.Height*scale));
        return new((outputWidth-width)/2, (outputHeight-height)/2, width, height);
    }

    private static void ValidateSize(long width, long height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || checked(width*height) > MaximumPixels)
            throw new InvalidDataException("Invalid or over-budget scene size.");
    }
}

// Only public, non-native geometry goes on the wire. Native nodes stay in local evidence reports.
public sealed record ProbeSceneConfig(uint Version, int Width, int Height, WindowBounds ContentRect, int NodeCount)
{
    // Probe-only dimensions for decoded-pixel diagnostics; no desktop origin is exposed.
    public int SourceWidth {get;init;}
    public int SourceHeight {get;init;}
}

/// <summary>Associates asynchronous encoded output with the scene captured at input, not CurrentScene.</summary>
public sealed class FrameSceneLedger(int capacity)
{
    private readonly Dictionary<long, ProbeSceneConfig?> pending = new();
    private long lastTime = -1;
    public int Count => pending.Count;

    public void Add(long timestamp100Ns, ProbeSceneConfig? scene)
    {
        if (capacity is < 1 or > 256 || pending.Count >= capacity) throw new InvalidDataException("Frame metadata budget exceeded.");
        if (timestamp100Ns < 0 || timestamp100Ns <= lastTime || timestamp100Ns/10 <= lastTime/10 && lastTime >= 0)
            throw new InvalidDataException("Frame timestamps must be unique at microsecond precision.");
        if (scene is not null && (scene.Version == 0 || scene.NodeCount is < 1 or > OwnedWindowScene.MaximumNodes))
            throw new InvalidDataException("Invalid captured scene metadata.");
        pending.Add(timestamp100Ns, scene);
        lastTime = timestamp100Ns;
    }

    public ProbeSceneConfig? Take(long timestamp100Ns)
    {
        if (!pending.Remove(timestamp100Ns, out var scene)) throw new InvalidDataException("Encoded sample has no matching capture metadata.");
        return scene;
    }
}
