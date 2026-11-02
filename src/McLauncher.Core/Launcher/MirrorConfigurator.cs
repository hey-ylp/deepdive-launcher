using System.Net;
using System.Net.Http;

namespace McLauncher.Core.Launcher;

/// <summary>
/// 下载源配置：造一个「所有请求都走 BMCLAPI 镜像、自带超时重试」的 HttpClient。
/// </summary>
public static class MirrorConfigurator
{
    /// <summary>BMCLAPI 主站。</summary>
    public const string BmclApiBase = BmclMirrorHandler.BmclApiBase;

    /// <summary>版本清单地址（直接指到 BMCLAPI，省掉官方源的慢查询）。</summary>
    public const string VersionManifestUrl = BmclApiBase + "/mc/game/version_manifest_v2.json";

    /// <summary>官方版本清单地址（备用，用于对比速度）。</summary>
    public const string MojangVersionManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    // ── 并发 ──────────────────────────────────────────────────────────
    // 一次完整安装要拉 4000+ 个小文件（4000 资源 + 1000 JRE），并发数直接决定总耗时。
    // CmlLib 的 CreateAsCoreCount 只给到 CPU 核数（本机 8 上下），对海量小文件太小。
    //
    // ⚠️ 这三个是「运行时可调」的（设置页的下载线程数），所以不能是 readonly ——
    // 改完必须让 MinecraftLauncherService 重建下载器，否则旧的 ParallelGameInstaller 还按老值跑。

    /// <summary>单机并发连接上限。</summary>
    public static int MaxConnectionsPerServer { get; private set; } = EnvInt("MCLAUNCHER_MAX_CONNECTIONS", 256);
    /// <summary>校验（哈希）并发数。</summary>
    public static int MaxCheckers { get; private set; } = EnvInt("MCLAUNCHER_MAX_CHECKERS", 32);
    /// <summary>下载并发数。沙箱代理下过高反而会被 502/滴流，可用环境变量调。</summary>
    public static int MaxDownloaders { get; private set; } = EnvInt("MCLAUNCHER_MAX_DOWNLOADERS", 32);
    /// <summary>下载队列容量。</summary>
    public static int BoundedCapacity { get; private set; } = EnvInt("MCLAUNCHER_BOUNDED_CAPACITY", 256);

    /// <summary>界面上可选的线程档位。再多对家用带宽没意义，反而容易被服务端限流。</summary>
    public static IReadOnlyList<int> ConcurrencyOptions { get; } = new[] { 8, 16, 32, 48, 64 };

    /// <summary>当前档位（就是 <see cref="MaxDownloaders"/>，给界面显示用）。</summary>
    public static int CurrentConcurrency => MaxDownloaders;

    /// <summary>
    /// 改下载并发。
    /// </summary>
    /// <remarks>
    /// 连接池上限和队列容量跟着联动：并发提到 64 而连接池还是 32，多出来的线程全在排队等连接，
    /// 用户会觉得「调了没变化」。
    /// </remarks>
    public static void ApplyConcurrency(int downloaders, int checkers = 0)
    {
        MaxDownloaders = Math.Clamp(downloaders, 1, 128);

        // 校验是纯 CPU 活，跟着下载线程走就行，没必要单独让用户调
        MaxCheckers = checkers > 0 ? Math.Clamp(checkers, 1, 128) : Math.Clamp(MaxDownloaders, 4, 64);

        MaxConnectionsPerServer = Math.Max(256, MaxDownloaders * 2);
        BoundedCapacity = Math.Max(256, MaxDownloaders * 8);
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    // ── 超时与重试 ────────────────────────────────────────────────────

    /// <summary>单次请求超时（秒）。超时后换连接重试。</summary>
    public const int AttemptTimeoutSeconds = 30;

    /// <summary>单文件最大尝试次数。</summary>
    public const int MaxAttempts = 4;

    /// <summary>
    /// 单个请求的总时限兜底。
    /// </summary>
    /// <remarks>
    /// ⚠️ 别改小。曾经定成 60 秒想砍掉「滴流」的请求，结果把正常的慢请求也误杀了
    /// （实测：沙箱代理下单次拉版本清单能超过 60 秒，直接 TaskCanceledException 崩掉整个流程）。
    /// 现在不需要靠它收敛了 —— 收敛交给：
    /// <list type="bullet">
    /// <item><see cref="BmclMirrorHandler"/> 的单次 30 秒超时 + 4 次重试（管「等响应头」阶段）</item>
    /// <item><see cref="MinecraftLauncherService.InstallAsync"/> 的进度看门狗（管「边读边写」的 body 阶段）</item>
    /// </list>
    /// 所以这里放宽成一个大兜底就行。
    /// </remarks>
    public static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 是否<b>优先</b>用 BMCLAPI 镜像。默认否 —— 默认先用官方源。
    /// </summary>
    /// <remarks>
    /// 设环境变量 <c>MCLAUNCHER_USE_MIRROR=1</c> 可改成优先走镜像。
    /// <para>
    /// 为什么默认官方源：2026-10-28 实测（同一台机器、同一份代码）
    /// <list type="bullet">
    /// <item>官方源：971 MB / 476 秒 ≈ <b>2 MB/s</b>，零重试，一次成功</item>
    /// <item>BMCLAPI：当晚整个不可达（连首页都超时），单请求能等 45 秒</item>
    /// </list>
    /// 官方源现在并不是「慢到不可用」，而 BMCLAPI 是单点，挂了就全挂。
    /// 所以把官方源放前面，镜像只作兜底 —— 两个源互为备份，谁挂了另一个顶上。
    /// </para>
    /// </remarks>
    public static bool PreferMirror { get; } =
        string.Equals(Environment.GetEnvironmentVariable("MCLAUNCHER_USE_MIRROR"), "1",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>UA。BMCLAPI / Modrinth 都要求带。</summary>
    public const string UserAgent = "McLauncher/0.1.0 (+.NET8)";

    /// <summary>
    /// 是否把请求交给系统 / 环境变量里的 HTTP 代理。
    /// </summary>
    /// <remarks>
    /// ⚠️ 踩过的坑之一。WorkBuddy 沙箱会注入 <c>http_proxy=http://127.0.0.1:52394</c>
    /// 之类的环境变量做网络出口，.NET 默认会读它、把<b>所有</b>请求都塞给那个本地代理。
    /// 试过 <c>UseProxy = false</c> 想绕开 —— 结果第一个请求（版本清单）就直接失败，
    /// 说明沙箱把出口焊死在代理上，绕不过去。
    /// 好消息是代理并不慢，反而<b>并发越高越快</b>（64 路 ~1.15 MB/s，8 路只有 ~100 KB/s），
    /// 所以并发要放宽。它偶发的 502 / 滴流由 <see cref="BmclMirrorHandler"/> 的重试和
    /// <see cref="MinecraftLauncherService.InstallAsync"/> 的进度看门狗兜住。
    /// 普通桌面环境没有这些环境变量，true 就是 .NET 的默认行为，无副作用。
    /// </remarks>
    public static bool UseSystemProxy => true;

    /// <summary>
    /// 创建 HttpClient 与镜像处理器（需要读重试统计时用返回的 Handler）。
    /// </summary>
    /// <param name="useMirror">true = 走 BMCLAPI；false = 直连官方源（对比用）。</param>
    /// <param name="attemptTimeoutSeconds">单次超时，0 = 用默认 <see cref="AttemptTimeoutSeconds"/>。</param>
    /// <param name="maxAttempts">最大尝试次数，0 = 用默认 <see cref="MaxAttempts"/>。</param>
    /// <remarks>
    /// 后两个参数是给「元数据类请求」用的：拉个几十 KB 的版本清单，
    /// 没必要按默认的 30 秒 × 4 次硬耗 2 分钟才肯回退官方源。
    /// </remarks>
    public static (HttpClient Http, BmclMirrorHandler? Handler) Create(
        bool useMirror = true,
        int attemptTimeoutSeconds = 0,
        int maxAttempts = 0)
    {
        var inner = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = MaxConnectionsPerServer,
            AllowAutoRedirect = true,
            UseProxy = UseSystemProxy,
        };

        // 两个模式都挂这个 handler：
        //  - 镜像模式：Mojang + 加载器 maven 全走 BMCLAPI
        //  - 官方模式：Mojang 直连，但加载器 maven（forge / neoforged / fabric）仍然走镜像
        // 后者的原因见 BmclMirrorHandler 的注释：maven.neoforged.net 在国内被 DNS 污染，
        // 直连等于必挂 —— 装 NeoForge 会永远停在第 42 个文件。
        // 顺带官方模式也获得了「单次超时 + 自动重试」。
        var mirror = new BmclMirrorHandler
        {
            InnerHandler = inner,
            MirrorMojang = useMirror,
            AttemptTimeout = TimeSpan.FromSeconds(
                attemptTimeoutSeconds > 0 ? attemptTimeoutSeconds : AttemptTimeoutSeconds),
            MaxAttempts = maxAttempts > 0 ? maxAttempts : MaxAttempts,
        };

        var http = new HttpClient(mirror)
        {
            Timeout = HttpClientTimeout,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        return (http, mirror);
    }
}
