using Xunit;

namespace GpuSwitcher.Tests;

/// <summary>The service installer locks its folder down to admins. It must only ever do that to GPU Switcher's own folder.</summary>
public class InstallFolderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "gsw-tests-" + Guid.NewGuid().ToString("N"));

    string Folder(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var f in files)
        {
            var path = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        return dir;
    }

    [Fact]
    public void ExtractedReleaseZip_IsDedicated() =>
        Assert.True(BootGuardService.IsDedicatedFolder(Folder("GpuSwitcher-1.0.0-win-x64",
            "GpuSwitcher.exe", "gpusw.exe", "GpuSwitcherSvc.exe", "README.md", "CHANGELOG.md", @"native\gshook64.dll")));

    [Fact]
    public void MultiFileInstall_IsDedicated() =>
        Assert.True(BootGuardService.IsDedicatedFolder(Folder("GpuSwitcher",
            "GpuSwitcher.exe", "GpuSwitcher.dll", "GpuSwitcher.Core.dll", "gpusw.deps.json", "gpusw.runtimeconfig.json",
            "System.ServiceProcess.ServiceController.dll", @"logs\service.log")));

    [Fact]
    public void DownloadsWithOtherFiles_IsNotDedicated() =>
        Assert.False(BootGuardService.IsDedicatedFolder(Folder("Downloads", "GpuSwitcher.exe", "holiday.jpg")));

    [Fact]
    public void GpuSwitcherNamedFolderWithForeignFile_IsNotDedicated() =>
        Assert.False(BootGuardService.IsDedicatedFolder(Folder("GpuSwitcher", "GpuSwitcher.exe", "notes.txt")));

    [Fact]
    public void GpuSwitcherNamedFolderWithForeignSubfolder_IsNotDedicated() =>
        Assert.False(BootGuardService.IsDedicatedFolder(Folder("GpuSwitcher", "GpuSwitcher.exe", @"Photos\a.jpg")));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }
}
