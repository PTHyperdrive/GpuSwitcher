using System.Diagnostics;
using System.Runtime.InteropServices;
using GpuSwitcher.Interop;

namespace GpuSwitcher;

public sealed record InjectResult(bool Ok, uint Status, string Message)
{
    public const uint TargetPresent = 0x4, PrefHooked = 0x2, FactoryHooked = 0x1, Already = 0x8;
}

/// <summary>Loads gshook{32,64}.dll into a running process and calls GsInit with the adapter to expose.</summary>
public static unsafe class Injector
{
    const uint Magic = 0x31575347;

    [StructLayout(LayoutKind.Sequential)]
    struct GsConfig { public uint Magic, Version, Mode, LuidLow; public int LuidHigh; }

    public static string NativeDir { get; } = ResolveNativeDir();

    /// <summary>native\ next to the exe when it is there (release zip, installed copy) or when the exe sits in its own
    /// GpuSwitcher folder; otherwise a per-user folder, so a lone exe in Downloads never litters that folder.</summary>
    static string ResolveNativeDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "native");
        if (File.Exists(Path.Combine(local, "gshook64.dll"))) return local;
        try { if (BootGuardService.IsDedicatedFolder(AppContext.BaseDirectory)) return local; } catch { /* unreadable: fall through */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuSwitcher", "native", AppInfo.Version);
    }
    public static string Hook64 => Path.Combine(NativeDir, "gshook64.dll");
    public static string Hook32 => Path.Combine(NativeDir, "gshook32.dll");
    public static string Helper32 => Path.Combine(NativeDir, "gsinject32.exe");
    public static bool Available => EnsureNative();

    static readonly string[] NativeFiles = { "gshook64.dll", "gshook32.dll", "gsinject32.exe", "gsinject64.exe" };
    static int _extracted;

    /// <summary>The hook binaries normally sit in native\ next to the exe. A lone downloaded .exe carries them as
    /// embedded resources and unpacks them on first use (when the folder is writable).</summary>
    public static bool EnsureNative()
    {
        if (File.Exists(Hook64)) return true;
        if (Interlocked.Exchange(ref _extracted, 1) == 1) return File.Exists(Hook64);
        try
        {
            var asm = typeof(Injector).Assembly;
            var dir = Directory.CreateDirectory(NativeDir);
            // Sandboxed (restricted-token) and packaged (AppContainer) apps must be able to load the hook from here.
            var acl = dir.GetAccessControl();
            foreach (var sid in new[] { "S-1-5-32-545", "S-1-15-2-1", "S-1-15-2-2" })
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid),
                    System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            try { dir.SetAccessControl(acl); } catch (UnauthorizedAccessException) { /* not the owner: inherited ACL stays */ }
            foreach (var name in NativeFiles)
            {
                using var src = asm.GetManifestResourceStream("native/" + name);
                var dest = Path.Combine(NativeDir, name);
                if (src == null || File.Exists(dest)) continue;
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* read-only folder: registry routing still works */ }
        return File.Exists(Hook64);
    }

    static IntPtr _localHook;
    static nint _initRva;
    static readonly object Gate = new();

    public static InjectResult Inject(ProcInfo proc, long targetLuid, InjectMode mode)
    {
        if (mode == InjectMode.Off) return new(false, 0, "injection off");
        if (!Environment.Is64BitProcess) return new(false, 0, "GpuSwitcher must run as a 64-bit process to inject");
        return proc.IsWow64 ? InjectViaHelper(proc.Pid, targetLuid, mode) : Inject64(proc.Pid, targetLuid, mode);
    }

    static InjectResult InjectViaHelper(int pid, long luid, InjectMode mode)
    {
        if (!File.Exists(Helper32) || !File.Exists(Hook32)) return new(false, 0, "32-bit hook not installed");
        var psi = new ProcessStartInfo(Helper32, $"{pid} {(int)mode} {luid:X} \"{Hook32}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(15000);
        if (output.StartsWith("status ") && uint.TryParse(output.AsSpan(7), System.Globalization.NumberStyles.HexNumber, null, out var st))
            return Interpret(st);
        return new(false, 0, "helper: " + output);
    }

    static InjectResult Inject64(int pid, long luid, InjectMode mode)
    {
        lock (Gate)
        {
            if (_localHook == IntPtr.Zero)
            {
                if (!File.Exists(Hook64)) return new(false, 0, "gshook64.dll missing");
                _localHook = Native.LoadLibraryW(Hook64);   // DllMain is trivial; GsInit is not called here
                if (_localHook == IntPtr.Zero) return new(false, 0, $"cannot load hook locally ({Marshal.GetLastWin32Error()})");
                _initRva = Native.GetProcAddress(_localHook, "GsInit") - _localHook;
            }
        }

        const uint access = Native.PROCESS_CREATE_THREAD | Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_OPERATION |
                            Native.PROCESS_VM_WRITE | Native.PROCESS_VM_READ | Native.SYNCHRONIZE;
        var h = Native.OpenProcess(access, false, pid);
        if (h == IntPtr.Zero) return new(false, 0, $"cannot open process ({Marshal.GetLastWin32Error()}): elevated or protected");
        IntPtr remote = IntPtr.Zero;
        try
        {
            var path = Hook64 + "\0";
            nuint pathBytes = (nuint)(path.Length * 2);
            nuint cfgOffset = (pathBytes + 15) & ~(nuint)15;
            remote = Native.VirtualAllocEx(h, IntPtr.Zero, cfgOffset + 64, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (remote == IntPtr.Zero) return new(false, 0, $"VirtualAllocEx failed ({Marshal.GetLastWin32Error()})");

            var remoteBase = FindModule(h, Hook64);
            if (remoteBase == IntPtr.Zero)
            {
                fixed (char* p = path)
                    if (!Native.WriteProcessMemory(h, remote, p, pathBytes, out _)) return new(false, 0, "write failed");
                var loadLib = Native.GetProcAddress(Native.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
                if (!RunRemote(h, loadLib, remote, out _)) return new(false, 0, $"LoadLibrary thread failed ({Marshal.GetLastWin32Error()})");
                remoteBase = FindModule(h, Hook64);
                if (remoteBase == IntPtr.Zero) return new(false, 0, "hook DLL was refused by the process (code-integrity / signature policy)");
            }

            var cfg = new GsConfig { Magic = Magic, Version = 1, Mode = (uint)mode, LuidLow = (uint)luid, LuidHigh = (int)(luid >> 32) };
            if (!Native.WriteProcessMemory(h, remote + (nint)cfgOffset, &cfg, (nuint)sizeof(GsConfig), out _)) return new(false, 0, "write failed");
            if (!RunRemote(h, remoteBase + _initRva, remote + (nint)cfgOffset, out var status)) return new(false, 0, "GsInit thread failed");
            return Interpret(status);
        }
        finally
        {
            if (remote != IntPtr.Zero) Native.VirtualFreeEx(h, remote, 0, Native.MEM_RELEASE);
            Native.CloseHandle(h);
        }
    }

    static bool RunRemote(IntPtr proc, IntPtr start, IntPtr param, out uint exitCode)
    {
        exitCode = 0;
        // NtCreateThreadEx also works across sessions (service in session 0 → app in the user's session),
        // where CreateRemoteThread fails with ERROR_NOT_ENOUGH_MEMORY.
        if (Native.NtCreateThreadEx(out var t, 0x1FFFFF, IntPtr.Zero, proc, start, param, 0, 0, 0, 0, IntPtr.Zero) != 0)
            t = Native.CreateRemoteThread(proc, IntPtr.Zero, 0, start, param, 0, out _);
        if (t == IntPtr.Zero) return false;
        try
        {
            if (Native.WaitForSingleObject(t, 10000) != 0) return false;
            return Native.GetExitCodeThread(t, out exitCode);
        }
        finally { Native.CloseHandle(t); }
    }

    static IntPtr FindModule(IntPtr proc, string path)
    {
        var mods = new IntPtr[1024];
        fixed (IntPtr* m = mods)
        {
            if (!Native.K32EnumProcessModulesEx(proc, m, mods.Length * IntPtr.Size, out int needed, Native.LIST_MODULES_DEFAULT)) return IntPtr.Zero;
            char* name = stackalloc char[1024];
            for (int i = 0; i < Math.Min(needed / IntPtr.Size, mods.Length); i++)
            {
                int len = Native.K32GetModuleFileNameExW(proc, mods[i], name, 1024);
                if (len > 0 && string.Equals(new string(name, 0, len), path, StringComparison.OrdinalIgnoreCase)) return mods[i];
            }
        }
        return IntPtr.Zero;
    }

    static InjectResult Interpret(uint st)
    {
        if (st >= 0xE0000000) return new(false, st, st switch
        {
            0xE0000001 => "hook rejected config",
            0xE0000002 => "DXGI unavailable in process",
            0xE0000003 => "could not patch DXGI vtable",
            _ => $"hook error 0x{st:X8}",
        });
        if ((st & InjectResult.TargetPresent) == 0 && (st & InjectResult.Already) == 0)
            return new(true, st, "hooked, but the assigned GPU is not visible to this process");
        return new(true, st, (st & InjectResult.Already) != 0 ? "hook retargeted" : "hooked");
    }

    /// <summary>For tests and the probe: load the hook into this process and call GsInit directly.</summary>
    public static uint HookSelf(long luid, InjectMode mode)
    {
        var lib = Native.LoadLibraryW(Hook64);
        if (lib == IntPtr.Zero) throw new FileNotFoundException("gshook64.dll", Hook64);
        var init = (delegate* unmanaged[Stdcall]<GsConfig*, uint>)Native.GetProcAddress(lib, "GsInit");
        var cfg = new GsConfig { Magic = Magic, Version = 1, Mode = (uint)mode, LuidLow = (uint)luid, LuidHigh = (int)(luid >> 32) };
        return init(&cfg);
    }
}
