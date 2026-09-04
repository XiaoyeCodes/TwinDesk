namespace Workbench.DesktopFixture;

// Finite, visibly labeled native scene generator. Never injects input or touches NX/TIA.
internal static class MediaSceneFixture
{
    public const string Title = "TwinDesk SC03 media scene fixture — NOT NX / TIA";
    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var root=new Form {Text=Title,ClientSize=new(900,540),StartPosition=FormStartPosition.CenterScreen,BackColor=Color.DarkBlue};
        root.Controls.Add(new Label {AutoSize=true,ForeColor=Color.White,Location=new(24,24),
            Text="SC03 · native owned-window cycle every 700 ms · no injected input · exits after 120 seconds"});
        using var popup=new Form {Text="SC03 actual owned popup",ClientSize=new(320,160),BackColor=Color.DarkOrange,
            StartPosition=FormStartPosition.Manual,ShowInTaskbar=false};
        popup.Controls.Add(new Label {AutoSize=true,Location=new(20,30),Text="Real WinForms window / WGC scene node\nNot NX or TIA"});
        using var tick=new System.Windows.Forms.Timer {Interval=700};
        int cycles=0;
        tick.Tick+=(_,_)=>
        {
            if(++cycles>=170){root.Close();return;}
            if(popup.Visible)popup.Hide();
            else {popup.Location=new(root.Left+180,root.Top+160);popup.Show(root);}
        };
        root.Shown+=(_,_)=>tick.Start();
        Application.Run(root);
        return 0;
    }
}
