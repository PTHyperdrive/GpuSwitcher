using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GpuSwitcher.Interop;
using Microsoft.Win32;

namespace GpuSwitcher;

/// <summary>What the SYSTEM service needs to act for a logged-on user: their GpuSwitcher folder and a way to start
/// the tray app in their session (with their normal, non-elevated token).</summary>
public static class UserSessions
{
    /// <summary>%APPDATA%\GpuSwitcher of the account <paramref name="sid"/>, resolved from the registry (works from SYSTEM).</summary>
    public static string? GpuSwitcherHome(string sid)
    {
        var profile = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}")
                              ?.GetValue("ProfileImagePath") as string;
        if (string.IsNullOrEmpty(profile)) return null;
        profile = Environment.ExpandEnvironmentVariables(profile);
        string? appData = null;
        using (var usf = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
            appData = usf?.GetValue("AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        appData = (appData ?? @"%USERPROFILE%\AppData\Roaming").Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
        if (appData.Contains('%')) appData = Path.Combine(profile, "AppData", "Roaming");
        return Path.Combine(appData, "GpuSwitcher");
    }

    public static List<uint> ActiveSessions()
    {
        var list = new List<uint>();
        if (!Native.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var mem, out var count)) return list;
        try
        {
            int size = Marshal.SizeOf<Native.WTS_SESSION_INFOW>();
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<Native.WTS_SESSION_INFOW>(mem + i * size);
                if (s.State == Native.WTSActive && s.SessionId != 0) list.Add(s.SessionId);
            }
        }
        finally { Native.WTSFreeMemory(mem); }
        return list;
    }

    public static string? SidOfSession(uint session)
    {
        if (!Native.WTSQueryUserToken(session, out var token)) return null;
        try { return ProcessUtil.TokenSid(token); }
        finally { Native.CloseHandle(token); }
    }

    /// <summary>Reads the session user's config as that user and reports whether they want the tray app at logon.</summary>
    public static bool WantsTrayAtLogon(uint session)
    {
        if (!Native.WTSQueryUserToken(session, out var raw)) return false;
        using var token = new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(raw);
        var sid = ProcessUtil.TokenSid(raw);
        var home = sid == null ? null : GpuSwitcherHome(sid);
        if (home == null) return false;
        return System.Security.Principal.WindowsIdentity.RunImpersonated(token, () =>
        {
            var file = new ConfigFile(home);
            return file.Exists && file.Load().TrayAtLogon;
        });
    }

    public static bool IsRunningInSession(string processName, uint session) =>
        Process.GetProcessesByName(processName).Any(p => { try { return p.SessionId == session; } finally { p.Dispose(); } });

    /// <summary>Starts <paramref name="exe"/> on the user's desktop in <paramref name="session"/> with the token
    /// Explorer would use (the limited one for admins), so the tray app never runs elevated by accident.</summary>
    public static int Launch(uint session, string exe, string arguments)
    {
        if (!Native.WTSQueryUserToken(session, out var token))
            throw new InvalidOperationException($"WTSQueryUserToken({session}) failed ({Marshal.GetLastWin32Error()})");
        IntPtr env = IntPtr.Zero;
        try
        {
            Native.CreateEnvironmentBlock(out env, token, false);
            var si = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{exe}\" {arguments}");
            if (!Native.CreateProcessAsUserW(token, exe, cmd, IntPtr.Zero, IntPtr.Zero, false, Native.CREATE_UNICODE_ENVIRONMENT, env,
                    Path.GetDirectoryName(exe), ref si, out var pi))
                throw new InvalidOperationException($"CreateProcessAsUser failed ({Marshal.GetLastWin32Error()})");
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
            Native.CloseHandle(token);
        }
    }
}
