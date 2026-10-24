namespace GpuSwitcher;

public sealed record Classification(Role Role, string Reason, bool IsGameLike);

public sealed class Classifier
{
    readonly AppConfig _cfg;
    readonly List<GameRoot> _roots;
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
    static readonly string OwnDir = AppContext.BaseDirectory.TrimEnd('\\') + "\\";

    public Classifier(AppConfig cfg, List<GameRoot> roots) { _cfg = cfg; _roots = roots; }

    public IReadOnlyList<GameRoot> Roots => _roots;

    /// <summary>Anti-cheat services and clients: never routed, never opened beyond basic query rights.</summary>
    static readonly HashSet<string> AntiCheatExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe", "EasyAntiCheat_Launcher.exe", "BEService.exe", "BEService_x64.exe", "BELauncher.exe",
        "vgc.exe", "vgtray.exe", "vgk.exe", "FACEIT.exe", "FACEITService.exe", "faceitclient.exe", "ESEA.exe", "eac_launcher.exe",
        "xigncode.exe", "GameGuard.des", "GameMon.des", "nProtect.exe", "PnkBstrA.exe", "PnkBstrB.exe", "EAAntiCheat.GameServiceLauncher.exe",
        "EAAntiCheat.GameService.exe", "mhyprot2.exe", "ACE-Tray.exe", "SGuard64.exe", "SGuardSvc64.exe",
    };

    /// <summary>Folder names that only ever hold games or game launchers (catches installs outside known libraries).</summary>
    static readonly HashSet<string> GameFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamapps", "XboxGames", "Roblox", "Riot Games", "Riot Client", "Riot Vanguard", "Blizzard", "Blizzard Entertainment", "Battle.net",
        "Ubisoft", "Ubisoft Game Launcher", "Electronic Arts", "EA Games", "EA Desktop", "Origin Games", "Epic Games", "Rockstar Games",
        "Bethesda.net Launcher", "GOG Galaxy", "GOG Games", "miHoYo", "HoYoPlay", "HoYoverse", ".minecraft", "Minecraft Launcher",
        "Mojang", "Wargaming.net", "Garena", "itch", "Arena Breakout Infinite", "Hypixel", "Lunar Client",
    };

    /// <summary>Always games, whatever the config says: overlay helpers that share textures with the game, and anti-cheat-protected clients.</summary>
    static readonly HashSet<string> BuiltInGameExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameOverlayUI.exe", "NVIDIA Overlay.exe", "RTSS.exe", "DiscordHookHelper.exe", "DiscordHookHelper64.exe",
        "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe", "obs64.exe", "obs32.exe",
    };

    public Classification Classify(string target, string? exePath, bool isService = false)
    {
        var path = exePath ?? target;
        var name = Path.GetFileName(path);
        var gameRoot = exePath != null ? GameLibraries.RootOf(exePath, _roots) : null;
        var gameFolder = exePath != null ? GameFolderOf(exePath) : null;
        bool listedGame = Contains(_cfg.GameExes, name) || BuiltInGameExes.Contains(name);
        bool gameLike = gameRoot != null || gameFolder != null || listedGame || AntiCheatExes.Contains(name);

        if (AntiCheatExes.Contains(name)) return new(Role.Ignore, "anti-cheat", true);
        if (isService) return new(Role.Ignore, "service (session 0)", gameLike);
        if (_cfg.Overrides.TryGetValue(target, out var forced))
            return new(forced, "your rule", gameLike || forced == Role.Game);
        if (exePath == null) return new(Role.Ignore, "no image path (protected process)", false);
        if (exePath.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) return new(Role.Ignore, "Windows component", false);
        if (exePath.StartsWith(OwnDir, StringComparison.OrdinalIgnoreCase)) return new(Role.Ignore, "GpuSwitcher itself", false);
        if (Contains(_cfg.IgnoreExes, name)) return new(Role.Ignore, "ignore list", gameLike || GameFingerprint(exePath) != null);
        if (gameRoot != null) return new(Role.Game, $"{gameRoot.Source} library", true);
        if (listedGame) return new(Role.Game, "game list", true);
        if (gameFolder != null) return new(Role.Game, $"game folder ({gameFolder})", true);
        if (GameFingerprint(exePath) is { } marker) return new(Role.Game, $"game files ({marker})", true);
        if (Contains(_cfg.MediaExes, name)) return new(Role.Media, "media list", false);
        return new(Role.App, "application", false);
    }

    /// <summary>Hard rule from the user: never inject into games, whatever the rule says.</summary>
    public bool MayInject(string target, string? exePath, Role role)
    {
        if (role is Role.Game or Role.Ignore || exePath == null) return false;
        var c = Classify(target, exePath);
        if (c.IsGameLike || GameFingerprint(exePath) != null) return false;
        if (Contains(_cfg.NoInjectExes, Path.GetFileName(exePath))) return false;
        return true;
    }

    static string? GameFolderOf(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir == null) return null;
        foreach (var seg in dir.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (GameFolders.Contains(seg)) return seg;
        return null;
    }

    // ---- game fingerprints: games living outside any known library ------------------------------

    static readonly string[] MarkerFiles =
    {
        "steam_api.dll", "steam_api64.dll", "UnityPlayer.dll", "GameAssembly.dll", "EOSSDK-Win64-Shipping.dll", "EOSSDK-Win32-Shipping.dll",
        "galaxy.dll", "galaxy64.dll", "BEClient_x64.dll", "BEClient.dll", "EasyAntiCheat_x64.dll", "EasyAntiCheat_EOS_Setup.exe",
        "start_protected_game.exe", "mhyprot2.sys", "xigncode.dll", "GameGuard.des",
    };
    static readonly string[] MarkerDirs = { "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "MonoBleedingEdge", "XIGNCODE", "GameGuard" };
    static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        // BattlEye/EasyAntiCheat install their services here; a marker there says nothing about the app
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
    };
    static readonly Dictionary<string, string?> FingerprintCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A marker proving the exe belongs to a game (engine runtime, Steamworks/EOS/Galaxy SDK, anti-cheat), or null.
    /// Looks in the exe's folder and up to three parents, never in shared container folders like Program Files.</summary>
    public static string? GameFingerprint(string exePath)
    {
        var name = Path.GetFileName(exePath);
        if (name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)) return "Unreal shipping build";
        var dir = Path.GetDirectoryName(exePath);
        lock (FingerprintCache)
            if (dir != null && FingerprintCache.TryGetValue(dir, out var cached)) return cached;

        string? found = null;
        var d = dir;
        for (int level = 0; level < 4 && d != null && found == null; level++)
        {
            if (Path.GetPathRoot(d) == d || Containers.Contains(d)) break;
            try
            {
                foreach (var f in MarkerFiles)
                    if (File.Exists(Path.Combine(d, f))) { found = f; break; }
                if (found == null)
                    foreach (var m in MarkerDirs)
                        if (Directory.Exists(Path.Combine(d, m))) { found = m + "\\"; break; }
                if (found == null && level == 0 && Directory.Exists(Path.Combine(d, Path.GetFileNameWithoutExtension(name) + "_Data")))
                    found = "Unity _Data folder";
            }
            catch { break; }
            d = Path.GetDirectoryName(d);
        }
        lock (FingerprintCache)
            if (dir != null) FingerprintCache[dir] = found;
        return found;
    }

    static bool Contains(List<string> list, string name) => list.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
}
