namespace GpuSwitcher.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? screenshot = Arg(args, "--screenshot");
        using var single = new Mutex(true, @"Local\GpuSwitcher.App", out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\GpuSwitcher.Show");
        if (!first && screenshot == null)
        {
            showEvent.Set();   // bring the running instance forward
            return 0;
        }

        // WinForms configuration must come before anything that may create a window (SetColorMode can).
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }

        // A screenshot run may coexist with the real tray instance: it must only observe.
        var engine = new RoutingEngine(observeOnly: screenshot != null);
        engine.Start();
        using var form = new MainForm(engine, startHidden: args.Contains("--tray") && screenshot == null);

        if (screenshot != null)
        {
            form.CaptureAndExit(screenshot, Arg(args, "--tab"), TimeSpan.FromSeconds(4));
            Application.Run(form);
            engine.Dispose();
            return 0;
        }

        var waiter = new Thread(() =>
        {
            while (showEvent.WaitOne())
                form.BeginInvoke(form.ShowFromTray);
        }) { IsBackground = true, Name = "show-signal" };
        waiter.Start();

        Application.Run(form);
        engine.Dispose();
        return 0;
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
