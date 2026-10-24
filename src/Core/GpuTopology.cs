using System.Runtime.InteropServices;
using GpuSwitcher.Interop;

namespace GpuSwitcher;

public enum AdapterKind { Discrete, Integrated, Virtual, Software }

public sealed class GpuAdapter
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required uint VendorId { get; init; }
    public required uint DeviceId { get; init; }
    public required uint SubSysId { get; init; }
    public required long Luid { get; init; }
    public required ulong DedicatedVideoMemory { get; init; }
    public required ulong SharedSystemMemory { get; init; }
    public required AdapterKind Kind { get; init; }
    public List<string> Monitors { get; } = new();
    public bool DrivesPrimaryDisplay { get; set; }
    /// <summary>D3DKMT_ADAPTERTYPE bits, or null if the kernel could not be asked.</summary>
    public uint? KmtFlags { get; init; }
    public string KmtText => KmtFlags is not { } f ? "?" : string.Join("|", new[]
    {
        (f & 0x01) != 0 ? "render" : null, (f & 0x02) != 0 ? "display" : null, (f & 0x04) != 0 ? "software" : null,
        (f & 0x10) != 0 ? "hybrid-discrete" : null, (f & 0x20) != 0 ? "hybrid-integrated" : null,
        (f & 0x40) != 0 ? "indirect-display" : null, (f & 0x80) != 0 ? "paravirtual" : null,
    }.Where(s => s != null));

    /// <summary>PCI location from D3DKMT (bus/device/function): tells identical cards apart and survives reboots.</summary>
    public uint? PciBus { get; init; }
    public uint? PciDevice { get; init; }
    public uint? PciFunction { get; init; }
    /// <summary>Another routable adapter has the same <see cref="Key"/> (two identical cards).</summary>
    public bool IsTwin { get; set; }

    /// <summary>The VEN&amp;DEV&amp;SUBSYS string Windows uses for SpecificAdapter=. Identical cards share it.</summary>
    public string Key => FormatKey(VendorId, DeviceId, SubSysId);
    public string Location => PciBus is { } b ? $"{b}.{PciDevice}.{PciFunction}" : $"luid{Luid:X}";
    /// <summary>Unique, stable identity: key plus PCI slot. What roles are stored as.</summary>
    public string Id => $"{Key}@{Location}";
    public string LuidHex => $"0x{Luid:X8}";
    public bool IsRoutable => Kind is AdapterKind.Discrete or AdapterKind.Integrated;
    public string VendorName => VendorId switch
    {
        0x10DE => "NVIDIA", 0x1002 => "AMD", 0x1022 => "AMD", 0x8086 => "Intel", 0x1414 => "Microsoft",
        0x5143 => "Qualcomm", _ => $"0x{VendorId:X4}"
    };
    public string ShortName
    {
        get
        {
            var n = Name.Replace("(R)", "").Replace("(TM)", "").Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "Radeon ")
                        .Replace("Intel  ", "Intel ").Replace("Graphics", "").Trim();
            while (n.Contains("  ")) n = n.Replace("  ", " ");
            return IsTwin && PciBus is { } bus ? $"{n} (bus {bus})" : n;
        }
    }

    public static string FormatKey(uint ven, uint dev, uint subsys) => $"{ven:X4}&{dev:X4}&{subsys:X8}";
    public override string ToString() => $"{Name} [{Key}]";
}

public static unsafe class GpuTopology
{
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
        public long Luid => ((long)LuidHigh << 32) | LuidLow;
        public string Name { get { fixed (char* p = Description) return new string(p).TrimEnd('\0').Trim(); } }
    }

    // IDXGIFactory1 vtable: 12 = EnumAdapters1.  IDXGIAdapter1 vtable: 10 = GetDesc1.
    internal static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
        IntPtr a; int hr = fn(factory, index, &a); adapter = a; return hr;
    }

    internal static int GetDesc1(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)(*(IntPtr**)adapter)[10];
        DXGI_ADAPTER_DESC1 d; int hr = fn(adapter, &d); desc = d; return hr;
    }

    /// <summary>Adapters in plain DXGI enumeration order (as seen by this process).</summary>
    internal static List<DXGI_ADAPTER_DESC1> EnumerateRaw()
    {
        var list = new List<DXGI_ADAPTER_DESC1>();
        int hr = Native.CreateDXGIFactory1(IID_IDXGIFactory1, out var factory);
        if (hr < 0) throw new COMException("CreateDXGIFactory1 failed", hr);
        try
        {
            for (uint i = 0; ; i++)
            {
                hr = EnumAdapters1(factory, i, out var adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (hr < 0) throw new COMException("EnumAdapters1 failed", hr);
                try { if (GetDesc1(adapter, out var d) >= 0) list.Add(d); }
                finally { Native.Release(adapter); }
            }
        }
        finally { Native.Release(factory); }
        return list;
    }

    /// <summary>All physical/virtual adapters with monitor attachment. Order = DXGI order with duplicates (same LUID) removed.</summary>
    public static List<GpuAdapter> Enumerate()
    {
        var result = new List<GpuAdapter>();
        var seen = new HashSet<long>();
        foreach (var d in EnumerateRaw())
        {
            if (!seen.Add(d.Luid)) continue;
            var (kmt, addr) = QueryKmt(d.Luid);
            result.Add(new GpuAdapter
            {
                PciBus = addr?.bus,
                PciDevice = addr?.dev,
                PciFunction = addr?.fn,
                Index = result.Count,
                Name = d.Name,
                VendorId = d.VendorId,
                DeviceId = d.DeviceId,
                SubSysId = d.SubSysId,
                Luid = d.Luid,
                DedicatedVideoMemory = d.DedicatedVideoMemory,
                SharedSystemMemory = d.SharedSystemMemory,
                Kind = Classify(d, kmt),
                KmtFlags = kmt,
            });
        }
        AttachMonitors(result);
        MarkTwins(result);
        return result;
    }

    /// <summary>Flags routable adapters that share a VEN&amp;DEV&amp;SUBSYS key with another routable adapter.</summary>
    public static void MarkTwins(IEnumerable<GpuAdapter> adapters)
    {
        foreach (var g in adapters.Where(a => a.IsRoutable).GroupBy(a => a.Key))
            foreach (var a in g) a.IsTwin = g.Count() > 1;
    }

    /// <summary>D3DKMT_ADAPTERTYPE (RenderSupported, IndirectDisplayDevice, HybridIntegrated…) and PCI address of an adapter.</summary>
    static (uint? flags, (uint bus, uint dev, uint fn)? address) QueryKmt(long luid)
    {
        var open = new Native.D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = new Native.LUID { LowPart = (uint)luid, HighPart = (int)(luid >> 32) } };
        if (Native.D3DKMTOpenAdapterFromLuid(ref open) != 0) return (null, null);
        try
        {
            uint flags = 0;
            var q = new Native.D3DKMT_QUERYADAPTERINFO { hAdapter = open.hAdapter, Type = Native.KMTQAITYPE_ADAPTERTYPE, pPrivateDriverData = &flags, PrivateDriverDataSize = 4 };
            uint? f = Native.D3DKMTQueryAdapterInfo(ref q) == 0 ? flags : null;

            uint* addr = stackalloc uint[3];   // D3DKMT_ADAPTERADDRESS { BusNumber, DeviceNumber, FunctionNumber }
            q = new Native.D3DKMT_QUERYADAPTERINFO { hAdapter = open.hAdapter, Type = Native.KMTQAITYPE_ADAPTERADDRESS, pPrivateDriverData = addr, PrivateDriverDataSize = 12 };
            // Virtual/software adapters answer with all-ones: no PCI slot.
            (uint, uint, uint)? a = Native.D3DKMTQueryAdapterInfo(ref q) == 0 && addr[0] != uint.MaxValue ? (addr[0], addr[1], addr[2]) : null;
            return (f, a);
        }
        finally
        {
            var close = new Native.D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
            Native.D3DKMTCloseAdapter(ref close);
        }
    }

    static AdapterKind Classify(in DXGI_ADAPTER_DESC1 d, uint? kmt)
    {
        if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || d.VendorId == 0x1414) return AdapterKind.Software;
        if (kmt is { } k)
        {
            if ((k & 0x04) != 0) return AdapterKind.Software;
            // Indirect-display (Parsec/virtual monitors) and display-only adapters can't render: never route to them.
            if ((k & 0x01) == 0 || (k & 0x40) != 0) return AdapterKind.Virtual;
            if ((k & 0x20) != 0) return AdapterKind.Integrated;
            if ((k & 0x10) != 0) return AdapterKind.Discrete;
        }
        const ulong GiB = 1UL << 30;
        var name = d.Name;
        if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Indirect", StringComparison.OrdinalIgnoreCase)
            || (d.DedicatedVideoMemory == 0 && d.VendorId is not (0x8086 or 0x1002 or 0x1022 or 0x10DE)))
            return AdapterKind.Virtual;
        // Integrated GPUs carve out a small (or zero) VRAM aperture and live on system memory.
        if (d.VendorId == 0x8086 && d.DedicatedVideoMemory <= GiB) return AdapterKind.Integrated;
        if (d.DedicatedVideoMemory <= GiB && d.SharedSystemMemory > d.DedicatedVideoMemory) return AdapterKind.Integrated;
        return AdapterKind.Discrete;
    }

    static void AttachMonitors(List<GpuAdapter> adapters)
    {
        string? primaryGdi = null;
        var mon = Native.MonitorFromPoint(default, Native.MONITOR_DEFAULTTOPRIMARY);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (mon != IntPtr.Zero && Native.GetMonitorInfoW(mon, ref mi)) primaryGdi = mi.szDevice;

        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return;
        var paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[nm];
        if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return;

        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var adapter = adapters.FirstOrDefault(a => a.Luid == p.sourceInfo.adapterId.Value)
                       ?? adapters.FirstOrDefault(a => a.Luid == p.targetInfo.adapterId.Value);
            if (adapter == null) continue;

            var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = sizeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = p.targetInfo.adapterId;
            target.header.id = p.targetInfo.id;
            string monitorName = "Display";
            if (Native.DisplayConfigGetDeviceInfo(&target) == 0)
            {
                var n = new string(target.monitorFriendlyDeviceName).TrimEnd('\0');
                if (n.Length > 0) monitorName = n;
            }

            var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = sizeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = p.sourceInfo.adapterId;
            source.header.id = p.sourceInfo.id;
            if (Native.DisplayConfigGetDeviceInfo(&source) == 0)
            {
                var gdi = new string(source.viewGdiDeviceName).TrimEnd('\0');
                if (primaryGdi != null && string.Equals(gdi, primaryGdi, StringComparison.OrdinalIgnoreCase))
                {
                    adapter.DrivesPrimaryDisplay = true;
                    monitorName += " (primary)";
                }
            }
            adapter.Monitors.Add(monitorName);
        }
    }
}
