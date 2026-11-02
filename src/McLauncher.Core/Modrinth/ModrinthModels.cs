using System.Text.Json.Serialization;

namespace McLauncher.Core.Modrinth;

/// <summary>
/// 搜索请求的参数。
/// </summary>
public sealed class ModrinthSearchQuery
{
    /// <summary>关键词。空字符串 = 按排序浏览（不做关键词匹配）。</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>内容类型。决定 <c>project_type</c> facet 和可选加载器。</summary>
    public ContentType Type { get; init; } = ContentType.Mod;

    /// <summary>游戏版本筛选（如 <c>1.21.1</c>）。null / 空 = 不限。</summary>
    public string? GameVersion { get; init; }

    /// <summary>加载器筛选（如 <c>fabric</c>）。null / 空 = 不限。</summary>
    public string? Loader { get; init; }

    /// <summary>从第几条开始（分页）。</summary>
    public int Offset { get; init; }

    /// <summary>每页条数。Modrinth 上限 100。</summary>
    public int Limit { get; init; } = 20;

    /// <summary>排序：relevance / downloads / follows / newest / updated。</summary>
    public string SortBy { get; init; } = "relevance";
}

/// <summary>搜索结果的一条。</summary>
public sealed class ModrinthSearchHit
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>
    /// 项目类型。<b>注意：数据包在这里也是 <c>mod</c></b>（见 <see cref="ContentTypeInfo"/> 的说明）。
    /// </summary>
    [JsonPropertyName("project_type")]
    public string ProjectType { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; init; }

    [JsonPropertyName("follows")]
    public int Follows { get; init; }

    [JsonPropertyName("license")]
    public string License { get; init; } = string.Empty;

    [JsonPropertyName("date_modified")]
    public DateTimeOffset DateModified { get; init; }

    /// <summary>分类 + 加载器混在一起（如 <c>fabric</c>、<c>optimization</c>）。</summary>
    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>精选分类（Modrinth 挑出来的那几项，比 <see cref="Categories"/> 短得多）。</summary>
    [JsonPropertyName("display_categories")]
    public IReadOnlyList<string> DisplayCategories { get; init; } = Array.Empty<string>();

    /// <summary>该项目支持的全部游戏版本。</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();

    /// <summary>Modrinth 给的主色（整数 RGB），用来在没图标时画个色块。</summary>
    [JsonPropertyName("color")]
    public int? Color { get; init; }
}

/// <summary>搜索接口的响应。</summary>
public sealed class ModrinthSearchResult
{
    [JsonPropertyName("hits")]
    public IReadOnlyList<ModrinthSearchHit> Hits { get; init; } = Array.Empty<ModrinthSearchHit>();

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("total_hits")]
    public int TotalHits { get; init; }
}

/// <summary><c>/tag/game_version</c> 的一项。</summary>
public sealed class ModrinthGameVersion
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("version_type")]
    public string VersionType { get; init; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; init; }

    [JsonPropertyName("major")]
    public bool Major { get; init; }
}

/// <summary><c>/tag/loader</c> 的一项（只要名字和适用类型，图标 SVG 丢掉）。</summary>
public sealed class ModrinthLoader
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("supported_project_types")]
    public IReadOnlyList<string> SupportedProjectTypes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 一个项目下的一个<b>具体文件版本</b>（对应 <c>GET /project/{id}/version</c> 的一项）。
/// </summary>
/// <remarks>
/// <para>
/// 搜索结果只告诉你「这个项目支持 1.16.5 ~ 1.21.1」，但实际能下的文件是按版本分开的：
/// 同一个模组会为每个游戏版本 + 每种加载器各发一份 jar。要真的装进去，
/// 必须落到这一层 —— 选哪个文件、下多大、依赖谁，全在这。
/// </para>
/// <para>
/// <b>文件名（<see cref="ModrinthVersionFile.FileName"/>）不能自己拼</b>：
/// 有的项目叫 <c>sodium-fabric-0.5.11+mc1.21.jar</c>，有的叫 <c>Sodium-0.5.11.jar</c>，
/// 还有带空格和中文的。必须原样用服务器给的名字，否则游戏加载器可能认不出来。
/// </para>
/// </remarks>
public sealed class ModrinthVersion
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>版本标题（发布者自己写的，可能是「v1.2 for 1.21」这种）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>版本号（<c>1.2.3</c>）。列表里主要显示它。</summary>
    [JsonPropertyName("version_number")]
    public string VersionNumber { get; init; } = string.Empty;

    /// <summary><c>release</c> / <c>beta</c> / <c>alpha</c>。</summary>
    [JsonPropertyName("version_type")]
    public string VersionType { get; init; } = string.Empty;

    [JsonPropertyName("game_versions")]
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("loaders")]
    public IReadOnlyList<string> Loaders { get; init; } = Array.Empty<string>();

    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; init; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<ModrinthVersionFile> Files { get; init; } = Array.Empty<ModrinthVersionFile>();

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = Array.Empty<ModrinthDependency>();
}

/// <summary>版本里的一个可下载文件。</summary>
public sealed class ModrinthVersionFile
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("filename")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>主文件。一个版本可能挂好几个文件（源码包、附加包），只有这个是给游戏用的。</summary>
    [JsonPropertyName("primary")]
    public bool Primary { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("hashes")]
    public ModrinthHashes Hashes { get; init; } = new();
}

/// <summary>文件校验值。<b>只用来验证下载完整性</b>，不参与任何安全判断。</summary>
public sealed class ModrinthHashes
{
    [JsonPropertyName("sha1")]
    public string? Sha1 { get; init; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; init; }
}

/// <summary>依赖关系。<c>dependency_type</c> 是 <c>required</c> 时必须先装。</summary>
public sealed class ModrinthDependency
{
    /// <summary>具体版本 ID。有它就能直接把那一个文件拉下来。</summary>
    [JsonPropertyName("version_id")]
    public string? VersionId { get; init; }

    /// <summary>只给了项目 ID（没锁版本），得自己去挑一个合适的。</summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("dependency_type")]
    public string DependencyType { get; init; } = string.Empty;
}

