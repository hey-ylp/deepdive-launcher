using System.Reflection;

namespace DeepDive.Core.Launcher;

/// <summary>
/// B1 占位类型：只用来读取几个核心依赖的实际版本，验证 NuGet 引用链是否成立。
/// 真正的启动逻辑（MinecraftLauncher 封装）留到 B2。
/// </summary>
public static class LibraryInfo
{
    private static string VersionOf<T>() =>
        typeof(T).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>CmlLib.Core 版本。</summary>
    public static string CoreVersion => VersionOf<CmlLib.Core.MinecraftLauncher>();

    /// <summary>CmlLib.Core.Installer.Forge 版本（含 Forge + NeoForge 安装器）。</summary>
    public static string ForgeInstallerVersion => VersionOf<CmlLib.Core.Installer.Forge.ForgeInstaller>();
}
