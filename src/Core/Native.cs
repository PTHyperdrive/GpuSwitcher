using System.Runtime.InteropServices;

namespace GpuSwitcher.Interop;

internal static unsafe class Native
{
    // ---- DXGI / D3D11 -------------------------------------------------------------------
    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    public static uint Release(IntPtr unk)
    {
        if (unk == IntPtr.Zero) return 0;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)unk)[2];
        return fn(unk);
    }

    public static int QueryInterface(IntPtr unk, in Guid iid, out IntPtr result)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)unk)[0];
        IntPtr r;
        fixed (Guid* g = &iid) { int hr = fn(unk, g, &r); result = r; return hr; }
    }

    // ---- Processes ----------------------------------------------------------------------
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, char* buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32EnumProcesses(int* pids, int cb, out int needed);

    [DllImport("kernel32.dll")]
    public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    public static extern int GetApplicationUserModelId(IntPtr h, ref int length, char* buffer);

    [DllImport("kernel32.dll")]
    public static extern bool ReadProcessMemory(IntPtr h, IntPtr address, void* buffer, nint size, out nint read);

    [DllImport("kernel32.dll")]
    public static extern bool IsWow64Process(IntPtr h, out bool wow64);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    public static extern bool ProcessIdToSessionId(int pid, out uint sessionId);

    // ---- Injection ----------------------------------------------------------------------
    public const uint PROCESS_CREATE_THREAD = 0x0002;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
    public const uint LIST_MODULES_DEFAULT = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr address, nuint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr h, IntPtr address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr h, IntPtr address, void* buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attrs, nuint stack, IntPtr start, IntPtr param, uint flags, out uint tid);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeThread(IntPtr thread, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32EnumProcessModulesEx(IntPtr h, IntPtr* modules, int cb, out int needed, uint filter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int K32GetModuleFileNameExW(IntPtr h, IntPtr module, char* name, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int K32GetModuleBaseNameW(IntPtr h, IntPtr module, char* name, int size);

    public const uint LIST_MODULES_ALL = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr h, int infoClass, void* info, int length, out int returnLength);

    public const int ProcessBasicInformation = 0;
    public const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    // ---- Windows ------------------------------------------------------------------------
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint GW_OWNER = 4;
    public const uint WM_CLOSE = 0x0010;

    // ---- cross-session thread creation ------------------------------------------------------
    /// <summary>Unlike CreateRemoteThread, works from session 0 (the service) into a user session.</summary>
    [DllImport("ntdll.dll")]
    public static extern int NtCreateThreadEx(out IntPtr thread, uint access, IntPtr objectAttributes, IntPtr process,
        IntPtr startRoutine, IntPtr argument, uint createFlags, nuint zeroBits, nuint stackSize, nuint maxStackSize, IntPtr attributeList);

    // ---- tokens / users ----------------------------------------------------------------------
    public const uint TOKEN_QUERY = 0x0008, TOKEN_DUPLICATE = 0x0002, TOKEN_ASSIGN_PRIMARY = 0x0001;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, void* info, int length, out int returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);

    public const int TokenUser = 1;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFOW { public uint SessionId; public IntPtr WinStationName; public int State; }
    public const int WTSActive = 0;

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUserW(IntPtr token, string? application, System.Text.StringBuilder commandLine, IntPtr processAttrs,
        IntPtr threadAttrs, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFOW startup, out PROCESS_INFORMATION info);

    // ---- ETW real-time consumer ---------------------------------------------------------------
    // Offsets below were checked against the 10.0.26100 SDK headers with offsetof().
    public const uint WNODE_FLAG_TRACED_GUID = 0x20000;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x100;
    public const uint EVENT_TRACE_USE_MS_FLUSH_TIMER = 0x10;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    public static extern int EnableTraceEx2(ulong handle, in Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll")]
    public static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    public static extern int ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    public static extern int CloseTrace(ulong handle);

    // ---- D3DKMT (kernel adapter facts) --------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public void* pPrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    public const int KMTQAITYPE_ADAPTERADDRESS = 6;
    public const int KMTQAITYPE_ADAPTERTYPE = 15;

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    [DllImport("gdi32.dll")]
    public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    // ---- Monitors / display config ------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; public long Value => ((long)HighPart << 32) | LowPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId;
        public uint connectorInstance;
        public fixed char monitorFriendlyDeviceName[64];
        public fixed char monitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public fixed char viewGdiDeviceName[32];
    }

    public const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(void* request);
}
