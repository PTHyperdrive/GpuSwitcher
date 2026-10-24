using System.Diagnostics;
using Microsoft.Win32;

namespace GpuSwitcher;

/// <summary>Ordered "Key=Value;" field list, the format Windows uses for every UserGpuPreferences value.</summary>
public sealed class PrefFields
{
    readonly List<KeyValuePair<string, string>> _fields = new();

    public static PrefFields Parse(string? raw)
    {
        var f = new PrefFields();
        if (string.IsNullOrEmpty(raw)) return f;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            f.Set(part[..eq].Trim(), part[(eq + 1)..].Trim());
        }
        return f;
    }

    public string? Get(string key) => _fields.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public void Set(string key, string value)
    {
        int i = _fields.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _fields[i] = new(key, value); else _fields.Add(new(key, value));
    }

    public bool Remove(string key) => _fields.RemoveAll(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
    public bool IsEmpty => _fields.Count == 0;
    public override string ToString() => string.Concat(_fields.Select(p => $"{p.Key}={p.Value};"));
}

public sealed class GpuPreferenceEntry
{
    public required string Target { get; init; }   // exe path or packaged-app AUMID
    public required string Raw { get; init; }
    public PrefFields Fields => PrefFields.Parse(Raw);
    public string? SpecificAdapter => Fields.Get(GpuPreferenceStore.SpecificAdapterField);
    public long? GpuPreference => long.TryParse(Fields.Get(GpuPreferenceStore.GpuPreferenceField), out var v) ? v : null;
    /// <summary>True when the entry actually steers GPU selection (not just AppStatus/AutoHDR bookkeeping).</summary>
    public bool IsRouting => SpecificAdapter != null || GpuPreference is > 0;
    public bool IsPackagedApp => !Target.Contains('\\') && Target.Contains('!');
}

/// <summary>One user's HKCU\Software\Microsoft\DirectX\UserGpuPreferences — the store behind Settings ▸ Display ▸ Graphics.
/// DXGI consults it at device-creation time. The service reaches other users' hives through HKEY_USERS\&lt;SID&gt;
/// (opened per call, never held, so profiles can still unload at log-off).</summary>
public sealed class PreferenceStore
{
    public static PreferenceStore CurrentUser { get; } = new(Registry.CurrentUser, "", "HKCU");
    public static PreferenceStore ForSid(string sid) => new(Registry.Users, sid + "\\", "HKU\\" + sid);

    static readonly HashSet<string> NonAppValues = new(StringComparer.OrdinalIgnoreCase)
    { "DirectXUserGlobalSettings", "GraphicsFeaturesNotificationConfig" };

    readonly RegistryKey _root;
    readonly string _path;
    readonly string _regExeRoot;

    PreferenceStore(RegistryKey root, string prefix, string regExeRoot)
    {
        _root = root;
        _path = prefix + GpuPreferenceStore.KeyPath;
        _regExeRoot = regExeRoot;
    }

    public List<GpuPreferenceEntry> ReadAll()
    {
        var list = new List<GpuPreferenceEntry>();
        using var key = _root.OpenSubKey(_path);
        if (key == null) return list;
        foreach (var name in key.GetValueNames())
        {
            if (NonAppValues.Contains(name) || name.Length == 0) continue;
            if (key.GetValueKind(name) != RegistryValueKind.String) continue;
            list.Add(new GpuPreferenceEntry { Target = name, Raw = key.GetValue(name) as string ?? "" });
        }
        return list;
    }

    public GpuPreferenceEntry? Read(string target)
    {
        using var key = _root.OpenSubKey(_path);
        if (key?.GetValue(target) is not string raw) return null;
        return new GpuPreferenceEntry { Target = target, Raw = raw };
    }

    public string? ReadGlobal()
    {
        using var key = _root.OpenSubKey(_path);
        return key?.GetValue("DirectXUserGlobalSettings") as string;
    }

    /// <summary>Pins <paramref name="target"/> to one adapter, preserving unrelated fields (AppStatus, AutoHDR…).</summary>
    public void SetSpecificAdapter(string target, string adapterKey)
    {
        using var key = _root.CreateSubKey(_path, true);
        var fields = PrefFields.Parse(key.GetValue(target) as string);
        fields.Set(GpuPreferenceStore.SpecificAdapterField, adapterKey);
        fields.Set(GpuPreferenceStore.GpuPreferenceField, GpuPreferenceStore.SpecificAdapterPreference.ToString());
        key.SetValue(target, fields.ToString(), RegistryValueKind.String);
    }

    /// <summary>Removes routing fields; deletes the value entirely when nothing else remains.</summary>
    public void ClearRouting(string target)
    {
        using var key = _root.OpenSubKey(_path, true);
        if (key?.GetValue(target) is not string raw) return;
        var fields = PrefFields.Parse(raw);
        fields.Remove(GpuPreferenceStore.SpecificAdapterField);
        fields.Remove(GpuPreferenceStore.GpuPreferenceField);
        if (fields.IsEmpty) key.DeleteValue(target, false);
        else key.SetValue(target, fields.ToString(), RegistryValueKind.String);
    }

    public void Delete(string target)
    {
        using var key = _root.OpenSubKey(_path, true);
        key?.DeleteValue(target, false);
    }

    /// <summary>Exports the whole key with reg.exe so it can be restored by double-clicking the .reg.</summary>
    public string Backup(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"UserGpuPreferences-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
        var psi = new ProcessStartInfo("reg.exe", $"export \"{_regExeRoot}\\{GpuPreferenceStore.KeyPath}\" \"{file}\" /y")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
        if (p.ExitCode != 0 || !File.Exists(file)) throw new IOException("reg export failed: " + p.StandardError.ReadToEnd());
        return file;
    }
}

/// <summary>The current user's store (what the tray app and CLI use), plus format constants.</summary>
public static class GpuPreferenceStore
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string SpecificAdapterField = "SpecificAdapter";
    public const string GpuPreferenceField = "GpuPreference";
    /// <summary>The GpuPreference value Settings writes together with SpecificAdapter= ("use this exact GPU").</summary>
    public const long SpecificAdapterPreference = 1073741824;

    static PreferenceStore Me => PreferenceStore.CurrentUser;
    public static List<GpuPreferenceEntry> ReadAll() => Me.ReadAll();
    public static GpuPreferenceEntry? Read(string target) => Me.Read(target);
    public static string? ReadGlobal() => Me.ReadGlobal();
    public static void SetSpecificAdapter(string target, string adapterKey) => Me.SetSpecificAdapter(target, adapterKey);
    public static void ClearRouting(string target) => Me.ClearRouting(target);
    public static void Delete(string target) => Me.Delete(target);
    public static string Backup(string directory) => Me.Backup(directory);

    /// <summary>Human description of what an entry asks Windows for.</summary>
    public static string Describe(GpuPreferenceEntry? e, IReadOnlyList<GpuAdapter> adapters)
    {
        if (e == null || !e.IsRouting) return "Windows default";
        if (e.SpecificAdapter is { } k)
        {
            var a = adapters.FirstOrDefault(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            return a != null ? a.ShortName : $"missing GPU {k}";
        }
        return e.GpuPreference switch { 1 => "Power saving", 2 => "High performance", _ => $"GpuPreference={e.GpuPreference}" };
    }
}