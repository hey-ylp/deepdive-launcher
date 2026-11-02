namespace McLauncher.Core.Modrinth;

/// <summary>
/// 能从 Modrinth 下载的四类内容。
/// </summary>
/// <remarks>
/// <b>没有「数据包」</b>。Modrinth 上它不是一个独立项目类型：所谓数据包，
/// 是某些模组项目额外发布的 datapack 版本（<c>project_type:datapack</c> 能筛到，
/// 但返回结果的 <c>project_type</c> 仍写着 <c>mod</c>）。硬做成一个分类，
/// 用户点进去会看到一堆明明要装进 mods 目录的模组 —— 误导大于便利，所以不做。
/// </remarks>
public enum ContentType
{
    Mod,
    Modpack,
    Shader,
    ResourcePack,
}

/// <summary>
/// 一类内容的元数据：怎么筛、能配哪些加载器、装到哪个目录。
/// </summary>
/// <remarks>
/// <para>
/// 加载器列表不是拍脑袋写的，是拿 <c>GET /tag/loader</c> 的
/// <c>supported_project_types</c> 核对过的：
/// </para>
/// <code>
/// fabric   -> [mod, modpack]
/// neoforge -> [mod, modpack]
/// iris     -> [shader]
/// minecraft-> [resourcepack]
/// datapack -> [datapack, mod]
/// </code>
/// <para>
/// 所以「光影包能不能选 Forge」这种问题在 UI 层就该被挡住 ——
/// 不是等请求发出去了才由服务器返回空列表。
/// </para>
/// </remarks>
public sealed record ContentTypeInfo(
    ContentType Type,
    string DisplayName,
    string ProjectType,
    string InstallFolder,
    string Note,
    IReadOnlyList<string> Loaders)
{
    /// <summary>
    /// 五类内容的完整定义。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>数据包那条是实测出来的意外</b>：Modrinth 的 <c>/tag/project_type</c>
    /// 里明明列了 <c>datapack</c>，但搜索结果里这些项目的 <c>project_type</c>
    /// 字段仍然是 <c>mod</c> —— 因为 Modrinth 把数据包当成模组项目的一种
    /// <b>发布形态</b>（同一个项目可以同时出 mod 版和 datapack 版）。
    /// </para>
    /// <para>
    /// 因此筛数据包只能用 <c>project_type:datapack</c> 这个 facet（有效），
    /// 而不能指望返回结果里读 <c>project_type == "datapack"</c>（永远读不到）。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ContentTypeInfo> All { get; } = new[]
    {
        new ContentTypeInfo(
            ContentType.Mod,
            "模组",
            "mod",
            "mods",
            "装进 mods 目录，需要对应的加载器",
            new[] { "fabric", "forge", "neoforge", "quilt" }),

        new ContentTypeInfo(
            ContentType.Modpack,
            "整合包",
            "modpack",
            string.Empty,
            "整合包自带一堆模组和配置，安装等于新建一个实例",
            new[] { "fabric", "forge", "neoforge", "quilt" }),

        new ContentTypeInfo(
            ContentType.Shader,
            "光影包",
            "shader",
            "shaderpacks",
            "装进 shaderpacks 目录，靠 Iris / OptiFine 加载",
            new[] { "iris", "optifine", "canvas", "vanilla" }),

        new ContentTypeInfo(
            ContentType.ResourcePack,
            "资源包",
            "resourcepack",
            "resourcepacks",
            "装进 resourcepacks 目录，原版游戏就能用，没有加载器概念",
            // 空数组：Modrinth 给资源包挂的加载器只有 minecraft，等于「原版能读」，
            // 所有资源包都满足。做成下拉项没有任何筛选价值，只是多一个必然全选的选项。
            Array.Empty<string>()),
    };

    /// <summary>按枚举取定义。</summary>
    public static ContentTypeInfo Get(ContentType type) =>
        All.First(item => item.Type == type);

    /// <summary>加载器值 → 显示名。</summary>
    public static string LoaderLabel(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        "iris" => "Iris",
        "optifine" => "OptiFine",
        "canvas" => "Canvas",
        "vanilla" => "原版",
        "minecraft" => "原版",
        "datapack" => "数据包",
        _ => loader,
    };

    /// <summary>
    /// 所有出现在 Modrinth <c>categories</c> 里的加载器名。
    /// </summary>
    /// <remarks>
    /// 搜索结果里加载器和普通分类（optimization、library、adventure…）混在同一个
    /// <c>categories</c> 数组里，没有额外字段区分。所以要标「支持哪些加载器」，
    /// 只能拿这份白名单去过滤 —— 认不出来的就是功能分类，不当加载器显示。
    /// </remarks>
    public static IReadOnlySet<string> KnownLoaders { get; } = new HashSet<string>(
        new[]
        {
            "fabric", "forge", "neoforge", "quilt", "liteloader", "modloader", "rift",
            "iris", "optifine", "canvas", "vanilla", "minecraft", "datapack",
            "ornithe", "babric", "bta-babric", "legacy-fabric", "nilloader", "java-agent",
            "bukkit", "spigot", "paper", "purpur", "folia", "sponge",
            "bungeecord", "waterfall", "velocity", "geyser",
        },
        StringComparer.OrdinalIgnoreCase);
}
