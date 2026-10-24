using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuSwitcher;

public enum Role { Game, App, Media, Ignore }

public enum InjectMode
{
    /// <summary>No hook; registry routing only.</summary>
    Off,
    /// <summary>Hook puts the assigned GPU first in every DXGI enumeration.</summary>
    Reorder,
    /// <summary>Hook hides every other GPU from DXGI enumeration, so the app cannot pick anything else.</summary>
    Exclusive,
}

public sealed class OwnedEntry
{
    public Role Role { get; set; }
    public string AdapterKey { get; set; } = "";
    public DateTime WrittenUtc { get; set; }
    /// <summary>The value existed before us (e.g. Windows' AppStatus=0); on release only our fields are removed.</summary>
    public bool Merged { get; set; }
    /// <summary>Seen using a GPU at least once. Unverified launch-time entries are dropped when the process exits without touching a GPU.</summary>
    public bool Verified { get; set; }
    public string Source { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>Role → adapter key (VEN&amp;DEV&amp;SUBSYS). Missing/"auto" = pick automatically; "windows" = leave to Windows.</summary>
    public Dictionary<Role, string> RoleGpu { get; set; } = new();
    public bool AutoRoute { get; set; } = true;
    public bool RouteUnknownAtLaunch { get; set; } = true;
    public bool NotifyRestart { get; set; } = true;
    public InjectMode AppInjection { get; set; } = InjectMode.Exclusive;
    public InjectMode MediaInjection { get; set; } = InjectMode.Exclusive;

    public List<string> GameExes { get; set; } = new()
    {
        // Capture/overlay tools must share the game's GPU (game capture hooks the game's swapchain).
        "obs64.exe", "obs32.exe", "Streamlabs OBS.exe", "Streamlabs Desktop.exe",
        // In-game overlays render in a helper process and share textures with the game.
        "GameOverlayUI.exe", "NVIDIA Overlay.exe", "RTSS.exe", "DiscordHookHelper.exe", "DiscordHookHelper64.exe",
        "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe",
    };

    public List<string> MediaExes { get; set; } = new()
    {
        "vlc.exe", "mpv.exe", "mpvnet.exe", "mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "mpc-be.exe", "mpc-qt.exe",
        "PotPlayerMini64.exe", "PotPlayerMini.exe", "PotPlayer64.exe", "kmplayer64.exe", "smplayer.exe", "HandBrake.exe",
        "ffmpeg.exe", "ffplay.exe", "StaxRip.exe", "Shutter Encoder.exe", "Plex.exe", "Plex HTPC.exe", "JellyfinMediaPlayer.exe",
        "kodi.exe", "Video.UI.exe", "Microsoft.Media.Player.exe", "svp4.exe", "madHcCtrl.exe",
    };

    public List<string> IgnoreExes { get; set; } = new()
    {
        // Choose their own capture adapter / per-monitor renderers; routing them breaks or does nothing.
        "parsecd.exe", "pservice.exe", "wallpaper32.exe", "wallpaper64.exe", "sunshine.exe", "AnyDesk.exe",
        "TeamViewer.exe", "rustdesk.exe", "GpuSwitcher.exe", "gpusw.exe",
    };

    /// <summary>Apps that keep registry routing but never get the hook (exe file names).</summary>
    public List<string> NoInjectExes { get; set; } = new();

    public List<string> ExtraGameFolders { get; set; } = new();
    public Dictionary<string, Role> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OwnedEntry> Owned { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NonGpu { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastBackup { get; set; }

    /// <summary>Role → adapter key as last resolved by the tray app, which can see monitors. The service (session 0) cannot.</summary>
    public Dictionary<Role, string> ResolvedRoles { get; set; } = new();
    /// <summary>Game libraries as last discovered by the tray app.</summary>
    public List<GameRoot> ResolvedGameRoots { get; set; } = new();
    /// <summary>The boot-guard service starts the tray app the moment this user logs on.</summary>
    public bool TrayAtLogon { get; set; }

    internal void Normalize()
    {
        ResolvedRoles ??= new();
        ResolvedGameRoots ??= new();
        Overrides = new(Overrides ?? new(), StringComparer.OrdinalIgnoreCase);
        Owned = new(Owned ?? new(), StringComparer.OrdinalIgnoreCase);
        NonGpu = new(NonGpu ?? new(), StringComparer.OrdinalIgnoreCase);
        RoleGpu ??= new();
    }
}

/// <summary>One user's GpuSwitcher folder (%APPDATA%\GpuSwitcher). Shared by the tray app, the CLI and the boot-guard
/// service (which runs as SYSTEM in session 0), so it is serialized with a lock file rather than a session-local mutex.</summary>
public sealed class ConfigFile
{
    public static ConfigFile Default { get; } = new(Environment.GetEnvironmentVariable("GPUSWITCHER_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuSwitcher"));

    public ConfigFile(string home) => Home = home;

    public string Home { get; }
    public string ConfigPath => Path.Combine(Home, "config.json");
    public string BackupDir => Path.Combine(Home, "backups");
    public string LogPath => Path.Combine(Home, "log.txt");
    /// <summary>JSON lines the service appends for the tray app (routes and hooks it performed).</summary>
    public string EventsPath => Path.Combine(Home, "service-events.jsonl");
    public bool Exists => File.Exists(ConfigPath);
    public DateTime LastWrite => File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppConfig Load()
    {
        using var _ = Lock();
        return LoadUnlocked();
    }

    /// <summary>Load → mutate → save atomically with respect to every other GpuSwitcher process.</summary>
    public AppConfig Update(Action<AppConfig> mutate) => UpdateIf(c => { mutate(c); return true; });

    /// <summary>Like Update, but only writes the file when <paramref name="mutate"/> returns true.</summary>
    public AppConfig UpdateIf(Func<AppConfig, bool> mutate)
    {
        using var _ = Lock();
        var cfg = LoadUnlocked();
        if (mutate(cfg)) SaveUnlocked(cfg);
        return cfg;
    }

    FileStream Lock()
    {
        Directory.CreateDirectory(Home);
        var path = Path.Combine(Home, "config.lock");
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(10); }
        }
    }

    AppConfig LoadUnlocked()
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig()
                : new AppConfig();
        }
        catch (JsonException)
        {
            File.Copy(ConfigPath, ConfigPath + ".corrupt", true);
            cfg = new AppConfig();
        }
        cfg.Normalize();
        return cfg;
    }

    void SaveUnlocked(AppConfig cfg)
    {
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
        File.Move(tmp, ConfigPath, true);
    }
}

/// <summary>The current user's config (tray app and CLI).</summary>
public static class ConfigStore
{
    static ConfigFile Me => ConfigFile.Default;
    public static string Home => Me.Home;
    public static string ConfigPath => Me.ConfigPath;
    public static string BackupDir => Me.BackupDir;
    public static string LogPath => Me.LogPath;
    public static string EventsPath => Me.EventsPath;
    public static DateTime LastWrite => Me.LastWrite;
    public static AppConfig Load() => Me.Load();
    public static AppConfig Update(Action<AppConfig> mutate) => Me.Update(mutate);
}