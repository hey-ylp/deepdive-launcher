using System.Diagnostics;

namespace DeepDive.Core.Launcher;

/// <summary>一次下载源连通性探测的结果。</summary>
/// <param name="Ok">是否拿到了正常响应。</param>
/// <param name="Milliseconds">耗时（毫秒）。失败时是超时前的耗时。</param>
/// <param name="Detail">失败原因或状态码，用于界面提示。</param>
public sealed record SourceProbeResult(bool Ok, int Milliseconds, string Detail)
{
    /// <summary>「0.8 秒可达」/「连不上（6.0 秒超时）」。</summary>
    public string Describe() => Ok
        ? $"{Milliseconds / 1000.0:F1} 秒可达"
        : $"连不上（{Detail}）";
}

/// <summary>
/// 下载源测速：只拉版本清单的响应头，不读内容。
/// </summary>
/// <remarks>
/// 切换源这个动作本身不会暴露问题 —— 真正下东西的时候才发现连不上就太晚了
/// （用户已经在等游戏装好）。所以在切完立刻测一下，几秒钟就能知道他到底选对没有。
/// </remarks>
public static class SourceProbe
{
    /// <summary>单次探测的超时（秒）。超过就当它连不上，再等下去也没意义。</summary>
    public const int TimeoutSeconds = 6;

    /// <summary>探测指定源。</summary>
    public static async Task<SourceProbeResult> ProbeAsync(
        bool useMirror,
        CancellationToken cancellationToken = default)
    {
        var url = useMirror
            ? MirrorConfigurator.VersionManifestUrl
            : MirrorConfigurator.MojangVersionManifestUrl;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            // 单次尝试、单次超时：这是「测速」，不是「下载」。重试会让「连不上」变成 24 秒后才知道。
            var (http, _) = MirrorConfigurator.Create(
                useMirror,
                attemptTimeoutSeconds: TimeoutSeconds,
                maxAttempts: 1);

            using (http)
            using (var response = await http
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                       .ConfigureAwait(false))
            {
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                    return new SourceProbeResult(false, (int)stopwatch.ElapsedMilliseconds,
                        $"HTTP {(int)response.StatusCode}");

                return new SourceProbeResult(true, (int)stopwatch.ElapsedMilliseconds,
                    $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new SourceProbeResult(false, (int)stopwatch.ElapsedMilliseconds, $"{TimeoutSeconds} 秒超时");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new SourceProbeResult(false, (int)stopwatch.ElapsedMilliseconds, ex.GetType().Name);
        }
    }
}
