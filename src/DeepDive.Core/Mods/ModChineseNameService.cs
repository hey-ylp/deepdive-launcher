using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeepDive.Core.Launcher;

namespace DeepDive.Core.Mods;

/// <summary>
/// 一个待查的模组。带原始名是为了核对 —— 搜出来的可能不是同一个模组。
/// </summary>
/// <param name="ModId">jar 里写的 modId。</param>
/// <param name="OriginalName">jar 里写的显示名（英文名），用来跟搜索结果对账。</param>
public sealed record ModNameQuery(string ModId, string? OriginalName = null);

/// <summary>
/// 查模组的中文名（modId → 「钠」「苹果皮」这种）。
/// </summary>
/// <remarks>
/// <para>
/// <b>两个数据源，一快一全：</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>SCL Data</b>（<c>gitee.com/SteveXMH/scl-data</c>，CC0）：把 modId 做 URL-safe base64 当文件名，
/// 文件内容就是中文名纯文本。一次请求几 KB、0.3~0.8 秒，国内 gitee 直连很稳。
/// 缺点是<b>收录很少</b> —— 实测 20 个热门模组只命中 3 个（jei / appleskin / sodium），
/// lithium、iris、botania 这些全都没有。
/// </item>
/// <item>
/// <b>MC百科搜索</b>（<c>search.mcmod.cn</c>）：覆盖面全，结果长这样
/// <c>[BOT] 植物魔法 (Botania)</c> —— 中文名、英文名都在里面，能拿来核对。
/// 缺点是抓 HTML（网页改版就废）、每个模组 40 多 KB、还被限速。
/// </item>
/// </list>
/// <para>
/// 所以流程是：先走 SCL（快、合法），没命中的再用 MC百科补。
/// </para>
/// <para>
/// <b>结果全部落盘缓存</b>：一个模组的中文名不会变，查一次就够。
/// 连「查过但没有中文名」也记下来（存空串），否则每次开窗口都要为那十几个冷门模组
/// 重新吃一遍请求。断网时只用缓存，一个请求都不发。
/// </para>
/// </remarks>
public sealed class ModChineseNameService : IDisposable
{
    /// <summary>SCL Data 根地址（末尾带斜杠，后面直接拼 base64）。</summary>
    public const string SclBaseUrl = "https://gitee.com/SteveXMH/scl-data/raw/master/mcmod/cname/";

    /// <summary>MC百科搜索。</summary>
    public const string McmodSearchUrl = "https://search.mcmod.cn/s?key={0}&site=all";

    /// <summary>SCL 的并发。纯文本小文件，可以放开点。</summary>
    private const int SclConcurrency = 6;

    /// <summary>MC百科的并发。<b>故意压到 1</b>，那是人家没开放的页面，别把人打崩。</summary>
    private const int McmodConcurrency = 1;

    /// <summary>MC百科两次请求之间的间隔。</summary>
    private static readonly TimeSpan McmodGap = TimeSpan.FromMilliseconds(400);

    private readonly HttpClient _http;
    private readonly string _cacheFile;

    /// <summary>modId（大小写不敏感）→ 中文名。空串表示「查过，没有中文名」。</summary>
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _cacheLock = new();

    private bool _cacheLoaded;

    /// <summary>上一个 MC百科请求的时间，用来控速。</summary>
    private DateTime _lastMcmodRequest = DateTime.MinValue;

    private readonly SemaphoreSlim _mcmodGate = new(1, 1);

    /// <summary>诊断用：把「查了什么、拿回什么」吐出来。界面和探针都靠它排查。</summary>
    public Action<string>? Log { get; set; }

    public ModChineseNameService(string? cacheFile = null)
    {
        _cacheFile = cacheFile ?? LauncherPaths.ModChineseNameCacheFile;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // gitee 和 mcmod 都会拒没有 UA 的脚本请求
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0 Safari/537.36 DeepDive/0.2");

        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
    }

    /// <summary>已经查过的模组数（含没有中文名的）。</summary>
    public int CachedCount
    {
        get { lock (_cacheLock) return _cache.Count; }
    }

    /// <summary>批量查中文名。<b>不抛异常</b>，查不到的就是字典里没有。</summary>
    /// <param name="modIds">模组 ID 列表（没有原始名的版本，只用来自查）。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="onFound">每查到一个回调一次（可能来自不同线程）。</param>
    public Task<IReadOnlyDictionary<string, string>> LookupAsync(
        IEnumerable<string> modIds,
        CancellationToken cancellationToken = default,
        Action<string, string>? onFound = null)
        => LookupAsync(
            modIds.Select(id => new ModNameQuery(id)),
            cancellationToken,
            onFound);

    /// <summary>批量查中文名。<b>不抛异常</b>，查不到的就是字典里没有。</summary>
    /// <param name="queries">模组列表，带原始名的话核对得更准。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <param name="onFound">
    /// 每查到一个就回调一次（可能来自不同线程）。界面用它可以「查到一个显示一个」，
    /// 而不是等几十秒后整批刷出来。
    /// </param>
    /// <returns>modId → 中文名。只包含真有中文名的。</returns>
    public async Task<IReadOnlyDictionary<string, string>> LookupAsync(
        IEnumerable<ModNameQuery> queries,
        CancellationToken cancellationToken = default,
        Action<string, string>? onFound = null)
    {
        EnsureCacheLoaded();

        var wanted = queries
            .Where(q => !string.IsNullOrWhiteSpace(q.ModId))
            .GroupBy(q => q.ModId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (wanted.Count == 0) return EmptyResult;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var needNetwork = new List<ModNameQuery>();

        // ① 缓存里有的直接拿，不联网
        lock (_cacheLock)
        {
            foreach (var q in wanted)
            {
                if (!_cache.TryGetValue(q.ModId, out var name))
                {
                    needNetwork.Add(q);
                    continue;
                }

                if (name.Length > 0) result[q.ModId] = name;
            }
        }

        if (needNetwork.Count == 0)
        {
            foreach (var (id, name) in result) onFound?.Invoke(id, name);
            return result;
        }

        // ② SCL Data：快，先走它
        var found = await LookupSclAsync(needNetwork, cancellationToken, onFound).ConfigureAwait(false);

        foreach (var (id, name) in found)
            if (name is { Length: > 0 }) result[id] = name;

        // ③ 剩下没查到的，用 MC百科补
        var stillMissing = needNetwork
            .Where(q => !found.ContainsKey(q.ModId) || string.IsNullOrEmpty(found[q.ModId]))
            .ToList();

        if (stillMissing.Count > 0)
        {
            var more = await LookupMcmodAsync(stillMissing, cancellationToken, onFound).ConfigureAwait(false);

            foreach (var (id, name) in more)
            {
                if (name is not { Length: > 0 }) continue;

                result[id] = name;
                found[id] = name;   // 让下面写缓存时能带上
            }
        }

        // ④ 落缓存：查到的存名字，确认没有的存空串，网络错误的不存（下次还该重试）
        var dirty = false;
        lock (_cacheLock)
        {
            foreach (var (id, name) in found)
            {
                if (name is null) continue;          // 网络问题，别记住
                if (_cache.ContainsKey(id)) continue;

                _cache[id] = name;
                dirty = true;
            }
        }

        if (dirty) SaveCache();

        return result;
    }

    private static readonly Dictionary<string, string> EmptyResult = new(StringComparer.OrdinalIgnoreCase);

    // ───────────────────────── 源 ①：SCL Data ─────────────────────────

    /// <summary>返回 modId → 中文名。<c>null</c> 值表示网络失败（别缓存），空串表示确实没有。</summary>
    private async Task<Dictionary<string, string?>> LookupSclAsync(
        IReadOnlyList<ModNameQuery> queries,
        CancellationToken cancellationToken,
        Action<string, string>? onFound)
    {
        var output = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var outputLock = new object();

        using var gate = new SemaphoreSlim(SclConcurrency);

        var tasks = queries.Select(async q =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var name = await FetchSclOneAsync(q.ModId, cancellationToken).ConfigureAwait(false);

                lock (outputLock) output[q.ModId] = name;

                if (name is { Length: > 0 }) onFound?.Invoke(q.ModId, name);
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关窗口了，拿到多少算多少
        }

        return output;
    }

    private async Task<string?> FetchSclOneAsync(string modId, CancellationToken cancellationToken)
    {
        var url = SclBaseUrl + ToUrlSafeBase64(modId);

        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return string.Empty;   // 这个模组没收录

            if (!response.IsSuccessStatusCode)
                return null;           // 5xx / 429：网络层面的事，下次再试

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return CleanName(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;   // 断网 / 超时 / DNS 挂
        }
    }

    // ───────────────────────── 源 ②：MC百科搜索 ─────────────────────────

    /// <summary>
    /// 用 MC百科搜索补遗漏。
    /// </summary>
    /// <remarks>
    /// 结果长这样：<c>[BOT] 植物魔法 (Botania)</c>。括号里是英文名，<b>拿它跟 jar 里写的名字对账</b> ——
    /// 不核对的话，搜 <c>create</c> 可能拿回来「创造：XXX」这种完全不相干的模组，
    /// 把人家好好的模组名改错比不改更糟糕。
    /// </remarks>
    private async Task<Dictionary<string, string?>> LookupMcmodAsync(
        IReadOnlyList<ModNameQuery> queries,
        CancellationToken cancellationToken,
        Action<string, string>? onFound)
    {
        var output = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (queries.Count == 0) return output;

        foreach (var q in queries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await _mcmodGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 控速：人家没开放接口，别一个接一个地砸
                var since = DateTime.UtcNow - _lastMcmodRequest;
                if (since < McmodGap)
                    await Task.Delay(McmodGap - since, cancellationToken).ConfigureAwait(false);

                _lastMcmodRequest = DateTime.UtcNow;

                var name = await FetchMcmodOneAsync(q, cancellationToken).ConfigureAwait(false);
                output[q.ModId] = name;

                if (name is { Length: > 0 }) onFound?.Invoke(q.ModId, name);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                output[q.ModId] = null;
            }
            finally
            {
                _mcmodGate.Release();
            }
        }

        return output;
    }

    private async Task<string?> FetchMcmodOneAsync(ModNameQuery query, CancellationToken cancellationToken)
    {
        var url = string.Format(McmodSearchUrl, Uri.EscapeDataString(query.ModId));

        // 实测：同一个连接连续请求时，服务端大约一半的次数会回一个「空壳页」——
        // HTTP 200、HTML 完整、就是一条搜索结果都没有。python 单发一个请求从来不会这样，
        // 所以基本可以断定是连接复用被区别对待了。关掉 keep-alive 每次都新建连接，再兜两次重试。
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (name, hadResults) = await FetchMcmodOnceAsync(url, query, cancellationToken)
                .ConfigureAwait(false);

            if (hadResults) return name;

            // 三次都是空壳页：当网络问题处理（返回 null 表示「别缓存，下次再来」），
            // 不能当成「这个模组没有中文名」记下来 —— 否则一次运气不好就永久显示英文名了。
            if (attempt == 3) return null;

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>一次 MC百科请求。返回 (中文名, 页面里到底有没有搜索结果)。</summary>
    private async Task<(string? Name, bool HadResults)> FetchMcmodOnceAsync(
        string url, ModNameQuery query, CancellationToken cancellationToken)
    {
        try
        {
            // 必须显式要 HTTP/1.1：.NET 默认协商 HTTP/2，而 MC百科在 h2 下
            // 回的基本都是空壳页，HTTP/1.1 才是完整结果。
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.ConnectionClose = true;

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log?.Invoke($"[中文名] {query.ModId}：MC百科 HTTP {(int)response.StatusCode}");
                return (null, true);   // 当网络问题，别重试
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var matches = McmodResultLink.Matches(html);
            if (matches.Count == 0) return (null, false);   // 空壳页，值得重试一次

            // 只看第一条 —— 搜索结果按相关度排，第一条对不上就说明这个 modId 在 MC百科上
            // 对应的是别的模组；再往下翻只会翻出「XX 非官方版」「XX 附属」这类，越翻越错。
            foreach (Match match in matches)
            {
                var text = StripTags(match.Groups[1].Value);
                if (text.Length == 0) continue;

                // 结果里还会有一列是纯链接文字（"www.mcmod.cn/class/744.html"），跳过
                if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                var parsed = ParseMcmodTitle(text, query);

                Log?.Invoke(parsed is null
                    ? $"[中文名] {query.ModId}：MC百科第一条「{Trim(text, 30)}」对不上"
                    : $"[中文名] {query.ModId}：MC百科「{Trim(text, 30)}」→ {parsed}");

                return (parsed, true);
            }

            Log?.Invoke($"[中文名] {query.ModId}：MC百科没有可用的搜索结果");
            return (string.Empty, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, true);
        }
    }

    private static readonly Regex McmodResultLink = new(
        @"<a[^>]*?href=""[^""]*?/class/\d+\.html""[^>]*>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string StripTags(string html)
        => System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(html, " ")).Trim()
            .Replace("\u00a0", " ")
            .Split('\n', '\r')
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.Length > 0) ?? string.Empty;

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 解析 <c>[AS] 苹果皮 (AppleSkin)</c>，核对英文名后返回中文名。
    /// </summary>
    /// <returns>中文名；对不上或没有中文名时返回 <c>null</c>。</returns>
    private static string? ParseMcmodTitle(string title, ModNameQuery query)
    {
        var text = title.Trim();
        if (text.Length == 0) return null;

        // 剥掉开头的 [AS] / 【AS】
        while (text.StartsWith('[') || text.StartsWith('【'))
        {
            var close = text.StartsWith('[') ? text.IndexOf(']') : text.IndexOf('】');
            if (close < 0 || close >= text.Length - 1) break;

            var rest = text[(close + 1)..].Trim();
            if (rest.Length == 0) break;
            text = rest;
        }

        // 括号里的英文名：取最后一对圆括号
        var open = text.LastIndexOf('(');
        var closeParen = text.LastIndexOf(')');

        string? englishName = null;
        var chinese = text;

        if (open >= 0 && closeParen > open)
        {
            englishName = text[(open + 1)..closeParen].Trim();
            chinese = text[..open].Trim();
        }

        if (chinese.Length == 0) return null;

        // 有英文名就必须对得上，否则宁可不要 —— 搜错模组比不显示更糟
        if (!string.IsNullOrEmpty(englishName))
        {
            if (!LooksSameMod(englishName, query)) return null;
        }
        else
        {
            // 没括号的（"FabricAPI"、"ClothConfig API"）通常是「中文名就是英文名」，
            // 这种显示出来跟原名差不多，不值得覆盖
            return null;
        }

        // 中文名里一个汉字都没有，说明这个模组没中文名
        if (!chinese.Any(IsChinese)) return null;

        return chinese.Length > 40 ? null : chinese;
    }

    private static bool IsChinese(char c) => c is >= '\u4e00' and <= '\u9fff';

    /// <summary>
    /// 搜索结果里的英文名跟 jar 里写的对得上吗。
    /// </summary>
    /// <remarks>
    /// 归一化后互相包含就算 —— 模组作者写名字很随意：
    /// jar 里可能是 <c>Lithium</c>、<c>Xaero's Minimap</c>、<c>AppleSkin</c>，
    /// 搜索结果可能是 <c>Lithium</c>、<c>Xaero's Minimap</c>、<c>AppleSkin</c>，
    /// 也可能多个 <c>Mod</c> 后缀。modId 也拿来比一遍，因为它更规整。
    /// </remarks>
    private static bool LooksSameMod(string englishName, ModNameQuery query)
    {
        var target = Normalize(englishName);
        if (target.Length < 3) return false;

        foreach (var candidate in new[] { query.OriginalName, query.ModId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            var other = Normalize(candidate);
            if (other.Length < 3) continue;

            if (target == other) return true;

            // 短的至少 5 个字符才允许「包含」判定，否则 "iris" 会命中一大堆
            var shorter = target.Length <= other.Length ? target : other;
            var longer = target.Length <= other.Length ? other : target;

            if (shorter.Length >= 5 && longer.Contains(shorter, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>小写 + 只留字母数字。去掉空格、连字符、撇号、点 —— 这些都是各写各的。</summary>
    private static string Normalize(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        var length = 0;

        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    // ───────────────────────── 公共 ─────────────────────────

    /// <summary>
    /// 把 SCL 返回的纯文本收拾成能显示的名字。
    /// </summary>
    /// <remarks>
    /// SCL 里有些名字带缩写，比如苹果皮存的是 <c>[AS] 苹果皮</c> —— 前面的 <c>[AS]</c>
    /// 是英文缩写，中文名在方括号后面。剥掉更干净。
    /// </remarks>
    private static string CleanName(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return string.Empty;

        var newline = text.IndexOfAny(new[] { '\r', '\n' });
        if (newline > 0) text = text[..newline].Trim();

        while (text.StartsWith('[') || text.StartsWith('【'))
        {
            var close = text.StartsWith('[') ? text.IndexOf(']') : text.IndexOf('】');
            if (close < 0 || close >= text.Length - 1) break;

            var rest = text[(close + 1)..].Trim();
            if (rest.Length == 0) break;

            text = rest;
        }

        // 长到离谱的多半不是名字，而是错误页或者整段介绍
        return text.Length > 40 ? string.Empty : text;
    }

    /// <summary>modId → URL-safe base64（去掉 <c>=</c> 填充，gitee 路径里不能带）。</summary>
    private static string ToUrlSafeBase64(string modId)
    {
        var bytes = Encoding.UTF8.GetBytes(modId);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    // ── 缓存读写 ──

    private void EnsureCacheLoaded()
    {
        lock (_cacheLock)
        {
            if (_cacheLoaded) return;
            _cacheLoaded = true;

            try
            {
                if (!File.Exists(_cacheFile)) return;

                var json = File.ReadAllText(_cacheFile);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data is null) return;

                foreach (var (key, value) in data)
                {
                    if (!string.IsNullOrWhiteSpace(key)) _cache[key] = value ?? string.Empty;
                }
            }
            catch
            {
                // 缓存坏了就当没有 —— 顶多重新查一遍，不能让窗口打不开
            }
        }
    }

    private void SaveCache()
    {
        try
        {
            Dictionary<string, string> snapshot;
            lock (_cacheLock) snapshot = new(_cache, StringComparer.OrdinalIgnoreCase);

            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再替换：写到一半崩掉不会把好的缓存变成半截 JSON
            var temp = _cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                // 中文直出，不要 \u82F9\u679C\u76AE —— 这文件用户可能会打开看
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

            File.Move(temp, _cacheFile, overwrite: true);
        }
        catch
        {
            // 存不下就算了，内存里还有，下次开窗口重新查
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _mcmodGate.Dispose();
    }
}
