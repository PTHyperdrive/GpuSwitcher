using System.Globalization;
using GpuSwitcher;

return Cli.Run(args);

static class Cli
{
    const string Usage = """
        gpusw — GpuSwitcher command line

          gpus                               list GPUs, monitors and role assignment
          ps [--all] [--seconds N]           which GPU every process is really using
          rules                              every entry in the Windows per-app GPU store
          classify <exe>                     how an exe would be routed, and why
          route <exe|aumid> <game|app|media|ignore|clear>
                                             pin an app to a role (clear = remove your rule)
          set-gpu <game|app|media> <auto|windows|#index|VEN&DEV&SUBSYS>
          pre-route <games|startmenu> [--dry-run]
          unroute <exe|aumid>                remove the routing entry, rule and memory for one app
          unroute-all                        remove every entry GpuSwitcher wrote
          restart <exe|aumid> [--force]      close and relaunch an app so it moves GPU
          inject <pid> [--role app|media] [--mode reorder|exclusive]
          probe [--load SECONDS] [--hook reorder|exclusive --target #index]
                                             what D3D11/D3D12/DXGI/Vulkan pick in *this* process
          watch                              run the routing engine in the console
          service install|uninstall|status|start|stop
                                             the boot-guard service (SYSTEM, from boot, ETW-instant)
          guard                              run the boot guard in this console (elevated)
          autostart on|off                   open the tray app at logon (via the boot guard when installed)
          backup                             export the registry key to a .reg file
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine($"gpusw {AppInfo.Version}  ·  {AppInfo.RepoUrl}"); Console.WriteLine(Usage); return 0; }
        if (args[0] is "version" or "--version" or "-v") { Console.WriteLine(AppInfo.Version); return 0; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "gpus" => Gpus(),
                "ps" => Ps(args),
                "rules" => Rules(),
                "classify" => Classify(args),
                "route" => Route(args),
                "set-gpu" => SetGpu(args),
                "pre-route" => PreRoute(args),
                "unroute" => Unroute(args),
                "unroute-all" => UnrouteAll(),
                "restart" => Restart(args),
                "inject" => Inject(args),
                "probe" => ProbeCmd(args),
                "watch" => Watch(),
                "service" => ServiceCmd(args),
                "guard" => Guard(),
                "autostart" => Autostart(args),
                "backup" => BackupCmd(),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }
    static bool Has(string[] a, string flag) => a.Any(x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));
    static string? Opt(string[] a, string flag)
    {
        int i = Array.FindIndex(a, x => x.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    static string Mb(double bytes) => bytes >= 1 << 30 ? $"{bytes / (1 << 30):0.0} GB" : $"{bytes / (1 << 20):0} MB";

    static RoutingEngine Engine(bool observe = false)
    {
        var e = new RoutingEngine(observe);
        e.Initialize();
        return e;
    }

    static int Gpus()
    {
        var e = Engine(observe: true);
        var roles = e.Roles;
        foreach (var a in e.Adapters)
        {
            var assigned = roles.Values.Where(r => r.Adapter == a).Select(r => r.Role.ToString()).ToList();
            Console.WriteLine($"#{a.Index}  {a.Name}");
            Console.WriteLine($"     kind {a.Kind} ({a.KmtText}), key {a.Key}, PCI {a.Location}{(a.IsTwin ? " [identical twin]" : "")}, LUID {a.LuidHex}, VRAM {Mb(a.DedicatedVideoMemory)}");
            Console.WriteLine($"     monitors: {(a.Monitors.Count == 0 ? "none" : string.Join(", ", a.Monitors))}");
            if (assigned.Count > 0) Console.WriteLine($"     roles: {string.Join(", ", assigned)}");
        }
        Console.WriteLine();
        foreach (var r in roles.Values) Console.WriteLine($"{r.Role,-6} → {r.Adapter?.ShortName ?? "Windows decides",-28} ({r.How})");
        Console.WriteLine();
        Console.WriteLine($"hook: {(Injector.Available ? Injector.NativeDir : "not installed")}");
        Console.WriteLine($"config: {ConfigStore.ConfigPath}");
        return 0;
    }

    static int Ps(string[] args)
    {
        double seconds = double.TryParse(Opt(args, "--seconds"), CultureInfo.InvariantCulture, out var s) ? s : 2.2;
        using var e = new RoutingEngine(observeOnly: true);
        e.Start();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        var apps = e.GetApps(Has(args, "--all"));
        Console.WriteLine($"{"Process",-28} {"PIDs",4} {"Role",-6} {"Routed to",-22} {"Actually on",-22} {"Busy",6} {"Dec",5} {"Enc",5} {"Mem",8}  Status");
        foreach (var a in apps)
        {
            Console.WriteLine($"{Trunc(a.Name, 28),-28} {a.Pids.Count,4} {a.Role,-6} {Trunc(a.Routing, 22),-22} {Trunc(a.ActualAdapter?.ShortName ?? "-", 22),-22} " +
                              $"{a.Busy,5:0.0}% {a.Decode,4:0}% {a.Encode,4:0}% {Mb(a.Memory),8}  {a.StatusText}");
        }
        if (e.Snapshot is { } snap)
        {
            Console.WriteLine();
            foreach (var ad in e.Adapters.Where(x => x.IsRoutable))
                if (snap.ByAdapter.TryGetValue(ad.Luid, out var u))
                    Console.WriteLine($"{ad.ShortName,-24} 3D {u.Render,5:0.0}%  compute {u.Compute,5:0.0}%  decode {u.Decode,5:0.0}%  encode {u.Encode,5:0.0}%  copy {u.Copy,5:0.0}%  VRAM {Mb(u.DedicatedUsed)}");
        }
        return 0;
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    static int Rules()
    {
        var e = Engine(observe: true);
        foreach (var r in e.GetRules())
        {
            var flags = (r.Stale ? " STALE" : "") + (r.Running ? " running" : "");
            Console.WriteLine($"[{r.Owner}{(r.Role is { } role ? "/" + role : "")}{flags}] {r.Target}");
            Console.WriteLine($"    {r.Routing,-26} {r.Raw}");
        }
        return 0;
    }

    static int Classify(string[] args)
    {
        if (args.Length < 2) return Fail("classify <exe>");
        var e = Engine(observe: true);
        var path = Path.GetFullPath(args[1]);
        var c = e.Classify(path, path);
        var adapter = c.Role == Role.Ignore ? null : e.Roles[c.Role].Adapter;
        Console.WriteLine($"{c.Role} ({c.Reason}) → {adapter?.ShortName ?? "Windows decides"}; game-like: {c.IsGameLike}");
        Console.WriteLine($"current entry: {GpuPreferenceStore.Read(path)?.Raw ?? "(none)"}");
        return 0;
    }

    static Role? ParseRole(string s) => s.ToLowerInvariant() switch
    {
        "game" => Role.Game, "app" => Role.App, "media" => Role.Media, "ignore" or "windows" => Role.Ignore, "clear" => null,
        _ => throw new ArgumentException($"unknown role '{s}'"),
    };

    static string NormalizeTarget(string t) => t.Contains('!') && !t.Contains('\\') ? t : Path.GetFullPath(t);

    static int Route(string[] args)
    {
        if (args.Length < 3) return Fail("route <exe|aumid> <game|app|media|ignore|clear>");
        var e = Engine();
        Console.WriteLine(e.SetOverride(NormalizeTarget(args[1]), ParseRole(args[2])));
        return 0;
    }

    static int SetGpu(string[] args)
    {
        if (args.Length < 3) return Fail("set-gpu <game|app|media> <auto|windows|#index|KEY>");
        var e = Engine();
        var role = ParseRole(args[1]) ?? throw new ArgumentException("role required");
        var v = args[2];
        if (v.StartsWith('#'))
        {
            int idx = int.Parse(v[1..]);
            v = e.Adapters.First(a => a.Index == idx).Key;
        }
        e.SetRoleGpu(role, v);
        var b = e.Roles[role];
        Console.WriteLine($"{role} → {b.Adapter?.ShortName ?? "Windows decides"} ({b.How})");
        return 0;
    }

    static int PreRoute(string[] args)
    {
        if (args.Length < 2) return Fail("pre-route <games|startmenu> [--dry-run]");
        var e = Engine();
        bool dry = Has(args, "--dry-run");
        var (routed, skipped) = args[1].ToLowerInvariant() switch
        {
            "games" => e.PreRouteGames(dry, s => Console.WriteLine("  " + s)),
            "startmenu" => e.PreRouteStartMenu(dry, s => Console.WriteLine("  " + s)),
            _ => throw new ArgumentException("games or startmenu"),
        };
        Console.WriteLine($"{(dry ? "would route" : "routed")} {routed}, skipped {skipped}");
        if (!dry) foreach (var r in e.GameRoots) Console.WriteLine($"  library: {r.Path} ({r.Source})");
        return 0;
    }

    static int Unroute(string[] args)
    {
        if (args.Length < 2) return Fail("unroute <exe|aumid>");
        var e = Engine();
        var target = NormalizeTarget(args[1]);
        e.RemoveEntry(target);
        e.UpdateConfig(c => { c.Overrides.Remove(target); c.NonGpu.Remove(target); });
        Console.WriteLine($"{target}: routing removed");
        return 0;
    }

    static int UnrouteAll()
    {
        var e = Engine();
        Console.WriteLine($"removed {e.ReleaseAll()} entries");
        return 0;
    }

    static int Restart(string[] args)
    {
        if (args.Length < 2) return Fail("restart <exe|aumid> [--force]");
        using var e = new RoutingEngine(observeOnly: true);
        e.Start();
        var target = NormalizeTarget(args[1]);
        var r = e.Restart(target, Has(args, "--force"));
        Console.WriteLine(r.Message);
        return r.Relaunched ? 0 : 1;
    }

    static GpuAdapter PickAdapter(RoutingEngine e, string? spec, Role fallbackRole)
    {
        if (spec == null) return e.Roles[fallbackRole].Adapter ?? throw new InvalidOperationException($"no GPU for {fallbackRole}");
        if (spec.StartsWith('#')) return e.Adapters.First(a => a.Index == int.Parse(spec[1..]));
        return e.Adapters.First(a => a.Key.Equals(spec, StringComparison.OrdinalIgnoreCase));
    }

    static InjectMode ParseMode(string? s) => (s ?? "exclusive").ToLowerInvariant() switch
    {
        "reorder" => InjectMode.Reorder, "exclusive" => InjectMode.Exclusive, _ => throw new ArgumentException("reorder|exclusive"),
    };

    static int Inject(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var pid)) return Fail("inject <pid> [--role app|media] [--mode reorder|exclusive] [--target #i]");
        var e = Engine(observe: true);
        var info = ProcessUtil.Inspect(pid) ?? throw new InvalidOperationException("cannot open process");
        var role = ParseRole(Opt(args, "--role") ?? "app") ?? Role.App;
        var c = e.Classify(info.RegistryTarget ?? "", info.ExePath);
        if (c.IsGameLike || role == Role.Game) return Fail($"refusing: {info.Name} is a game ({c.Reason}). Games are never injected.");
        var adapter = PickAdapter(e, Opt(args, "--target"), role);
        var r = Injector.Inject(info, adapter.Luid, ParseMode(Opt(args, "--mode")));
        Console.WriteLine($"{info.Name} [{pid}] → {adapter.ShortName}: {r.Message} (status 0x{r.Status:X})");
        return r.Ok ? 0 : 1;
    }

    static int ProbeCmd(string[] args)
    {
        var adapters = GpuTopology.Enumerate();
        if (Opt(args, "--hook") is { } hookMode)
        {
            var e = Engine(observe: true);
            var a = PickAdapter(e, Opt(args, "--target"), Role.App);
            var st = Injector.HookSelf(a.Luid, ParseMode(hookMode));
            Console.WriteLine($"self-hook → {a.ShortName} ({hookMode}): status 0x{st:X}");
        }
        double load = double.TryParse(Opt(args, "--load"), CultureInfo.InvariantCulture, out var l) ? l : 0;
        if (Opt(args, "--delay") is { } d) Thread.Sleep(TimeSpan.FromSeconds(double.Parse(d, CultureInfo.InvariantCulture)));
        Console.Write(Probe.Run(adapters, load));
        return 0;
    }

    static int Watch()
    {
        using var e = new RoutingEngine();
        e.Logged += le => Console.WriteLine(le);
        e.Notify += (t, m) => Console.WriteLine($"** {t}: {m}");
        e.Start();
        Console.WriteLine("watching — Ctrl+C to stop");
        var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; done.Set(); };
        done.Wait();
        return 0;
    }

    static int ServiceCmd(string[] args)
    {
        var verb = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        switch (verb)
        {
            case "install":
                foreach (var step in BootGuardService.Install(BootGuardService.BundledExe)) Console.WriteLine(step);
                break;
            case "uninstall":
                foreach (var step in BootGuardService.Uninstall()) Console.WriteLine(step);
                break;
            case "start": BootGuardService.Start(); break;
            case "stop": BootGuardService.Stop(); break;
            case "status": break;
            default: return Fail("service install|uninstall|status|start|stop");
        }
        Console.WriteLine($"{BootGuardService.Name}: {BootGuardService.Query()}  ({BootGuardService.InstalledExe ?? "not installed"})");
        var log = Path.Combine(Path.GetDirectoryName(BootGuardService.InstalledExe ?? BootGuardService.BundledExe)!, "logs", "service.log");
        if (verb == "status" && File.Exists(log))
            foreach (var line in File.ReadLines(log).TakeLast(15)) Console.WriteLine("  " + line);
        return 0;
    }

    static int Autostart(string[] args)
    {
        bool on = args.Length < 2 || args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        bool service = BootGuardService.Query() != BootGuardService.State.NotInstalled;
        ConfigStore.Update(c => c.TrayAtLogon = on);
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        var tray = Path.Combine(AppContext.BaseDirectory, "GpuSwitcher.exe");
        if (on && !service) run.SetValue("GpuSwitcher", $"\"{tray}\" --tray");
        else run.DeleteValue("GpuSwitcher", false);
        Console.WriteLine(!on ? "autostart off" : service ? "the boot guard opens the tray app the moment you log on" : "Run-key autostart (install the boot guard to start before everything else)");
        return 0;
    }

    static int Guard()
    {
        if (!BootGuardService.IsElevated) return Fail("guard needs an elevated prompt (ETW kernel session)");
        if (BootGuardService.Query() == BootGuardService.State.Running) return Fail("the boot-guard service is running; stop it first (gpusw service stop)");
        using var g = new LaunchGuard();
        g.Log += Console.WriteLine;
        g.Start();
        Console.WriteLine("boot guard in console — Ctrl+C to stop");
        var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; done.Set(); };
        done.Wait();
        Console.WriteLine($"routed {g.Routed}, hooked {g.Hooked}, ETW latency {g.EtwLatencyMs:0.0} ms");
        return 0;
    }

    static int BackupCmd()
    {
        Console.WriteLine(GpuPreferenceStore.Backup(ConfigStore.BackupDir));
        return 0;
    }
}
