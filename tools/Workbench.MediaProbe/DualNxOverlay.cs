using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Workbench.Windows;

// Same-desktop, single-user experiment: a dedicated browser stays visually on top
// while Windows hit-testing passes physical input to the real NX windows beneath it.
// There is no synthetic NX UI or background PostMessage input path.
internal sealed class DualNxOverlay(WindowInfo left, WindowInfo right) : IDisposable
{
    private const string BrowserInstruction = "当前是普通 Edge/Chrome 标签页或内置浏览器，只能看画面。先关闭此预览页，再按 Win+R 运行 msedge.exe --app=http://127.0.0.1:8093/ --new-window；独立应用窗口顶部没有标签栏和地址栏。";
    private const int ExStyle = -20, Layered = 0x80000, Transparent = 0x20;
    private const int SwpNoActivate = 0x10, SwpNoMove = 0x02, SwpNoSize = 0x01, SwpShowWindow = 0x40;
    private const int SwpKeepGeometry = SwpNoMove | SwpNoSize | SwpNoActivate;
    private const int MaxOccluders = 8;
    private readonly object gate = new();
    private Session? session;
    private string? lastReason;
    private int lastOccluderCount;
    private bool? lastRestoreVerified;

    public object Status()
    {
        lock (gate)
        {
            var browser = FindBrowser();
            return new
            {
                supported = (browser != 0 && GetForegroundWindow() == browser) || session is not null,
                active = session is not null,
                minimizedOccluders = session?.Occluders.Count ?? lastOccluderCount,
                lastRestoreVerified,
                side = session?.Side ?? "",
                focusReady = session is { } current &&
                    (current.Side == "left" && GetForegroundWindow() == (nint)left.Handle ||
                     current.Side == "right" && GetForegroundWindow() == (nint)right.Handle),
                reason = session is null ? lastReason ?? (browser == 0
                    ? BrowserInstruction
                    : GetForegroundWindow() != browser
                        ? "请切换到独立 Chrome / Edge 应用窗口后开始跨栏控制。"
                        : "两个 NX 实例已绑定。点击开始后，浏览器画面留在最前，实体键鼠直接进入下方真实 NX；F12 退出。")
                    : "跨栏控制已启动。鼠标可跨越左右窗口；键盘焦点未切换时，单击目标画面。F12 退出。"
            };
        }
    }

    public void RecordError(string error)
    {
        lock (gate) { if (session is null) lastReason = "接管未启动：" + error; }
    }

    public string Start(OverlayRequest request)
    {
        lock (gate)
        {
            if (session is not null) throw new InvalidOperationException("跨栏控制已在运行。");
            if (request.Panes is not { Length: 2 } || request.Viewport is null) throw new InvalidOperationException("缺少双栏几何。");
            var browser = FindBrowser();
            if (browser == 0) throw new InvalidOperationException(BrowserInstruction);
            if (GetForegroundWindow() != browser) throw new InvalidOperationException("请在独立浏览器窗口内点击连接。");
            if (!WindowsInputEnvironment.InteractiveDesktop()) throw new InvalidOperationException("桌面已锁定或不是交互桌面。");
            VerifyNx();
            if (!GetClientRect(browser, out var client) || client.Right <= 0 || client.Bottom <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // Edge app windows draw their title bar inside the Win32 client area.
            // The page viewport begins below that bar. Derive its physical inset
            // from the client height and browser-reported innerHeight.
            double scale = client.Right / request.Viewport.Width;
            double topInset = client.Bottom - request.Viewport.Height * scale;
            if (!double.IsFinite(scale) || !double.IsFinite(topInset) || scale is < .75 or > 4 ||
                topInset is < -2 or > 120)
                throw new InvalidOperationException(
                    $"独立浏览器窗口与网页视口比例不匹配（scale={scale:F3}, topInset={topInset:F1}px）。");
            var origin = new Point();
            if (!ClientToScreen(browser, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
            origin.Y += (int)Math.Round(Math.Max(0, topInset));
            var rects = request.Panes.Select(p => ToScreen(p, request.Viewport, origin, scale, scale)).ToArray();
            if (rects[0].Right > rects[1].Left || Math.Abs(rects[0].Top - rects[1].Top) > 3 ||
                rects.Any(r => r.Left < origin.X || r.Top < origin.Y || r.Right > origin.X + client.Right ||
                    r.Bottom > origin.Y + (int)Math.Round(request.Viewport.Height * scale)))
                throw new InvalidOperationException("双栏位置不在浏览器内容区，或左右栏交叠。");
            var leftPlacement = Placement((nint)left.Handle);
            var rightPlacement = Placement((nint)right.Handle);
            nint originalStyle = GetWindowLongPtrW(browser, ExStyle);
            bool browserTopmost = ((long)originalStyle & 8) != 0;
            var next = new Session(browser, originalStyle, browserTopmost, leftPlacement, rightPlacement, rects);
            try
            {
                // Align each native NX window with the video rectangle before
                // making the browser click-through. F12/close/lock restores both.
                PlaceNx((nint)left.Handle, rects[0]);
                PlaceNx((nint)right.Handle, rects[1]);
                Marshal.GetLastWin32Error();
                SetLastError(0);
                nint prior = SetWindowLongPtrW(browser, ExStyle, (nint)((long)originalStyle | Layered | Transparent));
                if (prior == 0 && Marshal.GetLastWin32Error() != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!SetLayeredWindowAttributes(browser, 0, 255, 2) ||
                    !SetWindowPos(browser, -1, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                ResolveOcclusion(next);
                session = next;
                _ = Task.Run(() => Monitor(next));
                lastOccluderCount = next.Occluders.Count;
                lastRestoreVerified = null;
                lastReason = null;
                return $"已对齐两个真实 NX 窗口；临时最小化 {next.Occluders.Count} 个遮挡窗口。鼠标可自由跨栏；按 F12 退出并恢复原窗口。";
            }
            catch
            {
                lastOccluderCount = next.Occluders.Count;
                lastRestoreVerified = Restore(next);
                throw;
            }
        }
    }

    private void Monitor(Session current)
    {
        string reason = "F12 已退出，窗口布局已恢复。";
        int iterations = 0;
        try
        {
            while (true)
            {
                if ((GetAsyncKeyState(0x7B) & 0x8000) != 0) break;
                if (!IsWindow(current.Browser) || !IsWindow((nint)left.Handle) || !IsWindow((nint)right.Handle))
                { reason = "绑定窗口已关闭，接管已停止。"; break; }
                if (!WindowsInputEnvironment.InteractiveDesktop() || IsIconic((nint)left.Handle) || IsIconic((nint)right.Handle))
                { reason = "桌面锁定或 NX 最小化，接管已停止。"; break; }
                var origin = new Point();
                if (!GetClientRect(current.Browser, out var client) || !ClientToScreen(current.Browser, ref origin) ||
                    origin.X != current.Origin.X || origin.Y != current.Origin.Y ||
                    client.Right != current.ClientWidth || client.Bottom != current.ClientHeight)
                { reason = "浏览器窗口位置或尺寸改变，接管已停止。"; break; }
                if (!AtExpectedRect((nint)left.Handle, current.Rects[0]) ||
                    !AtExpectedRect((nint)right.Handle, current.Rects[1]))
                { reason = "NX 窗口位置或尺寸改变，接管已停止。"; break; }
                if (++iterations % 6 == 0 && !SurfacesClear(current))
                { reason = "有其他窗口重新遮挡 NX，接管已停止并恢复原窗口。"; break; }
                if (GetCursorPos(out var cursor))
                {
                    int i = current.Rects[0].Contains(cursor) ? 0 : current.Rects[1].Contains(cursor) ? 1 : -1;
                    current.Side = i == 0 ? "left" : i == 1 ? "right" : "";
                    nint target = i == 0 ? (nint)left.Handle : i == 1 ? (nint)right.Handle : 0;
                    if (target != 0 && GetForegroundWindow() != target && !AnyMouseButtonDown())
                        SetForegroundWindow(target); // Windows may deny hover activation; native click still selects NX.
                }
                Thread.Sleep(35);
            }
        }
        catch (Exception e) { reason = "接管监视异常，已尝试恢复：" + e.Message; }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(session, current))
                {
                    lastRestoreVerified = Restore(current);
                    session = null;
                    lastReason = lastRestoreVerified == true ? reason : reason + "；部分窗口恢复未确认，请检查原桌面布局。";
                }
            }
        }
    }

    private void VerifyNx()
    {
        var live = WindowCatalog.Find("ugraf");
        foreach (var original in new[] { left, right })
        {
            var w = live.SingleOrDefault(x => x.Handle == original.Handle && x.ProcessId == original.ProcessId &&
                x.ProcessStartedAtUtc == original.ProcessStartedAtUtc && x.ExecutablePath == original.ExecutablePath);
            if (w is null || !w.Visible || w.Minimized || w.Cloaked || w.SessionId != left.SessionId)
                throw new InvalidOperationException("两个原始 NX 窗口必须仍在同一用户会话、可见且非最小化。");
        }
    }

    private static nint FindBrowser()
    {
        var matches = new List<nint>();
        foreach (var name in new[] { "chrome", "msedge" })
            foreach (var w in WindowCatalog.Find(name))
                if (string.Equals(w.Title,"双 NX 控制台",StringComparison.Ordinal) &&
                    GetAncestor((nint)w.Handle, 2) == (nint)w.Handle && !w.Minimized &&
                    w.ClassName.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal))
                    matches.Add((nint)w.Handle);
        return matches.Count == 1 ? matches[0] : 0;
    }

    private static Rect ToScreen(PaneRect pane, Viewport viewport, Point origin, double sx, double sy)
    {
        if (!double.IsFinite(pane.X) || !double.IsFinite(pane.Y) || !double.IsFinite(pane.Width) || !double.IsFinite(pane.Height) ||
            pane.X < 0 || pane.Y < 0 || pane.Width < viewport.Width * .25 || pane.Height < 200 ||
            pane.X + pane.Width > viewport.Width || pane.Y + pane.Height > viewport.Height ||
            Math.Abs(pane.Width / pane.Height - 16.0 / 9) > .08)
            throw new InvalidOperationException("视频栏几何无效；请最大化独立浏览器窗口。");
        return new Rect(origin.X + (int)Math.Round(pane.X * sx), origin.Y + (int)Math.Round(pane.Y * sy),
            origin.X + (int)Math.Round((pane.X + pane.Width) * sx), origin.Y + (int)Math.Round((pane.Y + pane.Height) * sy));
    }

    private static WindowPlacement Placement(nint hwnd)
    {
        var p = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref p)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return p;
    }
    private static void PlaceNx(nint hwnd, Rect rect)
    {
        if (!ShowWindow(hwnd, 9) && !IsWindow(hwnd)) throw new InvalidOperationException("NX window closed.");
        if (!SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoActivate | SwpShowWindow)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0 || (GetAsyncKeyState(4) & 0x8000) != 0;
    private static bool AtExpectedRect(nint hwnd, Rect expected) =>
        GetWindowRect(hwnd, out var actual) &&
        Math.Abs(actual.Left - expected.Left) <= 3 && Math.Abs(actual.Top - expected.Top) <= 3 &&
        Math.Abs(actual.Right - expected.Right) <= 3 && Math.Abs(actual.Bottom - expected.Bottom) <= 3;
    private void ResolveOcclusion(Session current)
    {
        for (int side = 0; side < 2; side++)
        {
            nint nx = (nint)(side == 0 ? left.Handle : right.Handle);
            if (!AtExpectedRect(nx, current.Rects[side]))
                throw new InvalidOperationException("NX 窗口对齐失败，已撤销接管。");
            foreach (var point in CheckPoints(current.Rects[side]))
            {
                nint hit = HitRoot(point);
                if (TargetHit(hit, side)) continue;
                // A normal window may have moved above NX while the browser was raised.
                // Try the reversible Z-order move before minimizing any other app.
                if (!SetWindowPos(nx, 0, 0, 0, 0, 0, SwpKeepGeometry))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                hit = HitRoot(point);
                while (!TargetHit(hit, side))
                {
                    if (current.Occluders.Count >= MaxOccluders)
                        throw new InvalidOperationException("遮挡窗口超过安全上限；已撤销接管。");
                    MinimizeOccluder(hit, current);
                    hit = HitRoot(point);
                }
            }
        }
    }

    private bool SurfacesClear(Session current) =>
        Enumerable.Range(0, 2).All(side => CheckPoints(current.Rects[side])
            .All(point => TargetHit(HitRoot(point), side)));

    private bool TargetHit(nint hit, int side)
    {
        if (hit == (nint)(side == 0 ? left.Handle : right.Handle)) return true;
        if (hit == 0) return false;
        GetWindowThreadProcessId(hit, out uint pid);
        return pid == (uint)(side == 0 ? left.ProcessId : right.ProcessId);
    }

    private void MinimizeOccluder(nint hit, Session current)
    {
        if (hit == 0 || hit == current.Browser || hit == (nint)left.Handle || hit == (nint)right.Handle)
            throw new InvalidOperationException("视频栏下方未命中目标 NX，且遮挡者不可最小化；已撤销接管。");
        if (!IsWindow(hit) || !IsWindowVisible(hit) || IsIconic(hit) || GetAncestor(hit, 2) != hit)
            throw new InvalidOperationException("遮挡窗口身份不稳定；已撤销接管。");
        GetWindowThreadProcessId(hit, out uint pid);
        if (pid == 0 || pid == (uint)left.ProcessId || pid == (uint)right.ProcessId)
            throw new InvalidOperationException("NX 关联窗口或未知窗口遮挡视频栏；不会自动最小化，已撤销接管。");
        var className = new StringBuilder(128);
        if (GetClassNameW(hit, className, className.Capacity) == 0 ||
            className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW")
            throw new InvalidOperationException("系统桌面窗口遮挡视频栏；不会自动最小化，已撤销接管。");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.SessionId != left.SessionId)
                throw new InvalidOperationException("其他用户会话窗口遮挡视频栏；已撤销接管。");
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("遮挡窗口进程已退出；已撤销接管。");
        }
        var placement = Placement(hit);
        current.Occluders.Add(new Occluder(hit, placement));
        if (!ShowWindowAsync(hit, 6))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "遮挡窗口无法最小化；已撤销接管。");
        for (int i = 0; i < 10 && !IsIconic(hit); i++) Thread.Sleep(40);
        if (!IsIconic(hit)) throw new InvalidOperationException("遮挡窗口未最小化；已撤销接管。");
    }

    private static nint HitRoot(Point point)
    {
        nint hit = WindowFromPhysicalPoint(point);
        return hit == 0 ? 0 : GetAncestor(hit, 2);
    }

    private static Point[] CheckPoints(Rect rect)
    {
        int x = Math.Clamp((rect.Right - rect.Left) / 8, 12, 80);
        int y = Math.Clamp((rect.Bottom - rect.Top) / 8, 12, 80);
        return [
            new Point { X = rect.Left + (rect.Right - rect.Left) / 2, Y = rect.Top + (rect.Bottom - rect.Top) / 2 },
            new Point { X = rect.Left + x, Y = rect.Top + y },
            new Point { X = rect.Right - x, Y = rect.Top + y },
            new Point { X = rect.Left + x, Y = rect.Bottom - y },
            new Point { X = rect.Right - x, Y = rect.Bottom - y }
        ];
    }
    private bool Restore(Session current)
    {
        bool restored = true;
        if (IsWindow(current.Browser))
        {
            SetLastError(0);
            nint prior = SetWindowLongPtrW(current.Browser, ExStyle, current.OriginalStyle);
            if (prior == 0 && Marshal.GetLastWin32Error() != 0) restored = false;
            if (!SetWindowPos(current.Browser, current.BrowserTopmost ? -1 : -2, 0, 0, 0, 0,
                SwpKeepGeometry)) restored = false;
        }
        if (!RestorePlacement((nint)left.Handle, current.LeftPlacement)) restored = false;
        if (!RestorePlacement((nint)right.Handle, current.RightPlacement)) restored = false;
        for (int i = current.Occluders.Count - 1; i >= 0; i--)
        {
            var item = current.Occluders[i];
            if (!RestorePlacement(item.Handle, item.Placement)) restored = false;
        }
        return restored;
    }
    private static bool RestorePlacement(nint hwnd, WindowPlacement original)
    {
        if (!IsWindow(hwnd)) return true;
        var placement = original;
        if (!SetWindowPlacement(hwnd, ref placement)) return false;
        for (int wait = 0; wait < 10 && IsIconic(hwnd); wait++) Thread.Sleep(40);
        return !IsIconic(hwnd);
    }
    public void Dispose() { lock (gate) { if (session is { } s) { lastRestoreVerified = Restore(s); session = null; } } }

    private sealed class Session(nint browser, nint originalStyle, bool browserTopmost,
        WindowPlacement leftPlacement, WindowPlacement rightPlacement, Rect[] rects)
    {
        public nint Browser { get; } = browser;
        public nint OriginalStyle { get; } = originalStyle;
        public bool BrowserTopmost { get; } = browserTopmost;
        public WindowPlacement LeftPlacement { get; } = leftPlacement;
        public WindowPlacement RightPlacement { get; } = rightPlacement;
        public Rect[] Rects { get; } = rects;
        public List<Occluder> Occluders { get; } = [];
        public Point Origin { get; } = ClientOrigin(browser);
        public int ClientWidth { get; } = ClientSize(browser).Right;
        public int ClientHeight { get; } = ClientSize(browser).Bottom;
        public volatile string Side = "";
    }
    private sealed record Occluder(nint Handle, WindowPlacement Placement);
    private static Point ClientOrigin(nint hwnd) { var p = new Point(); ClientToScreen(hwnd, ref p); return p; }
    private static Rect ClientSize(nint hwnd) { GetClientRect(hwnd, out var r); return r; }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect(int l, int t, int r, int b)
    {
        public int Left=l, Top=t, Right=r, Bottom=b;
        public readonly bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    {
        public int Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetClassNameW(nint hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint code);
}

internal sealed record PaneRect(double X, double Y, double Width, double Height);
internal sealed record Viewport(double Width, double Height);
internal sealed record OverlayRequest(PaneRect[] Panes, Viewport Viewport);
