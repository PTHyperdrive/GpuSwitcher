using Xunit;

namespace GpuSwitcher.Tests;

/// <summary>Role assignment across hardware this machine may grow into: an AMD card, a second NVIDIA card of another
/// model, an identical twin, cards removed. Adapters are simulated with the values DXGI/D3DKMT report.</summary>
public class RoleResolverTests
{
    static long _luid = 0x10000;

    static GpuAdapter Gpu(string name, uint ven, uint dev, uint subsys, ulong vramGb, AdapterKind kind, uint bus,
                          int monitors = 0, bool primary = false)
    {
        var a = new GpuAdapter
        {
            Index = 0, Name = name, VendorId = ven, DeviceId = dev, SubSysId = subsys, Luid = _luid++,
            DedicatedVideoMemory = vramGb << 30, SharedSystemMemory = 16UL << 30, Kind = kind, PciBus = bus, PciDevice = 0, PciFunction = 0,
        };
        for (int i = 0; i < monitors; i++) a.Monitors.Add($"Monitor {i}");
        a.DrivesPrimaryDisplay = primary;
        return a;
    }

    static GpuAdapter Rtx2060(uint bus = 1, int monitors = 3, bool primary = true) =>
        Gpu("NVIDIA GeForce RTX 2060", 0x10DE, 0x1F03, 0x15817377, 12, AdapterKind.Discrete, bus, monitors, primary);
    static GpuAdapter Uhd770() => Gpu("Intel(R) UHD Graphics 770", 0x8086, 0xA780, 0x7D311462, 0, AdapterKind.Integrated, 0);
    static GpuAdapter ParsecIdd() => Gpu("NVIDIA GeForce RTX 2060", 0x10DE, 0x1F03, 0x15817377, 12, AdapterKind.Virtual, 1);
    static GpuAdapter Rx6600(uint bus = 3, int monitors = 0) => Gpu("AMD Radeon RX 6600", 0x1002, 0x73FF, 0x52401849, 8, AdapterKind.Discrete, bus, monitors);
    static GpuAdapter Gtx1070(uint bus = 4) => Gpu("NVIDIA GeForce GTX 1070", 0x10DE, 0x1B81, 0x85981043, 8, AdapterKind.Discrete, bus);

    static Dictionary<Role, RoleBinding> Resolve(List<GpuAdapter> adapters, AppConfig? cfg = null, bool monitors = true)
    {
        GpuTopology.MarkTwins(adapters);
        return RoleResolver.Resolve(adapters, cfg ?? new AppConfig(), monitors);
    }

    [Fact]
    public void Today_Rtx2060_Uhd770_ParsecDuplicateIgnored()
    {
        var rtx = Rtx2060(); var igpu = Uhd770();
        var r = Resolve(new() { rtx, igpu, ParsecIdd() });
        Assert.Same(rtx, r[Role.Game].Adapter);
        Assert.Same(igpu, r[Role.App].Adapter);
        Assert.Same(igpu, r[Role.Media].Adapter);
        Assert.False(rtx.IsTwin);   // the Parsec indirect display is not a second card
    }

    [Fact]
    public void AmdCardAdded_AppsMoveToIt_MediaStaysOnIntel()
    {
        var rtx = Rtx2060(); var amd = Rx6600(); var igpu = Uhd770();
        var r = Resolve(new() { rtx, igpu, amd });
        Assert.Same(rtx, r[Role.Game].Adapter);
        Assert.Same(amd, r[Role.App].Adapter);
        Assert.Same(igpu, r[Role.Media].Adapter);
    }

    [Fact]
    public void SecondNvidiaOfAnotherModel_AppsMoveToIt()
    {
        var rtx = Rtx2060(); var gtx = Gtx1070(); var igpu = Uhd770();
        var r = Resolve(new() { rtx, gtx, igpu });
        Assert.Same(rtx, r[Role.Game].Adapter);
        Assert.Same(gtx, r[Role.App].Adapter);
        Assert.Same(igpu, r[Role.Media].Adapter);
        Assert.False(gtx.IsTwin);
    }

    [Fact]
    public void IdenticalTwinNvidia_AppsGoToTheOtherCard_BothNamedBySlot()
    {
        var main = Rtx2060(bus: 1, monitors: 3, primary: true);
        var twin = Rtx2060(bus: 5, monitors: 0, primary: false);
        var igpu = Uhd770();
        var r = Resolve(new() { main, twin, igpu, ParsecIdd() });
        Assert.Same(main, r[Role.Game].Adapter);
        Assert.Same(twin, r[Role.App].Adapter);          // not collapsed into one "RTX 2060"
        Assert.True(main.IsTwin && twin.IsTwin);
        Assert.Equal(main.Key, twin.Key);                 // Windows' store can't tell them apart…
        Assert.NotEqual(main.Id, twin.Id);                // …the slot-qualified id can
        Assert.Equal("RTX 2060 (bus 5)", twin.ShortName);
    }

    [Fact]
    public void IdenticalTwins_GamesGetNoRegistryEntry_AppsDo()
    {
        var main = Rtx2060(bus: 1); var twin = Rtx2060(bus: 5, monitors: 0, primary: false); var amd = Rx6600();
        GpuTopology.MarkTwins(new[] { main, twin, amd });
        Assert.False(RoutingEngine.CanNameInRegistry(Role.Game, main));   // Windows could pick either twin for a game
        Assert.True(RoutingEngine.CanNameInRegistry(Role.App, twin));     // the hook pins apps to the exact card
        Assert.True(RoutingEngine.CanNameInRegistry(Role.Game, amd));     // distinct models are unambiguous
    }

    [Fact]
    public void MainGpuIsSticky_WhenABiggerCardIsAddedAndThePrimaryMonitorMoves()
    {
        var rtx = Rtx2060(primary: false, monitors: 2);
        var big = Gpu("NVIDIA GeForce RTX 3090", 0x10DE, 0x2204, 0x38811462, 24, AdapterKind.Discrete, 6, monitors: 1, primary: true);
        var cfg = new AppConfig { ResolvedRoles = { [Role.Game] = rtx.Id } };
        var r = Resolve(new() { rtx, big, Uhd770() }, cfg);
        Assert.Same(rtx, r[Role.Game].Adapter);
        Assert.Same(big, r[Role.App].Adapter);
    }

    [Fact]
    public void MainGpuRemoved_FallsBackToTheCardDrivingThePrimaryMonitor()
    {
        var amd = Rx6600(monitors: 2);
        amd.DrivesPrimaryDisplay = true;
        var cfg = new AppConfig { ResolvedRoles = { [Role.Game] = Rtx2060().Id } };   // that card is gone
        var r = Resolve(new() { amd, Uhd770() }, cfg);
        Assert.Same(amd, r[Role.Game].Adapter);
    }

    [Fact]
    public void ExplicitChoiceBySlotId_PicksTheExactTwin()
    {
        var main = Rtx2060(bus: 1); var twin = Rtx2060(bus: 5, monitors: 0, primary: false);
        var cfg = new AppConfig { RoleGpu = { [Role.Media] = twin.Id } };
        var r = Resolve(new() { main, twin, Uhd770() }, cfg);
        Assert.Same(twin, r[Role.Media].Adapter);
        Assert.Equal("your choice", r[Role.Media].How);
    }

    [Fact]
    public void LegacyBareKeyChoice_StillWorks()
    {
        var rtx = Rtx2060(); var amd = Rx6600();
        var cfg = new AppConfig { RoleGpu = { [Role.App] = amd.Key } };
        var r = Resolve(new() { rtx, amd, Uhd770() }, cfg);
        Assert.Same(amd, r[Role.App].Adapter);
    }

    [Fact]
    public void ChosenCardMissing_FallsBackToAutoAndSaysSo()
    {
        var rtx = Rtx2060(); var igpu = Uhd770();
        var cfg = new AppConfig { RoleGpu = { [Role.App] = Rx6600().Id } };
        var r = Resolve(new() { rtx, igpu }, cfg);
        Assert.Same(igpu, r[Role.App].Adapter);
        Assert.Contains("not installed", r[Role.App].How);
    }

    [Fact]
    public void WindowsDecides_LeavesRoleUnrouted()
    {
        var cfg = new AppConfig { RoleGpu = { [Role.App] = "windows" } };
        var r = Resolve(new() { Rtx2060(), Uhd770() }, cfg);
        Assert.Null(r[Role.App].Adapter);
    }

    [Fact]
    public void NoIntegratedGpu_MediaFollowsApps()
    {
        var rtx = Rtx2060(); var amd = Rx6600();
        var r = Resolve(new() { rtx, amd });
        Assert.Same(amd, r[Role.Media].Adapter);
    }

    [Fact]
    public void Service_UsesWhatTheTrayPublished_EvenThoughDxgiOrderDiffersInSession0()
    {
        // Session 0 lists the iGPU first and sees no monitors at all.
        var igpu = Uhd770(); var rtx = Rtx2060(monitors: 0, primary: false); var amd = Rx6600();
        var cfg = new AppConfig { ResolvedRoles = { [Role.Game] = rtx.Id, [Role.App] = amd.Id, [Role.Media] = igpu.Id } };
        var r = Resolve(new() { igpu, rtx, amd }, cfg, monitors: false);
        Assert.Same(rtx, r[Role.Game].Adapter);
        Assert.Same(amd, r[Role.App].Adapter);
        Assert.Same(igpu, r[Role.Media].Adapter);
    }

    [Fact]
    public void Service_WithTwins_PublishedSlotIdsKeepThemApart()
    {
        var main = Rtx2060(bus: 1, monitors: 0, primary: false); var twin = Rtx2060(bus: 5, monitors: 0, primary: false);
        var cfg = new AppConfig { ResolvedRoles = { [Role.Game] = main.Id, [Role.App] = twin.Id } };
        var r = Resolve(new() { twin, main, Uhd770() }, cfg, monitors: false);   // order flipped on purpose
        Assert.Same(main, r[Role.Game].Adapter);
        Assert.Same(twin, r[Role.App].Adapter);
    }
}
