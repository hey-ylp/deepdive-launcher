namespace McLauncher.Core.Launcher;

/// <summary>模组加载器类型。</summary>
public enum ModLoaderKind
{
    /// <summary>原版，不装任何加载器。</summary>
    Vanilla = 0,

    /// <summary>Forge（含 1.7.10 ~ 1.21.x 的旧版安装器分支）。</summary>
    Forge = 1,

    /// <summary>NeoForge（1.20.1 之后的社区分叉，需要独立安装器）。</summary>
    NeoForge = 2,

    /// <summary>Fabric（轻量，只写版本 JSON + 几个库）。</summary>
    Fabric = 3,
}

/// <summary>某个加载器可用于某 MC 版本的一个构建。</summary>
/// <param name="LoaderVersion">加载器自己的版本号，如 Forge 的 <c>47.4.28</c>、Fabric 的 <c>0.16.9</c>。</param>
/// <param name="IsRecommended">Forge 官方标记的推荐版（promotions 里的 <c>recommended</c>）。</param>
/// <param name="IsLatest">官方标记的最新版（promotions 里的 <c>latest</c>）。</param>
public sealed record LoaderVersionInfo(
    string LoaderVersion,
    bool IsRecommended = false,
    bool IsLatest = false)
{
    /// <summary>给下拉框用的显示文本。</summary>
    public string DisplayName
    {
        get
        {
            var tag = IsRecommended ? "  ← 推荐"
                : IsLatest ? "  ← 最新"
                : string.Empty;
            return LoaderVersion + tag;
        }
    }
}

/// <summary>加载器安装完成后的结果。</summary>
/// <param name="VersionName">写进 <c>versions/</c> 的目录名，也是启动时传给 CmlLib 的版本名。</param>
/// <param name="Kind">哪种加载器。</param>
/// <param name="MinecraftVersion">对应的原版版本号。</param>
/// <param name="LoaderVersion">加载器版本号（原版为空字符串）。</param>
public sealed record LoaderInstallResult(
    string VersionName,
    ModLoaderKind Kind,
    string MinecraftVersion,
    string LoaderVersion)
{
    /// <summary>是不是纯原版。</summary>
    public bool IsVanilla => Kind == ModLoaderKind.Vanilla;
}
