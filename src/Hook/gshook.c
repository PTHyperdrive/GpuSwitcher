// gshook.dll — GpuSwitcher in-process adapter filter.
//
// Injected (never into games) by GpuSwitcher. GsInit() patches the vtable shared by every DXGI
// factory object in the process, so EnumAdapters / EnumAdapters1 / EnumAdapterByGpuPreference
// return the assigned GPU first (Reorder) or as the only hardware GPU (Exclusive). D3D11/D3D12
// "default adapter" device creation and the Vulkan loader's device sort both go through these.
//
// No code is generated or patched: only three data pointers in dxgi.dll's vtable are swapped.

#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define INITGUID
#include <windows.h>
#include <dxgi1_6.h>

#define GS_MAGIC   0x31575347u   /* 'GSW1' */
#define GS_REORDER   1u
#define GS_EXCLUSIVE 2u
#define MAX_ADAPTERS 16

#define ST_FACTORY_HOOKED   0x0001u
#define ST_PREF_HOOKED      0x0002u
#define ST_TARGET_PRESENT   0x0004u
#define ST_ALREADY          0x0008u
#define ST_ERR_CONFIG       0xE0000001u
#define ST_ERR_DXGI         0xE0000002u
#define ST_ERR_PROTECT      0xE0000003u

typedef struct GS_CONFIG {
    DWORD magic;
    DWORD version;
    DWORD mode;
    DWORD luidLow;
    LONG  luidHigh;
} GS_CONFIG;

typedef HRESULT (STDMETHODCALLTYPE *PFN_EnumAdapters)(IDXGIFactory *, UINT, IDXGIAdapter **);
typedef HRESULT (STDMETHODCALLTYPE *PFN_EnumAdapters1)(IDXGIFactory1 *, UINT, IDXGIAdapter1 **);
typedef HRESULT (STDMETHODCALLTYPE *PFN_EnumByPref)(IDXGIFactory6 *, UINT, DXGI_GPU_PREFERENCE, REFIID, void **);

static PFN_EnumAdapters  o_EnumAdapters;
static PFN_EnumAdapters1 o_EnumAdapters1;
static PFN_EnumByPref    o_EnumByPref;

static volatile LONG g_installed;
static volatile DWORD g_mode;
static LUID  g_target;
static DWORD g_tls = TLS_OUT_OF_INDEXES;

static BOOL InHook(void)   { return g_tls != TLS_OUT_OF_INDEXES && TlsGetValue(g_tls) != NULL; }
static void EnterHook(void) { TlsSetValue(g_tls, (LPVOID)1); }
static void LeaveHook(void) { TlsSetValue(g_tls, NULL); }
static BOOL IsTarget(LUID l) { return l.LowPart == g_target.LowPart && l.HighPart == g_target.HighPart; }

/* Given the real adapters (LUID + flags) in their original order, produce the visible order. */
static UINT Remap(const LUID *luids, const UINT *flags, UINT n, UINT *order)
{
    UINT m = 0, t = MAX_ADAPTERS, i;
    for (i = 0; i < n; i++) if (IsTarget(luids[i])) { t = i; break; }
    if (t == MAX_ADAPTERS) {               /* assigned GPU not present: stay out of the way */
        for (i = 0; i < n; i++) order[i] = i;
        return n;
    }
    order[m++] = t;
    for (i = 0; i < n; i++) {
        if (i == t) continue;
        if (g_mode == GS_REORDER || (flags[i] & DXGI_ADAPTER_FLAG_SOFTWARE)) order[m++] = i;
    }
    return m;
}

static UINT RealOrder(IDXGIFactory1 *f, LUID *luids, UINT *flags)
{
    UINT n = 0, i;
    for (i = 0; i < MAX_ADAPTERS; i++) {
        IDXGIAdapter1 *a = NULL;
        DXGI_ADAPTER_DESC1 d;
        if (FAILED(o_EnumAdapters1(f, i, &a)) || !a) break;
        ZeroMemory(&d, sizeof d);
        IDXGIAdapter1_GetDesc1(a, &d);
        IDXGIAdapter1_Release(a);
        luids[n] = d.AdapterLuid;
        flags[n] = d.Flags;
        n++;
    }
    return n;
}

static HRESULT STDMETHODCALLTYPE h_EnumAdapters1(IDXGIFactory1 *f, UINT index, IDXGIAdapter1 **pp)
{
    LUID luids[MAX_ADAPTERS]; UINT flags[MAX_ADAPTERS], order[MAX_ADAPTERS], n;
    HRESULT hr;
    if (InHook() || !pp) return o_EnumAdapters1(f, index, pp);
    EnterHook();
    n = Remap(luids, flags, RealOrder(f, luids, flags), order);
    if (index >= n) { *pp = NULL; hr = DXGI_ERROR_NOT_FOUND; }
    else hr = o_EnumAdapters1(f, order[index], pp);
    LeaveHook();
    return hr;
}

static HRESULT STDMETHODCALLTYPE h_EnumAdapters(IDXGIFactory *f, UINT index, IDXGIAdapter **pp)
{
    LUID luids[MAX_ADAPTERS]; UINT flags[MAX_ADAPTERS], order[MAX_ADAPTERS], n;
    HRESULT hr;
    if (InHook() || !pp) return o_EnumAdapters(f, index, pp);
    EnterHook();
    n = Remap(luids, flags, RealOrder((IDXGIFactory1 *)f, luids, flags), order);
    if (index >= n) { *pp = NULL; hr = DXGI_ERROR_NOT_FOUND; }
    else hr = o_EnumAdapters(f, order[index], pp);
    LeaveHook();
    return hr;
}

static HRESULT STDMETHODCALLTYPE h_EnumByPref(IDXGIFactory6 *f, UINT index, DXGI_GPU_PREFERENCE pref, REFIID riid, void **ppv)
{
    LUID luids[MAX_ADAPTERS]; UINT flags[MAX_ADAPTERS], order[MAX_ADAPTERS], n = 0, i;
    HRESULT hr;
    if (InHook() || !ppv) return o_EnumByPref(f, index, pref, riid, ppv);
    EnterHook();
    for (i = 0; i < MAX_ADAPTERS; i++) {
        IDXGIAdapter1 *a = NULL;
        DXGI_ADAPTER_DESC1 d;
        if (FAILED(o_EnumByPref(f, i, pref, &IID_IDXGIAdapter1, (void **)&a)) || !a) break;
        ZeroMemory(&d, sizeof d);
        IDXGIAdapter1_GetDesc1(a, &d);
        IDXGIAdapter1_Release(a);
        luids[n] = d.AdapterLuid;
        flags[n] = d.Flags;
        n++;
    }
    n = Remap(luids, flags, n, order);
    if (index >= n) { *ppv = NULL; hr = DXGI_ERROR_NOT_FOUND; }
    else hr = o_EnumByPref(f, order[index], pref, riid, ppv);
    LeaveHook();
    return hr;
}

static BOOL PatchSlot(void **vtbl, int slot, void *hook, void **orig)
{
    DWORD old;
    if (vtbl[slot] == hook) return TRUE;
    if (!VirtualProtect(&vtbl[slot], sizeof(void *), PAGE_READWRITE, &old)) return FALSE;
    *orig = vtbl[slot];
    MemoryBarrier();
    InterlockedExchangePointer(&vtbl[slot], hook);
    VirtualProtect(&vtbl[slot], sizeof(void *), old, &old);
    return TRUE;
}

/* Thread entry for CreateRemoteThread; also callable in-process (tests). */
__declspec(dllexport) DWORD WINAPI GsInit(LPVOID param)
{
    const GS_CONFIG *cfg = (const GS_CONFIG *)param;
    IDXGIFactory1 *f1 = NULL;
    IDXGIFactory6 *f6 = NULL;
    DWORD status = 0;
    void **vtbl;
    UINT i;

    if (!cfg || cfg->magic != GS_MAGIC || cfg->version != 1 || (cfg->mode != GS_REORDER && cfg->mode != GS_EXCLUSIVE))
        return ST_ERR_CONFIG;

    g_target.LowPart = cfg->luidLow;
    g_target.HighPart = cfg->luidHigh;
    g_mode = cfg->mode;
    if (InterlockedExchange(&g_installed, 1)) return ST_ALREADY | ST_FACTORY_HOOKED;   /* retarget only */

    if (g_tls == TLS_OUT_OF_INDEXES) g_tls = TlsAlloc();
    if (FAILED(CreateDXGIFactory1(&IID_IDXGIFactory1, (void **)&f1)) || !f1) { g_installed = 0; return ST_ERR_DXGI; }

    /* Every CDXGIFactory shares one vtable: IDXGIFactory(7)=EnumAdapters, IDXGIFactory1(12)=EnumAdapters1,
       IDXGIFactory6(29)=EnumAdapterByGpuPreference. */
    vtbl = *(void ***)f1;
    for (i = 0; i < MAX_ADAPTERS; i++) {
        IDXGIAdapter1 *a = NULL; DXGI_ADAPTER_DESC1 d;
        if (FAILED(IDXGIFactory1_EnumAdapters1(f1, i, &a)) || !a) break;
        ZeroMemory(&d, sizeof d);
        IDXGIAdapter1_GetDesc1(a, &d);
        IDXGIAdapter1_Release(a);
        if (IsTarget(d.AdapterLuid)) status |= ST_TARGET_PRESENT;
    }

    if (!PatchSlot(vtbl, 12, (void *)h_EnumAdapters1, (void **)&o_EnumAdapters1) ||
        !PatchSlot(vtbl, 7, (void *)h_EnumAdapters, (void **)&o_EnumAdapters)) {
        IDXGIFactory1_Release(f1);
        g_installed = 0;
        return ST_ERR_PROTECT;
    }
    status |= ST_FACTORY_HOOKED;

    if (SUCCEEDED(IDXGIFactory1_QueryInterface(f1, &IID_IDXGIFactory6, (void **)&f6)) && f6) {
        void **vtbl6 = *(void ***)f6;
        if (PatchSlot(vtbl6, 29, (void *)h_EnumByPref, (void **)&o_EnumByPref)) status |= ST_PREF_HOOKED;
        IDXGIFactory6_Release(f6);
    }
    IDXGIFactory1_Release(f1);
    return status;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        HMODULE self;
        DisableThreadLibraryCalls(inst);
        /* vtables will point into this module: never allow it to unload */
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, (LPCWSTR)(void *)DllMain, &self);
    }
    return TRUE;
}
