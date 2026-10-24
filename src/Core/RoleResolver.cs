namespace GpuSwitcher;

/// <summary>Decides which GPU serves each role. Pure function of the adapter list and the config, shared by the tray
/// app (which can see monitors) and the boot-guard service (which cannot, and leans on what the tray app published).</summary>
public static class RoleResolver
{
    public static Dictionary<Role, RoleBinding> Resolve(IReadOnlyList<GpuAdapter> adapters, AppConfig cfg, bool knowsMonitors)
    {
        var routable = adapters.Where(a => a.IsRoutable).ToList();
        var discrete = routable.Where(a => a.Kind == AdapterKind.Discrete).ToList();
        var integrated = routable.Where(a => a.Kind == AdapterKind.Integrated).ToList();

        // Roles are stored as Id (key@pci-slot); older configs hold the bare VEN&DEV&SUBSYS key.
        GpuAdapter? Find(string? idOrKey)
        {
            if (string.IsNullOrEmpty(idOrKey)) return null;
            return routable.FirstOrDefault(a => a.Id.Equals(idOrKey, StringComparison.OrdinalIgnoreCase))
                ?? routable.Where(a => a.Key.Equals(idOrKey, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(a => a.DrivesPrimaryDisplay).ThenByDescending(a => a.Monitors.Count).FirstOrDefault();
        }
        GpuAdapter? Published(Role r) => Find(cfg.ResolvedRoles.GetValueOrDefault(r));

        RoleBinding Bind(Role role, Func<(GpuAdapter? adapter, string how)> auto)
        {
            cfg.RoleGpu.TryGetValue(role, out var value);
            if (string.Equals(value, "windows", StringComparison.OrdinalIgnoreCase))
                return new RoleBinding { Role = role, Adapter = null, How = "Windows decides" };
            if (!string.IsNullOrEmpty(value) && !value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (Find(value) is { } chosen) return new RoleBinding { Role = role, Adapter = chosen, How = "your choice" };
                var (fa, fh) = auto();
                return new RoleBinding { Role = role, Adapter = fa, How = $"{fh} — your GPU {value} is not installed" };
            }
            var (a, h) = auto();
            return new RoleBinding { Role = role, Adapter = a, How = h };
        }

        // Games: the main GPU stays the main GPU. Adding a bigger card or moving the primary monitor onto another
        // card does not steal the role; only removing the card (or choosing explicitly) does.
        var game = Bind(Role.Game, () =>
        {
            if (Published(Role.Game) is { } kept) return (kept, "auto, kept as main GPU");
            var pick = knowsMonitors
                ? discrete.OrderByDescending(a => a.DrivesPrimaryDisplay).ThenByDescending(a => a.Monitors.Count)
                          .ThenByDescending(a => a.DedicatedVideoMemory).FirstOrDefault()
                : discrete.OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault();
            return (pick ?? integrated.FirstOrDefault(), "auto");
        });
        var gameLuid = game.Adapter?.Luid;

        // Apps: the best other discrete card (an identical twin counts: it is a different card), else the iGPU.
        var app = Bind(Role.App, () =>
        {
            if (!knowsMonitors && Published(Role.App) is { } p && p.Luid != gameLuid) return (p, "auto");
            var pick = discrete.Where(a => a.Luid != gameLuid).OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault()
                    ?? integrated.FirstOrDefault(a => a.Luid != gameLuid)
                    ?? game.Adapter;
            return (pick, "auto");
        });

        // Media: Intel Quick Sync if present, else any iGPU, else wherever apps go.
        var media = Bind(Role.Media, () =>
        {
            if (!knowsMonitors && Published(Role.Media) is { } p) return (p, "auto");
            var pick = integrated.FirstOrDefault(a => a.VendorId == 0x8086) ?? integrated.FirstOrDefault() ?? app.Adapter;
            return (pick, "auto");
        });

        return new() { [Role.Game] = game, [Role.App] = app, [Role.Media] = media };
    }
}
