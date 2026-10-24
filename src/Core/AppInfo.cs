using System.Reflection;

namespace GpuSwitcher;

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "?";

    public const string RepoUrl = "https://github.com/PTHyperdrive/GpuSwitcher";
}
