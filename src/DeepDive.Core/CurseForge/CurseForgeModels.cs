using System.Text.Json.Serialization;

namespace DeepDive.Core.CurseForge;

/// <summary>CurseForge API 的响应外层（<c>{ "data": ..., "pagination": ... }</c>）。</summary>
public sealed class CfEnvelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("pagination")]
    public CfPagination? Pagination { get; init; }
}

public sealed class CfPagination
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("resultCount")]
    public int ResultCount { get; init; }

    [JsonPropertyName("totalCount")]
    public long TotalCount { get; init; }
}

/// <summary>搜索／详情里的一条模组。</summary>
public sealed class CfMod
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; init; }

    [JsonPropertyName("dateModified")]
    public DateTimeOffset DateModified { get; init; }

    [JsonPropertyName("logo")]
    public CfImage? Logo { get; init; }

    [JsonPropertyName("categories")]
    public IReadOnlyList<CfCategory> Categories { get; init; } = Array.Empty<CfCategory>();

    [JsonPropertyName("authors")]
    public IReadOnlyList<CfAuthor> Authors { get; init; } = Array.Empty<CfAuthor>();

    /// <summary>
    /// 每个「游戏版本 + 加载器」组合的最新文件索引。
    /// </summary>
    /// <remarks>
    /// 这是 CurseForge 唯一能直接拿到的「支持哪些版本」来源 —— 它没有 Modrinth 那种
    /// 独立的 <c>versions</c> 数组。字段里的 <c>gameVersion</c> 是 <c>1.20.1</c>，
    /// <c>modLoader</c> 是加载器（见 <see cref="CfLoader"/>）。
    /// </remarks>
    [JsonPropertyName("latestFilesIndexes")]
    public IReadOnlyList<CfFileIndex> LatestFilesIndexes { get; init; } = Array.Empty<CfFileIndex>();
}

public sealed class CfImage
{
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class CfCategory
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;
}

public sealed class CfAuthor
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class CfFileIndex
{
    [JsonPropertyName("gameVersion")]
    public string GameVersion { get; init; } = string.Empty;

    [JsonPropertyName("fileId")]
    public long FileId { get; init; }

    [JsonPropertyName("filename")]
    public string Filename { get; init; } = string.Empty;

    [JsonPropertyName("releaseType")]
    public int ReleaseType { get; init; }

    /// <summary>加载器，<b>可能是 null</b>（老文件没标）。</summary>
    [JsonPropertyName("modLoader")]
    public int? ModLoader { get; init; }
}

/// <summary>一个具体文件。</summary>
public sealed class CfFile
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("modId")]
    public long ModId { get; init; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>1 = release，2 = beta，3 = alpha。</summary>
    [JsonPropertyName("releaseType")]
    public int ReleaseType { get; init; }

    [JsonPropertyName("fileDate")]
    public DateTimeOffset FileDate { get; init; }

    [JsonPropertyName("fileLength")]
    public long FileLength { get; init; }

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; init; }

    [JsonPropertyName("hashes")]
    public IReadOnlyList<CfHash> Hashes { get; init; } = Array.Empty<CfHash>();

    /// <summary>
    /// <b>混装数组</b>：游戏版本（<c>1.20.1</c>）和加载器名（<c>Forge</c>）在同一个列表里，
    /// 必须按前缀把两者拆开，不能整体当版本用。
    /// </summary>
    [JsonPropertyName("gameVersions")]
    public IReadOnlyList<string> GameVersions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<CfDependency> Dependencies { get; init; } = Array.Empty<CfDependency>();

    /// <summary>
    /// 官方给的下载地址。<b>不可信</b> —— 实测指向 <c>edge.forgecdn.net</c>，直连会 404。
    /// 真正能下的是 <see cref="CurseForgeClient.BuildDownloadUrl"/> 拼出来的镜像地址。
    /// </summary>
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }
}

public sealed class CfHash
{
    /// <summary>1 = sha1，2 = md5。</summary>
    [JsonPropertyName("algo")]
    public int Algo { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}

public sealed class CfDependency
{
    [JsonPropertyName("modId")]
    public long ModId { get; init; }

    /// <summary>3 = 必需依赖，2 = 可选，5 = 不兼容。</summary>
    [JsonPropertyName("relationType")]
    public int RelationType { get; init; }
}

/// <summary>CurseForge 的加载器枚举值 → 小写名（和 Modrinth 的写法对齐）。</summary>
public static class CfLoader
{
    /// <summary>写进 URL 的 <c>modLoaderType</c> 值。</summary>
    public static int? ToApiValue(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null,
    };

    /// <summary>API 返回值 → 小写名。</summary>
    public static string FromApiValue(int? loader) => loader switch
    {
        1 => "forge",
        2 => "cauldron",
        3 => "liteloader",
        4 => "fabric",
        5 => "quilt",
        6 => "neoforge",
        _ => string.Empty,
    };

    /// <summary>文件的 <c>gameVersions</c> 里出现的加载器名 → 小写名。</summary>
    public static string FromName(string name) => name.ToLowerInvariant() switch
    {
        "forge" => "forge",
        "fabric" => "fabric",
        "quilt" => "quilt",
        "neoforge" => "neoforge",
        "liteloader" => "liteloader",
        "cauldron" => "cauldron",
        "rift" => "rift",
        _ => string.Empty,
    };
}
