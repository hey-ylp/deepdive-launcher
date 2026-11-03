namespace DeepDive.Core.Content;

/// <summary>内容来自哪个平台。</summary>
public enum ContentPlatform
{
    Modrinth,
    CurseForge,
}

/// <summary>
/// 一条内容项目（搜索结果的通用形态）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要这层</b>：界面层原来直接吃 <c>ModrinthSearchHit</c>，接第二个平台时
/// 要么改界面，要么把 CurseForge 的数据硬塞进 Modrinth 的类里 —— 后者会让
/// 「这个字段是哪个平台的语义」变成一场猜谜，早晚出错。
/// </para>
/// <para>
/// 字段刻意和 Modrinth 对齐（<see cref="Categories"/> 里放加载器小写名、
/// <see cref="Versions"/> 放游戏版本），这样现有的加载器识别、版本兼容判断、
/// 分类翻译全都能直接复用，不用为 CurseForge 再写一套。
/// </para>
/// </remarks>
public sealed class ContentProject
{
    /// <summary>平台内的项目 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>URL 用的短名。<b>CurseForge 没有这个概念</b>，用它自己的 slug。</summary>
    public string Slug { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>英文简介。中文译文由界面层异步补。</summary>
    public string Description { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    /// <summary>图标地址。<b>CurseForge 的要走镜像</b>，直连 media.forgecdn.net 国内很慢。</summary>
    public string? IconUrl { get; init; }

    public long Downloads { get; init; }

    /// <summary>关注数。CurseForge 没有这个指标，恒为 0。</summary>
    public int Follows { get; init; }

    public DateTimeOffset DateModified { get; init; }

    /// <summary>主色（整数 RGB），没有图标时拿它画色块。CurseForge 不提供，为 null。</summary>
    public int? Color { get; init; }

    /// <summary>分类与加载器混在一起，加载器用小写名（<c>forge</c> / <c>fabric</c>）。</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>支持的游戏版本。</summary>
    public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();

    public ContentPlatform Platform { get; init; } = ContentPlatform.Modrinth;
}

/// <summary>一条可下载的版本（版本列表窗口用）。</summary>
public sealed class ContentVersionEntry
{
    public string Id { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    /// <summary>版本标题，直接显示。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>版本号（<c>1.2.3</c>）。CurseForge 没有独立字段，从文件名里取。</summary>
    public string VersionNumber { get; init; } = string.Empty;

    /// <summary><c>release</c> / <c>beta</c> / <c>alpha</c>。</summary>
    public string ReleaseType { get; init; } = "release";

    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();

    public DateTimeOffset DatePublished { get; init; }

    public long Downloads { get; init; }

    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// 真正能下到的地址。
    /// </summary>
    /// <remarks>
    /// ⚠️ CurseForge 的 API 会返回 <c>edge.forgecdn.net</c> 的直链，但实测那个地址
    /// <b>404</b>（2026-11 验证）。必须换成 MCIM 的文件代理地址才下得动。
    /// </remarks>
    public string DownloadUrl { get; init; } = string.Empty;

    public long Size { get; init; }

    public string? Sha1 { get; init; }

    public IReadOnlyList<ContentDependency> Dependencies { get; init; } = Array.Empty<ContentDependency>();
}

/// <summary>依赖关系。和 Modrinth 的字段名保持一致，便于共用处理逻辑。</summary>
public sealed class ContentDependency
{
    public string? ProjectId { get; init; }

    public string? VersionId { get; init; }

    /// <summary><c>required</c> / <c>optional</c> / <c>incompatible</c> / <c>embedded</c>。</summary>
    public string Type { get; init; } = string.Empty;
}
