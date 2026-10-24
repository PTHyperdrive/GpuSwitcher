using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GpuSwitcher;

public sealed record GameRoot(string Path, string Source);

/// <summary>Discovers where games are installed, so anything launched from there is treated as a game.</summary>
public static partial class GameLibraries
{
    /// <param name="userHive">The user's HKCU (or HKU\&lt;SID&gt; when called from the service).</param>
    public static List<GameRoot> Discover(IEnumerable<string> extraFolders, RegistryKey? userHive = null)
    {
        var roots = new List<GameRoot>();
        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\');
                if (path.Length <= 3 || !Directory.Exists(path)) return;   // never a whole drive
                if (roots.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
                roots.Add(new GameRoot(path, source));
            }
            catch { /* malformed path */ }
        }

        userHive ??= Registry.CurrentUser;
        Safe(() => Steam(Add, userHive));
        Safe(() => Epic(Add));
        Safe(() => RegistryInstalls(Add, userHive));
        Safe(() =>
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                Add(Path.Combine(d.RootDirectory.FullName, "XboxGames"), "Xbox");
                Add(Path.Combine(d.RootDirectory.FullName, "Riot Games"), "Riot");
                Add(Path.Combine(d.RootDirectory.FullName, "Games"), "Games folder");
            }
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            Add(Path.Combine(pf, "EA Games"), "EA");
            Add(Path.Combine(pf86, "Origin Games"), "EA");
            Add(Path.Combine(pf, "Epic Games"), "Epic");
            Add(Path.Combine(pf86, "GOG Galaxy", "Games"), "GOG");
            Add(Path.Combine(pf86, "Ubisoft", "Ubisoft Game Launcher", "games"), "Ubisoft");
        });
        foreach (var f in extraFolders) Add(f, "user");
        return roots;
    }

    static void Safe(Action a) { try { a(); } catch { /* a launcher that is not installed */ } }

    static void Steam(Action<string?, string> add, RegistryKey userHive)
    {
        var steam = userHive.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string
                 ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
        if (steam == null) return;
        steam = steam.Replace('/', '\\');
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in libs) add(Path.Combine(lib, "steamapps", "common"), "Steam");
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    static void Epic(Action<string?, string> add)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("InstallLocation", out var loc)) add(loc.GetString(), "Epic");
            }
            catch { /* corrupt manifest */ }
        }
    }

    static void RegistryInstalls(Action<string?, string> add, RegistryKey userHive)
    {
        using (var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            if (gog != null)
                foreach (var id in gog.GetSubKeyNames())
                    using (var k = gog.OpenSubKey(id)) add(k?.GetValue("path") as string, "GOG");

        using (var ubi = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
            if (ubi != null)
                foreach (var id in ubi.GetSubKeyNames())
                    using (var k = ubi.OpenSubKey(id)) add(k?.GetValue("InstallDir") as string, "Ubisoft");

        foreach (var hive in new[] { Registry.LocalMachine, userHive })
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    var publisher = k?.GetValue("Publisher") as string ?? "";
                    string? source = publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ? "Battle.net"
                                   : publisher.Contains("Riot Games", StringComparison.OrdinalIgnoreCase) ? "Riot"
                                   : null;
                    // Launcher apps themselves (Battle.net.exe, Riot Client) are fine to treat as games too.
                    if (source != null) add(k?.GetValue("InstallLocation") as string, source);
                }
            }
    }

    static readonly string[] SkipDirs =
    {
        "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "__Installer", "Installers", "DirectX", "vcredist",
        "EasyAntiCheat", "BattlEye", "dotNetFx", "PhysX", "Support", "_Redist", "Prerequisites",
    };

    static readonly Regex SkipExe = new(
        @"^(unins.*|.*setup.*|.*redist.*|vc_?redist.*|dxsetup|dxwebsetup|.*crash.?(handler|report|sender|pad).*|bugsplat.*|easyanticheat.*|beservice.*|dotnet.*|ue4prereq.*|ueprereq.*|cefsharp.*|installer.*)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every runnable .exe inside each game folder (one level below a root), minus installers/crash handlers.</summary>
    public static IEnumerable<string> GameExecutables(IEnumerable<GameRoot> roots, int maxDepth = 5, int maxPerGame = 60)
    {
        foreach (var root in roots)
        {
            IEnumerable<string> gameDirs;
            try { gameDirs = Directory.EnumerateDirectories(root.Path).ToList(); }
            catch { continue; }
            foreach (var game in gameDirs)
            {
                int n = 0;
                foreach (var exe in Walk(game, 0, maxDepth))
                {
                    if (n++ >= maxPerGame) break;
                    yield return exe;
                }
            }
        }
    }

    static IEnumerable<string> Walk(string dir, int depth, int maxDepth)
    {
        string[] files, dirs;
        try { files = Directory.GetFiles(dir, "*.exe"); dirs = Directory.GetDirectories(dir); }
        catch { yield break; }
        foreach (var f in files)
            if (!SkipExe.IsMatch(Path.GetFileName(f))) yield return f;
        if (depth >= maxDepth) yield break;
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (SkipDirs.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var f in Walk(d, depth + 1, maxDepth)) yield return f;
        }
    }

    public static GameRoot? RootOf(string path, IEnumerable<GameRoot> roots) =>
        roots.FirstOrDefault(r => path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase));
}
