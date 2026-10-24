// gsinject.exe <pid> <mode 1=reorder|2=exclusive> <luid-hex> <full path to gshook dll>
//
// Same-bitness injector. GpuSwitcher injects 64-bit processes itself and uses the 32-bit build of
// this helper for WOW64 processes. Prints the GsInit status word; exit code 0 = hooked.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <wchar.h>

typedef struct GS_CONFIG { DWORD magic, version, mode, luidLow; LONG luidHigh; } GS_CONFIG;

static int fail(const char *what) { printf("error %s %lu\n", what, GetLastError()); return 2; }

typedef LONG (NTAPI *PFN_NtCreateThreadEx)(PHANDLE, ACCESS_MASK, PVOID, HANDLE, PVOID, PVOID, ULONG, SIZE_T, SIZE_T, SIZE_T, PVOID);

/* NtCreateThreadEx works across sessions (the service runs in session 0); CreateRemoteThread does not. */
static HANDLE RemoteThread(HANDLE proc, LPTHREAD_START_ROUTINE start, LPVOID param)
{
    HANDLE th = NULL;
    PFN_NtCreateThreadEx create = (PFN_NtCreateThreadEx)(void *)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtCreateThreadEx");
    if (create && create(&th, 0x1FFFFF, NULL, proc, (PVOID)start, param, 0, 0, 0, 0, NULL) >= 0 && th) return th;
    return CreateRemoteThread(proc, NULL, 0, start, param, 0, NULL);
}

static BYTE *FindRemoteModule(HANDLE proc, const wchar_t *path)
{
    HMODULE mods[1024]; DWORD needed = 0, i;
    if (!EnumProcessModulesEx(proc, mods, sizeof mods, &needed, LIST_MODULES_DEFAULT)) return NULL;
    for (i = 0; i < needed / sizeof(HMODULE) && i < 1024; i++) {
        wchar_t name[MAX_PATH * 2];
        if (GetModuleFileNameExW(proc, mods[i], name, MAX_PATH * 2) && _wcsicmp(name, path) == 0) return (BYTE *)mods[i];
    }
    return NULL;
}

int wmain(int argc, wchar_t **argv)
{
    DWORD pid, mode, status = 0, code = 0;
    unsigned long long luid;
    const wchar_t *dll;
    HMODULE local; FARPROC init; SIZE_T rva, pathBytes;
    HANDLE proc, th;
    BYTE *remote, *remoteBase;
    GS_CONFIG cfg;
    LPTHREAD_START_ROUTINE loadLib;

    if (argc != 5) { printf("usage: gsinject <pid> <mode> <luid-hex> <dll>\n"); return 64; }
    pid = wcstoul(argv[1], NULL, 10);
    mode = wcstoul(argv[2], NULL, 10);
    luid = _wcstoui64(argv[3], NULL, 16);
    dll = argv[4];

    local = LoadLibraryW(dll);
    if (!local) return fail("load-local");
    init = GetProcAddress(local, "GsInit");
    if (!init) return fail("export");
    rva = (BYTE *)init - (BYTE *)local;

    proc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                       PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);
    if (!proc) return fail("open");

    remoteBase = FindRemoteModule(proc, dll);
    pathBytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    remote = (BYTE *)VirtualAllocEx(proc, NULL, pathBytes + 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return fail("alloc");

    if (!remoteBase) {
        loadLib = (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        if (!WriteProcessMemory(proc, remote, dll, pathBytes, NULL)) return fail("write-path");
        th = RemoteThread(proc, loadLib, remote);
        if (!th) return fail("thread-load");
        WaitForSingleObject(th, 10000);
        CloseHandle(th);
        remoteBase = FindRemoteModule(proc, dll);
        if (!remoteBase) { printf("error not-loaded 0\n"); return 3; }
    }

    cfg.magic = 0x31575347u; cfg.version = 1; cfg.mode = mode;
    cfg.luidLow = (DWORD)(luid & 0xFFFFFFFFu); cfg.luidHigh = (LONG)(luid >> 32);
    if (!WriteProcessMemory(proc, remote + ((pathBytes + 15) & ~(SIZE_T)15), &cfg, sizeof cfg, NULL)) return fail("write-cfg");
    th = RemoteThread(proc, (LPTHREAD_START_ROUTINE)(remoteBase + rva), remote + ((pathBytes + 15) & ~(SIZE_T)15));
    if (!th) return fail("thread-init");
    WaitForSingleObject(th, 10000);
    GetExitCodeThread(th, &status);
    CloseHandle(th);
    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(proc);

    printf("status %08lX\n", status);
    code = (status >= 0xE0000000u || status == STILL_ACTIVE) ? 1 : 0;
    return (int)code;
}
