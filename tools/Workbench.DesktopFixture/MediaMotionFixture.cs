using System.Diagnostics;

namespace Workbench.DesktopFixture;

// A real changing HWND for WGC/media scheduling measurements. It is never an NX/TIA substitute.
internal static class MediaMotionFixture
{
    public const string Title="TwinDesk LC05 motion fixture — NOT NX / TIA";

    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var area=Screen.PrimaryScreen!.WorkingArea;
        using var window=new MotionWindow
        {
            Text=Title,StartPosition=FormStartPosition.Manual,
            Bounds=new Rectangle(area.Left+48,area.Top+48,Math.Min(960,area.Width-96),Math.Min(600,area.Height-96))
        };
        // WinForms timers are rounded by the desktop timer quantum; 16 ms yields
        // roughly one repaint per two 15.6 ms ticks on this test host.
        using var timer=new System.Windows.Forms.Timer {Interval=16};
        var watch=Stopwatch.StartNew();
        timer.Tick+=(_,_)=>
        {
            if(watch.Elapsed>=TimeSpan.FromSeconds(90)){timer.Stop();window.Close();return;}
            window.AdvanceFrame();
            window.Invalidate();
        };
        window.Shown+=(_,_)=>timer.Start();
        window.FormClosed+=(_,_)=>timer.Stop();
        Application.Run(window);
        Console.WriteLine($"LC05 fixture rendered {window.FrameCount()} frames in {watch.Elapsed.TotalSeconds:F2} s; window only, not NX/TIA.");
        return 0;
    }

    private sealed class MotionWindow:Form
    {
        private int frame;
        public void AdvanceFrame()=>frame++;
        public int FrameCount()=>frame;
        public MotionWindow()
        {
            DoubleBuffered=true;
            BackColor=Color.FromArgb(18,32,44);
            ForeColor=Color.White;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var canvas=ClientRectangle;
            int travel=Math.Max(1,canvas.Width-96);
            int x=frame*17%travel;
            e.Graphics.FillRectangle(Brushes.Orange,x,Math.Max(52,canvas.Height/2-30),72,60);
            e.Graphics.DrawString($"LC05 real HWND / frame {frame}",Font,Brushes.White,16,16);
        }
    }
}
