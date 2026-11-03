using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepDive.Core.Content;
using DeepDive.Core.Modrinth;

namespace DeepDive.Core.CurseForge;

/// <summary>
/// CurseForge 内容源，<b>全部走 MCIM 镜像</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须走镜像</b>：CurseForge 官方 API 要自己的 key，个人申请只能通过 Google
/// 账号登录 <c>console.curseforge.com</c>。拿不到 key 就没有第二条路。
/// MCIM（<c>mod.mcimirror.top</c>）完整反代了 CurseForge API，用它的 key，我们不用管。
/// </para>
/// <para>
/// <b>官方返回的 downloadUrl 是废的</b>：API 会给 <c>edge.forgecdn.net</c> 的直链，
/// 但 2026-11 实测直连返回 <b>404</b>；同一个文件走镜像的 <c>/files/</c> 端点
/// 却正常返回 206 并 302 到可用节点。所以一律用 <see cref="BuildDownloadUrl"/> 重拼地址。
/// </para>
/// </remarks>
public sealed class CurseForgeClient
{
    /// <summary>MCIM 的 CurseForge 反代根路径。</summary>
    public const string MirrorBaseUrl = "https://mod.mcimirror.top/curseforge";

    /// <summary>MCIM 的文件 CDN（<c>/files/{高位}/{低位三位}/{文件名}</c>）。</summary>
    public const string MirrorFileBase = "https://mod.mcimirror.top/files";

    /// <summary>Minecraft 在 CurseForge 的 gameId，固定值。</summary>
    public const int MinecraftGameId = 432;

    private const string UserAgent = "DeepDive/0.3.0 (Minecraft launcher; local build)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>日志回调，签名与其它服务一致。</summary>
    public event Action<object, string>? Log;

    public CurseForgeClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>内容类型 → CurseForge 的 classId。</summary>
    public static int ClassIdOf(ContentType type) => type switch
    {
        ContentType.Mod => 6,
        ContentType.Modpack => 4471,
        ContentType.Shader => 6552,
        ContentType.ResourcePack => 12,
        _ => 6,
    };

    /// <summary>Modrinth 风格的排序名 → CurseForge 的 sortField。</summary>
    private static int SortFieldOf(string? sortBy) => sortBy switch
    {
        "downloads" => 6,      // TotalDownloads
        "follows" => 2,        // Popularity
        "newest" => 11,        // ReleasedDate
        "updated" => 3,        // LastUpdated
        _ => 2,                // 默认按热度，比 Featured 实用
    };

    /// <summary>搜一页。</summary>
    public async Task<(IReadOnlyList<ContentProject> Items, long Total)> SearchAsync(
        string query,
        ContentType type,
        string? gameVersion,
        string? loader,
        int offset,
        int limit,
        string? sortBy,
        CancellationToken cancellationToken = default)
    {
        // CurseForge 的 pageSize 上限是 50，超了直接 400
        var pageSize = Math.Clamp(limit, 1, 50);

        var url = $"{MirrorBaseUrl}/v1/mods/search"
                  + $"?gameId={MinecraftGameId}"
                  + $"&classId={ClassIdOf(type)}"
                  + $"&index={Math.Max(0, offset)}"
                  + $"&pageSize={pageSize}"
                  + $"&sortField={SortFieldOf(sortBy)}"
                  + "&sortOrder=desc";

        if (!string.IsNullOrWhiteSpace(query))
            url += $"&searchFilter={Uri.EscapeDataString(query.Trim())}";

        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        if (CfLoader.ToApiValue(loader) is { } loaderId)
            url += $"&modLoaderType={loaderId}";

        var watch = Stopwatch.StartNew();
        var json = await GetStringAsync(url, "搜索", cancellationToken).ConfigureAwait(false);
        watch.Stop();

        var envelope = JsonSerializer.Deserialize<CfEnvelope<List<CfMod>>>(json, JsonOptions);
        var mods = envelope?.Data ?? new List<CfMod>();
        var total = envelope?.Pagination?.TotalCount ?? mods.Count;

        Raise($"[CurseForge] 搜索返回 {mods.Count} 条（共 {total}），{watch.ElapsedMilliseconds} ms");

        var items = mods.Select(ToProject).ToList();
        return (items, total);
    }

    /// <summary>列出一个项目的文件（版本）。</summary>
    public async Task<IReadOnlyList<ContentVersionEntry>> GetFilesAsync(
        string projectId,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var url = $"{MirrorBaseUrl}/v1/mods/{Uri.EscapeDataString(projectId)}/files?pageSize=50";

        if (!string.IsNullOrWhiteSpace(gameVersion))
            url += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        if (CfLoader.ToApiValue(loader) is { } loaderId)
            url += $"&modLoaderType={loaderId}";

        var json = await GetStringAsync(url, "读取版本列表", cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<CfEnvelope<List<CfFile>>>(json, JsonOptions);

        return (envelope?.Data ?? new List<CfFile>()).Select(ToVersion).ToList();
    }

    /// <summary>
    /// 批量取项目名（<c>modId → 名字</c>）。
    /// </summary>
    /// <remarks>
    /// 用在「带几个前置」的悬停提示上。用 <c>POST /v1/mods</c> 一次查完，
    /// 不要逐个查 —— 一屏十来个依赖串行发请求要等十几秒。
    /// 失败返回空字典，调用方按「显示原 ID」兜底。
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetProjectNamesAsync(
        IReadOnlyList<string> projectIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var ids = projectIds
            .Where(id => long.TryParse(id, out _))
            .Select(long.Parse)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return result;

        var body = JsonSerializer.Serialize(new { modIds = ids });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http
            .PostAsync($"{MirrorBaseUrl}/v1/mods", content, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return result;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<CfEnvelope<List<CfMod>>>(json, JsonOptions);

        foreach (var mod in envelope?.Data ?? new List<CfMod>())
            result[mod.Id.ToString()] = mod.Name;

        return result;
    }

    /// <summary>
    /// 拼真正能下到的文件地址。
    /// </summary>
    /// <remarks>
    /// MCIM 的文件路径规则是把 fileId 拆成「除以 1000 的商」和「余数补足三位」：
    /// <c>9362318</c> → <c>/files/9362/318/文件名</c>。
    /// 直接用 API 返回的 <c>downloadUrl</c>（edge.forgecdn.net）实测 404。
    /// </remarks>
    public static string BuildDownloadUrl(long fileId, string fileName)
    {
        var head = fileId / 1000;
        var tail = fileId % 1000;
        return $"{MirrorFileBase}/{head}/{tail:D3}/{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>把 CurseForge 的模组转成通用项目。</summary>
    private static ContentProject ToProject(CfMod mod)
    {
        // 加载器从 latestFilesIndexes 里提取（CurseForge 没有专门的加载器数组）
        var loaders = mod.LatestFilesIndexes
            .Select(i => CfLoader.FromApiValue(i.ModLoader))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versions = mod.LatestFilesIndexes
            .Select(i => i.GameVersion)
            .Where(IsGameVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 功能分类用 slug（小写），和加载器混在一个数组里 —— 上层靠 KnownLoaders 白名单区分
        var categories = loaders
            .Concat(mod.Categories.Select(c => c.Slug).Where(s => s.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ContentProject
        {
            Id = mod.Id.ToString(),
            Slug = mod.Slug,
            Title = mod.Name,
            Description = mod.Summary,
            Author = mod.Authors.FirstOrDefault()?.Name ?? string.Empty,
            IconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url,
            Downloads = mod.DownloadCount,
            Follows = 0,
            DateModified = mod.DateModified,
            Color = null,
            Categories = categories,
            Versions = versions,
            Platform = ContentPlatform.CurseForge,
        };
    }

    /// <summary>把 CurseForge 的文件转成通用版本。</summary>
    private static ContentVersionEntry ToVersion(CfFile file)
    {
        // gameVersions 是混装数组：游戏版本 + 加载器名，按能否解析成版本号拆开
        var gameVersions = file.GameVersions.Where(IsGameVersion).ToList();
        var loaders = file.GameVersions
            .Select(CfLoader.FromName)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sha1 = file.Hashes.FirstOrDefault(h => h.Algo == 1)?.Value;

        return new ContentVersionEntry
        {
            Id = file.Id.ToString(),
            ProjectId = file.ModId.ToString(),
            Name = string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
            VersionNumber = ExtractVersion(file.FileName),
            ReleaseType = file.ReleaseType switch
            {
                2 => "beta",
                3 => "alpha",
                _ => "release",
            },
            GameVersions = gameVersions,
            Loaders = loaders,
            DatePublished = file.FileDate,
            Downloads = file.DownloadCount,
            FileName = file.FileName,
            DownloadUrl = BuildDownloadUrl(file.Id, file.FileName),
            Size = file.FileLength,
            Sha1 = sha1,
            Dependencies = file.Dependencies
                .Select(d => new ContentDependency
                {
                    ProjectId = d.ModId.ToString(),
                    VersionId = null,
                    Type = d.RelationType switch
                    {
                        3 => "required",
                        2 => "optional",
                        5 => "incompatible",
                        _ => "other",
                    },
                })
                .ToList(),
        };
    }

    /// <summary>能不能当游戏版本号看（<c>1.20.1</c> 这种，纯数字加点）。</summary>
    private static bool IsGameVersion(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return char.IsDigit(value[0]) && value.All(c => char.IsDigit(c) || c == '.');
    }

    /// <summary>从文件名里抠版本号，抠不出来就返回空串。</summary>
    private static string ExtractVersion(string fileName)
    {
        // jei-1.20.1-forge-15.74.0.233.jar → 15.74.0.233（取最后一段纯数字加点）
        var name = Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);

        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var p = parts[i];
            if (p.Length > 0 && char.IsDigit(p[0]) && p.Count(c => c == '.') >= 1)
                return p.TrimStart('v');
        }

        return string.Empty;
    }

    private async Task<string> GetStringAsync(string url, string what, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Raise($"[CurseForge] {what}失败：HTTP {(int)response.StatusCode} {url}");
            throw new HttpRequestException(
                $"CurseForge {what}失败（HTTP {(int)response.StatusCode}）。镜像可能暂时不可用。");
        }

        return body;
    }

    private void Raise(string message) => Log?.Invoke(this, message);
}
