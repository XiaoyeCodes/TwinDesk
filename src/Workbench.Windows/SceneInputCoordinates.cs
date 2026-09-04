namespace Workbench.Windows;

public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Pure physical-pixel math. Mapping is not permission to inject at that point.</summary>
public static class SceneInputCoordinates
{
    // u/v describe the content itself, not the encoded frame or CSS canvas including its bars.
    public static ScreenPoint ToScreen(OwnedWindowScene scene, double u, double v)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!double.IsFinite(u) || !double.IsFinite(v) || u is < 0 or > 1 || v is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(u), "Coordinates must be finite and inside the displayed content.");
        var b = scene.Bounds;
        if (b.Width is < 1 or > 8192 || b.Height is < 1 or > 8192 || (long)b.Width*b.Height > OwnedWindowScene.MaximumPixels)
            throw new InvalidDataException("Invalid source geometry.");
        return new(checked(b.X+Math.Min(b.Width-1,(int)Math.Floor(u*b.Width))),
            checked(b.Y+Math.Min(b.Height-1,(int)Math.Floor(v*b.Height))));
    }

    public static bool Contains(WindowBounds bounds, ScreenPoint point) => bounds.Width > 0 && bounds.Height > 0
        && point.X >= bounds.X && point.Y >= bounds.Y
        && point.X < (long)bounds.X+bounds.Width && point.Y < (long)bounds.Y+bounds.Height;

    // Called only with a freshly validated native hit. No alpha-based or topmost-rectangle guess.
    // Native backend must also check foreground, integrity, desktop and target identity immediately before injection.
    public static bool AllowsNativeHit(OwnedWindowScene scene, ScreenPoint point, WindowInfo actualTopLevel) =>
        Contains(scene.Bounds,point) && scene.Nodes.Any(node =>
            OwnedWindowScene.SameIdentity(node.Window,actualTopLevel)
            && node.Window.BindingGeneration == actualTopLevel.BindingGeneration
            && node.Window.Owner == actualTopLevel.Owner && node.Window.Dpi == actualTopLevel.Dpi
            && node.Window.Enabled && actualTopLevel.Enabled && actualTopLevel.Visible
            && !actualTopLevel.Minimized && !actualTopLevel.Cloaked
            && node.Window.CaptureBounds == actualTopLevel.CaptureBounds
            && Contains(node.Window.CaptureBounds,point));

    // Windows absolute mouse coordinates include the virtual desktop; never assume a (0,0) primary screen.
    public static ScreenPoint ToAbsolute(ScreenPoint point, WindowBounds virtualDesktop)
    {
        if (!Contains(virtualDesktop,point)) throw new ArgumentOutOfRangeException(nameof(point));
        static int Axis(int p,int origin,int size) => size == 1 ? 0
            : checked((int)Math.Round(((long)p-origin)*65535d/(size-1),MidpointRounding.AwayFromZero));
        return new(Axis(point.X,virtualDesktop.X,virtualDesktop.Width),Axis(point.Y,virtualDesktop.Y,virtualDesktop.Height));
    }
}
