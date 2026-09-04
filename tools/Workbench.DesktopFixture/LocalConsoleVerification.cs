using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Workbench.Windows;

namespace Workbench.DesktopFixture;

internal static class LocalConsoleVerification
{
    public static int Run(string directory)
    {
        string path=Path.GetFullPath(directory);
        if(Directory.Exists(path)||File.Exists(path))throw new InvalidOperationException("New evidence directory required.");
        Directory.CreateDirectory(path);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form=new Form {Text="Local console native verification — own fixture only",Width=640,Height=180};
        var label=new Label {Dock=DockStyle.Top,Height=65,Text="Finite native test: injected events must pass through; watchdog must unhook.\nNo physical-input or NX acceptance is claimed."};
        var text=new TextBox {Dock=DockStyle.Bottom};form.Controls.Add(label);form.Controls.Add(text);
        int downs=0,ups=0,exit=1;text.KeyDown+=(_,_)=>downs++;text.KeyUp+=(_,_)=>ups++;
        form.Shown+=async (_,_)=>
        {
            try
            {
                text.Focus();
                var focusWait=Stopwatch.StartNew();
                while(GetForegroundWindow()!=form.Handle && focusWait.ElapsedMilliseconds<60000)await Task.Delay(100);
                await Task.Delay(250);
                if(GetForegroundWindow()!=form.Handle)throw new InvalidOperationException("Own fixture must be foreground.");
                long handle=form.Handle;string? stopped=null;
                using var bridge=new LocalConsoleBridge([handle],reason=>Volatile.Write(ref stopped,reason));
                var transport=new WindowsInputTransport();
                if(GetForegroundWindow()!=form.Handle)throw new InvalidOperationException("Own fixture focus changed.");
                uint sent=0;
                try { sent=transport.Send([NativeInputEvents.PhysicalKey("KeyA",false),NativeInputEvents.PhysicalKey("KeyA",true)]); }
                finally {transport.Send([NativeInputEvents.PhysicalKey("KeyA",true)]);}
                await Task.Delay(120);
                long injected=bridge.IgnoredInjected,physical=bridge.PhysicalEvents;int forwarded=bridge.Drain().Length;
                var clock=Stopwatch.StartNew();
                while(bridge.Active && clock.ElapsedMilliseconds<1600)await Task.Delay(30);
                bool released=(GetAsyncKeyState(0x41)&0x8000)==0;
                bool pass=sent==2 && injected>=2 && physical==0 && forwarded==0 && downs>=1 && ups>=1 &&
                    released && !bridge.Active && stopped=="LOCAL_TARGET_OR_HEARTBEAT_LOST";
                bridge.Dispose();
                File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new {
                    time=DateTimeOffset.Now,status=pass?"PASS":"FAIL",scope="Own Windows fixture: native hooks installed, SendInput excluded from forwarding, watchdog stops. No human device/NX acceptance.",
                    sent,injected,physical,forwarded,downs,ups,released,stopped,watchdogObservedMs=clock.ElapsedMilliseconds,
                    binaries=new[]{typeof(LocalConsoleBridge).Assembly.Location,typeof(LocalConsoleVerification).Assembly.Location}.Select(file=>new {
                        file,sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))})
                },new JsonSerializerOptions {WriteIndented=true}));
                exit=pass?0:1;
            }
            catch(Exception e){File.WriteAllText(Path.Combine(path,"failure.txt"),e.ToString());}
            finally{form.Close();}
        };
        Application.Run(form);return exit;
    }
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
}
