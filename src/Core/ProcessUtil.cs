using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuSwitcher.Interop;

namespace GpuSwitcher;

public sealed class ProcInfo
{
    public required int Pid { get; init; }
    public required string? ExePath { get; init; }
    public string? Aumid { get; init; }
    public DateTime StartTime { get; init; }
    public int ParentPid { get; init; }
    public bool IsWow64 { get; init; }
    public int SessionId { get; init; } = -1;
    /// <summary>Session 0: a service. Never on the user's desktop, so never routed.</summary>
    public bool IsService => SessionId == 0;
    public string Name => ExePath != null ? Path.GetFileName(ExePath) : $"pid {Pid}";
    /// <summary>The UserGpuPreferences value name for this process: the AUMID for packaged apps, else the exe path.</summary>
    public string? RegistryTarget => Aumid ?? ExePath;
}

public static unsafe class ProcessUtil
{
    public static int[] EnumeratePids()
    {
        int cap = 2048;
        while (true)
        {
            var buf = new int[cap];
            fixed (int* p = buf)
            {
                if (!Native.K32EnumProcesses(p, cap * 4, out int needed)) return Array.Empty<int>();
                if (needed < cap * 4) return buf.AsSpan(0, needed / 4).ToArray();
            }
            cap *= 2;
        }
    }

    public static ProcInfo? Inspect(int pid)
    {
        if (pid <= 4) return null;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            string? path = null;
            char* buf = stackalloc char[1024];
            int len = 1024;
            if (Native.QueryFullProcessImageNameW(h, 0, buf, ref len)) path = new string(buf, 0, len);

            string? aumid = null;
            int alen = 512;
            char* abuf = stackalloc char[512];
            if (Native.GetApplicationUserModelId(h, ref alen, abuf) == 0 && alen > 1) aumid = new string(abuf, 0, alen - 1);

            DateTime start = default;
            if (Native.GetProcessTimes(h, out var c, out _, out _, out _)) start = DateTime.FromFileTime(c);

            Native.PROCESS_BASIC_INFORMATION pbi;
            int parent = 0;
            if (Native.NtQueryInformationProcess(h, Native.ProcessBasicInformation, &pbi, sizeof(Native.PROCESS_BASIC_INFORMATION), out _) == 0)
                parent = (int)pbi.InheritedFromUniqueProcessId;

            Native.IsWow64Process(h, out var wow);
            int session = Native.ProcessIdToSessionId(pid, out var sid) ? (int)sid : -1;
            return new ProcInfo { Pid = pid, ExePath = path, Aumid = aumid, StartTime = start, ParentPid = parent, IsWow64 = wow, SessionId = session };
        }
        finally { Native.CloseHandle(h); }
    }

    public static string? GetCommandLine(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = 8192;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var mem = Marshal.AllocHGlobal(size);
                try
                {
                    int st = Native.NtQueryInformationProcess(h, Native.ProcessCommandLineInformation, (void*)mem, size, out int ret);
                    if (st == 0)
                    {
                        var us = (Native.UNICODE_STRING*)mem;
                        return new string((char*)us->Buffer, 0, us->Length / 2);
                    }
                    if (ret > size) { size = ret; continue; }
                    return null;
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            return null;
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Reads the target's current directory out of its PEB (same-bitness 64-bit targets only).</summary>
    public static string? GetCurrentDirectory(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (Native.IsWow64Process(h, out var wow) && wow) return null;
            Native.PROCESS_BASIC_INFORMATION pbi;
            if (Native.NtQueryInformationProcess(h, Native.ProcessBasicInformation, &pbi, sizeof(Native.PROCESS_BASIC_INFORMATION), out _) != 0) return null;
            IntPtr pp;
            if (!Native.ReadProcessMemory(h, pbi.PebBaseAddress + 0x20, &pp, IntPtr.Size, out _)) return null;   // PEB.ProcessParameters
            Native.UNICODE_STRING cd;
            if (!Native.ReadProcessMemory(h, pp + 0x38, &cd, sizeof(Native.UNICODE_STRING), out _)) return null; // CurrentDirectory.DosPath
            if (cd.Length == 0 || cd.Length > 4096) return null;
            var chars = new char[cd.Length / 2];
            fixed (char* c = chars)
                if (!Native.ReadProcessMemory(h, cd.Buffer, c, cd.Length, out _)) return null;
            return new string(chars);
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Splits a raw command line into (argv[0], rest) following CommandLineToArgvW's rules for argv[0].</summary>
    public static (string exe, string args) SplitCommandLine(string cmd)
    {
        cmd = cmd.TrimStart();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return (cmd.Trim('"'), "");
            return (cmd[1..end], cmd[(end + 1)..].TrimStart());
        }
        int sp = cmd.IndexOfAny([' ', '\t']);
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].TrimStart());
    }

    public static List<IntPtr> TopLevelWindows(ICollection<int> pids)
    {
        var list = new List<IntPtr>();
        var set = pids as HashSet<int> ?? new HashSet<int>(pids);
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out int pid);
            if (set.Contains(pid) && Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero)
                list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly HashSet<string> GraphicsModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll",
    };

    /// <summary>Name of the first graphics-API DLL loaded in the process, or null. Cheap enough to poll during startup.</summary>
    public static string? LoadedGraphicsApi(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var mods = stackalloc IntPtr[512];
            if (!Native.K32EnumProcessModulesEx(h, mods, 512 * IntPtr.Size, out int needed, Native.LIST_MODULES_ALL)) return null;
            char* name = stackalloc char[260];
            for (int i = 0; i < Math.Min(needed / IntPtr.Size, 512); i++)
            {
                int len = Native.K32GetModuleBaseNameW(h, mods[i], name, 260);
                if (len <= 0) continue;
                var s = new string(name, 0, len);
                if (GraphicsModules.Contains(s)) return s;
            }
            return null;
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>String SID of the account a process runs as (same for its elevated and limited tokens).</summary>
    public static string? GetOwnerSid(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!Native.OpenProcessToken(h, Native.TOKEN_QUERY, out var token)) return null;
            try { return TokenSid(token); }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>The process owner's token, for impersonating them while touching their profile (null if unavailable).</summary>
    public static Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? OpenUserToken(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            const uint TOKEN_IMPERSONATE = 0x0004;
            if (!Native.OpenProcessToken(h, Native.TOKEN_QUERY | Native.TOKEN_DUPLICATE | TOKEN_IMPERSONATE, out var token)) return null;
            return new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(token);
        }
        finally { Native.CloseHandle(h); }
    }

    public static string? TokenSid(IntPtr token)
    {
        byte* buf = stackalloc byte[256];
        if (!Native.GetTokenInformation(token, Native.TokenUser, buf, 256, out _)) return null;
        var sid = *(IntPtr*)buf;   // TOKEN_USER.User.Sid
        if (!Native.ConvertSidToStringSidW(sid, out var str)) return null;
        try { return Marshal.PtrToStringUni(str); }
        finally { Native.LocalFree(str); }
    }

    public static bool IsAlive(int pid)
    {
        var h = Native.OpenProcess(Native.SYNCHRONIZE, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return Native.WaitForSingleObject(h, 0) != 0; } finally { Native.CloseHandle(h); }
    }
}

public sealed record RestartResult(bool Closed, bool Relaunched, string Message);

/// <summary>Gracefully closes every process of one app and relaunches the root process with its original command line.</summary>
public static class AppRestarter
{
    public static RestartResult Restart(IReadOnlyCollection<ProcInfo> group, bool force, TimeSpan timeout)
    {
        if (group.Count == 0) return new(false, false, "No running processes.");
        var pids = group.Select(p => p.Pid).ToHashSet();
        var root = group.Where(p => !pids.Contains(p.ParentPid)).OrderBy(p => p.StartTime).FirstOrDefault() ?? group.First();

        string? aumid = root.Aumid;
        string? cmd = ProcessUtil.GetCommandLine(root.Pid);
        string? cwd = ProcessUtil.GetCurrentDirectory(root.Pid);
        if (aumid == null && (cmd == null || root.ExePath == null))
            return new(false, false, "Cannot read the command line (elevated or protected process?). Run GpuSwitcher elevated or restart the app yourself.");

        var windows = ProcessUtil.TopLevelWindows(pids);
        foreach (var w in windows) Native.PostMessageW(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && pids.Any(ProcessUtil.IsAlive)) Thread.Sleep(150);

        var survivors = pids.Where(ProcessUtil.IsAlive).ToList();
        if (survivors.Count > 0)
        {
            if (!force)
                return new(false, false, windows.Count == 0
                    ? "The app has no window to close (tray/background app). Use force restart."
                    : $"{survivors.Count} process(es) did not exit (unsaved work prompt?). Use force restart.");
            foreach (var pid in survivors)
            {
                try { Process.GetProcessById(pid).Kill(); } catch { /* already gone */ }
            }
            Thread.Sleep(500);
        }

        try
        {
            if (aumid != null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
            }
            else
            {
                var (_, args) = ProcessUtil.SplitCommandLine(cmd!);
                Process.Start(new ProcessStartInfo(root.ExePath!, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = cwd != null && Directory.Exists(cwd) ? cwd : Path.GetDirectoryName(root.ExePath!)!,
                });
            }
            return new(true, true, $"Restarted {root.Name}.");
        }
        catch (Exception ex)
        {
            return new(true, false, $"Closed, but relaunch failed: {ex.Message}");
        }
    }
}
