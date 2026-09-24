using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Workbench.Windows;

// Same-desktop, single-user experiment: a dedicated browser stays visually on top
// while Windows hit-testing passes physical input to the real NX windows beneath it.
// There is no synthetic NX UI or background PostMessage input path.
internal sealed class DualNxOverlay(WindowInfo left, WindowInfo right) : IDisposable
{
    private const int ExStyle = -20, Layered = 0x80000, Transparent = 0x20;
    private const int SwpNoActivate = 0x10, SwpNoMove = 0x02, SwpNoSize = 0x01, SwpShowWindow = 0x40;
    private readonly object gate = new();
    private Session? session;
    private string? lastReason;

    public object Status()
    {
        lock (gate)
        {
            var browser = FindBrowser();
            return new
            {
                supported = (browser != 0 && GetForegroundWindow() == browser) || session is not null,
                active = session is not null,
                side = session?.Side ?? "",
                focusReady = session is { } current &&
                    (current.Side == "left" && GetForegroundWindow() == (nint)left.Handle ||
                     current.Side == "right" && GetForegroundWindow() == (nint)right.Handle),
                reason = session is null ? lastReason ?? (browser == 0
                    ? "请在独立 Chrome / Edge 应用窗口打开 127.0.0.1:8093；内置浏览器和普通标签页不支持跨栏接管。"
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
            if (browser == 0) throw new InvalidOperationException("未找到独立 Chrome / Edge 应用窗口；不能修改内置浏览器。");
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
                for (int i = 0; i < 2; i++)
                {
                    nint nx = (nint)(i == 0 ? left.Handle : right.Handle);
                    if (!WindowAt(nx, rects[i]))
                        throw new InvalidOperationException("视频栏下方未命中对应 NX 原生窗口；已撤销接管。");
                }
                session = next;
                _ = Task.Run(() => Monitor(next));
                lastReason = null;
                return "已对齐两个真实 NX 窗口。鼠标可自由跨栏；键盘焦点未切换时单击目标画面。按 F12 退出并恢复窗口。";
            }
            catch
            {
                Restore(next);
                throw;
            }
        }
    }

    private void Monitor(Session current)
    {
        string reason = "F12 已退出，窗口布局已恢复。";
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
                if (ReferenceEquals(session, current)) { Restore(current); session = null; lastReason = reason; }
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
    private static bool WindowAt(nint hwnd, Rect rect)
    {
        if (!AtExpectedRect(hwnd, rect)) return false;
        var center = new Point { X = rect.Left + (rect.Right - rect.Left) / 2,
            Y = rect.Top + (rect.Bottom - rect.Top) / 2 };
        nint hit = WindowFromPhysicalPoint(center);
        return hit != 0 && GetAncestor(hit, 2) == hwnd;
    }
    private void Restore(Session current)
    {
        if (IsWindow(current.Browser))
        {
            SetWindowLongPtrW(current.Browser, ExStyle, current.OriginalStyle);
            SetWindowPos(current.Browser, current.BrowserTopmost ? -1 : -2, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        if (IsWindow((nint)left.Handle)) { var p = current.LeftPlacement; SetWindowPlacement((nint)left.Handle, ref p); }
        if (IsWindow((nint)right.Handle)) { var p = current.RightPlacement; SetWindowPlacement((nint)right.Handle, ref p); }
    }
    public void Dispose() { lock (gate) { if (session is { } s) { Restore(s); session = null; } } }

    private sealed class Session(nint browser, nint originalStyle, bool browserTopmost,
        WindowPlacement leftPlacement, WindowPlacement rightPlacement, Rect[] rects)
    {
        public nint Browser { get; } = browser;
        public nint OriginalStyle { get; } = originalStyle;
        public bool BrowserTopmost { get; } = browserTopmost;
        public WindowPlacement LeftPlacement { get; } = leftPlacement;
        public WindowPlacement RightPlacement { get; } = rightPlacement;
        public Rect[] Rects { get; } = rects;
        public Point Origin { get; } = ClientOrigin(browser);
        public int ClientWidth { get; } = ClientSize(browser).Right;
        public int ClientHeight { get; } = ClientSize(browser).Bottom;
        public volatile string Side = "";
    }
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
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint code);
}

internal sealed record PaneRect(double X, double Y, double Width, double Height);
internal sealed record Viewport(double Width, double Height);
internal sealed record OverlayRequest(PaneRect[] Panes, Viewport Viewport);
