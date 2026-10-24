using System.Collections.Concurrent;
using System.ServiceProcess;
using GpuSwitcher;

if (args.Contains("--console"))
{
    // Debug run in an elevated console: same guard, log to stdout.
    using var guard = new LaunchGuard();
    guard.Log += Console.WriteLine;
    guard.Start();
    Console.WriteLine("boot guard running in console — Ctrl+C to stop");
    var done = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
    done.Wait();
    Console.WriteLine($"routed {guard.Routed}, hooked {guard.Hooked}, ETW latency {guard.EtwLatencyMs:0.0} ms");
    return;
}

ServiceBase.Run(new BootGuardHost());

sealed class BootGuardHost : ServiceBase
{
    LaunchGuard? _guard;
    ServiceLog? _log;
    Timer? _stats;

    public BootGuardHost()
    {
        ServiceName = BootGuardService.Name;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _log = new ServiceLog(Path.Combine(AppContext.BaseDirectory, "logs", "service.log"));
        _log.Add($"{DateTime.Now:HH:mm:ss.fff} starting v{AppInfo.Version} (pid {Environment.ProcessId})");
        _guard = new LaunchGuard();
        _guard.Log += _log.Add;
        _guard.Start();
        _stats = new Timer(_ => _log.Add($"{DateTime.Now:HH:mm:ss.fff} stats: routed {_guard.Routed}, hooked {_guard.Hooked}, ETW latency {_guard.EtwLatencyMs:0.0} ms"),
            null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        // Service (re)started while users are already logged on: bring their tray app up too.
        ThreadPool.QueueUserWorkItem(_ => { foreach (var s in UserSessions.ActiveSessions()) LaunchTray(s, "already logged on"); });
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (change.Reason != SessionChangeReason.SessionLogon) return;
        _guard?.WarmUp((uint)change.SessionId);   // before the user's first app starts
        ThreadPool.QueueUserWorkItem(_ => LaunchTray((uint)change.SessionId, "logon"));
    }

    void LaunchTray(uint session, string why)
    {
        try
        {
            if (!UserSessions.WantsTrayAtLogon(session) || UserSessions.IsRunningInSession("GpuSwitcher", session)) return;
            var pid = UserSessions.Launch(session, Path.Combine(AppContext.BaseDirectory, "GpuSwitcher.exe"), "--tray");
            _log?.Add($"{DateTime.Now:HH:mm:ss.fff} tray app started in session {session} ({why}), pid {pid}");
        }
        catch (Exception ex) { _log?.Add($"{DateTime.Now:HH:mm:ss.fff} tray launch in session {session} failed: {ex.Message}"); }
    }

    protected override void OnStop()
    {
        _stats?.Dispose();
        _guard?.Dispose();
        _log?.Add($"{DateTime.Now:HH:mm:ss.fff} stopped");
        _log?.Dispose();
    }

    protected override void OnShutdown() => OnStop();
}

/// <summary>Log writer on its own thread: lines can be produced while impersonating a user, but the file
/// (in the admin-only install folder) is always written as SYSTEM.</summary>
sealed class ServiceLog : IDisposable
{
    readonly BlockingCollection<string> _lines = new();
    readonly Thread _writer;

    public ServiceLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new Thread(() =>
        {
            foreach (var line in _lines.GetConsumingEnumerable())
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2_000_000) File.Move(path, path + ".1", true);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { /* never let logging take the guard down */ }
            }
        }) { IsBackground = true, Name = "service-log" };
        _writer.Start();
    }

    public void Add(string line) { if (!_lines.IsAddingCompleted) _lines.Add(line); }

    public void Dispose()
    {
        _lines.CompleteAdding();
        _writer.Join(2000);
    }
}
