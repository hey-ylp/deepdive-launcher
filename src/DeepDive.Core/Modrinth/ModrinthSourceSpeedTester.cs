using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeepDive.Core.Launcher;

namespace DeepDive.Core.Modrinth;

/// <summary>单个模组源的一次测速结果。</summary>
/// <param name="Key">源的键（<c>official</c> / <c>mcimirror</c>）。</param>
/// <param name="Display">界面上显示的名字。</param>
/// <param name="LatencyMs">首字节耗时（毫秒）。失败为 <c>null</c>。</param>
/// <param name="Error">失败原因，成功为 <c>null</c>。</param>
public sealed record ModrinthSpeedResult(string Key, string Display, int? LatencyMs, string? Error)
{
    /// <summary>这一项测通了没有。</summary>
    public bool Ok => LatencyMs.HasValue;
}

/// <summary>
/// 模组源测速：拿一次真实搜索请求，量到「收到响应头」为止的时间。
/// </summary>
/// <remarks>
/// <para>
/// <b>测的是首字节，不是下载速度</b>。模组源体验差主要差在「点搜索之后转圈多久」，
/// 那一段时间由 DNS + TCP + TLS 握手 + 服务端处理决定，跟带宽没关系。
/// 所以到这里就停，不读响应体。
/// </para>
/// <para>
/// <b>每次新建 <see cref="HttpClient"/></b>：池化连接会跳过握手，测出来的数字漂亮但没有意义。
/// 用户第一次打开内容页就是冷连接，要测的正是那个场景。
/// </para>
/// <para>
/// 请求走的是 <c>{BaseUrl}/search</c>，和 <see cref="ModrinthClient"/> 真实搜索同一条路径 ——
/// 换个假端点（比如首页 <c>/</c>）可能被 CDN 直接回 200，测不到 API 的真实响应。
/// </para>
/// </remarks>
public static class ModrinthSourceSpeedTester
{
    /// <summary>单次测速的硬超时。比真实搜索的 12 秒短，测速不该让人干等。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>测一个源。</summary>
    public static async Task<ModrinthSpeedResult> TestAsync(
        ModrinthSourceChoice choice, CancellationToken cancellationToken = default)
    {
        // 带 query 参数：Modrinth 的 /search 没 query 也能回，但带上去更贴近真实请求
        var url = $"{choice.BaseUrl}/search?limit=1&query=minecraft";

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        using var http = new HttpClient(handler) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(ModrinthClient.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var clock = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // ResponseHeadersRead：读到响应头就返回，不把 JSON 拉下来。
            // 拉 body 测出来的就变成「带宽 + 服务端生成结果」的混合值了。
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            clock.Stop();
            var ms = (int)clock.ElapsedMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                return new ModrinthSpeedResult(
                    choice.Key, choice.Display, ms, $"HTTP {(int)response.StatusCode}");
            }

            return new ModrinthSpeedResult(choice.Key, choice.Display, ms, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时包成 TaskCanceledException，外层 token 没取消说明是超时
            clock.Stop();
            return new ModrinthSpeedResult(
                choice.Key, choice.Display, null, $"超时（>{Timeout.TotalSeconds:F0} 秒）");
        }
        catch (Exception ex)
        {
            clock.Stop();
            return new ModrinthSpeedResult(choice.Key, choice.Display, null, ex.Message);
        }
    }
}
