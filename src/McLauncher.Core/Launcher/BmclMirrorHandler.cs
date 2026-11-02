using System.Net;
using System.Net.Http;

namespace McLauncher.Core.Launcher;

/// <summary>
/// 把 Mojang / 加载器官方的下载地址重写到 BMCLAPI 国内镜像，并给每个请求加上
/// 「单次超时 + 自动重试」。
/// </summary>
/// <remarks>
/// <para>
/// <b>重写规则</b>来源：https://bmclapidoc.bangbang93.com/ （2026-10-28 逐条实测核对）。
/// 模型是「绝对 URL 前缀替换」，不是「换 host」—— 因为 BMCLAPI 对部分源会加/去路径段：
/// </para>
/// <list type="bullet">
/// <item><c>launchermeta / piston-meta / piston-data / launcher.mojang.com/...</c> → <c>bmclapi2.bangbang93.com/...</c>（路径原样）</item>
/// <item><c>libraries.minecraft.net/...</c> → <c>bmclapi2.bangbang93.com/maven/...</c>（加 /maven）</item>
/// <item><c>resources.download.minecraft.net/...</c> → <c>bmclapi2.bangbang93.com/assets/...</c>（加 /assets）</item>
/// <item><c>files.minecraftforge.net/maven/...</c> → <c>bmclapi2.bangbang93.com/maven/...</c></item>
/// <item><c>maven.neoforged.net/releases/...</c> → <c>bmclapi2.bangbang93.com/maven/...</c>（去 /releases 换 /maven）</item>
/// <item><c>meta.fabricmc.net/...</c> → <c>bmclapi2.bangbang93.com/fabric-meta/...</c></item>
/// <item><c>maven.fabricmc.net/...</c> → <c>bmclapi2.bangbang93.com/maven/...</c></item>
/// </list>
/// <para>
/// ⚠️ BMCLAPI 返回的版本清单里，版本 JSON / assetsIndex 的 URL 仍是 <c>piston-meta.mojang.com</c>，
/// 它不会替你换 —— 所以 piston-* 那几条规则是必须的。
/// </para>
/// <para>
/// <b>为什么需要重试</b>：BMCLAPI 对小文件是「按需回源」的。首次请求某个 JRE/资源文件时它要先
/// 去上游取，这一趟可能挂 60 秒以上（实测 22KB 的 DLL 有 200ms 拿到的，也有卡 61 秒的）。
/// 一次安装要拉 4000+ 个小文件，只要有几个卡住，整个并发池就被拖死 —— 实测吞吐因此掉到 100KB/s。
/// 加超时+重试后，卡住的请求会被换一条连接重来，通常第二次就命中已回源的缓存。
/// </para>
/// </remarks>
public sealed class BmclMirrorHandler : DelegatingHandler
{
    /// <summary>BMCLAPI 主站。</summary>
    public const string BmclApiBase = "https://bmclapi2.bangbang93.com";

    /// <summary>
    /// 重写规则表。第三项 <c>IsLoaderMaven</c> 决定它<b>是否无条件走镜像</b>。
    /// </summary>
    /// <remarks>
    /// 加载器（Forge / NeoForge / Fabric）的 maven <b>必须无条件重写</b>，跟用户选哪个源无关：
    /// <c>maven.neoforged.net</c> 在国内被 DNS 污染（解析到 199.59.150.43，根本不是他家地址），
    /// <c>maven.minecraftforge.net</c> 能解析但连一次要 20 秒以上、3 次里 2 次失败。
    /// 实测代价：装 NeoForge 1.20.1 时 46 个文件下到第 42 个（fastcsv）就永久卡死 ——
    /// 那正是 maven.neoforged.net 上的包。
    /// </remarks>
    private static readonly (string From, string To, bool IsLoaderMaven)[] PrefixRules =
    {
        // 版本清单 / 版本 JSON / assetsIndex（现代 host）
        ("https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/", false),
        ("http://launchermeta.mojang.com/",  "https://bmclapi2.bangbang93.com/", false),
        ("https://piston-meta.mojang.com/",  "https://bmclapi2.bangbang93.com/", false),
        ("https://piston-data.mojang.com/",  "https://bmclapi2.bangbang93.com/", false),

        // 旧版 host（1.6 之前的老版本还会用）
        ("https://launcher.mojang.com/",     "https://bmclapi2.bangbang93.com/", false),
        ("http://launcher.mojang.com/",      "https://bmclapi2.bangbang93.com/", false),

        // 库文件
        ("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/", false),

        // 资源文件（assets）
        ("http://resources.download.minecraft.net/",  "https://bmclapi2.bangbang93.com/assets/", false),
        ("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/", false),

        // ── 模组加载器：无论选哪个源都走镜像 ──
        ("https://files.minecraftforge.net/maven/", "https://bmclapi2.bangbang93.com/maven/", true),
        ("https://maven.minecraftforge.net/",       "https://bmclapi2.bangbang93.com/maven/", true),
        ("https://maven.neoforged.net/releases/",   "https://bmclapi2.bangbang93.com/maven/", true),
        ("https://maven.fabricmc.net/",             "https://bmclapi2.bangbang93.com/maven/", true),
        ("https://meta.fabricmc.net/",              "https://bmclapi2.bangbang93.com/fabric-meta/", true),

        // Java 运行时清单（无尾斜杠的兜底，必须放最后）
        ("https://launchermeta.mojang.com",  "https://bmclapi2.bangbang93.com", false),
    };

    /// <summary>
    /// 是否连 Mojang 的资源也走镜像。
    /// </summary>
    /// <remarks>
    /// 由用户选的下载源决定：选官方源时这里为 <c>false</c>，但加载器 maven 仍然会重写 ——
    /// 那是「能不能装上」的问题，不是「快不快」的问题，不该交给用户去猜。
    /// </remarks>
    public bool MirrorMojang { get; init; } = true;

    /// <summary>单次尝试的超时。超时后换一条连接重试。</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>最大尝试次数（含首次）。</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>是否启用重试。关掉后行为等同普通直通。</summary>
    public bool EnableRetry { get; init; } = true;

    private int _rewriteCount;
    private int _retryCount;
    private int _timeoutCount;

    /// <summary>已重写的请求数。</summary>
    public int RewriteCount => Volatile.Read(ref _rewriteCount);

    /// <summary>已发起的重试次数。</summary>
    public int RetryCount => Volatile.Read(ref _retryCount);

    /// <summary>因单次超时而重试的次数 —— 这个数字能直接反映镜像的「按需回源」严重程度。</summary>
    public int TimeoutCount => Volatile.Read(ref _timeoutCount);

    /// <summary>判断一个地址会不会被重写（不做实际改写），用于预检/测试。</summary>
    public static bool ShouldRewrite(string url) => Match(url) is not null;

    /// <summary>返回重写后的地址（全量规则，不看模式）；无需重写时返回 null。</summary>
    public static string? Rewrite(string url)
    {
        var rule = Match(url);
        return rule is null ? null : rule.Value.To + url[rule.Value.From.Length..];
    }

    /// <summary>
    /// 按当前模式决定这个地址要不要重写：官方源模式下只重写加载器 maven。
    /// </summary>
    private string? RewriteForCurrentMode(string url)
    {
        var rule = Match(url);
        if (rule is null) return null;

        // 官方源模式：Mojang 的资源直连，加载器 maven 照样走镜像
        if (!MirrorMojang && !rule.Value.IsLoaderMaven) return null;

        return rule.Value.To + url[rule.Value.From.Length..];
    }

    private static (string From, string To, bool IsLoaderMaven)? Match(string url)
    {
        // 顺序敏感：长前缀在前，兜底的无尾斜杠规则在最后
        foreach (var rule in PrefixRules)
        {
            if (url.StartsWith(rule.From, StringComparison.OrdinalIgnoreCase))
                return rule;
        }
        return null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1) URL 重写。原始地址要留着 —— 镜像挂了得能退回去（见下面 preferOriginal）
        var originalUri = request.RequestUri;
        var rule = originalUri is null ? null : Match(originalUri.AbsoluteUri);

        Uri? rewrittenUri = null;
        var isLoaderMaven = rule is { IsLoaderMaven: true };

        if (originalUri is not null && rule is { } r && (MirrorMojang || r.IsLoaderMaven))
        {
            rewrittenUri = new Uri(r.To + originalUri.AbsoluteUri[r.From.Length..], UriKind.Absolute);
            request.RequestUri = rewrittenUri;
            Interlocked.Increment(ref _rewriteCount);
        }

        // 2) 只有无 body 的 GET/HEAD 能安全重试（重试需要克隆请求）
        var canRetry = EnableRetry &&
                       (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head);
        var attempts = canRetry ? MaxAttempts : 1;

        Exception? lastError = null;
        var preferOriginal = false;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var current = attempt == 1 ? request : CloneForRetry(request);

            // 什么时候用原始地址：
            //  · 上一次从镜像拿到了 404（镜像没这个文件）
            //  · 加载器 maven 的第 2 次尝试起 —— BMCLAPI 的 maven 是 302 跳到下载节点
            //    （bmclapi20.sakura.sld.tw），那个节点经常连不上。实测 sponge-mixin 在
            //    镜像上永远挂死，而 maven.fabricmc.net 虽然只有 37 KB/s，但它能下完。
            //    死磕镜像 = 永远装不完，退回去 = 慢一点但能成。
            var targetUri = preferOriginal || (isLoaderMaven && attempt > 1)
                ? originalUri
                : rewrittenUri ?? originalUri;

            if (targetUri is not null && current.RequestUri != targetUri)
                current.RequestUri = targetUri;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // 加载器 maven 在镜像上只给 12 秒：BMCLAPI 的 maven 是 302 跳到下载节点，
            // 那个节点要么秒回、要么根本连不上 —— 等满 30 秒再回退纯属浪费
            // （实测用户装 Fabric 那次，光超时重试就吃了 32 次 × 30 秒）。
            var attemptTimeout = isLoaderMaven && !preferOriginal && rewrittenUri is not null
                ? TimeSpan.FromSeconds(Math.Min(12, AttemptTimeout.TotalSeconds))
                : AttemptTimeout;

            attemptCts.CancelAfter(attemptTimeout);

            try
            {
                var response = await base.SendAsync(current, attemptCts.Token).ConfigureAwait(false);

                // 镜像上没有这个文件 → 立刻改用原始地址，不要拿 404 去重试镜像
                if (response.StatusCode == HttpStatusCode.NotFound &&
                    rewrittenUri is not null && attempt < attempts)
                {
                    response.Dispose();
                    preferOriginal = true;
                    Interlocked.Increment(ref _retryCount);
                    continue;
                }

                // 5xx / 429 / 408 也当作可重试
                if (canRetry && attempt < attempts && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    Interlocked.Increment(ref _retryCount);
                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单次尝试超时（不是调用方取消）→ 换连接重来
                Interlocked.Increment(ref _timeoutCount);
                Interlocked.Increment(ref _retryCount);
                lastError = new TimeoutException(
                    $"请求超时（单次上限 {AttemptTimeout.TotalSeconds:F0} 秒）：{current.RequestUri}");
                current.Dispose();

                if (attempt >= attempts) break;
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                current.Dispose();

                if (attempt >= attempts) break;
                Interlocked.Increment(ref _retryCount);
                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new HttpRequestException("下载失败，重试次数已用尽");
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code == HttpStatusCode.RequestTimeout ||
        code == HttpStatusCode.TooManyRequests ||
        (int)code >= 500;

    private static Task BackoffAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(150 * Math.Pow(2, attempt - 1)), ct);

    /// <summary>克隆一个请求用于重试（GET/HEAD，无 body）。</summary>
    private static HttpRequestMessage CloneForRetry(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
