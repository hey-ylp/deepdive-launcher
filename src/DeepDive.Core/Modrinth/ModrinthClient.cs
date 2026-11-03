using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepDive.Core.Modrinth;

/// <summary>
/// Modrinth API v2 的客户端。只做搜索和标签表，不负责下载文件（那是 4.2 的事）。
/// </summary>
/// <remarks>
/// <para>
/// <b>三条硬性约束</b>（不遵守会被拒绝或限流）：
/// </para>
/// <list type="number">
///   <item><b>必须带 User-Agent</b> —— 不带会被 Cloudflare 直接挡掉，返回 403 或空。</item>
///   <item>限流 <b>300 次/分钟</b>（按 IP）。超了返回 429，带 <c>Retry-After</c> 头。</item>
///   <item>搜索的 <c>facets</c> 是<b>嵌套 JSON 数组</b>，外层是 AND、内层是 OR，整体还要 URL 编码一遍。</item>
/// </list>
/// <para>
/// facet 形状：
/// </para>
/// <code>
/// [["project_type:mod"],["categories:fabric"],["versions:1.21.1"]]
///  └── 必须同时满足这三组 ──┘
/// </code>
/// </remarks>
public sealed class ModrinthClient : IDisposable
{
    /// <summary>官方 API 基址。</summary>
    public const string OfficialBaseUrl = "https://api.modrinth.com/v2";

    /// <summary>MCIM 镜像基址（API 结构完全兼容，直接把 host 换掉即可）。</summary>
    public const string MirrorBaseUrl = "https://mod.mcimirror.top/modrinth";

    /// <summary>
    /// User-Agent。<b>必须能标识出这是哪个程序</b>，Modrinth 会拿它做封禁依据。
    /// </summary>
    // 测速那边也要用同一个 UA，别各写一份
    internal const string UserAgent = "DeepDive/0.1.0 (Minecraft launcher; local build)";

    /// <summary>
    /// 当前 API 基址。默认官方源，可在设置页切到镜像（<see cref="MirrorBaseUrl"/>）。
    /// </summary>
    /// <remarks>
    /// 做成可写属性而不是构造参数，是因为本类是 DI 单例 —— 用户切镜像不该重建整个客户端。
    /// 所有接口（搜索 / 版本列表 / 依赖）都从它拼 URL，改一处全局生效。
    /// </remarks>
    public string BaseUrl { get; set; } = OfficialBaseUrl;

    /// <summary>单个请求最多重试几次（限流和网络抖动都算）。</summary>
    private const int MaxRetries = 3;

    private readonly HttpClient _http;

    /// <summary>标签表只拉一次，多个窗口共用。用信号量防止并发首拉重复请求。</summary>
    private readonly SemaphoreSlim _tagGate = new(1, 1);

    private IReadOnlyList<ModrinthGameVersion>? _gameVersions;

    /// <summary>日志回调，签名与其它服务保持一致。</summary>
    public event Action<object, string>? Log;

    public ModrinthClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler)
        {
            // 实测正常 1~2 秒。原来给 20 秒，赶上服务端抽风时用户会对着「搜索中…」
            // 干等一分半（20 秒超时 × 3 次重试）—— 12 秒就放弃重来，总时长压到半分钟左右，
            // 而且重试期间界面上会显示进度（见 ModrinthClient.Log → 窗口状态行）。
            Timeout = TimeSpan.FromSeconds(12),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>按条件搜一页。</summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);
        var offset = Math.Max(query.Offset, 0);

        var url =
            $"{BaseUrl}/search" +
            $"?query={Uri.EscapeDataString(query.Query ?? string.Empty)}" +
            $"&limit={limit}" +
            $"&offset={offset}" +
            $"&index={Uri.EscapeDataString(query.SortBy)}" +
            $"&facets={Uri.EscapeDataString(BuildFacets(query))}";

        var json = await GetStringAsync(url, $"搜索「{query.Query}」", cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<ModrinthSearchResult>(json)
               ?? new ModrinthSearchResult();
    }

    /// <summary>
    /// 拼 facets 字符串。
    /// </summary>
    /// <remarks>
    /// 外层数组的每个元素是一组「或」，组之间是「与」。所以：
    /// <c>[["project_type:mod"],["categories:fabric"]]</c> = 类型是 mod <b>且</b> 支持 fabric。
    /// </remarks>
    private static string BuildFacets(ModrinthSearchQuery query)
    {
        var info = ContentTypeInfo.Get(query.Type);
        var groups = new List<string>
        {
            $"[{JsonSerializer.Serialize("project_type:" + info.ProjectType)}]",
        };

        if (!string.IsNullOrWhiteSpace(query.Loader))
        {
            var loaders = info.Loaders;

            // 明明不是这个类型的加载器却传进来（比如给光影包传 forge），当没传处理，
            // 否则会得到一个必然为空的 facets，用户只会看到「没有结果」而不知道为什么
            if (loaders.Count == 0 || loaders.Contains(query.Loader, StringComparer.OrdinalIgnoreCase))
                groups.Add($"[{JsonSerializer.Serialize("categories:" + query.Loader)}]");
        }

        if (!string.IsNullOrWhiteSpace(query.GameVersion))
            groups.Add($"[{JsonSerializer.Serialize("versions:" + query.GameVersion)}]");

        return "[" + string.Join(",", groups) + "]";
    }

    /// <summary>
    /// 拉一个项目的全部可下载版本。
    /// </summary>
    /// <param name="projectIdOrSlug">项目 ID 或 slug，两个都能用。</param>
    /// <remarks>
    /// 这个接口一次返回<b>全部</b>版本（Sodium 有 200 多个），不能分页，也没法按版本筛。
    /// 一次几 MB，所以只在用户真的点了「下载」的时候调，不能挂在搜索结果上预取 ——
    /// 一页 20 条就是 20 个几 MB 的请求。
    /// </remarks>
    public async Task<IReadOnlyList<ModrinthVersion>> GetProjectVersionsAsync(
        string projectIdOrSlug,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectIdOrSlug);

        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        var json = await GetStringAsync(url, "读取版本列表", cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<List<ModrinthVersion>>(json) ?? new List<ModrinthVersion>();
    }

    /// <summary>按版本 ID 取一个版本（安装依赖时用）。</summary>
    public async Task<ModrinthVersion?> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var url = $"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}";
        var json = await GetStringAsync(url, "读取依赖版本", cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<ModrinthVersion>(json);
    }

    /// <summary>全部游戏版本（含快照），按发布时间从新到旧。</summary>
    public async Task<IReadOnlyList<ModrinthGameVersion>> GetGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_gameVersions is not null) return _gameVersions;

        await _tagGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_gameVersions is not null) return _gameVersions;

            var json = await GetStringAsync($"{BaseUrl}/tag/game_version", "读取游戏版本表", cancellationToken)
                .ConfigureAwait(false);

            var list = JsonSerializer.Deserialize<List<ModrinthGameVersion>>(json)
                       ?? new List<ModrinthGameVersion>();

            _gameVersions = list.OrderByDescending(v => v.Date).ToList();
            Log?.Invoke(this, $"Modrinth 版本表：{_gameVersions.Count} 个");
            return _gameVersions;
        }
        finally
        {
            _tagGate.Release();
        }
    }

    /// <summary>正式版（release）列表，按时间从新到旧 —— 给下拉框用。</summary>
    public async Task<IReadOnlyList<string>> GetReleaseVersionsAsync(CancellationToken cancellationToken = default)
    {
        var all = await GetGameVersionsAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(v => v.VersionType == "release")
                  .Select(v => v.Version)
                  .ToList();
    }

    /// <summary>
    /// 版本名的「新旧序号」表：数值越大越新。
    /// </summary>
    /// <remarks>
    /// 用来回答「这个模组最高支持到哪个版本」。不能按字符串比大小 ——
    /// <c>"1.9" &gt; "1.10"</c> 在字符串比较里是成立的，那样会算出荒唐的结论。
    /// 只能按 Mojang 的发布时间排。
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> GetVersionRankAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await GetGameVersionsAsync(cancellationToken).ConfigureAwait(false);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 从旧到新编号，序号大的更新
        var ordered = all.OrderBy(v => v.Date).ToList();
        for (var i = 0; i < ordered.Count; i++)
            rank[ordered[i].Version] = i;

        return rank;
    }

    /// <summary>
    /// 批量取项目标题（<c>projectId → 标题</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用在「这个模组带几个前置」的悬停提示上：依赖里只有 <c>project_id</c>，
    /// 直接显示一串 ID 用户看不懂。
    /// </para>
    /// <para>
    /// 一次请求查完，不要每个依赖发一次 —— 版本窗口一屏十来行，串行查要十几秒。
    /// 查不到的 ID 不进字典，调用方按「还是显示 ID」兜底。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> GetProjectTitlesAsync(
        IReadOnlyList<string> projectIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (projectIds.Count == 0) return result;

        // Modrinth 的 ids 参数要求 JSON 数组字面量，不是逗号分隔
        var ids = "[" + string.Join(",", projectIds.Distinct().Select(id => $"\"{id}\"")) + "]";
        var url = $"{BaseUrl}/projects?ids={Uri.EscapeDataString(ids)}";

        var json = await GetStringAsync(url, "批量读取项目名", cancellationToken).ConfigureAwait(false);

        var projects = JsonSerializer.Deserialize<List<ModrinthProjectBrief>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        foreach (var project in projects ?? new List<ModrinthProjectBrief>())
        {
            if (string.IsNullOrWhiteSpace(project.Id)) continue;
            result[project.Id] = string.IsNullOrWhiteSpace(project.Title) ? project.Slug : project.Title;
        }

        return result;
    }

    /// <summary><c>/projects</c> 返回的精简结构（只要名字，不要描述那些）。</summary>
    private sealed class ModrinthProjectBrief
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [JsonPropertyName("slug")]
        public string Slug { get; init; } = string.Empty;
    }

    /// <summary>GET 一个 URL，带限流与网络抖动重试，返回响应正文。</summary>
    private async Task<string> GetStringAsync(string url, string what, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt > MaxRetries)
                    {
                        throw new InvalidOperationException(
                            $"Modrinth 限流了（429），{MaxRetries} 次重试都没排上队。等一分钟再试。");
                    }

                    var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(5 * attempt);
                    Log?.Invoke(this, $"Modrinth 限流（429），{wait.TotalSeconds:F0} 秒后重试（第 {attempt} 次）");
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Modrinth {what}失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。" +
                        (body.Length > 0 ? $"服务器说：{Trim(body, 300)}" : string.Empty));
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt <= MaxRetries && ShouldRetry(ex, cancellationToken))
            {
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Log?.Invoke(this,
                    $"网络不通，{wait.TotalSeconds:F0} 秒后重试（第 {attempt}/{MaxRetries} 次）：{what}");
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>只重试网络层问题。业务错误（404、403）和用户取消都不该重试。</summary>
    private static bool ShouldRetry(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;

        return ex switch
        {
            HttpRequestException => true,
            // HttpClient 超时会包成 TaskCanceledException，且外层 token 没被取消
            TaskCanceledException => true,
            _ => false,
        };
    }

    /// <summary>读 <c>Retry-After</c> 头（可能是秒数，也可能是 HTTP 日期）。</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;

        if (retryAfter.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(1);

        if (retryAfter.Date is { } date)
        {
            var span = date - DateTimeOffset.UtcNow;
            return span > TimeSpan.Zero ? span : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose()
    {
        _http.Dispose();
        _tagGate.Dispose();
    }
}
