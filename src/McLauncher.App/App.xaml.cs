using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using McLauncher.App.Diagnostics;
using McLauncher.App.ViewModels;
using McLauncher.Core.Launcher;
using McLauncher.Core.Modrinth;
using McLauncher.Core.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace McLauncher.App;

/// <summary>
/// 应用入口。负责搭依赖注入容器，把主窗口从容器里取出来。
/// </summary>
public partial class App : Application
{
    /// <summary>全局服务容器。</summary>
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// 启动时把临时目录抢过来的作用域。故意<b>整个进程生命周期都不释放</b>。
    /// </summary>
    private static TempDirectoryScope? _tempScope;

    /// <summary>启动环境自检结果，主界面日志区会原样打出来。</summary>
    public static IReadOnlyList<string> StartupDiagnostics { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// 构造函数里就把临时目录换掉 —— 必须早于任何窗口创建。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这台机器上实测过：进程对 <c>C:\Users\Administrator\AppData\Local\Temp</c> 的写入会被拒。
    /// 受影响的远不止下载器和安装器 —— <b>WPF 自己也要往 <c>%TEMP%\WPF</c> 写东西</b>，
    /// 结果连「打开内置文件夹窗口」都直接炸：
    /// </para>
    /// <code>
    /// UnauthorizedAccessException: Access to the path 'C:\Users\Administrator\AppData\Local\Temp\WPF' is denied.
    /// </code>
    /// <para>
    /// 所以不能等安装那一刻才换目录，得在 <see cref="Application.Run"/> 之前换掉，
    /// WPF 之后所有 <c>Path.GetTempPath()</c> 的调用就都落在 <c>实例根\temp</c> 里了。
    /// </para>
    /// </remarks>
    public App()
    {
        StartupDiagnostics = ProbeStartupEnvironment(out var originalTemp);

        // 老版本把设置 / 日志 / 缓存混在 instances 里，现在统一搬到 launcher\。
        // 必须赶在任何东西读写这些文件之前做，所以放在构造函数最前面。
        try
        {
            var migrated = LauncherPaths.MigrateLegacyData();
            if (migrated.Count > 0)
            {
                StartupDiagnostics = StartupDiagnostics
                    .Append($"旧数据已迁到 launcher\\：{string.Join("、", migrated)}")
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics = StartupDiagnostics
                .Append($"[警告] 迁移旧数据失败：{ex.GetType().Name}: {ex.Message}（按新位置继续，不看旧文件）")
                .ToList();
        }

        try
        {
            // 临时目录跟着启动器数据走：launcher\temp，不再往游戏目录里塞
            _tempScope = TempDirectoryScope.Activate(LauncherPaths.DataRoot);
        }
        catch (Exception ex)
        {
            // 连自己目录都建不出来就只能认了，但至少要让用户在日志里看见
            StartupDiagnostics = StartupDiagnostics
                .Append($"[警告] 切换临时目录失败：{ex.GetType().Name}: {ex.Message}（继续用 {originalTemp}）")
                .ToList();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底：UI 线程上任何没被接住的异常，都不要让整个进程静默退出。
        // 之前就吃过这个亏 —— IntegrityWindow 少定义一个资源，XamlParseException
        // 一路逃到 Dispatcher，表现为「点一下按钮程序就没了」，日志里一个字都没有。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var sc = new ServiceCollection();
        ConfigureServices(sc);
        Services = sc.BuildServiceProvider();

        Services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>
    /// UI 线程异常：记日志、弹窗告知，然后标记为已处理 —— 让程序继续活着。
    /// </summary>
    /// <remarks>
    /// 不标记 <c>Handled</c> 的话 WPF 会直接结束进程，用户看到的就是「闪退」，
    /// 而真正的错误信息（异常类型 + 堆栈）根本来不及读。
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        try
        {
            WriteCrashLog("UI 线程", ex);
        }
        catch
        {
            // 写日志本身失败也不能再抛，否则就真没救了
        }

        MessageBox.Show(
            $"程序遇到了一个错误，但已经保住了没退出。\n\n"
            + $"{ex.GetType().Name}: {ex.Message}\n\n"
            + $"详细信息已写入：\n{LauncherPaths.CrashLogFile}",
            "出错了",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    /// <summary>非 UI 线程的未处理异常（后台任务炸了）：只记，没法拦。</summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            try { WriteCrashLog("后台线程", ex); } catch { }
        }
    }

    /// <summary>没人 await 的 Task 异常：标记成已观察，避免继续冒泡。</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { WriteCrashLog("未观察的 Task", e.Exception); } catch { }
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        var path = LauncherPaths.CrashLogFile;
        Directory.CreateDirectory(LauncherPaths.DataRoot);
        File.AppendAllText(path,
            $"""

            ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {source} =====
            {ex}

            """);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.GetService<LoaderInstallerService>()?.Dispose();
        Services?.GetService<ModrinthClient>()?.Dispose();
        Services?.GetService<TranslationService>()?.Dispose();
        Services?.GetService<MinecraftLauncherService>()?.Dispose();
        _tempScope?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 把「谁在跑、临时目录能不能写」这些一次性查清楚写进日志。
    /// </summary>
    /// <remarks>
    /// 加这段是因为之前排查「拒绝访问」全靠猜：Windows 的拒绝访问有太多种来源
    /// （令牌被过滤、ACL 被改、目录被重定向、沙箱），没有现场数据就只能来回试。
    /// 现在开机就把身份、令牌级别、两处临时目录的实测可写性记下来，一眼能定位。
    /// </remarks>
    private static IReadOnlyList<string> ProbeStartupEnvironment(out string originalTemp)
    {
        var lines = new List<string>();
        originalTemp = string.Empty;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);

            lines.Add($"自检 · 账号：{identity.Name}");
            lines.Add($"自检 · 是否管理员角色：{principal.IsInRole(WindowsBuiltInRole.Administrator)}" +
                      $" / {DescribeToken(identity)}");

            // 沙箱里跑的时候，浏览器得用一份临时配置才起得来（写不了自己的 User Data）。
            // 网页本身是能加载的，但那份配置是干净的 —— 没书签、没登录。开机就说清楚，
            // 免得用户对着一个陌生浏览器发懵。
            if (Diagnostics.TokenIntegrity.IsSandboxed)
            {
                lines.Add("自检 · 受限启动：进程跑在低完整性沙箱里 —— 打开网页时会用一份临时浏览器配置" +
                          "（没有书签和登录状态）。想用自己的浏览器，请从资源管理器双击 McLauncher.App.exe。");
            }

            // 令牌里的组能说明「为什么写不进 Temp」：Low 完整性、受限 SID、AppContainer
            // 都会让普通 API 直接拒绝访问，而 ACL 看上去完全正常。所以原样打出来。
            if (identity.Groups is { Count: > 0 } groups)
            {
                var names = new List<string>();
                foreach (var sid in groups)
                {
                    try
                    {
                        names.Add(sid.Translate(typeof(NTAccount))?.Value ?? sid.Value);
                    }
                    catch
                    {
                        names.Add(sid.Value);
                    }
                }

                lines.Add($"自检 · 令牌组（{names.Count}）：{string.Join(" | ", names)}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"[警告] 读取身份失败：{ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            originalTemp = Path.GetTempPath();
            lines.Add($"自检 · 系统临时目录：{originalTemp}");
            lines.Add($"自检 · 系统临时目录可写：{CanWriteTo(originalTemp)}");
        }
        catch (Exception ex)
        {
            lines.Add($"[警告] 读取临时目录失败：{ex.GetType().Name}: {ex.Message}");
        }

        // 名字中英对照自检：词表是本地数据，跑不起来只可能是静态初始化出问题，
        // 提前在日志里落一条，省得用户看到全是英文却查不出原因
        try
        {
            var samples = new[] { ("Sodium", "sodium"), ("Iris Shaders", "iris"), ("Entity Culling", "entityculling") };
            var rendered = samples.Select(s =>
                $"{s.Item1}→{NameLocalizer.ChineseTitle(s.Item1, s.Item2) ?? "(英文)"}");
            lines.Add($"自检 · 名字中文化：{string.Join("；", rendered)}");
        }
        catch (Exception ex)
        {
            lines.Add($"[警告] 名字中文化不可用：{ex.GetType().Name}: {ex.Message}");
        }

        return lines;
    }

    /// <summary>
    /// 把当前进程的访问令牌说成一句人话（完整性级别 + 有没有被受限过滤）。
    /// </summary>
    /// <remarks>
    /// 这里以前打的是 <c>identity.Token</c>，那是内核分配的句柄值：每次启动都不一样
    /// （836 / 848 / 912 …），既不表示权限高低，也没法跨次比较，只会把日志搅浑。
    /// 真正能定位「拒绝访问」的是两件事：
    /// <list type="bullet">
    /// <item>完整性级别 —— UAC 过滤后的管理员是「中等完整性」，ACL 看着全对也写不进 Program Files；</item>
    /// <item>受限令牌（S-1-5-12）—— 沙箱、RunAs 受限模式会额外剥权限，这种最难猜。</item>
    /// </list>
    /// </remarks>
    private static string DescribeToken(WindowsIdentity identity)
    {
        var restricted = false;

        // 受限令牌（S-1-5-12）：沙箱 / RunAs 受限模式会额外剥权限，比完整性级别更难猜
        if (identity.Groups is { Count: > 0 } groups)
        {
            foreach (var sid in groups)
            {
                if (sid.Value == "S-1-5-12")
                {
                    restricted = true;
                    break;
                }
            }
        }

        if (identity.IsSystem) return "SYSTEM 令牌";

        // 完整性级别从令牌里直接问，不从 Groups 翻 —— 那列表里常常没有这个 SID
        var integrity = TokenIntegrity.Describe();
        return restricted ? $"{integrity} · 受限令牌" : integrity;
    }

    /// <summary>真去建一个文件试试 —— ACL 这种事只能实测，不能靠猜。</summary>
    private static string CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $"writecheck_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return "是";
        }
        catch (Exception ex)
        {
            return $"否（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    private static void ConfigureServices(IServiceCollection sc)
    {
        // 下载调优必须在第一个 MinecraftLauncherService 建出来之前落地 ——
        // 并发数是构造下载器时吃进去的，晚了就不生效
        LauncherSettings.ApplyTuning(LauncherPaths.InstanceRoot);

        // 核心服务：B2 阶段一个实例（MinecraftPath 一实例一路径，天然支持多实例隔离）
        // 下载源优先看用户在界面上选过什么（instances\launcher-settings.json），
        // 没选过才用编译期默认（官方源优先）；设 MCLAUNCHER_USE_MIRROR=1 改默认值
        sc.AddSingleton(_ => new MinecraftLauncherService(
            LauncherPaths.InstanceRoot,
            LauncherSettings.ResolveUseMirror(LauncherPaths.InstanceRoot)));

        // 加载器安装：内部持有自己的镜像 HttpClient（NeoForge 的 maven 域名 DNS 被污染，必须走镜像）
        sc.AddSingleton<LoaderInstallerService>();

        // 多实例：构造时会把「上次用的实例」恢复回来，所以它必须在 MainViewModel 之前被建出来
        sc.AddSingleton<InstanceManager>();

        // 卸载版本（连同该版本的模组与存档）
        sc.AddSingleton<VersionRemovalService>();

        // 目录解析：隔离开启时模组/存档在 versions/<版本名>/ 里，路径不能在外面写死
        sc.AddSingleton<GameFolderService>();

        // 文件完整性校验：启动前自动跑快速档，界面上「校验文件」按钮跑深度档（算 sha1）
        sc.AddSingleton<FileIntegrityChecker>();

        // Modrinth 内容搜索：持有自己的 HttpClient（必须带 User-Agent，否则会被 Cloudflare 挡）
        sc.AddSingleton<ModrinthClient>();

        // 内容下载：把模组/光影/资源包下到游戏会读的目录，带 sha1 校验和依赖递归
        sc.AddSingleton<ContentInstaller>();

        // 整合包安装：解 .mrpack → 新建实例 → 装原版+加载器 → 下文件 → 解 overrides
        sc.AddSingleton<ModpackInstaller>();

        // 模组管理：扫描 mods 目录、启用禁用（改后缀）、移除（挪到 .trash）
        sc.AddSingleton<Core.Mods.ModManagerService>();

        // 模组中文名：modId → 中文名。查过就永久缓存，断网只用缓存。
        sc.AddSingleton<Core.Mods.ModChineseNameService>();

        // 微软登录：设备码流程 + Xbox Live → XSTS → Minecraft 四段跳。
        // 单例是因为它内部持有一个 HttpClient，重建会白开一堆连接池。
        sc.AddSingleton<Core.Auth.MicrosoftAuthService>();

        // 从官方启动器导入令牌。个人开发者拿不到 Mojang 的应用批准，
        // 这条是唯一能进正版服务器的路（读本机明文文件里的 Java 令牌）。
        sc.AddSingleton<Core.Auth.OfficialLauncherTokenService>();

        // 简介翻译：走 MyMemory 免费接口，译文缓存在 launcher\ 下（所有实例共用一份）
        sc.AddSingleton(_ => new TranslationService(LauncherPaths.DataRoot));

        // EasyTier 联机：core/cli/wintun 放在 exe 同目录的 easytier\ 下
        sc.AddSingleton(_ => new EasyTierService(
            Path.Combine(LauncherPaths.AppDirectory, "easytier")));

        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();
    }
}
