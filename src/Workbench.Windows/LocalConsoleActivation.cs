using System.Runtime.InteropServices;

namespace Workbench.Windows;

// One explicit connection action, never a focus-stealing retry loop. The caller must still
// validate the fresh native environment and displayed scene before installing device hooks.
public static class LocalConsoleActivation
{
    public static bool Request(OwnedWindowScene scene)
    {
        if(!WindowsInputEnvironment.InteractiveDesktop())return false;
        var candidates=scene.Nodes.Select(n=>n.Window).Where(w=>w.Enabled && w.Visible && !w.Minimized && !w.Cloaked).ToArray();
        var target=candidates.FirstOrDefault(w=>w.Owner==0) ?? candidates.FirstOrDefault(w=>
            w.ClassName!="#32768" && !w.ClassName.Contains("tooltip",StringComparison.OrdinalIgnoreCase));
        return target is not null && SetForegroundWindow((nint)target.Handle);
    }
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
}
