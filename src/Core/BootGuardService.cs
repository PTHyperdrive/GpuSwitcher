using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace GpuSwitcher;

/// <summary>Install/uninstall/query of the boot-guard Windows service.</summary>
public static class BootGuardService
{
    public const string Name = "GpuSwitcherBootGuard";
    public const string DisplayName = "GPU Switcher Boot Guard";
    public const string ExeName = "GpuSwitcherSvc.exe";

    public enum State { NotInstalled, Stopped, Starting, Running, Stopping, Other }

    public static string BundledExe => Path.Combine(AppContext.BaseDirectory, ExeName);

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static State Query()
    {
        try
        {
            using var sc = new ServiceController(Name);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => State.Running,
                ServiceControllerStatus.Stopped => State.Stopped,
                ServiceControllerStatus.StartPending => State.Starting,
                ServiceControllerStatus.StopPending => State.Stopping,
                _ => State.Other,
            };
        }
        catch (InvalidOperationException) { return State.NotInstalled; }
    }

    public static string? InstalledExe =>
        (Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Name}")?.GetValue("ImagePath") as string)?.Trim('"');

    /// <summary>Creates (or re-points) the auto-start service, locks the install folder down to admins/SYSTEM
    /// (a SYSTEM service must not load binaries a normal user can replace), and starts it.</summary>
    public static IEnumerable<string> Install(string exe)
    {
        if (!IsElevated) throw new UnauthorizedAccessException("installing the service needs an elevated (administrator) prompt");
        if (!File.Exists(exe)) throw new FileNotFoundException($"{ExeName} not found next to gpusw.exe; use the full release zip", exe);
        bool installed = Query() != State.NotInstalled;
        if (installed) Stop();

        // The folder gets locked to admins/SYSTEM. Only ever do that to a folder that is GPU Switcher's alone;
        // anything else (say, Downloads) is left untouched and the app is copied to Program Files first.
        var dir = Path.GetDirectoryName(exe)!;
        if (!IsDedicatedFolder(dir))
        {
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GpuSwitcher");
            yield return CopyApp(dir, target);
            dir = target;
            exe = Path.Combine(target, ExeName);
        }

        // Protect the folder itself, then make everything below inherit from it. (Applying /inheritance:r with /T
        // to files strips their ACL while the (OI)(CI) grants only land on folders, leaving files unreadable.)
        yield return Icacls($"\"{dir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F " +
                            "*S-1-5-32-545:(OI)(CI)RX *S-1-15-2-1:(OI)(CI)RX *S-1-15-2-2:(OI)(CI)RX /Q");
        yield return Icacls($"\"{dir}\\*\" /reset /T /Q");

        yield return installed
            ? Sc($"config {Name} binPath= \"\\\"{exe}\\\"\" start= auto")
            : Sc($"create {Name} binPath= \"\\\"{exe}\\\"\" start= auto DisplayName= \"{DisplayName}\"");
        yield return Sc($"description {Name} \"Routes every new process to its GPU (games / apps / media) from boot, before anything else starts. Never hooks games.\"");
        yield return Sc($"failure {Name} reset= 86400 actions= restart/2000/restart/5000/restart/60000");
        yield return Sc($"start {Name}");
    }

    static readonly string[] OwnFilePatterns =
    {
        "GpuSwitcher*", "gpusw.*", "System.*.dll", "Microsoft.*.dll", "*.deps.json", "*.runtimeconfig.json", "*.pdb",
        "README*", "CHANGELOG*", "LICENSE*", "*.old-*",
    };
    static readonly string[] OwnDirs = { "native", "logs" };

    /// <summary>True when <paramref name="dir"/> is named for GPU Switcher and holds nothing but its files.</summary>
    public static bool IsDedicatedFolder(string dir)
    {
        if (!Path.GetFileName(dir.TrimEnd('\\')).StartsWith("GpuSwitcher", StringComparison.OrdinalIgnoreCase)) return false;
        var ours = OwnFilePatterns.SelectMany(p => Directory.EnumerateFiles(dir, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.EnumerateFiles(dir).Any(f => !ours.Contains(f))) return false;
        return Directory.EnumerateDirectories(dir).All(d => OwnDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Copies exactly the app: the three executables and, for a multi-file build, the assemblies their
    /// .deps.json files name. Nothing else from <paramref name="from"/> (which may be a shared folder) is touched.</summary>
    static string CopyApp(string from, string to)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in new[] { "GpuSwitcher", "gpusw", "GpuSwitcherSvc" })
        {
            names.UnionWith(new[] { $"{app}.exe", $"{app}.dll", $"{app}.deps.json", $"{app}.runtimeconfig.json" });
            var deps = Path.Combine(from, $"{app}.deps.json");
            if (!File.Exists(deps)) continue;   // single-file build: everything is inside the exe
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(deps));
            foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
                foreach (var lib in target.Value.EnumerateObject())
                    if (lib.Value.TryGetProperty("runtime", out var runtime))
                        foreach (var asset in runtime.EnumerateObject()) names.Add(Path.GetFileName(asset.Name));
        }
        Directory.CreateDirectory(Path.Combine(to, "native"));
        foreach (var name in names)
        {
            var f = Path.Combine(from, name);
            if (File.Exists(f)) File.Copy(f, Path.Combine(to, name), true);
        }
        var native = Path.Combine(from, "native");
        if (Directory.Exists(native))
            foreach (var f in Directory.EnumerateFiles(native))
                File.Copy(f, Path.Combine(to, "native", Path.GetFileName(f)), true);
        return $"copied the app to {to} (its own folder is locked down; {from} is left as it is)";
    }

    public static IEnumerable<string> Uninstall()
    {
        if (!IsElevated) throw new UnauthorizedAccessException("removing the service needs an elevated (administrator) prompt");
        if (Query() == State.NotInstalled) { yield return "not installed"; yield break; }
        Stop();
        yield return Sc($"delete {Name}");
    }

    public static void Stop()
    {
        try
        {
            using var sc = new ServiceController(Name);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
        }
        catch (InvalidOperationException) { /* not installed */ }
    }

    public static void Start()
    {
        using var sc = new ServiceController(Name);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
    }

    static string Sc(string args) => Run("sc.exe", args);
    static string Icacls(string args) => Run("icacls.exe", args);

    static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} {args}\n{output}");
        return $"{exe} {args.Split(' ')[0]}: ok";
    }
}
