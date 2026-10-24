using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GpuSwitcher.Interop;

namespace GpuSwitcher;

/// <summary>Asks the graphics APIs, from inside this process, which GPU they would use. Copy gpusw.exe to any
/// path, route that path, and run "probe" to see the routing take effect exactly as an app would.</summary>
public static unsafe class Probe
{
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_IDXGIFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    static readonly Guid IID_IDXGIAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");

    public static string Run(IReadOnlyList<GpuAdapter> adapters, double loadSeconds)
    {
        var sb = new StringBuilder();
        string Name(long luid) => adapters.FirstOrDefault(a => a.Luid == luid)?.ShortName ?? $"LUID 0x{luid:X}";

        sb.AppendLine($"process      : {Environment.ProcessPath}");
        try { sb.AppendLine("EnumAdapters1: " + string.Join(" | ", GpuTopology.EnumerateRaw().Select(d => Name(d.Luid)))); }
        catch (Exception ex) { sb.AppendLine("EnumAdapters1: " + ex.Message); }
        foreach (var (pref, label) in new[] { (0, "unspecified"), (1, "min power"), (2, "high perf") })
        {
            try { sb.AppendLine($"ByPref {label,-11}: " + string.Join(" | ", EnumByPreference(pref).Select(Name))); }
            catch (Exception ex) { sb.AppendLine($"ByPref {label}: {ex.Message}"); }
        }

        long d11 = 0;
        try { d11 = D3D11DefaultAdapter(loadSeconds); sb.AppendLine($"D3D11 default: {Name(d11)}"); }
        catch (Exception ex) { sb.AppendLine("D3D11 default: " + ex.Message); }
        try { sb.AppendLine($"D3D12 default: {Name(D3D12DefaultAdapter())}"); }
        catch (Exception ex) { sb.AppendLine("D3D12 default: " + ex.Message); }
        try { sb.AppendLine("Vulkan order : " + string.Join(" | ", VulkanDevices())); }
        catch (Exception ex) { sb.AppendLine("Vulkan order : " + ex.Message); }
        try { sb.AppendLine("OpenGL       : " + OpenGLRenderer()); }
        catch (Exception ex) { sb.AppendLine("OpenGL       : " + ex.Message); }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits,
            cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits,
            cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr ctx);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);

    /// <summary>GL_RENDERER of a context created on a hidden window (on the primary monitor).</summary>
    public static string OpenGLRenderer()
    {
        var hwnd = CreateWindowExW(0, "STATIC", "gpusw-gl", 0x80000000 /*WS_POPUP*/, 0, 0, 64, 64, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("no window");
        var dc = GetDC(hwnd);
        try
        {
            var pfd = new PIXELFORMATDESCRIPTOR
            {
                nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1,
                dwFlags = 0x4 | 0x20 | 0x1 /*DRAW_TO_WINDOW|SUPPORT_OPENGL|DOUBLEBUFFER*/, cColorBits = 32, cDepthBits = 24,
            };
            int fmt = ChoosePixelFormat(dc, ref pfd);
            if (fmt == 0 || !SetPixelFormat(dc, fmt, ref pfd)) throw new InvalidOperationException("no pixel format");
            var ctx = wglCreateContext(dc);
            if (ctx == IntPtr.Zero) throw new InvalidOperationException("wglCreateContext failed");
            try
            {
                wglMakeCurrent(dc, ctx);
                var r = Marshal.PtrToStringAnsi(glGetString(0x1F01)) ?? "?";
                var v = Marshal.PtrToStringAnsi(glGetString(0x1F00)) ?? "?";
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                return $"{r} ({v})";
            }
            finally { wglDeleteContext(ctx); }
        }
        finally { ReleaseDC(hwnd, dc); DestroyWindow(hwnd); }
    }

    static List<long> EnumByPreference(int pref)
    {
        var list = new List<long>();
        Marshal.ThrowExceptionForHR(Native.CreateDXGIFactory1(IID_IDXGIFactory1, out var f1));
        try
        {
            if (Native.QueryInterface(f1, IID_IDXGIFactory6, out var f6) < 0) throw new NotSupportedException("no IDXGIFactory6");
            try
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)f6)[29];
                var iid = IID_IDXGIAdapter1;
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr a;
                    if (fn(f6, i, pref, &iid, &a) < 0) break;
                    if (GpuTopology.GetDesc1(a, out var d) >= 0) list.Add(d.Luid);
                    Native.Release(a);
                }
            }
            finally { Native.Release(f6); }
        }
        finally { Native.Release(f1); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    /// <summary>Creates a D3D11 device on the default (null) adapter, optionally loads it, returns its LUID.</summary>
    public static long D3D11DefaultAdapter(double loadSeconds)
    {
        int hr = Native.D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out var dev, out _, out var ctx);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(Native.QueryInterface(dev, IID_IDXGIDevice, out var dxgiDev));
            IntPtr adapter;
            try
            {
                var getAdapter = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(IntPtr**)dxgiDev)[7];
                IntPtr a; Marshal.ThrowExceptionForHR(getAdapter(dxgiDev, &a)); adapter = a;
            }
            finally { Native.Release(dxgiDev); }
            long luid;
            try
            {
                Marshal.ThrowExceptionForHR(Native.QueryInterface(adapter, IID_IDXGIAdapter1, out var a1));
                GpuTopology.GetDesc1(a1, out var d);
                Native.Release(a1);
                luid = d.Luid;
            }
            finally { Native.Release(adapter); }

            if (loadSeconds > 0) Load(dev, ctx, loadSeconds);
            return luid;
        }
        finally { Native.Release(ctx); Native.Release(dev); }
    }

    static void Load(IntPtr dev, IntPtr ctx, double seconds)
    {
        var vt = *(IntPtr**)dev;
        var createTex = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)vt[5];
        var createRtv = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)vt[9];
        var cvt = *(IntPtr**)ctx;
        var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)cvt[47];
        var clear = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)cvt[50];
        var flush = (delegate* unmanaged[Stdcall]<IntPtr, void>)cvt[111];

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 4096, Height = 4096, MipLevels = 1, ArraySize = 1, Format = 28 /*R8G8B8A8_UNORM*/,
            SampleCount = 1, Usage = 0, BindFlags = 0x20 /*RENDER_TARGET*/,
        };
        const int N = 4;   // 4 x 64 MiB
        var tex = new IntPtr[N];
        var rtv = new IntPtr[N];
        try
        {
            for (int i = 0; i < N; i++)
            {
                IntPtr t, r;
                Marshal.ThrowExceptionForHR(createTex(dev, &desc, IntPtr.Zero, &t)); tex[i] = t;
                Marshal.ThrowExceptionForHR(createRtv(dev, t, IntPtr.Zero, &r)); rtv[i] = r;
            }
            var sw = Stopwatch.StartNew();
            float* color = stackalloc float[4];
            int frame = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                for (int i = 0; i < N; i++)
                {
                    color[0] = (frame % 255) / 255f; color[1] = i / 4f; color[2] = 0.5f; color[3] = 1;
                    clear(ctx, rtv[i], color);
                    copy(ctx, tex[(i + 1) % N], tex[i]);
                }
                flush(ctx);
                frame++;
                Thread.Sleep(2);
            }
        }
        finally
        {
            foreach (var r in rtv) Native.Release(r);
            foreach (var t in tex) Native.Release(t);
        }
    }

    [DllImport("d3d12.dll")]
    static extern int D3D12CreateDevice(IntPtr adapter, int minFeatureLevel, in Guid riid, out IntPtr device);

    public static long D3D12DefaultAdapter()
    {
        Marshal.ThrowExceptionForHR(D3D12CreateDevice(IntPtr.Zero, 0xb000, IID_ID3D12Device, out var dev));
        try
        {
            // ID3D12Device::GetAdapterLuid (slot 43) returns the struct through a hidden pointer.
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Native.LUID*, Native.LUID*>)(*(IntPtr**)dev)[43];
            Native.LUID l;
            fn(dev, &l);
            return l.Value;
        }
        finally { Native.Release(dev); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VkInstanceCreateInfo
    {
        public int sType; public IntPtr pNext; public uint flags; public IntPtr pApplicationInfo;
        public uint enabledLayerCount; public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames;
    }

    public static List<string> VulkanDevices()
    {
        var lib = Native.LoadLibraryW("vulkan-1.dll");
        if (lib == IntPtr.Zero) throw new DllNotFoundException("vulkan-1.dll");
        var create = (delegate* unmanaged[Stdcall]<VkInstanceCreateInfo*, IntPtr, IntPtr*, int>)Native.GetProcAddress(lib, "vkCreateInstance");
        var destroy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Native.GetProcAddress(lib, "vkDestroyInstance");
        var enumDev = (delegate* unmanaged[Stdcall]<IntPtr, uint*, IntPtr*, int>)Native.GetProcAddress(lib, "vkEnumeratePhysicalDevices");
        var props = (delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)Native.GetProcAddress(lib, "vkGetPhysicalDeviceProperties");

        var ci = new VkInstanceCreateInfo { sType = 1 };
        IntPtr inst;
        int r = create(&ci, IntPtr.Zero, &inst);
        if (r != 0) throw new InvalidOperationException($"vkCreateInstance = {r}");
        try
        {
            uint n = 0;
            enumDev(inst, &n, null);
            var devs = new IntPtr[n];
            var names = new List<string>();
            fixed (IntPtr* d = devs) enumDev(inst, &n, d);
            byte* buf = stackalloc byte[2048];
            for (int i = 0; i < n; i++)
            {
                props(devs[i], buf);
                names.Add(Marshal.PtrToStringUTF8((IntPtr)(buf + 20)) ?? "?");   // VkPhysicalDeviceProperties.deviceName
            }
            return names;
        }
        finally { destroy(inst, IntPtr.Zero); }
    }
}
