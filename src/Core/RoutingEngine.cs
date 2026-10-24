namespace GpuSwitcher;

public sealed record LogEntry(DateTime Time, string Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Level,-5} {Message}";
}

public enum RouteStatus { OnTarget, RestartNeeded, IgnoresPreference, Idle, Ignored, NotRunning }

public sealed class AppView
{
    public required string Target { get; init; }
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public List<int> Pids { get; init; } = new();
    public Role Role { get; init; }
    public string Reason { get; init; } = "";
    public bool HasOverride { get; init; }
    public bool Owned { get; init; }
    public string Routing { get; init; } = "";            // what the registry asks for
    public GpuAdapter? TargetAdapter { get; init; }
    public GpuAdapter? ActualAdapter { get; init; }
    public bool UsesGpu { get; init; }
    public double Busy { get; init; }
    public double Decode { get; init; }
    public double Encode { get; init; }
    public long Memory { get; init; }
    public RouteStatus Status { get; init; }
    public string StatusText { get; init; } = "";
    public string Hook { get; init; } = "";
    public bool IsGameLike { get; init; }
}

public sealed class RuleView
{
    public required string Target { get; init; }
    public required string Raw { get; init; }
    public string Routing { get; init; } = "";
    public string Owner { get; init; } = "";
    public Role? Role { get; init; }
    public bool Stale { get; init; }
    public bool Running { get; init; }
    public bool IsRouting { get; init; }
}

public sealed class RoleBinding
{
    public Role Role { get; init; }
    public GpuAdapter? Adapter { get; init; }
    public string How { get; init; } = "";
}

/// <summary>Watches process launches and GPU counters, writes per-app GPU routing, injects the hook into apps.</summary>
public sealed class RoutingEngine : IDisposable
{
    readonly object _lock = new();
    AppConfig _cfg;
    DateTime _cfgStamp;
    List<GpuAdapter> _adapters = new();
    Dictionary<Role, RoleBinding> _roles = new();
    List<GameRoot> _roots = new();
    Classifier _classifier;
    readonly Dictionary<int, ProcInfo> _procs = new();
    readonly HashSet<int> _gpuPids = new();
    readonly Dictionary<int, string> _hooks = new();
    readonly HashSet<string> _notified = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<LogEntry> _log = new();
    GpuUsageSampler? _sampler;
    GpuSnapshot? _snap;
    List<GpuPreferenceEntry> _entries = new();
    Timer? _procTimer, _gpuTimer;
    int _procBusy, _gpuBusy, _tick;
    DateTime _started;
    DateTime _lastNotify = DateTime.MinValue;
    string _topologySig = "";

    public event Action? Updated;
    public event Action<LogEntry>? Logged;
    public event Action<string, string>? Notify;   // title, text

    readonly bool _observeOnly;

    /// <param name="observeOnly">Measure and report only: never write routing or inject (used by read-only CLI commands).</param>
    public RoutingEngine(bool observeOnly = false)
    {
        _observeOnly = observeOnly;
        _cfg = ConfigStore.Load();
        _cfgStamp = ConfigStore.LastWrite;
        _classifier = new Classifier(_cfg, _roots);
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Discovers GPUs and libraries and reconciles owned entries. Does not start watching.</summary>
    public void Initialize()
    {
        lock (_lock)
        {
            _roots = GameLibraries.Discover(_cfg.ExtraGameFolders);
            _classifier = new Classifier(_cfg, _roots);
            RefreshTopologyLocked(log: !_observeOnly);   // also publishes roles + libraries for the service
            if (!_observeOnly) ReconcileLocked();
            _entries = GpuPreferenceStore.ReadAll();
        }
    }

    public void Start()
    {
        Initialize();
        lock (_lock)
        {
            _started = DateTime.Now;
            foreach (var pid in ProcessUtil.EnumeratePids())
                if (ProcessUtil.Inspect(pid) is { } p) _procs[pid] = p;
            try { _sampler = new GpuUsageSampler(); }
            catch (Exception ex) { Log("WARN", "GPU counters unavailable: " + ex.Message); }
            Log("INFO", $"Watching {_procs.Count} processes; auto-routing {(_cfg.AutoRoute ? "on" : "off")}, hook {(Injector.Available ? "ready" : "missing")}.");
        }
        UpdateServiceState();
        _procTimer = new Timer(_ => ProcTick(), null, 50, ServiceGuarding ? 1000 : 50);
        _gpuTimer = new Timer(_ => GpuTick(), null, 300, 1000);
    }

    /// <summary>True while the boot-guard service is running: it routes and hooks launches (instantly, via ETW),
    /// so this process only watches, learns and reports.</summary>
    public bool ServiceGuarding { get; private set; }
    bool _uiVisible = true;

    void UpdateServiceState()
    {
        bool before = ServiceGuarding;
        try { ServiceGuarding = !_observeOnly && BootGuardService.Query() == BootGuardService.State.Running; }
        catch { ServiceGuarding = false; }
        if (before == ServiceGuarding || _procTimer == null) return;
        _procTimer.Change(0, ServiceGuarding ? 1000 : 50);
        Log("INFO", ServiceGuarding ? "Boot-guard service is running: it handles launches now." : "Boot-guard service stopped: this app handles launches.");
    }

    /// <summary>The window is hidden in the tray: sample GPU counters every 3 s instead of every second.</summary>
    public void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible) return;
        _uiVisible = visible;
        _gpuTimer?.Change(visible ? 200 : 3000, visible ? 1000 : 3000);
    }

    public void Dispose()
    {
        _procTimer?.Dispose();
        _gpuTimer?.Dispose();
    }

    // ---------------------------------------------------------------- public state

    public AppConfig Config { get { lock (_lock) return _cfg; } }
    public IReadOnlyList<GpuAdapter> Adapters { get { lock (_lock) return _adapters.ToList(); } }
    public IReadOnlyDictionary<Role, RoleBinding> Roles { get { lock (_lock) return new Dictionary<Role, RoleBinding>(_roles); } }
    public IReadOnlyList<GameRoot> GameRoots { get { lock (_lock) return _roots.ToList(); } }
    public GpuSnapshot? Snapshot { get { lock (_lock) return _snap; } }
    public IReadOnlyList<LogEntry> LogEntries { get { lock (_log) return _log.ToList(); } }
    public DateTime StartedAt => _started;

    public Classification Classify(string target, string? exePath) { lock (_lock) return _classifier.Classify(target, exePath); }

    // ---------------------------------------------------------------- topology / roles

    void RefreshTopologyLocked(bool log)
    {
        _adapters = GpuTopology.Enumerate();
        _topologySig = string.Join(";", _adapters.Select(a => $"{a.Key}@{a.Luid:X}:{a.Monitors.Count}"));
        ResolveRolesLocked();
        if (log)
            foreach (var r in _roles.Values)
                Log("INFO", $"{r.Role} → {(r.Adapter?.ShortName ?? "Windows decides")} ({r.How})");
    }

    void ResolveRolesLocked()
    {
        _roles = RoleResolver.Resolve(_adapters, _cfg, knowsMonitors: true);
        PublishResolvedLocked();
    }

    /// <summary>Hands the monitor-aware role decisions and game libraries to the boot-guard service via config.json.</summary>
    void PublishResolvedLocked()
    {
        if (_observeOnly) return;
        var roles = _roles.Values.Where(r => r.Adapter != null).ToDictionary(r => r.Role, r => r.Adapter!.Id);
        bool rolesSame = roles.Count == _cfg.ResolvedRoles.Count && roles.All(kv => _cfg.ResolvedRoles.TryGetValue(kv.Key, out var v) && v == kv.Value);
        bool rootsSame = _roots.Select(r => r.Path).SequenceEqual(_cfg.ResolvedGameRoots.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        if (rolesSame && rootsSame) return;
        var roots = _roots.ToList();
        Mutate(c => { c.ResolvedRoles = roles; c.ResolvedGameRoots = roots; });
    }

    /// <summary>With two identical cards, SpecificAdapter= matches both and Windows may pick either. Apps are fine (the
    /// hook selects the exact card by LUID), but games are never hooked, so they get no entry and use Windows' default:
    /// the card driving the primary monitor.</summary>
    public static bool CanNameInRegistry(Role role, GpuAdapter adapter) => !(role == Role.Game && adapter.IsTwin);

    GpuAdapter? AdapterFor(Role role) => _roles.TryGetValue(role, out var b) ? b.Adapter : null;
    GpuAdapter? ByKey(string? key) => key == null ? null : _adapters.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- config mutation

    List<Action<AppConfig>>? _pending;

    void Mutate(Action<AppConfig> change)
    {
        if (_pending != null)
        {
            change(_cfg);           // visible immediately; replayed onto the file once the batch ends
            _pending.Add(change);
            return;
        }
        _cfg = ConfigStore.Update(change);
        _cfgStamp = ConfigStore.LastWrite;
        _classifier = new Classifier(_cfg, _roots);
    }

    /// <summary>Coalesces many Mutate calls into one load-modify-save of config.json.</summary>
    T Batch<T>(Func<T> body)
    {
        if (_pending != null) return body();
        _pending = new();
        try { return body(); }
        finally
        {
            var changes = _pending;
            _pending = null;
            if (changes.Count > 0) Mutate(c => { foreach (var a in changes) a(c); });
        }
    }

    void Batch(Action body) => Batch(() => { body(); return 0; });

    void ReloadIfChangedLocked()
    {
        var stamp = ConfigStore.LastWrite;
        if (stamp == _cfgStamp) return;
        _cfg = ConfigStore.Load();
        _cfgStamp = stamp;
        _classifier = new Classifier(_cfg, _roots);
        ResolveRolesLocked();
        ReconcileLocked();
    }

    void EnsureBackupLocked()
    {
        if (_cfg.LastBackup != null && File.Exists(_cfg.LastBackup)) return;
        try
        {
            var file = GpuPreferenceStore.Backup(ConfigStore.BackupDir);
            Mutate(c => c.LastBackup = file);
            Log("INFO", $"Backed up the original GPU preferences to {file}");
        }
        catch (Exception ex) { Log("WARN", "Backup failed: " + ex.Message); }
    }

    /// <summary>Writes SpecificAdapter for target unless a routing entry that is not ours exists (unless forced).</summary>
    bool WriteRouteLocked(string target, Role role, GpuAdapter adapter, string source, bool force)
    {
        if (!CanNameInRegistry(role, adapter)) return false;
        var existing = GpuPreferenceStore.Read(target);
        _cfg.Owned.TryGetValue(target, out var own);
        if (own == null && existing?.IsRouting == true && !force) return false;   // user's / Windows' own choice
        if (own != null && own.AdapterKey == adapter.Key && own.Role == role &&
            string.Equals(existing?.SpecificAdapter, adapter.Key, StringComparison.OrdinalIgnoreCase)) return false;

        EnsureBackupLocked();
        GpuPreferenceStore.SetSpecificAdapter(target, adapter.Key);
        var entry = new OwnedEntry
        {
            Role = role,
            AdapterKey = adapter.Key,
            WrittenUtc = DateTime.UtcNow,
            Merged = own?.Merged ?? existing != null,
            Verified = own?.Verified ?? false,
            Source = own?.Source ?? source,
        };
        Mutate(c => { c.Owned[target] = entry; c.NonGpu.Remove(target); });
        return true;
    }

    void ReleaseLocked(string target)
    {
        GpuPreferenceStore.ClearRouting(target);
        Mutate(c => c.Owned.Remove(target));
    }

    /// <summary>Re-points owned entries after a role/GPU change and lets go of entries the user changed elsewhere.</summary>
    void ReconcileLocked() => Batch(() =>
    {
        foreach (var (target, own) in _cfg.Owned.ToList())
        {
            var reg = GpuPreferenceStore.Read(target);
            if (reg == null || !string.Equals(reg.SpecificAdapter, own.AdapterKey, StringComparison.OrdinalIgnoreCase))
            {
                Mutate(c => c.Owned.Remove(target));
                Log("INFO", $"{Short(target)}: changed outside GpuSwitcher, leaving it alone");
                continue;
            }
            var exe = target.Contains('\\') ? target : null;
            var role = _cfg.Overrides.TryGetValue(target, out var o) ? o : exe != null ? _classifier.Classify(target, exe).Role : own.Role;
            var desired = role == Role.Ignore ? null : AdapterFor(role);
            if (desired != null && !CanNameInRegistry(role, desired)) desired = null;   // identical cards: games use Windows' default
            if (desired == null)
            {
                ReleaseLocked(target);
                Log("INFO", $"{Short(target)}: routing removed ({role} → Windows decides)");
            }
            else if (!desired.Key.Equals(own.AdapterKey, StringComparison.OrdinalIgnoreCase) || role != own.Role)
            {
                GpuPreferenceStore.SetSpecificAdapter(target, desired.Key);
                var key = desired.Key;
                var now = DateTime.UtcNow;
                Mutate(c => { if (c.Owned.TryGetValue(target, out var e)) { e.AdapterKey = key; e.Role = role; e.WrittenUtc = now; } });
                Log("INFO", $"{Short(target)}: re-routed to {desired.ShortName}");
            }
        }
    });

    // ---------------------------------------------------------------- watchers

    void ProcTick()
    {
        if (Interlocked.Exchange(ref _procBusy, 1) == 1) return;
        try
        {
            var pids = ProcessUtil.EnumeratePids();
            var launched = new List<ProcInfo>();
            lock (_lock)
            {
                var now = new HashSet<int>(pids);
                foreach (var pid in pids)
                {
                    if (_procs.ContainsKey(pid)) continue;
                    var info = ProcessUtil.Inspect(pid) ?? new ProcInfo { Pid = pid, ExePath = null };
                    _procs[pid] = info;
                    launched.Add(info);
                }
                foreach (var gone in _procs.Keys.Where(p => !now.Contains(p)).ToList()) OnExitLocked(gone);
                foreach (var p in launched) OnLaunchLocked(p);
            }
            CheckPendingGraphics();
        }
        catch (Exception ex) { Log("ERROR", "process watcher: " + ex.Message); }
        finally { Volatile.Write(ref _procBusy, 0); }
    }

    sealed class Pending
    {
        public required ProcInfo Proc { get; init; }
        public DateTime Until { get; init; }
        public bool Injected { get; init; }
    }

    /// <summary>Freshly launched processes we are watching for a graphics DLL (d3d11/dxgi/vulkan…) to load.</summary>
    readonly Dictionary<int, Pending> _watchGfx = new();
    readonly HashSet<int> _gfxPids = new();

    void CheckPendingGraphics()
    {
        List<Pending> check;
        lock (_lock)
        {
            if (_watchGfx.Count == 0) return;
            if (ServiceGuarding) { _watchGfx.Clear(); return; }
            var now = DateTime.UtcNow;
            foreach (var pid in _watchGfx.Where(kv => kv.Value.Until < now).Select(kv => kv.Key).ToList()) _watchGfx.Remove(pid);
            check = _watchGfx.Values.ToList();
        }
        foreach (var pe in check)
        {
            var api = ProcessUtil.LoadedGraphicsApi(pe.Proc.Pid);
            if (api == null) continue;
            lock (_lock)
                if (_watchGfx.Remove(pe.Proc.Pid)) OnGraphicsLocked(pe.Proc, api, pe.Injected);
        }
    }

    /// <summary>A just-launched process loaded a graphics API: it is a GPU user. Route and hook it now, usually before its device exists.</summary>
    void OnGraphicsLocked(ProcInfo p, string api, bool alreadyInjected)
    {
        _gfxPids.Add(p.Pid);
        if (p.RegistryTarget is not { } target) return;
        var c = _classifier.Classify(target, p.ExePath, p.IsService);
        if (c.Role == Role.Ignore || AdapterFor(c.Role) is not { } adapter) return;

        Batch(() =>
        {
            if (_cfg.NonGpu.Contains(target)) Mutate(cfg => cfg.NonGpu.Remove(target));
            if (_cfg.Owned.TryGetValue(target, out var own))
            {
                if (!own.Verified) Mutate(cfg => { if (cfg.Owned.TryGetValue(target, out var e)) e.Verified = true; });
            }
            else if (_cfg.AutoRoute && WriteRouteLocked(target, c.Role, adapter, "learned", force: false))
            {
                Mutate(cfg => { if (cfg.Owned.TryGetValue(target, out var e)) e.Verified = true; });
                Log("ROUTE", $"{p.Name} loaded {api} → {c.Role} on {adapter.ShortName}");
            }
        });
        if (!alreadyInjected) QueueInjectIfAllowed(p, target, c.Role, adapter);
    }

    void QueueInjectIfAllowed(ProcInfo p, string target, Role role, GpuAdapter adapter)
    {
        var mode = role switch { Role.App => _cfg.AppInjection, Role.Media => _cfg.MediaInjection, _ => InjectMode.Off };
        if (mode == InjectMode.Off || !Injector.Available || !_cfg.AutoRoute || !_classifier.MayInject(target, p.ExePath, role)) return;
        var luid = adapter.Luid;
        ThreadPool.QueueUserWorkItem(_ => InjectOne(p, luid, mode));
    }

    void OnLaunchLocked(ProcInfo p)
    {
        if (_observeOnly || ServiceGuarding || !_cfg.AutoRoute || p.RegistryTarget is not { } target) return;
        var c = _classifier.Classify(target, p.ExePath, p.IsService);
        if (c.Role == Role.Ignore) return;
        var adapter = AdapterFor(c.Role);
        if (adapter == null) return;

        bool known = _cfg.Owned.TryGetValue(target, out var own);
        bool overridden = _cfg.Overrides.ContainsKey(target);
        bool routeIt = c.Role != Role.App || overridden || known || (_cfg.RouteUnknownAtLaunch && !_cfg.NonGpu.Contains(target));
        if (routeIt && WriteRouteLocked(target, c.Role, adapter, "launch", force: overridden))
            Log("ROUTE", $"{p.Name} ({c.Role}) → {adapter.ShortName} at launch");

        // Known GPU users are hooked immediately; everything else as soon as it loads a graphics DLL.
        bool gpuUser = overridden || c.Role == Role.Media || (own?.Verified ?? false);
        if (gpuUser) QueueInjectIfAllowed(p, target, c.Role, adapter);
        // Never open game processes for memory reads (module scan): anti-cheats flag handles like that.
        if (c.Role == Role.Game || c.IsGameLike) return;
        if (_watchGfx.Count >= 32) _watchGfx.Remove(_watchGfx.MinBy(kv => kv.Value.Until).Key);
        _watchGfx[p.Pid] = new Pending { Proc = p, Until = DateTime.UtcNow.AddSeconds(8), Injected = gpuUser };
    }

    void InjectOne(ProcInfo p, long luid, InjectMode mode)
    {
        try
        {
            var cmd = ProcessUtil.GetCommandLine(p.Pid);
            // Chromium/Electron: only the GPU process talks to DXGI; renderers block unsigned DLLs anyway.
            if (cmd != null && cmd.Contains("--type=", StringComparison.Ordinal) && !cmd.Contains("--type=gpu-process", StringComparison.Ordinal))
                return;
            var r = Injector.Inject(p, luid, mode);
            lock (_lock)
            {
                _hooks[p.Pid] = r.Ok ? $"{mode}" : $"failed: {r.Message}";
                Log(r.Ok ? "HOOK" : "WARN", $"{p.Name} [{p.Pid}]: {r.Message} ({mode}, 0x{r.Status:X})");
            }
        }
        catch (Exception ex) { Log("WARN", $"{p.Name}: injection error {ex.Message}"); }
    }

    void OnExitLocked(int pid)
    {
        if (!_procs.Remove(pid, out var p)) return;
        bool usedGpu = _gpuPids.Remove(pid) | _gfxPids.Remove(pid);
        _hooks.Remove(pid);
        _watchGfx.Remove(pid);
        if (_observeOnly || ServiceGuarding || usedGpu || p.RegistryTarget is not { } target) return;
        if (!_cfg.Owned.TryGetValue(target, out var own) || own.Verified || own.Role != Role.App || own.Source != "launch") return;
        if (_cfg.Overrides.ContainsKey(target)) return;
        if (_procs.Values.Any(o => string.Equals(o.RegistryTarget, target, StringComparison.OrdinalIgnoreCase))) return;
        // Never touched a GPU: drop the speculative launch-time entry and remember not to add it again.
        ReleaseLocked(target);
        Mutate(c => c.NonGpu.Add(target));
    }

    void GpuTick()
    {
        if (Interlocked.Exchange(ref _gpuBusy, 1) == 1) return;
        try
        {
            var snap = _sampler?.Sample();
            var entries = GpuPreferenceStore.ReadAll();
            lock (_lock)
            {
                _tick++;
                if (!_observeOnly) ReloadIfChangedLocked();
                if (!_observeOnly) ReadServiceEventsLocked();
                if (_tick % 5 == 1) UpdateServiceState();
                if (_tick % 10 == 0 && !_observeOnly)
                {
                    var before = _topologySig;
                    RefreshTopologyLocked(log: false);
                    if (before != _topologySig)
                    {
                        Log("INFO", "GPU/monitor layout changed: " + string.Join(", ", _adapters.Where(a => a.IsRoutable).Select(a => a.ShortName)));
                        foreach (var r in _roles.Values) Log("INFO", $"{r.Role} → {(r.Adapter?.ShortName ?? "Windows decides")} ({r.How})");
                        ReconcileLocked();
                    }
                }
                _entries = entries;
                if (snap != null)
                {
                    _snap = snap;
                    if (!_observeOnly) LearnLocked(snap);
                }
            }
            if (!_observeOnly) NotifyRestartsIfNeeded();
            Updated?.Invoke();
        }
        catch (Exception ex) { Log("ERROR", "GPU sampler: " + ex.Message); }
        finally { Volatile.Write(ref _gpuBusy, 0); }
    }

    long _eventsOffset = -1;

    /// <summary>Picks up what the service did (routes, hooks) so the Apps tab and log show it.</summary>
    void ReadServiceEventsLocked()
    {
        var path = ConfigStore.EventsPath;
        try
        {
            if (!File.Exists(path)) { _eventsOffset = 0; return; }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_eventsOffset < 0 || _eventsOffset > fs.Length) _eventsOffset = _eventsOffset < 0 ? fs.Length : 0;   // skip history on start
            fs.Seek(_eventsOffset, SeekOrigin.Begin);
            using var r = new StreamReader(fs);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (System.Text.Json.JsonSerializer.Deserialize<ServiceEvent>(line) is not { } ev) continue;
                if (ev.Level == "HOOK") _hooks[ev.Pid] = ev.Message.Contains("Reorder") ? "Reorder" : "Exclusive";
                else if (ev.Level == "WARN") _hooks[ev.Pid] = "failed";
                Log(ev.Level, "[service] " + ev.Message);
            }
            _eventsOffset = fs.Length;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { /* retry next tick */ }
    }

    void LearnLocked(GpuSnapshot snap) => Batch(() =>
    {
        foreach (var (pid, per) in snap.ByPid)
        {
            bool uses = per.Values.Any(u => u.WorkTicks > 0 || u.Dedicated + u.Shared > 4 << 20);
            if (!uses) continue;
            if (!_procs.TryGetValue(pid, out var p))
            {
                if (ProcessUtil.Inspect(pid) is not { } fresh) continue;
                _procs[pid] = p = fresh;
            }
            if (!_gpuPids.Add(pid) || p.RegistryTarget is not { } target) continue;

            if (_cfg.Owned.TryGetValue(target, out var own))
            {
                if (!own.Verified) Mutate(c => { if (c.Owned.TryGetValue(target, out var e)) e.Verified = true; });
                continue;
            }
            if (_cfg.NonGpu.Contains(target)) Mutate(c => c.NonGpu.Remove(target));
            if (!_cfg.AutoRoute) continue;
            var c = _classifier.Classify(target, p.ExePath, p.IsService);
            if (c.Role == Role.Ignore || AdapterFor(c.Role) is not { } adapter) continue;
            if (WriteRouteLocked(target, c.Role, adapter, "learned", force: false))
            {
                Mutate(cfg => { if (cfg.Owned.TryGetValue(target, out var e)) e.Verified = true; });
                Log("ROUTE", $"{p.Name} ({c.Role}) → {adapter.ShortName}, takes effect next launch");
            }
        }
    });

    void NotifyRestartsIfNeeded()
    {
        if (!_cfg.NotifyRestart || DateTime.Now - _lastNotify < TimeSpan.FromMinutes(2)) return;
        var pending = GetApps(false).Where(a => a.Status == RouteStatus.RestartNeeded && a.Role != Role.Game)
                                    .Where(a => !_notified.Contains(a.Target)).ToList();
        if (pending.Count == 0) return;
        foreach (var a in pending) _notified.Add(a.Target);
        _lastNotify = DateTime.Now;
        var names = string.Join(", ", pending.Take(4).Select(a => Path.GetFileNameWithoutExtension(a.Name)));
        if (pending.Count > 4) names += $" +{pending.Count - 4}";
        Notify?.Invoke("Restart to move apps", $"{names} will switch GPU after a restart. Open GPU Switcher ▸ Apps to restart them.");
    }

    // ---------------------------------------------------------------- views

    public List<AppView> GetApps(bool includeIdle)
    {
        lock (_lock)
        {
            var list = new List<AppView>();
            var entries = _entries.ToDictionary(e => e.Target, StringComparer.OrdinalIgnoreCase);
            var defaultAdapter = _adapters.FirstOrDefault(a => a.IsRoutable);
            foreach (var g in _procs.Values.Where(p => p.RegistryTarget != null).GroupBy(p => p.RegistryTarget!, StringComparer.OrdinalIgnoreCase))
            {
                var pids = g.Select(p => p.Pid).ToList();
                var first = g.First();
                var c = _classifier.Classify(g.Key, first.ExePath, first.IsService);
                entries.TryGetValue(g.Key, out var entry);
                _cfg.Owned.TryGetValue(g.Key, out var own);

                bool routed = entry?.IsRouting == true;
                var roleAdapter = own != null ? AdapterFor(own.Role) : null;
                GpuAdapter? target = entry?.SpecificAdapter is { } k
                    ? (roleAdapter != null && roleAdapter.Key.Equals(k, StringComparison.OrdinalIgnoreCase) ? roleAdapter : ByKey(k))
                    : entry?.GpuPreference == 2 ? _adapters.Where(a => a.Kind == AdapterKind.Discrete).MaxBy(a => a.DedicatedVideoMemory) ?? defaultAdapter
                    : entry?.GpuPreference == 1 ? _adapters.FirstOrDefault(a => a.Kind == AdapterKind.Integrated) ?? defaultAdapter
                    : defaultAdapter;

                bool uses = false; long? luid = null; double busy = 0, dec = 0, enc = 0; long mem = 0;
                if (_snap != null)
                {
                    luid = _snap.DominantAdapter(pids, out uses);
                    foreach (var pid in pids)
                        if (_snap.ByPid.TryGetValue(pid, out var per))
                            foreach (var u in per.Values) { busy += u.Busy; dec += u.Decode; enc += u.Encode; mem += u.Dedicated + u.Shared; }
                }
                if (!includeIdle && !uses) continue;
                var actual = luid is { } l ? _adapters.FirstOrDefault(a => a.Luid == l) : null;

                var hooks = pids.Where(_hooks.ContainsKey).Select(p => _hooks[p]).Distinct().ToList();

                RouteStatus status; string text;
                if (!uses) { status = RouteStatus.Idle; text = "no GPU use"; }
                else if (!routed) { status = RouteStatus.Ignored; text = c.Role == Role.Ignore ? c.Reason : "not routed (Windows default)"; }
                else if (target == null) { status = RouteStatus.IgnoresPreference; text = "assigned GPU is not installed"; }
                else if (actual != null && actual.Luid == target.Luid) { status = RouteStatus.OnTarget; text = "on target"; }
                else
                {
                    // Routing is read at device creation: a process older than its entry has to be restarted.
                    var routedAt = own?.WrittenUtc.ToLocalTime() ?? DateTime.MinValue;
                    if (g.Min(p => p.StartTime) < routedAt) { status = RouteStatus.RestartNeeded; text = "restart to apply"; }
                    else
                    {
                        status = RouteStatus.IgnoresPreference;
                        text = hooks.Any(h => !h.StartsWith("failed")) ? "picked another GPU despite hook (OpenGL/D3D9?)" : "app picks its own GPU";
                    }
                }

                list.Add(new AppView
                {
                    Target = g.Key,
                    Name = first.Name,
                    ExePath = first.ExePath,
                    Pids = pids,
                    Role = c.Role,
                    Reason = c.Reason,
                    HasOverride = _cfg.Overrides.ContainsKey(g.Key),
                    Owned = own != null,
                    Routing = GpuPreferenceStore.Describe(entry, _adapters),
                    TargetAdapter = target,
                    ActualAdapter = actual,
                    UsesGpu = uses,
                    Busy = Math.Min(100, busy),
                    Decode = Math.Min(100, dec),
                    Encode = Math.Min(100, enc),
                    Memory = mem,
                    Status = status,
                    StatusText = text,
                    Hook = string.Join(", ", hooks),
                    IsGameLike = c.IsGameLike,
                });
            }
            return list.OrderByDescending(a => a.Busy).ThenByDescending(a => a.Memory).ToList();
        }
    }

    public List<RuleView> GetRules()
    {
        lock (_lock)
        {
            var running = _procs.Values.Select(p => p.RegistryTarget).Where(t => t != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GpuPreferenceStore.ReadAll().Select(e =>
            {
                _cfg.Owned.TryGetValue(e.Target, out var own);
                bool stale = e.SpecificAdapter != null && ByKey(e.SpecificAdapter) == null;
                string owner = own != null ? "GpuSwitcher" : e.IsRouting ? "Windows Settings / user" : "Windows (game DB)";
                return new RuleView
                {
                    Target = e.Target,
                    Raw = e.Raw,
                    Routing = GpuPreferenceStore.Describe(e, _adapters),
                    Owner = owner,
                    Role = own?.Role,
                    Stale = stale,
                    Running = running.Contains(e.Target),
                    IsRouting = e.IsRouting,
                };
            }).OrderBy(r => r.Owner).ThenBy(r => r.Target).ToList();
        }
    }

    public List<ProcInfo> ProcessesOf(string target)
    {
        lock (_lock)
            return _procs.Values.Where(p => string.Equals(p.RegistryTarget, target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ---------------------------------------------------------------- user actions

    /// <summary>Pin an app to a role (null = remove your rule). Takes over entries made in Windows Settings.</summary>
    public string SetOverride(string target, Role? role)
    {
        lock (_lock)
        {
            if (role is { } r) Mutate(c => c.Overrides[target] = r);
            else Mutate(c => c.Overrides.Remove(target));

            var exe = target.Contains('\\') ? target : _procs.Values.FirstOrDefault(p => p.RegistryTarget == target)?.ExePath;
            var effective = role ?? _classifier.Classify(target, exe).Role;
            if (effective == Role.Ignore || AdapterFor(effective) is not { } adapter)
            {
                if (_cfg.Owned.ContainsKey(target)) ReleaseLocked(target);
                Log("RULE", $"{Short(target)} → Windows decides");
                return $"{Short(target)}: left to Windows.";
            }
            WriteRouteLocked(target, effective, adapter, "manual", force: role != null);
            Log("RULE", $"{Short(target)} → {effective} ({adapter.ShortName})");
            return $"{Short(target)}: {effective} → {adapter.ShortName}.";
        }
    }

    public void SetRoleGpu(Role role, string value)
    {
        lock (_lock)
        {
            Mutate(c => c.RoleGpu[role] = value);
            ResolveRolesLocked();
            ReconcileLocked();
            var b = _roles[role];
            Log("RULE", $"{role} GPU → {(b.Adapter?.ShortName ?? "Windows decides")} ({b.How})");
        }
    }

    public void UpdateConfig(Action<AppConfig> change)
    {
        lock (_lock)
        {
            Mutate(change);
            _roots = GameLibraries.Discover(_cfg.ExtraGameFolders);
            _classifier = new Classifier(_cfg, _roots);
            ResolveRolesLocked();
            ReconcileLocked();
        }
    }

    public string TakeOver(string target)
    {
        lock (_lock)
        {
            var exe = target.Contains('\\') ? target : null;
            var c = _classifier.Classify(target, exe);
            if (c.Role == Role.Ignore || AdapterFor(c.Role) is not { } a) return $"{Short(target)} is classified {c.Role}; set a rule instead.";
            WriteRouteLocked(target, c.Role, a, "manual", force: true);
            Log("RULE", $"{Short(target)} taken over → {c.Role} ({a.ShortName})");
            return $"{Short(target)} → {a.ShortName}";
        }
    }

    public void RemoveEntry(string target)
    {
        lock (_lock)
        {
            if (_cfg.Owned.ContainsKey(target)) ReleaseLocked(target);
            else GpuPreferenceStore.ClearRouting(target);
            Log("RULE", $"{Short(target)}: routing removed");
        }
    }

    public int ReleaseAll()
    {
        lock (_lock) return Batch(() =>
        {
            int n = 0;
            foreach (var t in _cfg.Owned.Keys.ToList()) { ReleaseLocked(t); n++; }
            Log("RULE", $"Removed all {n} GpuSwitcher entries");
            return n;
        });
    }

    public (int routed, int skipped) PreRouteGames(bool dryRun, Action<string>? progress = null)
    {
        lock (_lock) return Batch(() =>
        {
            var adapter = AdapterFor(Role.Game);
            if (adapter == null) return (0, 0);
            int routed = 0, skipped = 0;
            foreach (var exe in GameLibraries.GameExecutables(_roots))
            {
                var c = _classifier.Classify(exe, exe);
                if (c.Role != Role.Game) { skipped++; continue; }
                if (dryRun) { progress?.Invoke(exe); routed++; continue; }
                if (WriteRouteLocked(exe, Role.Game, adapter, "pre-route", force: false)) { routed++; progress?.Invoke(exe); }
                else skipped++;
            }
            if (!dryRun) Log("ROUTE", $"Pre-routed {routed} game executables to {adapter.ShortName} ({skipped} skipped)");
            return (routed, skipped);
        });
    }

    public (int routed, int skipped) PreRouteStartMenu(bool dryRun, Action<string>? progress = null)
    {
        var targets = StartMenuTargets();
        lock (_lock) return Batch(() =>
        {
            int routed = 0, skipped = 0;
            foreach (var exe in targets)
            {
                var c = _classifier.Classify(exe, exe);
                if (c.Role == Role.Ignore || AdapterFor(c.Role) is not { } a) { skipped++; continue; }
                if (dryRun) { progress?.Invoke($"{c.Role,-5} {exe}"); routed++; continue; }
                if (WriteRouteLocked(exe, c.Role, a, "pre-route", force: false)) { routed++; progress?.Invoke($"{c.Role,-5} {exe}"); }
                else skipped++;
            }
            if (!dryRun) Log("ROUTE", $"Pre-routed {routed} Start-menu apps ({skipped} skipped)");
            return (routed, skipped);
        });
    }

    static List<string> StartMenuTargets()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return result.ToList();
        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var lnk in links)
            {
                try
                {
                    string target = shell.CreateShortcut(lnk).TargetPath;
                    if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                    var name = Path.GetFileName(target);
                    if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(target);
                }
                catch { /* broken shortcut */ }
            }
        }
        return result.ToList();
    }

    public RestartResult Restart(string target, bool force)
    {
        var group = ProcessesOf(target);
        var r = AppRestarter.Restart(group, force, TimeSpan.FromSeconds(8));
        Log(r.Relaunched ? "INFO" : "WARN", $"Restart {Short(target)}: {r.Message}");
        return r;
    }

    public string Backup()
    {
        var file = GpuPreferenceStore.Backup(ConfigStore.BackupDir);
        Log("INFO", "Backup written: " + file);
        return file;
    }

    // ---------------------------------------------------------------- logging

    void Log(string level, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, msg);
        lock (_log)
        {
            _log.AddLast(e);
            while (_log.Count > 1000) _log.RemoveFirst();
        }
        try
        {
            Directory.CreateDirectory(ConfigStore.Home);
            var fi = new FileInfo(ConfigStore.LogPath);
            if (fi.Exists && fi.Length > 2_000_000) File.Move(fi.FullName, fi.FullName + ".1", true);
            File.AppendAllText(ConfigStore.LogPath, e + Environment.NewLine);
        }
        catch { /* logging must never break routing */ }
        Logged?.Invoke(e);
    }

    static string Short(string target) => target.Contains('\\') ? Path.GetFileName(target) : target;
}
