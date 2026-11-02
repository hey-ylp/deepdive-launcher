using System.Net;
using System.Text.Json;

namespace DeepDive.Core.Translation;

/// <summary>一次翻译请求的结果。</summary>
/// <param name="Text">译文。<c>null</c> 表示没翻成。</param>
/// <param name="QuotaExhausted">
/// 免费额度用完了。<b>这是全局状态</b> —— 今天剩下的所有请求都会失败，
/// 调用方应该立刻停下来，别再一条条试。
/// </param>
/// <param name="Error">失败原因（日志用）。</param>
public sealed record TranslateOutcome(string? Text, bool QuotaExhausted, string? Error);

/// <summary>
/// MyMemory 免费翻译接口。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是它</b>：2026-10-29 实测了五条通道，只有这条能用 ——
/// Google 的 <c>translate.googleapis.com</c> 直接超时（10 秒无响应，国内访问不了），
/// DeepLX 的公共实例返回的是推广链接，LibreTranslate 公共站 301 到付费墙，
/// 微软的 <c>edge.microsoft.com/translate/auth</c> 返回 404。
/// </para>
/// <para>
/// <b>已知局限</b>：匿名每天约 5000 词的额度，超了要等。质量是机翻水平，
/// MC 专有名词会翻错（<c>nether</c> 会翻成「地下空间」而不是「下界」）。
/// 所以译文只放在<b>简介</b>上，模组名永远保留英文原文 —— 社区里大家就叫 Sodium，不叫「钠」。
/// </para>
/// <para>
/// 单次 <c>q</c> 参数上限 500 字节，超了整个请求会被拒，所以这里主动截断。
/// </para>
/// </remarks>
public sealed class MyMemoryTranslator : IDisposable
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";

    /// <summary>q 参数上限 500 字节，留点余量给 URL 编码后的膨胀。</summary>
    private const int MaxBytes = 460;

    /// <summary>额度用完时服务器会把这些字样塞进译文里，而不是返回错误码。</summary>
    private static readonly string[] QuotaMarkers =
    {
        "MYMEMORY WARNING",
        "YOU USED ALL AVAILABLE FREE TRANSLATIONS",
        "INVALID LANGUAGE PAIR",
        "PLEASE SELECT TWO DISTINCT LANGUAGES",
    };

    private readonly HttpClient _http;

    public MyMemoryTranslator()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler)
        {
            // 实测一般 1~2 秒。8 秒还没回来说明线路有问题，早点放弃比挂在那里强
            Timeout = TimeSpan.FromSeconds(8),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepDive/0.1.0 (Minecraft launcher; local build)");
    }

    /// <summary>翻一段英文。</summary>
    public async Task<TranslateOutcome> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var text = request.Text;

        if (string.IsNullOrWhiteSpace(text))
            return new TranslateOutcome(null, false, "空文本");

        // 单词不翻：Sodium / Create / JEI 这类是模组名，机翻成「钠」比英文更让人发懵。
        // 简介都是句子，必然带空格，这条不会误伤。
        if (!TranslationTextCleaner.WorthTranslating(text))
            return new TranslateOutcome(null, false, "太短或是单个词，不翻");

        return await SendAsync(text, "en|zh-CN", request.KeepAsIs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 中文关键词 → 英文（给「用中文搜 Modrinth」用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不复用简介那条路</b>：<see cref="TranslationTextCleaner.WorthTranslating"/>
    /// 会挡掉「没有空格」和「短于 12 字」的文本 —— 那条规则是为英文模组名定制的，
    /// 而中文查询恰好又短又没空格（「机械动力」四个字），照搬过来一条都翻不了。
    /// </para>
    /// <para>
    /// 反过来，中文查询短也就意味着机翻不容易跑偏，而且这里只用来<b>搜</b>，
    /// 搜错了用户一眼就能看出来（结果不对），不像简介那样会静悄悄误导人。
    /// </para>
    /// </remarks>
    public async Task<TranslateOutcome> TranslateQueryAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslateOutcome(null, false, "空文本");

        if (text.Contains('§'))
            return new TranslateOutcome(null, false, "含格式码，跳过");

        return await SendAsync(text, "zh-CN|en", Array.Empty<string>(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>真正发请求的那一段（两个方向共用）。</summary>
    private async Task<TranslateOutcome> SendAsync(
        string text,
        string languagePair,
        IReadOnlyList<string> keepAsIs,
        CancellationToken cancellationToken)
    {
        // 带 § 的是 MC 格式码文本（§a 绿色之类），机翻必然把格式码搅坏。
        // 这类翻车比不翻更糟，直接放弃，界面显示原文。
        if (text.Contains('§'))
            return new TranslateOutcome(null, false, "含格式码，跳过");

        // 先按字节截断，再上占位符 —— 反过来的话截断可能把 [[T1]] 从中间劈开
        var input = Truncate(text, MaxBytes);

        var (guarded, map) = PlaceholderGuard.Protect(input, keepAsIs);

        var url = $"{Endpoint}?q={Uri.EscapeDataString(guarded)}&langpair={Uri.EscapeDataString(languagePair)}";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new TranslateOutcome(null, true, "HTTP 429 限流");

            if (!response.IsSuccessStatusCode)
                return new TranslateOutcome(null, false, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return Parse(json, text, map);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 超时、DNS、连接重置 —— 都当这一条失败，不影响后面的
            return new TranslateOutcome(null, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>解析返回的 JSON，把「服务器拿警告当译文」这种情况识别出来。</summary>
    private static TranslateOutcome Parse(string json, string original, IReadOnlyList<string> placeholders)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var status = root.TryGetProperty("responseStatus", out var statusElement) &&
                         statusElement.TryGetInt32(out var code)
                ? code
                : 200;

            if (root.TryGetProperty("quotaFinished", out var quota) &&
                quota.ValueKind == JsonValueKind.True)
                return new TranslateOutcome(null, true, "额度用尽");

            if (status == 429)
                return new TranslateOutcome(null, true, "HTTP 429 限流");

            if (!root.TryGetProperty("responseData", out var data) ||
                !data.TryGetProperty("translatedText", out var translatedElement))
                return new TranslateOutcome(null, false, "返回里没有译文");

            var translated = translatedElement.GetString();
            if (string.IsNullOrWhiteSpace(translated))
                return new TranslateOutcome(null, false, "译文为空");

            foreach (var marker in QuotaMarkers)
            {
                if (translated.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    var exhausted = marker.Contains("MYMEMORY", StringComparison.OrdinalIgnoreCase);
                    return new TranslateOutcome(null, exhausted, marker);
                }
            }

            // 原样返回说明它没翻（可能本来就认不出是英文），当失败处理，免得界面出现两行一样的字
            if (string.Equals(translated.Trim(), original.Trim(), StringComparison.Ordinal))
                return new TranslateOutcome(null, false, "返回了原文");

            // 先把占位符换回模组名（Sodium、Fabric 这些），再擦 HTML 标签和改术语。
            // 顺序不能换：Clean 里的「等于原文」判断要拿真正的原文比，
            // 而占位符状态下两边永远不相等。
            var restored = PlaceholderGuard.Restore(translated, placeholders);

            var cleaned = TranslationTextCleaner.Clean(restored, original);
            if (cleaned is null)
                return new TranslateOutcome(null, false, "译文含标签或与原文相同，清理后不可用");

            return new TranslateOutcome(cleaned, false, null);
        }
        catch (JsonException ex)
        {
            return new TranslateOutcome(null, false, $"JSON 解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按 UTF-8 字节数截断，且不切断词。
    /// </summary>
    /// <remarks>
    /// 按字符数截会翻车：一个中文字 3 字节，500 字符可能 1500 字节，超了接口直接拒。
    /// </remarks>
    private static string Truncate(string text, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var length = 0;
        var bytes = 0;

        foreach (var c in text)
        {
            var size = System.Text.Encoding.UTF8.GetByteCount(c.ToString());
            if (bytes + size > maxBytes) break;

            bytes += size;
            length++;
        }

        if (length == 0) return text[..1];

        var cut = text[..length];

        // 尽量停在词边界，别把单词劈成两半（劈开的话机翻会瞎猜）
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > cut.Length / 2)
            cut = cut[..lastSpace];

        return cut.TrimEnd() + "…";
    }

    public void Dispose() => _http.Dispose();
}
