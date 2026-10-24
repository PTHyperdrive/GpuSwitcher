using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace GpuSwitcher;

/// <summary>The boot-guard brain (runs in the SYSTEM service from boot): routes and hooks every new process in every
/// user session the instant the kernel reports it, using that user's own GpuSwitcher config and registry hive.
/// Users who never ran the tray app (no config.json) are left untouched.</summary>
public sealed class LaunchGuard : IDisposable
{
    sealed class UserCtx
    {
        public required string Sid;
        public required ConfigFile File;
        public required PreferenceStore Store;
        public AppConfig Cfg = new();
        public DateTime Stamp, CheckedAt, RootsAt;
        public List<GameRoot> Roots = new();
        public Classifier Classifier = null!;
    }

    sealed class Tracked
    {
        public required ProcInfo Proc;
        public required UserCtx Ctx;
        public required string Target;
        public required Role Role;
        public required GpuAdapter Adapter;
        public required SafeAccessTokenHandle Token;
        public bool RoutedAtLaunch, Gfx, Injected;
    }

    readonly EtwProcessWatcher _etw = new();
    readonly BlockingCollection<Action> _events = new();
    readonly BlockingCollection<Tracked> _injections = new();
    readonly Dictionary<string, UserCtx?> _users = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _noConfigUntil = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Tracked> _tracked = new();
    readonly List<Thread> _threads = new();
    List<GpuAdapter> _adapters = new();
    DateTime _adaptersAt;
    Timer? _pollFallback;
    HashSet<int> _knownPids = new();
    volatile bool _stopping;

    public event Action<string>? Log;
    public bool EtwActive { get; private set; }
    public double EtwLatencyMs => _etw.AverageLatencyMs;
    public int Routed, Hooked;

    public void Start()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { /* not fatal */ }
        RefreshAdapters();
        Spawn("guard-events", ThreadPriority.Highest, () => { foreach (var a in _events.GetConsumingEnumerable()) Safe(a); });
        for (int i = 0; i < 2; i++)
            Spawn($"guard-inject-{i}", ThreadPriority.AboveNormal, () => { foreach (var t in _injections.GetConsumingEnumerable()) Safe(() => InjectNow(t)); });

        _etw.ProcessStarted += (pid, session, _) => { if (session != 0) _events.Add(() => OnStart(pid)); };
        _etw.GraphicsDllLoaded += (pid, dll) => _events.Add(() => OnGraphics(pid, dll));
        _etw.ProcessStopped += pid => _events.Add(() => OnStop(pid));
        // Load logged-on users' configs and game libraries now, so the first launch isn't the one paying for it.
        foreach (var session in UserSessions.ActiveSessions()) WarmUp(session);
        try
        {
            _etw.Start();
            EtwActive = true;
            Write($"ETW kernel-process session up ({(_etw.MillisecondFlush ? "10 ms" : "1 s")} flush). Adapters: " +
                  string.Join(", ", _adapters.Where(a => a.IsRoutable).Select(a => $"{a.ShortName} [{a.Key}]")));
        }
        catch (Exception ex)
        {
            Write("ETW unavailable, polling instead: " + ex.Message);
            _knownPids = ProcessUtil.EnumeratePids().ToHashSet();
            _pollFallback = new Timer(_ => Poll(), null, 25, 25);
        }
    }

    static SafeAccessTokenHandle? SessionToken(int session, string sid)
    {
        if (session <= 0 || !Interop.Native.WTSQueryUserToken((uint)session, out var raw)) return null;
        var token = new SafeAccessTokenHandle(raw);
        if (string.Equals(ProcessUtil.TokenSid(raw), sid, StringComparison.OrdinalIgnoreCase)) return token;
        token.Dispose();
        return null;
    }

    /// <summary>Prepares the user context of <paramref name="session"/> on the event thread (call at logon).</summary>
    public void WarmUp(uint session)
    {
        if (!Interop.Native.WTSQueryUserToken(session, out var raw)) return;
        var token = new SafeAccessTokenHandle(raw);
        var sid = ProcessUtil.TokenSid(raw);
        if (sid == null) { token.Dispose(); return; }
        _events.Add(() => { using (token) AsUser(token, () => { User(sid); }); });
    }

    void Spawn(string name, ThreadPriority prio, Action body)
    {
        var t = new Thread(() => body()) { IsBackground = true, Name = name, Priority = prio };
        _threads.Add(t);
        t.Start();
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Write("error: " + ex.Message); }
    }

    int _polling;
    void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var now = ProcessUtil.EnumeratePids().ToHashSet();
            foreach (var pid in now) if (!_knownPids.Contains(pid)) _events.Add(() => OnStart(pid));
            foreach (var pid in _knownPids) if (!now.Contains(pid)) _events.Add(() => OnStop(pid));
            _knownPids = now;
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    // ------------------------------------------------------------------ adapters & users

    void RefreshAdapters()
    {
        try { _adapters = GpuTopology.Enumerate(); } catch (Exception ex) { Write("adapter enumeration failed: " + ex.Message); }
        _adaptersAt = DateTime.UtcNow;
    }

    /// <summary>Same rules as the tray app, minus monitor knowledge: the tray app's published choices come first.</summary>
    GpuAdapter? AdapterFor(UserCtx u, Role role) =>
        RoleResolver.Resolve(_adapters, u.Cfg, knowsMonitors: false).GetValueOrDefault(role)?.Adapter;
    UserCtx? User(string sid)
    {
        if (_users.TryGetValue(sid, out var u) && u != null)
        {
            if (DateTime.UtcNow - u.CheckedAt > TimeSpan.FromMilliseconds(500))
            {
                u.CheckedAt = DateTime.UtcNow;
                if (u.File.LastWrite != u.Stamp) Reload(u);   // also picks up libraries the tray app published
            }
            return u;
        }
        if (_noConfigUntil.TryGetValue(sid, out var until) && DateTime.UtcNow < until) return null;

        var home = UserSessions.GpuSwitcherHome(sid);
        if (home == null || !File.Exists(Path.Combine(home, "config.json")))
        {
            _noConfigUntil[sid] = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
        u = new UserCtx { Sid = sid, File = new ConfigFile(home), Store = PreferenceStore.ForSid(sid) };
        Reload(u);
        DiscoverRoots(u);
        Rebuild(u);
        _users[sid] = u;
        Write($"user {sid}: config {home}, {u.Roots.Count} game libraries");
        return u;
    }

    void Reload(UserCtx u) => Adopt(u, u.File.Load());

    /// <summary>Adopt a config just read or written: keeps the change stamp and classifier in step with it.</summary>
    void Adopt(UserCtx u, AppConfig cfg)
    {
        u.Cfg = cfg;
        u.Stamp = u.File.LastWrite;
        Rebuild(u);
    }

    void DiscoverRoots(UserCtx u)
    {
        u.RootsAt = DateTime.UtcNow;
        try
        {
            using var hive = Registry.Users.OpenSubKey(u.Sid);
            u.Roots = GameLibraries.Discover(u.Cfg.ExtraGameFolders, hive);
        }
        catch { u.Roots = new(); }
    }

    void Rebuild(UserCtx u)
    {
        var roots = u.Roots.Concat(u.Cfg.ResolvedGameRoots)
                           .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        u.Classifier = new Classifier(u.Cfg, roots);
    }

    // ------------------------------------------------------------------ events

    /// <summary>Runs <paramref name="body"/> as the process owner: every touch of their profile (config, hive, event file)
    /// happens with their rights, never SYSTEM's, so a planted link in their profile cannot redirect a SYSTEM write.</summary>
    static T AsUser<T>(SafeAccessTokenHandle token, Func<T> body) => WindowsIdentity.RunImpersonated(token, body);
    static void AsUser(SafeAccessTokenHandle token, Action body) => WindowsIdentity.RunImpersonated(token, body);

    void OnStart(int pid)
    {
        if (_stopping) return;
        var p = ProcessUtil.Inspect(pid);
        if (p == null || p.IsService || p.RegistryTarget is not { } target) return;
        var sid = ProcessUtil.GetOwnerSid(pid);
        if (sid == null) return;
        // Impersonate the session user's normal token: a sandboxed process (AppContainer, low integrity) has the
        // same SID but cannot open its own profile. Runas'd processes fall back to their own token.
        var token = SessionToken(p.SessionId, sid) ?? ProcessUtil.OpenUserToken(pid);
        if (token == null) return;
        // DXGI must be asked as SYSTEM, not while impersonating.
        if (DateTime.UtcNow - _adaptersAt > TimeSpan.FromSeconds(30)) RefreshAdapters();

        Tracked? t = null;
        bool known = false;
        AsUser(token, () =>
        {
            if (User(sid) is not { } u || !u.Cfg.AutoRoute) return;
            var c = u.Classifier.Classify(target, p.ExePath);
            if (c.Role == Role.Ignore || AdapterFor(u, c.Role) is not { } adapter) return;

            u.Cfg.Owned.TryGetValue(target, out var own);
            bool overridden = u.Cfg.Overrides.ContainsKey(target);
            bool routeIt = c.Role != Role.App || overridden || own != null || (u.Cfg.RouteUnknownAtLaunch && !u.Cfg.NonGpu.Contains(target));
            bool routed = routeIt && WriteRoute(u, target, c.Role, adapter, "launch", overridden);
            if (routed) Emit(u, "ROUTE", pid, $"{p.Name} ({c.Role}) → {adapter.ShortName} at launch");

            // Games: route only. Never tracked, never opened for reading, never hooked.
            if (c.Role == Role.Game || c.IsGameLike) return;
            t = new Tracked { Proc = p, Ctx = u, Target = target, Role = c.Role, Adapter = adapter, RoutedAtLaunch = routed, Token = token };
            known = overridden || c.Role == Role.Media || (own?.Verified ?? false);
        });
        if (t == null) { token.Dispose(); return; }

        _tracked[pid] = t;
        if (known) QueueInject(t);
        // The DLL may have loaded before this event was handled (checked with SYSTEM rights: works for elevated apps too).
        if (ProcessUtil.LoadedGraphicsApi(pid) is { } api) OnGraphics(pid, api);
    }

    void OnGraphics(int pid, string dll)
    {
        if (!_tracked.TryGetValue(pid, out var t) || t.Gfx) return;
        t.Gfx = true;
        var u = t.Ctx;
        var target = t.Target;
        AsUser(t.Token, () =>
        {
            Adopt(u, u.File.UpdateIf(c =>
            {
                bool changed = c.NonGpu.Remove(target);
                if (c.Owned.TryGetValue(target, out var e) && !e.Verified) { e.Verified = true; changed = true; }
                return changed;
            }));
            if (!u.Cfg.Owned.ContainsKey(target) && WriteRoute(u, target, t.Role, t.Adapter, "learned", false))
            {
                Adopt(u, u.File.UpdateIf(c =>
                {
                    if (!c.Owned.TryGetValue(target, out var e) || e.Verified) return false;
                    e.Verified = true;
                    return true;
                }));
                Emit(u, "ROUTE", pid, $"{t.Proc.Name} loaded {dll} → {t.Role} on {t.Adapter.ShortName}");
            }
        });
        QueueInject(t);
    }

    void OnStop(int pid)
    {
        if (!_tracked.Remove(pid, out var t)) return;
        using var token = t.Token;
        if (t.Gfx || !t.RoutedAtLaunch || t.Role != Role.App) return;
        var u = t.Ctx;
        if (u.Cfg.Overrides.ContainsKey(t.Target)) return;
        if (_tracked.Values.Any(o => o.Ctx == u && string.Equals(o.Target, t.Target, StringComparison.OrdinalIgnoreCase))) return;
        // Never loaded a graphics API: drop the speculative launch-time entry and remember not to add it again.
        AsUser(token, () => Adopt(u, u.File.UpdateIf(c =>
        {
            if (!c.Owned.TryGetValue(t.Target, out var e) || e.Verified || e.Source != "launch") return false;
            u.Store.ClearRouting(t.Target);
            c.Owned.Remove(t.Target);
            c.NonGpu.Add(t.Target);
            return true;
        })));
    }
    bool WriteRoute(UserCtx u, string target, Role role, GpuAdapter adapter, string source, bool force)
    {
        if (!RoutingEngine.CanNameInRegistry(role, adapter)) return false;
        var existing = u.Store.Read(target);
        bool wrote = false;
        var cfg = u.File.UpdateIf(c =>
        {
            if (c.LastBackup == null) return false;   // the tray app backs up the original key before anything is written
            c.Owned.TryGetValue(target, out var own);
            if (own == null && existing?.IsRouting == true && !force) return false;   // the user's own Settings choice
            if (own != null && own.AdapterKey == adapter.Key && own.Role == role &&
                string.Equals(existing?.SpecificAdapter, adapter.Key, StringComparison.OrdinalIgnoreCase)) return false;
            u.Store.SetSpecificAdapter(target, adapter.Key);
            c.Owned[target] = new OwnedEntry
            {
                Role = role, AdapterKey = adapter.Key, WrittenUtc = DateTime.UtcNow,
                Merged = own?.Merged ?? existing != null, Verified = own?.Verified ?? false, Source = own?.Source ?? source,
            };
            c.NonGpu.Remove(target);
            wrote = true;
            return true;
        });
        Adopt(u, cfg);
        if (wrote) Interlocked.Increment(ref Routed);
        return wrote;
    }

    void QueueInject(Tracked t)
    {
        if (t.Injected) return;
        var mode = t.Role switch { Role.App => t.Ctx.Cfg.AppInjection, Role.Media => t.Ctx.Cfg.MediaInjection, _ => InjectMode.Off };
        if (mode == InjectMode.Off || !Injector.Available || !t.Ctx.Classifier.MayInject(t.Target, t.Proc.ExePath, t.Role)) return;
        t.Injected = true;
        _injections.Add(t);
    }

    void InjectNow(Tracked t)
    {
        var mode = t.Role == Role.Media ? t.Ctx.Cfg.MediaInjection : t.Ctx.Cfg.AppInjection;
        var cmd = ProcessUtil.GetCommandLine(t.Proc.Pid);
        // Chromium/Electron: only the GPU process talks to DXGI; renderers block unsigned DLLs anyway.
        if (cmd != null && cmd.Contains("--type=", StringComparison.Ordinal) && !cmd.Contains("--type=gpu-process", StringComparison.Ordinal))
            return;
        var r = Injector.Inject(t.Proc, t.Adapter.Luid, mode);   // as SYSTEM: reaches elevated apps too
        if (r.Ok) Interlocked.Increment(ref Hooked);
        var msg = $"{t.Proc.Name} [{t.Proc.Pid}]: {r.Message} ({mode}, 0x{r.Status:X})";
        try { AsUser(t.Token, () => Emit(t.Ctx, r.Ok ? "HOOK" : "WARN", t.Proc.Pid, msg)); }
        catch (ObjectDisposedException) { Write("HOOK  " + msg); }   // process already gone
    }

    // ------------------------------------------------------------------ output

    readonly object _emitLock = new();

    void Emit(UserCtx u, string level, int pid, string msg)
    {
        Write($"{level,-5} {msg}");
        lock (_emitLock)
        try
        {
            var line = JsonSerializer.Serialize(new ServiceEvent(DateTime.Now, level, pid, msg));
            var fi = new FileInfo(u.File.EventsPath);
            if (fi.Exists && fi.Length > 512 * 1024) File.Delete(fi.FullName);   // the tray app copes with truncation
            using var fs = new FileStream(u.File.EventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var w = new StreamWriter(fs);
            w.WriteLine(line);
        }
        catch { /* the tray app is a nicety; routing already happened */ }
    }

    void Write(string msg) => Log?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {msg}");

    public void Dispose()
    {
        _stopping = true;
        _pollFallback?.Dispose();
        _etw.Dispose();
        _events.CompleteAdding();
        _injections.CompleteAdding();
        foreach (var t in _threads) t.Join(2000);
    }
}

public sealed record ServiceEvent(DateTime Time, string Level, int Pid, string Message);
