using System.IO;

namespace DeepDive.Core.Translation;

/// <summary>
/// 翻译编排层：先查缓存，未命中再打免费接口，逐条回调给界面。
/// </summary>
/// <remarks>
/// <para>
/// <b>整个设计的核心是「渐进式」</b>：搜索结果一出来马上显示英文原文，
/// 译文在后台一条条补进去。用户不会对着空白等 20 秒，而是看着文字一条条变中文。
/// </para>
/// <para>
/// 三条约束决定了这里的写法：
/// </para>
/// <list type="number">
///   <item><b>串行 + 间隔</b> —— MyMemory 免费额度按天算，并发打只会更快撞墙。</item>
///   <item><b>额度一耗尽就整体停</b> —— 剩下的条目继续试只会全部失败，还拖慢界面。</item>
///   <item><b>翻译失败绝不抛</b> —— 这是附加功能，翻不出来就显示英文原文，不能影响搜内容。</item>
/// </list>
/// </remarks>
public sealed class TranslationService : IDisposable
{
    /// <summary>两条请求之间的间隔。实测 150ms 不会被当攻击，也不会慢到肉眼可感。</summary>
    private static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(150);

    private readonly MyMemoryTranslator _translator;
    private readonly TranslationCache _cache;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>今天额度用完了。进程内记住，别反复去撞。</summary>
    private volatile bool _quotaExhausted;

    /// <summary>日志回调，签名与其它服务保持一致。</summary>
    public event Action<object, string>? Log;

    /// <summary>
    /// </summary>
    /// <param name="instancesRoot">
    /// <c>instances</c> 目录。缓存写在它下面的 <c>translation-cache.json</c> ——
    /// 所有实例共用一份，因为译文和玩哪个实例没关系。
    /// </param>
    public TranslationService(string instancesRoot)
    {
        _cache = new TranslationCache(Path.Combine(instancesRoot, "translation-cache.json"));
        _translator = new MyMemoryTranslator();
    }

    /// <summary>缓存里已有多少条译文。</summary>
    public int CacheCount => _cache.Count;

    /// <summary>今天的免费额度是不是已经用完。</summary>
    public bool QuotaExhausted => _quotaExhausted;

    /// <summary>查缓存，不去网络。命中返回译文，否则 <c>null</c>。</summary>
    public string? FromCache(string text) => _cache.Get(text);

    /// <summary>
    /// 中文关键词 → 英文检索词。翻不出来返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 走的还是同一个 MyMemory 通道、同一份额度，但<b>缓存键加了 <c>q:</c> 前缀</b> ——
    /// 否则一个中文关键词「优化」和某条英文简介碰巧同名时会串味。
    /// </para>
    /// <para>
    /// 结果一定要缓存：同一个词用户会反复搜（「优化」之类的），
    /// 缓存住就只花一次额度。
    /// </para>
    /// </remarks>
    public async Task<string?> TranslateQueryAsync(string chineseText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(chineseText)) return null;

        var key = "q:" + chineseText.Trim();

        if (_cache.Get(key) is { } cached) return cached;
        if (_quotaExhausted) return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TranslateOutcome outcome;
        try
        {
            outcome = await _translator.TranslateQueryAsync(chineseText, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (outcome.QuotaExhausted) _quotaExhausted = true;

        if (outcome.Text is { } english)
        {
            _cache.Set(key, english);
            return english;
        }

        if (outcome.Error is not null && !_quotaExhausted)
            Log?.Invoke(this, $"关键词翻译失败（{outcome.Error}）：{Shorten(chineseText)}");

        return null;
    }

    /// <summary>
    /// 批量翻译，翻好一条回调一条。
    /// </summary>
    /// <param name="requests">要翻的内容（可以重复，内部按原文去重）。</param>
    /// <param name="onTranslated">
    /// 回调：参数是「原文 + 译文」。
    /// <b>可能在后台线程调用</b>，界面层要自己切回 UI 线程。
    /// </param>
    /// <param name="cancellationToken">用户切页或者关窗口就取消。</param>
    public async Task TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests,
        Action<string, string> onTranslated,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return;

        if (_quotaExhausted)
        {
            // 额度今天用完了。缓存里有的照样给，没有的就让界面留在英文。
            foreach (var request in requests)
            {
                if (_cache.Get(request.Text) is { } cached)
                    onTranslated(request.Text, cached);
            }

            return;
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        var cacheHits = 0;
        var translated = 0;
        var failed = 0;

        foreach (var request in requests)
        {
            var text = request.Text;

            if (cancellationToken.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!done.Add(text)) continue;

            // 单词、太短的、没字母的，静默跳过 —— 这些翻出来一定是灾难，
            // 而且不值得刷日志（一页里能有一半是这种也不奇怪）
            if (!TranslationTextCleaner.WorthTranslating(text)) continue;

            // 缓存优先 —— 这一步一个网络请求都不花
            if (_cache.Get(text) is { } hit)
            {
                cacheHits++;
                onTranslated(text, hit);
                continue;
            }

            if (_quotaExhausted) continue;

            string? result;
            string? error;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var outcome = await _translator
                    .TranslateAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                result = outcome.Text;
                error = outcome.Error;

                if (outcome.QuotaExhausted)
                {
                    _quotaExhausted = true;
                    Log?.Invoke(this, "翻译额度今天用完了，剩下的内容显示英文原文。明天自动恢复。");
                }
            }
            finally
            {
                _gate.Release();
            }

            if (result is not null)
            {
                _cache.Set(text, result);
                translated++;
                onTranslated(text, result);
            }
            else
            {
                failed++;
                if (error is not null && !_quotaExhausted)
                    Log?.Invoke(this, $"翻译跳过（{error}）：{Shorten(text)}");
            }

            if (_quotaExhausted) continue;

            try
            {
                await Task.Delay(RequestGap, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (translated > 0 || cacheHits > 0 || failed > 0)
        {
            Log?.Invoke(this,
                $"翻译完成：新翻 {translated} 条，缓存命中 {cacheHits} 条，跳过 {failed} 条" +
                (_quotaExhausted ? "（额度已用完）" : string.Empty));
        }
    }

    private static string Shorten(string text) =>
        text.Length <= 40 ? text : text[..40] + "…";

    public void Dispose()
    {
        _translator.Dispose();
        _gate.Dispose();
    }
}
