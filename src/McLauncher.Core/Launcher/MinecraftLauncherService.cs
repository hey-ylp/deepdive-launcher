using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;

namespace McLauncher.Core.Launcher;

/// <summary>
/// CmlLib.Core 的封装。对外只暴露「拉版本 / 下载 / 启动」三件事 + 进度事件。
/// </summary>
/// <remarks>
/// <para>
/// CmlLib 4.1.0 的 <see cref="MinecraftLauncherParameters"/> 有三个注入点，我们全部用上：
/// HttpClient（走镜像 + 重试）、VersionLoader（版本清单指到下载源）、GameInstaller（放宽并发）。
/// 关键是同一个 HttpClient 贯穿全流程，URL 重写才能一处生效。
/// </para>
/// <para>
/// <b>双源自动回退</b>：默认走 BMCLAPI，若清单/下载连续失败则自动切到官方源，
/// 并把后续所有操作都留在官方源上（通过 <see cref="SourceChanged"/> 通知 UI）。
/// 起因是 2026-10-28 实测 BMCLAPI 会整个不可达，而官方源同时是通的 —— 不能把鸡蛋放一个篮子里。
/// </para>
/// </remarks>
public sealed class MinecraftLauncherService : IDisposable
{
    /// <summary>最大续传遍数。</summary>
    /// <remarks>
    /// 20 遍太夸张：每遍至少 45 秒，用户能等十几分钟还看不到尽头。
    /// 真正该做的不是"多试几次"，而是把失败的源换掉（那是 <see cref="SwitchSource"/> 的事）。
    /// </remarks>
    public const int MaxInstallPasses = 6;

    /// <summary>
    /// 连续多少次「卡到连取消都不理」就彻底放弃。
    /// </summary>
    /// <remarks>
    /// 被甩掉的那一遍任务还在后台挂着（它就卡在内核里），重来只会在同一处再卡一遍。
    /// 实测用户撞上过连甩六遍的场景 —— 除了耗时间，什么也没解决。
    /// </remarks>
    public const int MaxConsecutiveEscapes = 3;

    /// <summary>
    /// 多少秒没有任何进展就算「卡住」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原来是 45 秒，实测太肉：连不上的时候（2026-10-29 整合包那次，官方源整段下不动）
    /// 要连着卡三遍才肯换源，用户干等两分多钟，看起来就是「卡死了」。
    /// </para>
    /// <para>
    /// ⚠️ 但也别改回 15 秒。踩过第二次：安装的最后阶段（解压 JRE、校验 4000+ 文件、
    /// 写版本 JSON）是纯本地 IO，<b>不产生任何下载进度事件</b>。15 秒一到就被当成卡死掐掉，
    /// 于是每一遍都走到最后再被掐死，无限循环 —— 日志表现是「卡在 (未知)」+ 每 15 秒掐一次。
    /// </para>
    /// <para>
    /// 现在 45 秒：真正的死连接照样能发现，「慢」交给慢速换源（18 秒低于 80 KB/s 就换）。
    /// 两者分工明确 —— 慢速换源负责速度，这里只负责彻底不动。
    /// </para>
    /// </remarks>
    public const int StallSeconds = 45;

    /// <summary>拉版本清单的最大尝试次数。</summary>
    public const int ManifestMaxAttempts = 3;

    /// <summary>
    /// 拉清单的单次硬上限（秒）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 必须有这个。清单请求是<b>允许快速失败</b>的 —— 拿不到就换源重来，
    /// 没必要在这一个请求上耗。不加的话会变成「外层重试 3 次 × 内层重试 4 次 × 每次 30 秒」，
    /// 最坏磨 6 分钟才肯切源（实测踩过）。
    /// </remarks>
    public const int ManifestAttemptTimeoutSeconds = 20;

    /// <summary>下载连续失败这么多遍就换源。别设大 —— 每一遍都要等看门狗超时，很费时间。</summary>
    public const int FallbackAfterFailedPasses = 3;

    /// <summary>
    /// 速度低于这个值（字节/秒）就算「太慢」，考虑换源。
    /// </summary>
    /// <remarks>
    /// 80 KB/s 取自实测：官方源直连 2 MB/s、镜像正常时几百 KB/s，
    /// 而缩到几十 KB/s 时基本是某个节点在滴流，换一个源往往立刻恢复。
    /// 定太高会把带宽本来就小的用户一直踢来踢去，反而更慢。
    /// </remarks>
    public const int SlowBytesPerSecond = 80 * 1024;

    /// <summary>连续多少个采样周期（3 秒一次）都太慢才换源。</summary>
    public const int SlowTicksToSwitch = 6;

    /// <summary>这一遍至少跑这么久才开始判慢速 —— 刚起步的数字是连接握手，不作数。</summary>
    public const int SlowGraceSeconds = 20;

    private string _instanceRoot;
    private readonly List<SourceBundle> _bundles = new();
    private bool _activeUseMirror;
    private bool _sourceSwitched;
    private bool _concurrencyStale;
    private bool _disposed;

    /// <summary>文件级下载进度。</summary>
    public event EventHandler<InstallerProgressChangedEventArgs>? FileProgressChanged;

    /// <summary>字节级进度。</summary>
    public event EventHandler<ByteProgress>? ByteProgressChanged;

    /// <summary>人可读状态文本。</summary>
    public event EventHandler<string>? Log;

    /// <summary>下载源发生切换（参数是新源的显示名）。</summary>
    public event EventHandler<string>? SourceChanged;

    /// <summary>当前实例的路径集合（切实例时整个换掉 —— 多实例隔离的基础）。</summary>
    public MinecraftPath GamePath { get; private set; }

    /// <summary>
    /// <c>libraries</c> / <c>assets</c> / <c>runtime</c> 所在的目录。
    /// </summary>
    /// <remarks>
    /// 多实例默认<b>共用</b>这几个大目录，所以它未必等于 <see cref="InstanceRoot"/>：
    /// 新实例的库全在「共用库宿主」那边，只有版本、模组、存档在自己目录里。
    /// </remarks>
    public string LibraryRoot { get; private set; }

    /// <summary>切换了实例（参数是新的实例根目录）。</summary>
    public event EventHandler<string>? InstanceChanged;

    /// <summary>
    /// 是否让每个版本拥有独立的游戏目录（mods / saves / logs / options.txt 各版本一套）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 开启时 <c>--gameDir</c> 指向 <c>versions/&lt;版本名&gt;/</c>，而 <c>libraries</c>、
    /// <c>assets</c>、<c>runtime</c> 仍然共享。效果：装了两个同为 1.20.1 的版本
    /// （比如一个原版加模组、一个 Fabric 加模组），模组和存档完全互不影响；
    /// 卸载某个版本时，它的模组和存档跟着版本目录一起走。
    /// </para>
    /// <para>
    /// 不开就是所有版本共用一个 <c>mods</c> 和 <c>saves</c> —— 装了不兼容的模组会互相打架。
    /// </para>
    /// </remarks>
    public bool IsolateVersionGameDir { get; set; } = true;

    /// <summary>实例根目录。</summary>
    public string InstanceRoot => _instanceRoot;

    /// <summary>当前是否在用镜像（可能在运行中因回退变成 false）。</summary>
    public bool IsUsingMirror => _activeUseMirror;

    /// <summary>
    /// 当前下载源对应的 <see cref="MinecraftLauncher"/>，供同程序集的加载器安装器复用。
    /// </summary>
    /// <remarks>
    /// 走属性而不是字段，是为了拿到<b>换源之后</b>的那个实例 —— 加载器安装器如果缓存了旧的
    /// Launcher，一旦中途回退官方源，它还会往已经废掉的镜像连接池上打。
    /// </remarks>
    internal MinecraftLauncher Launcher => Current.Launcher;

    /// <summary>当前下载源的 HttpClient（镜像前缀重写规则已挂在它的 handler 上）。</summary>
    internal HttpClient Http => Current.Http;


    /// <summary>当前下载源的显示名。</summary>
    public string SourceName => _activeUseMirror
        ? "BMCLAPI 镜像"
        : "官方源（Mojang 直连）";

    /// <summary>URL 重写次数（仅镜像源有意义）。</summary>
    public int RewriteCount => Current.Mirror?.RewriteCount ?? 0;

    /// <summary>重试次数。</summary>
    public int RetryCount => Current.Mirror?.RetryCount ?? 0;

    /// <summary>因单次超时而重试的次数。</summary>
    public int TimeoutCount => Current.Mirror?.TimeoutCount ?? 0;

    public MinecraftLauncherService(string instanceRoot, bool useMirror = true)
    {
        _instanceRoot = Path.GetFullPath(instanceRoot);
        LibraryRoot = _instanceRoot;
        _activeUseMirror = useMirror;

        RebuildGamePath();

        // 先把当前源建起来（懒建另一个，避免白开一条连接池）
        _ = Current;
    }

    /// <summary>
    /// 切换到另一个实例：重建路径集合，并丢掉已经建好的下载器。
    /// </summary>
    /// <param name="instanceRoot">新实例目录（<c>versions</c>、模组、存档在这）。</param>
    /// <param name="libraryRoot"><c>libraries</c> / <c>assets</c> / <c>runtime</c> 所在目录，一般是共用库宿主。</param>
    /// <remarks>
    /// <para>
    /// <b>必须清 <see cref="_bundles"/>。</b>每个 <c>MinecraftLauncher</c> 在构造时就把
    /// <see cref="MinecraftPath"/> 捕获进 <c>MinecraftLauncherParameters</c> 了，留着旧的
    /// 等于下载器还在往上一个实例里装文件 —— 换实例后装加载器，文件会落错地方，而且不报错。
    /// </para>
    /// <para>
    /// 顺带把「本运行内不再回切」的标记复位：换了实例就是新的一批下载，
    /// 不该继承上一个实例的换源决定。
    /// </para>
    /// </remarks>
    public void SwitchInstance(string instanceRoot, string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        var newRoot = Path.GetFullPath(instanceRoot);
        var newLibrary = Path.GetFullPath(libraryRoot);

        if (string.Equals(newRoot, _instanceRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(newLibrary, LibraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _instanceRoot = newRoot;
        LibraryRoot = newLibrary;

        RebuildGamePath();

        // 旧连接池也要放掉，否则每切实例漏一套连接
        foreach (var b in _bundles) b.Http.Dispose();
        _bundles.Clear();
        _sourceSwitched = false;
        _concurrencyStale = false;
        _ = Current;

        InstanceChanged?.Invoke(this, _instanceRoot);
    }

    /// <summary>
    /// 按当前实例根 + 库根重建 <see cref="GamePath"/>。
    /// </summary>
    /// <remarks>
    /// 两者相同时就是最朴素的 <c>new MinecraftPath(实例根)</c>（保持老行为）；
    /// 不同时让 <c>BasePath</c>/<c>Versions</c> 指向实例自己，其余指向库根。
    /// </remarks>
    [MemberNotNull(nameof(GamePath))]
    private void RebuildGamePath()
    {
        GamePath = string.Equals(_instanceRoot, LibraryRoot, StringComparison.OrdinalIgnoreCase)
            ? new MinecraftPath(_instanceRoot)
            : new MinecraftPath(LibraryRoot)
            {
                BasePath = _instanceRoot,
                Versions = Path.Combine(_instanceRoot, "versions"),
            };

        GamePath.CreateDirs();
    }

    // ───────────────────────── 下载源管理 ─────────────────────────

    private SourceBundle Current
    {
        get
        {
            foreach (var b in _bundles)
                if (b.UseMirror == _activeUseMirror) return b;
            var created = BuildSource(_activeUseMirror);
            _bundles.Add(created);
            return created;
        }
    }

    private SourceBundle BuildSource(bool useMirror)
    {
        var (http, mirror) = MirrorConfigurator.Create(useMirror);

        var parameters = MinecraftLauncherParameters.CreateDefault(GamePath, http);
        parameters.VersionLoader = new MojangJsonVersionLoaderV2(
            GamePath,
            http,
            useMirror ? MirrorConfigurator.VersionManifestUrl : MirrorConfigurator.MojangVersionManifestUrl)
        {
            UseLocalManifestWhenError = true,
        };
        parameters.GameInstaller = new ParallelGameInstaller(
            maxChecker: MirrorConfigurator.MaxCheckers,
            maxDownloader: MirrorConfigurator.MaxDownloaders,
            boundedCapacity: MirrorConfigurator.BoundedCapacity,
            httpClient: http);

        var launcher = new MinecraftLauncher(parameters);
        launcher.FileProgressChanged += (_, e) => FileProgressChanged?.Invoke(this, e);
        launcher.ByteProgressChanged += (_, e) => ByteProgressChanged?.Invoke(this, e);

        Log?.Invoke(this, useMirror
            ? $"使用 BMCLAPI 镜像：{MirrorConfigurator.VersionManifestUrl}"
            : $"使用官方源：{MirrorConfigurator.MojangVersionManifestUrl}");

        return new SourceBundle { UseMirror = useMirror, Http = http, Mirror = mirror, Launcher = launcher };
    }

    /// <summary>
    /// 手动指定下载源（界面上的「官方 / 镜像」切换）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 和 <see cref="SwitchSource"/> 的区别：那个是下载出问题时自动回退、整个运行期只切一次；
    /// 这个是用户自己点的，随时可以来回切，所以要把「不再回切」的标记复位 ——
    /// 手动选了镜像之后如果镜像又挂了，仍然应该有权自动退回官方源。
    /// </para>
    /// <para>
    /// 已经建好的两条连接池留在 <see cref="_bundles"/> 里复用，所以来回切不会反复握手。
    /// 正在跑的下载不会被打断（它握着旧的 Launcher），下一次下载才走新源。
    /// </para>
    /// </remarks>
    /// <returns>是否真的变了。</returns>
    public bool SetSource(bool useMirror, string? reason = null)
    {
        if (_activeUseMirror == useMirror) return false;

        _activeUseMirror = useMirror;
        _sourceSwitched = false;

        Log?.Invoke(this, reason is null
            ? $"下载源改为「{SourceName}」（手动切换）"
            : $"下载源改为「{SourceName}」（{reason}）");
        SourceChanged?.Invoke(this, SourceName);

        return true;
    }

    /// <summary>
    /// 改下载并发（界面上的「下载线程」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这里绝对不重建、不 Dispose 任何东西。</b>踩过：一开始当场重建下载器，
    /// 结果用户在下 NeoForge 那 46 个文件的中途拨了一下线程数 —— 正在用的连接池被关掉，
    /// 请求全挂在那儿既不成功也不失败，进度条就永远停在 16.7 / 18.2 MB。
    /// </para>
    /// <para>
    /// 现在只改数值并打一个「过期」标记，等下一次 <see cref="InstallAsync"/> 真正开始、
    /// 确认没有在跑的下载时，再 <see cref="RefreshSourceIfStale"/> 掉旧连接池。
    /// 正在下载的那一批完全不受影响，用的还是老并发。
    /// </para>
    /// </remarks>
    /// <returns>是否真的变了。</returns>
    public bool SetDownloadConcurrency(int downloaders)
    {
        if (downloaders == MirrorConfigurator.MaxDownloaders) return false;

        MirrorConfigurator.ApplyConcurrency(downloaders);
        _concurrencyStale = true;

        Log?.Invoke(this, $"下载线程数改为 {MirrorConfigurator.MaxDownloaders}"
                          + $"（校验 {MirrorConfigurator.MaxCheckers} 线程），下一次下载生效");
        return true;
    }

    /// <summary>
    /// 删掉 0 字节的库文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 下载被中断（进程被杀、连接挂死后被甩掉）会留下 0 字节的 <c>.jar</c>，
    /// 而 CmlLib 判断「要不要下载」看的是文件是否存在 —— 它不会去读内容。
    /// 结果是这个坏文件永远留着，启动时 Java 抛：
    /// </para>
    /// <code>
    /// java.util.zip.ZipException: zip file is empty
    ///   ... error reading ...\libraries\net\fabricmc\intermediary\1.20.1\intermediary-1.20.1.jar
    /// </code>
    /// <para>
    /// 用户看到这段根本不知道要删哪个文件。所以在每次安装前先扫一遍，删掉重下。
    /// 只扫 <c>.jar</c> 且只删长度为 0 的，不会误伤。
    /// </para>
    /// </remarks>
    private void PurgeEmptyLibraries()
    {
        try
        {
            var libraryRoot = GamePath.Library;
            if (!Directory.Exists(libraryRoot)) return;

            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(libraryRoot, "*.jar", SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(file).Length != 0) continue;
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 单个文件删不掉（被占用之类）就先放着，不要因为一个文件中断整个安装
                }
            }

            if (removed > 0)
                Log?.Invoke(this, $"清掉 {removed} 个上次中断留下的空库文件（0 字节），会重新下载");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"清理空库文件时出错（不影响继续下载）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 下载器还在用老并发时，重建一次。只能在<b>没有下载正在进行</b>的时候调。
    /// </summary>
    private void RefreshSourceIfStale()
    {
        if (!_concurrencyStale) return;
        _concurrencyStale = false;

        foreach (var b in _bundles) b.Http.Dispose();
        _bundles.Clear();

        _ = Current;

        Log?.Invoke(this, $"下载器已按 {MirrorConfigurator.MaxDownloaders} 线程重建");
    }

    /// <summary>切到另一个下载源（单次下载任务内只切一次，避免来回横跳）。</summary>
    private bool SwitchSource(string reason)
    {
        if (_sourceSwitched) return false;
        _sourceSwitched = true;
        _activeUseMirror = !_activeUseMirror;

        Log?.Invoke(this, $"⚠ {reason}");
        Log?.Invoke(this, _activeUseMirror
            ? "→ 自动切到「BMCLAPI 镜像」继续（这次下载内不再自动换源；还是慢的话在顶部手动切回去）"
            : "→ 自动切到「官方源」继续（这次下载内不再自动换源；还是慢的话在顶部手动切回去）");
        SourceChanged?.Invoke(this, SourceName);
        return true;
    }

    // ───────────────────────── 对外功能 ─────────────────────────

    /// <summary>拉取全部版本，按发布时间倒序。失败会重试，镜像连续失败会自动回退官方源。</summary>
    public async Task<IReadOnlyList<IVersionMetadata>> GetVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        VersionMetadataCollection? collection = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= ManifestMaxAttempts && collection is null; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(ManifestAttemptTimeoutSeconds));

            try
            {
                collection = await Current.Launcher
                    .GetAllVersionsAsync(attemptCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var reason = ex is OperationCanceledException
                    ? $"超过 {ManifestAttemptTimeoutSeconds} 秒没响应"
                    : Short(ex.Message);

                lastError = ex;
                Log?.Invoke(this, $"拉版本清单第 {attempt}/{ManifestMaxAttempts} 次失败（{reason}）");

                if (attempt == ManifestMaxAttempts)
                {
                    if (!SwitchSource($"下载源拉版本清单连续失败：{reason}"))
                        break;
                    attempt = 0;   // 换源后重新计数
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (collection is null)
            throw new IOException($"拉取版本清单失败：{Short(lastError?.Message)}", lastError);

        // 手动迭代 + 排序：不假设 VersionMetadataCollection 的 LINQ 支持
        var list = new List<IVersionMetadata>();
        foreach (var version in collection)
            list.Add(version);
        list.Sort((a, b) => b.ReleaseTime.CompareTo(a.ReleaseTime));

        Log?.Invoke(this, $"拿到 {list.Count} 个版本（最新正式版：{collection.LatestReleaseName}）");
        return list;
    }

    /// <summary>只下载，不启动。内部跑「进度看门狗 + 续传 + 自动换源」。</summary>
    /// <remarks>
    /// 为什么要这套东西：真实网络下会有卡住的请求。大文件冷缓存时不会直接失败，而是以极低速度
    /// 「滴流」—— 总时限永远触发不了，但一个文件就能拖几十分钟，把整个并发池拖死。
    /// 对策是看<b>字节进度有没有推进</b>而不是看总耗时：连续 <see cref="StallSeconds"/> 秒
    /// 一个字节都没动就掐掉这一遍换一遍（InstallAsync 可跳过已下好的文件，所以能续传）。
    /// 如果换了源还是不行，就再切官方源。
    /// </remarks>
    public async Task InstallAsync(string versionName, CancellationToken cancellationToken = default)
    {
        // 这里没有下载在跑，可以安全地把「改过线程数但还没生效」的下载器换掉
        RefreshSourceIfStale();

        // 上次被掐断留下的空 jar：CmlLib 只看「文件在不在」，会直接跳过，
        // 于是启动时 Java 报 `ZipException: zip file is empty`，用户完全不知道该删哪个文件。
        PurgeEmptyLibraries();

        // 「本次运行只自动换源一次」是按<b>一次下载任务</b>算的，不是按进程生命周期算。
        // 不复位的话：第一次下载卡住 → 自动切到另一个源 → 还是失败 → 之后无论重试多少次
        // 都再也不会换源了，用户看到的就是「再下没用了」。
        _sourceSwitched = false;

        Log?.Invoke(this, $"开始下载 {versionName} …");
        var totalClock = Stopwatch.StartNew();
        Exception? lastError = null;
        var pass = 0;
        var consecutiveEscapes = 0;

        while (pass < MaxInstallPasses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pass++;

            using var passCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // —— 进度看门狗 ——
            var lastBytes = -1L;
            var lastMovedAt = DateTime.UtcNow;
            var stalled = false;
            var tooSlow = false;

            // 卡住时报出「最后在下的那个文件」—— 光说"卡住了"没法查，
            // 但看到是 maven.neoforged.net 上的包，立刻就知道是加载器库的域名问题
            var lastFile = "(未知)";

            void OnByteProgress(object? _, ByteProgress progress)
            {
                if (progress.ProgressedBytes != lastBytes)
                {
                    lastBytes = progress.ProgressedBytes;
                    lastMovedAt = DateTime.UtcNow;
                }
            }

            void OnFileProgress(object? _, InstallerProgressChangedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Name))
                    lastFile = $"[{e.ProgressedTasks}/{e.TotalTasks}] {e.Name}";

                // 一个文件下完接着下一个，这也算「活着」——
                // 否则校验几千个文件（字节不动、文件数在涨）会被当成卡死误杀
                lastMovedAt = DateTime.UtcNow;
            }

            var launcher = Current.Launcher;
            launcher.ByteProgressChanged += OnByteProgress;
            launcher.FileProgressChanged += OnFileProgress;

            // 慢速判定用的采样：每 3 秒算一次这一轮的瞬时速度
            var sampleBytes = lastBytes < 0 ? 0 : lastBytes;
            var sampleAt = DateTime.UtcNow;
            var slowTicks = 0;
            var passStartedAt = DateTime.UtcNow;

            var watchdog = Task.Run(async () =>
            {
                try
                {
                    while (!passCts.IsCancellationRequested)
                    {
                        await Task.Delay(3000, passCts.Token).ConfigureAwait(false);

                        // ① 完全不动 → 卡死，立刻掐
                        if ((DateTime.UtcNow - lastMovedAt).TotalSeconds >= StallSeconds)
                        {
                            stalled = true;
                            Log?.Invoke(this,
                                $"⚠ 已 {StallSeconds} 秒无推进，卡在 {lastFile}；掐掉第 {pass} 遍、换一遍重来");
                            passCts.Cancel();
                            return;
                        }

                        // ② 在动，但慢得离谱 → 换源重来（PCL 就是这个行为：
                        //    它不等你彻底卡死，只要发现比另一个源慢太多就换）
                        var now = DateTime.UtcNow;
                        var elapsed = (now - sampleAt).TotalSeconds;
                        if (elapsed <= 0) continue;

                        var current = lastBytes < 0 ? 0 : lastBytes;
                        var speed = SpeedBytesPerSecond(sampleBytes, current, elapsed);
                        sampleBytes = current;
                        sampleAt = now;

                        // 刚起步那 20 秒的瞬时速度没参考价值（还在拉清单、建连接）
                        if ((now - passStartedAt).TotalSeconds < SlowGraceSeconds)
                            continue;

                        if (speed < SlowBytesPerSecond)
                            slowTicks++;
                        else
                            slowTicks = 0;

                        if (slowTicks >= SlowTicksToSwitch)
                        {
                            tooSlow = true;
                            Log?.Invoke(this,
                                $"⚠ 连续 {SlowTicksToSwitch * 3} 秒只有 {speed / 1024:F0} KB/s（低于 "
                                + $"{SlowBytesPerSecond / 1024} KB/s），卡在 {lastFile}；换源重来");
                            passCts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { /* 正常退出 */ }
            }, CancellationToken.None);

            var passClock = Stopwatch.StartNew();
            try
            {
                // 看门狗负责「取消」，但 CmlLib 里有些请求卡在内核里时不会理取消令牌，
                // 那个 await 就永远不返回 —— 界面表现就是进度条定格、日志停在最后一行。
                // 所以给它一个「取消后再等 20 秒」的逃生口：还不结束就甩掉这一遍，外面重来。
                // CmlLib 这里返回 ValueTask，转成 Task 才能和逃生口比先后
                var installTask = launcher.InstallAsync(versionName, passCts.Token).AsTask();
                var escape = EscapeAfterCancelAsync(passCts.Token, TimeSpan.FromSeconds(20));

                if (await Task.WhenAny(installTask, escape).ConfigureAwait(false) == escape)
                {
                    // 把未观察的异常吃掉，避免 UnobservedTaskException
                    _ = installTask.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);

                    // 被甩掉的那一遍其实还在后台挂着（它就卡在内核里），继续重来只会在
                    // 同一个地方再卡一次 —— 实测能连甩六遍，用户等的就是这个死循环。
                    // 连续挂死就到头了：换源也没用，因为挂住的连接根本不理取消。
                    if (++consecutiveEscapes >= MaxConsecutiveEscapes)
                    {
                        // 必须是"不重试"的异常类型：IOException 会被下面的 IsTransient 当可重试吞掉，
                        // 那就又转回死循环了
                        throw new InstallAbortedException(
                            $"连续 {MaxConsecutiveEscapes} 次卡死到连取消都没反应，{versionName} 这次放弃下载。"
                            + "多半是某个源在滴流：把下载源换成另一个、线程数降到 16，再点一次下载");
                    }

                    throw new TimeoutException(
                        $"取消后 20 秒仍未结束（连接挂死），甩掉这一遍重来");
                }

                // 能正常结束就说明连接是活的，挂死计数清零
                consecutiveEscapes = 0;

                await installTask.ConfigureAwait(false);

                passClock.Stop();
                totalClock.Stop();

                Log?.Invoke(this, $"{versionName} 下载完成：共 {pass} 遍，总耗时 {totalClock.Elapsed.TotalSeconds:F0} 秒");
                Log?.Invoke(this, $"统计：URL 重写 {RewriteCount} 次 / 重试 {RetryCount} 次 / 其中超时重试 {TimeoutCount} 次");
                return;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastError = ex;
                Log?.Invoke(this, $"第 {pass} 遍失败（{ex.GetType().Name}: {Short(ex.Message)}）");
            }
            finally
            {
                launcher.ByteProgressChanged -= OnByteProgress;
                launcher.FileProgressChanged -= OnFileProgress;
                await passCts.CancelAsync().ConfigureAwait(false);
                try { await watchdog.ConfigureAwait(false); } catch { /* ignore */ }
            }

            // 卡住（一个字节都不动）说明这个源不通，<b>立刻换源</b>，不要攒三遍。
            // 攒三遍的代价是 3 × 15 秒的纯等待，而换源只需要一次判断。
            if (stalled && SwitchSource($"下载卡住，连续 {StallSeconds} 秒无推进"))
            {
                pass = 0;
                continue;
            }

            // 慢得离谱（在动，但比另一个源差太多）也换 —— 否则用户就得守着 100 KB/s 等一小时
            if (tooSlow && SwitchSource(
                    $"下载太慢（持续低于 {SlowBytesPerSecond / 1024} KB/s）"))
            {
                pass = 0;
                continue;
            }

            // 连续失败够多次就换源再来
            if (pass >= FallbackAfterFailedPasses && SwitchSource(
                    $"下载已连续失败 {pass} 遍（最后错误：{Short(lastError?.Message)}）"))
            {
                pass = 0;
            }
        }

        throw new IOException(
            $"续传 {MaxInstallPasses} 遍仍未下载完 {versionName}，最后一次错误：{Short(lastError?.Message)}。"
            + "建议：在顶部把下载源换成另一个、把下载线程数降到 16，再点一次下载（已下好的文件不会重下）",
            lastError);
    }

    /// <summary>
    /// 启动游戏进程，<b>同时把它的 stdout / stderr 接进日志</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须接</b>（2026-10-31 血泪）：JVM 参数冲突时，Java 在启动的第一毫秒就往
    /// stderr 打一行 <c>Multiple garbage collectors selected</c> 然后退出，退出码 1。
    /// 不重定向的话这行字直接掉进虚空 —— 启动器只能看到「进程异常退出」，
    /// 而 <c>logs/latest.log</c> 还是上一次的旧文件，崩溃分析于是给出完全无关的结论。
    /// </para>
    /// <para>
    /// stdout 只转发前 <see cref="MaxStdoutLines"/> 行：游戏自己会写 <c>latest.log</c>，
    /// 全量镜像一遍既刷屏又占磁盘。stderr 全量留 —— 那都是真正出事的证据。
    /// </para>
    /// </remarks>
    public void StartGameProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var info = process.StartInfo;
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;

        if (!process.Start())
            throw new InvalidOperationException("游戏进程没能启动（Process.Start 返回 false）");

        lock (_outputLock)
        {
            _stderr.Clear();
            _stdoutLines = 0;
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            bool forward;
            lock (_outputLock)
            {
                _stdoutLines++;
                forward = _stdoutLines <= MaxStdoutLines;
            }

            // 只在启动初期转发，之后交给游戏自己的日志
            if (forward) Log?.Invoke(this, "[游戏] " + e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            lock (_outputLock)
            {
                if (_stderr.Count < MaxStderrLines) _stderr.Add(e.Data);
            }

            Log?.Invoke(this, "[游戏错误] " + e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// 上一个游戏进程往 stderr 写的前若干行。
    /// </summary>
    /// <remarks>
    /// JVM 拒绝启动时，真正的原因只在这里。界面上要能把它原样摆给用户看。
    /// </remarks>
    public IReadOnlyList<string> LastGameStandardError
    {
        get
        {
            lock (_outputLock) return _stderr.ToList();
        }
    }

    private readonly object _outputLock = new();
    private readonly List<string> _stderr = new();
    private int _stdoutLines;

    private const int MaxStdoutLines = 120;
    private const int MaxStderrLines = 200;

    /// <summary>下载（若需要）+ 构建进程 + 启动游戏。</summary>
    /// <remarks>
    /// <c>minimumRamMb</c> 就是 <c>-Xms</c>，<b>默认 0 是有意的（不写这个参数）</b>，
    /// 和 PCL 保持一致 —— PCL 源码里同样只拼 <c>-Xmx</c>，搜不到 <c>-Xms</c>。
    /// <para>
    /// 不写的效果：JVM 按物理内存的 1/64 起步，随用随扩。硬设一个大值只是让启动瞬间
    /// 先提交一批物理页，对 MC 这种「开局猛吃、随后平稳」的负载没有实际收益，
    /// 低内存机器上还会把物理页白白占住。真需要（排障、复现某个环境）再显式传。
    /// </para>
    /// </remarks>
    public async Task<Process> LaunchAsync(
        string versionName,
        string username,
        int maximumRamMb,
        int minimumRamMb = 0,
        CancellationToken cancellationToken = default)
    {
        var process = await BuildLaunchProcessAsync(
            versionName, username, maximumRamMb, minimumRamMb, cancellationToken).ConfigureAwait(false);

        Log?.Invoke(this, "启动游戏进程");
        StartGameProcess(process);
        return process;
    }

    /// <summary>
    /// 只下载，不启动。给界面上独立的「下载」按钮用。
    /// </summary>
    /// <remarks>
    /// 已经下过也能点 —— 等于「校验并补齐」，少的补、坏的覆盖，不会重下已有的文件。
    /// </remarks>
    public async Task InstallOnlyAsync(string versionName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);

        using var tempScope = TempDirectoryScope.Activate(LauncherPaths.DataRoot);

        Log?.Invoke(this, $"=== 只下载 {versionName}（不启动） ===");
        await InstallAsync(versionName, cancellationToken).ConfigureAwait(false);
        Log?.Invoke(this, $"{versionName} 文件已齐，随时可以点「启动」");
    }

    /// <summary>这个版本在本地装好了没（有 <c>versions/&lt;名字&gt;</c> 目录就算）。</summary>
    public bool IsInstalled(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName)) return false;

        return GetInstalledVersions().Contains(versionName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 下载（若需要）+ 构建启动进程，但<b>不启动</b>。
    /// </summary>
    /// <remarks>
    /// 拆出来是为了能验证「这个加载器版本到底能不能拼出合法启动命令」——
    /// 装完加载器只说明文件齐了，参数拼错（缺主类、缺库、Java 版本不匹配）
    /// 到 <c>BuildProcess</c> 这一步才会暴露。测试不需要真开一个游戏窗口。
    /// </remarks>
    public async Task<Process> BuildLaunchProcessAsync(
        string versionName,
        string username,
        int maximumRamMb,
        int minimumRamMb = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        ValidateMemory(maximumRamMb, minimumRamMb);

        // 下载/解压 natives 时 CmlLib 会用到系统临时目录，那里不一定可写，统一挪到实例自己的 temp。
        using var tempScope = TempDirectoryScope.Activate(LauncherPaths.DataRoot);

        // 先下载（走续传重试循环），再单独构建进程 —— 不用 InstallAndBuildProcessAsync，
        // 因为它一超时就整体抛异常、没有续传机会。
        await InstallAsync(versionName, cancellationToken).ConfigureAwait(false);

        return await BuildProcessCoreAsync(
            versionName, username, maximumRamMb, minimumRamMb, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <b>只构建启动进程，不下载。</b>给界面上独立的「启动」按钮用。
    /// </summary>
    /// <exception cref="InvalidOperationException">版本还没下载到本地。</exception>
    public async Task<Process> BuildProcessOnlyAsync(
        string versionName,
        string username,
        int maximumRamMb,
        int minimumRamMb = 0,
        CancellationToken cancellationToken = default,
        Auth.MinecraftAccount? account = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);
        ValidateMemory(maximumRamMb, minimumRamMb);

        if (!IsInstalled(versionName))
        {
            throw new InvalidOperationException(
                $"版本「{versionName}」还没有下载到本地，先点「下载」把文件补齐。");
        }

        using var tempScope = TempDirectoryScope.Activate(LauncherPaths.DataRoot);

        return await BuildProcessCoreAsync(
            versionName, username, maximumRamMb, minimumRamMb, cancellationToken, account).ConfigureAwait(false);
    }

    /// <summary>
    /// 按账号类型造会话。正版带令牌，离线只有名字。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>正版令牌过期时直接抛，不偷偷降级成离线。</b>降级的后果很隐蔽：
    /// 游戏能起来、单机也能玩，但一进正版服务器就被踢「未验证」，而用户完全想不到
    /// 是启动器把账号悄悄换掉了。宁可在这里拦住，提示重新登录。
    /// </para>
    /// <para>
    /// <b>离线账号的 UUID 必须自己算，不能用 <c>MSession.CreateOfflineSession</c>。</b>
    /// CmlLib 4.1.0 那个方法里是 <c>Guid.NewGuid()</c> —— <b>每次调用都给一个随机 UUID</b>
    /// （实测连调五次五个不同值）。而离线存档是按 UUID 存的
    /// （<c>saves/&lt;世界&gt;/playerdata/&lt;uuid&gt;.dat</c>），
    /// UUID 一变，游戏就认为来了个新玩家：世界还在，背包清空，人回到出生点。
    /// 那种"存档好好的但人物没了"的怪事就是这么来的。
    /// 这里改成按 <c>UUID.nameUUIDFromBytes("OfflinePlayer:" + name)</c> 算，
    /// 同一个名字永远是同一个 UUID。
    /// </para>
    /// <para>
    /// <c>UserType</c>：正版写 <c>msa</c>；离线写 <c>legacy</c>
    /// （等于告诉游戏"这不是微软账号"，别去走在线验证那套）。
    /// </para>
    /// </remarks>
    private static MSession BuildSession(string username, Auth.MinecraftAccount? account)
    {
        if (account is null || !account.IsMicrosoft)
        {
            var name = string.IsNullOrWhiteSpace(username) ? "Player" : username.Trim();

            return new MSession(name, Guid.NewGuid().ToString("N"), Auth.MinecraftAccount.OfflineUuid(name))
            {
                UserType = "legacy",
            };
        }

        if (!account.HasUsableToken)
        {
            throw new InvalidOperationException(
                $"账号「{account.Name}」的登录状态已经过期，先在账号里刷新一下（或者重新登录）。");
        }

        return new MSession(account.Name, account.AccessToken!, account.Uuid)
        {
            UserType = "msa",
        };
    }

    /// <summary>
    /// 把老存档里的角色数据复制一份到当前账号的 UUID 名下。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这个：</b>以前的版本用的 <c>MSession.CreateOfflineSession</c> 每次给随机 UUID，
    /// 存档里的 <c>playerdata/&lt;uuid&gt;.dat</c> 就跟着每次都不一样 —— 人一进去就是新角色。
    /// 现在 UUID 固定了，但<b>老存档里那个随机 UUID 的文件还在</b>，不处理的话
    /// 玩家会看到「世界还在、背包没了」，这正是我们刚修完的那个 bug 的最后一次发作。
    /// </para>
    /// <para>
    /// <b>只复制，不移动、不删除。</b>判断条件卡得很死 —— playerdata 里<b>恰好只有一个</b>
    /// <c>.dat</c>、文件名像 UUID、且不是当前账号的 —— 才算单人存档。
    /// 多人服务器存档里躺着十几个玩家数据，永远不满足这个条件，不会被碰。
    /// </para>
    /// <para>
    /// 用户改游戏名导致 UUID 变化时，这里同样会把旧角色接过来 —— 换个名字不清档，这是好事。
    /// </para>
    /// </remarks>
    private void MigrateLegacyPlayerData(string gameDir, string playerUuid)
    {
        var savesDir = Path.Combine(gameDir, "saves");
        if (!Directory.Exists(savesDir)) return;

        var targetId = playerUuid.Replace("-", string.Empty).ToLowerInvariant();

        IEnumerable<string> worlds;
        try { worlds = Directory.EnumerateDirectories(savesDir).ToList(); }
        catch { return; }

        foreach (var world in worlds)
        {
            var playerData = Path.Combine(world, "playerdata");

            string[] files;
            try
            {
                if (!Directory.Exists(playerData)) continue;
                files = Directory.GetFiles(playerData, "*.dat", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            // 只有「单人存档」才敢碰：恰好一个角色文件
            if (files.Length != 1) continue;

            var existingId = Path.GetFileNameWithoutExtension(files[0])
                .Replace("-", string.Empty).ToLowerInvariant();

            if (existingId.Length != 32) continue;      // 文件名不是 UUID，不猜
            if (existingId == targetId) continue;       // 已经是当前账号

            var target = Path.Combine(playerData, $"{targetId}.dat");
            if (File.Exists(target)) continue;          // 已经有当前账号的数据了，优先用它

            try
            {
                File.Copy(files[0], target);

                Log?.Invoke(this,
                    $"[存档] 世界「{Path.GetFileName(world)}」的旧角色数据已接到当前账号" +
                    $"（{existingId[..8]}… → {targetId[..8]}…），原文件保留未删");
            }
            catch (Exception ex)
            {
                Log?.Invoke(this,
                    $"[存档] 世界「{Path.GetFileName(world)}」角色数据接续失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>拼启动参数（<see cref="MLaunchOption"/>）并调 CmlLib 构建进程。下载的事一概不管。</summary>
    private async Task<Process> BuildProcessCoreAsync(
        string versionName,
        string username,
        int maximumRamMb,
        int minimumRamMb,
        CancellationToken cancellationToken,
        Auth.MinecraftAccount? account = null)
    {
        var gamePath = CreateGamePathFor(versionName);
        var session = BuildSession(username, account);

        // 存档里的角色数据是按 UUID 存的。老版本每次给随机 UUID，
        // 修好之后的第一次启动要把旧角色接过来，否则玩家看到的就是「世界还在、背包没了」。
        // UUID 为空说明 CmlLib 那边没给（不该发生），跳过就好，总比抛异常强。
        if (!string.IsNullOrWhiteSpace(session.UUID))
            MigrateLegacyPlayerData(gamePath.BasePath, session.UUID!);

        var option = new MLaunchOption
        {
            Path = gamePath,
            Session = session,
            MaximumRamMb = maximumRamMb,
            // 0 = 不写 -Xms，JVM 用默认的 1/64 物理内存起步。
            // 这是和 PCL 对齐的有意选择，不是漏了 —— 理由见 LaunchAsync 的 remarks。
            MinimumRamMb = minimumRamMb,
            VersionType = "McLauncher",
            GameLauncherName = "McLauncher",
            GameLauncherVersion = "0.1.0",
            // JavaPath 默认留空 → 交给 CmlLib 按版本 JSON 的 javaVersion.component 探测；
            // 探测不到时它会自己下载 JRE。用户在设置页手动指定了才覆盖（见下）。
        };

        // 指定了 Java 就用指定的那个。ResolveJavaPath 在文件不存在时会返回 null，
        // 也就是自动退回「让 CmlLib 挑」—— 不因为一个失效路径就让游戏起不来。
        if (LauncherSettings.ResolveJavaPath(_instanceRoot) is { } customJava)
        {
            option.JavaPath = customJava;
            Log?.Invoke(this, $"使用指定 Java：{customJava}");
        }

        // 自动中文是设置页里的开关。关掉的意思是「别碰 options.txt」——
        // 有些人就是要在英文环境下玩，或者自己选过别的语言，启动器不该每次都改回去。
        if (LauncherSettings.ResolveAutoChinese(_instanceRoot))
            EnsureChineseLanguage(versionName);
        else
            Log?.Invoke(this, "游戏语言：自动中文已关闭，不改 options.txt");

        // 全屏同理：MC 只认 options.txt 里的 fullscreen 行，没有启动参数能覆盖
        if (LauncherSettings.ResolveForceFullscreen(_instanceRoot))
            EnsureFullscreen(versionName);

        // 汉化资源包躺在 resourcepacks/ 里不会自动生效，得挂进 options.txt 才能加载
        EnsureLocalizationPacks(versionName);

        // Fabric 第一次启动要把游戏 jar 重映射一遍，写 .fabric\remappedJars\*.tmp。
        // 上一次崩在中途会留下一个几十字节的空 zip，下次启动 Fabric 想覆盖它却打不开，
        // 直接报「can't be written」然后退出 —— 用户完全看不出要删哪个文件。
        PurgeStaleRemapTemps(versionName);

        // 但那只是「残留」，真正的坑是有些机器的可写性判断本身是坏的（见 JavaAgentFix 注释）。
        // 那种机器上 Fabric 的重映射永远写不出文件，删多少次残留都没用，只能改判断。
        var jvmArgs = new List<MArgument>(2);

        // ⚠️ agent 只给 Fabric 系版本挂，别给所有版本挂。
        //
        // 它修的是 Fabric 重映射时 ZipFileSystem 的 isWritable 误判（见 JavaAgentFix 注释），
        // NeoForge / Forge / 原版根本不走那条路。而 -javaagent 会把 agent jar 塞进
        // **system classpath**，这会干扰 MixinExtras 的 @Local 解析 —— 实测 NeoForge
        // 21.4.135 + 148 个模组的整合包，挂了 agent 之后 lithium / accessories /
        // sodiumdynamiclights 的 mixin 全部报 InvalidInjectionException：
        //   Found unexpected argument type LocalBooleanRef at index 1, expected Vec3
        // 去掉 agent 后所有 mixin 正常注入，游戏能进主菜单。
        var needsWritableFix = versionName.Contains("fabric", StringComparison.OrdinalIgnoreCase);

        if (needsWritableFix)
        {
            var agentArgument = JavaAgentFix.BuildArgument(LibraryRoot);
            if (agentArgument is not null)
            {
                jvmArgs.Add(new MArgument(agentArgument));
                Log?.Invoke(this, "已加载可写性修正 agent（检查正常的机器上它自己会跳过）");
            }
        }

        // 注意：不要在这里给所有版本塞 -Dorg.lwjgl.system.stackSize / allocator 之类的全局参数。
        // 试过一次，结果让原本正常的 default 实例也崩了（OutOfMemoryError: Out of stack space）。
        // LWJGL 的堆外分配器、线程栈这些得交给游戏和它自带的 LWJGL 版本自己管，
        // 只有「可写性 agent」这种只针对坏机器的、且自检后跳过的补丁才适合全局挂。

        if (jvmArgs.Count > 0)
            option.ExtraJvmArguments = jvmArgs;

        // GC + 自定义参数：按实际要用的 Java 版本拼。必须在 BuildProcess 之前算好。
        var (jvmTuning, gcArguments) = await BuildJvmTuningAsync(versionName, cancellationToken)
            .ConfigureAwait(false);
        if (jvmTuning.Length > 0)
        {
            option.ExtraJvmArguments = option.ExtraJvmArguments is null
                ? jvmTuning
                : option.ExtraJvmArguments.Concat(jvmTuning).ToArray();
        }

        Log?.Invoke(this, $"构建启动命令…（账号 {username}" +
                          $"{(account is { IsMicrosoft: true } ? "（微软正版）" : "（离线）")}，" +
                          $"最大内存 {maximumRamMb} MB）");

        var process = await Current.Launcher
            .BuildProcessAsync(versionName, option, cancellationToken)
            .ConfigureAwait(false);

        // CmlLib 对新版（26.3 这类）会自己往命令行塞 -XX:+UseG1GC 和一组 G1 调优参数，
        // 我们再追加 ZGC 就成了「Multiple garbage collectors selected」，JVM 第一毫秒退出、
        // 退出码 1、连日志都不写 —— 表现和游戏闪退一模一样。所以构建完必须重排一次。
        if (gcArguments.Count > 0)
        {
            var original = process.StartInfo.Arguments;
            var normalized = JvmArgumentBuilder.NormalizeGc(original, gcArguments);

            if (!string.Equals(original, normalized, StringComparison.Ordinal))
            {
                process.StartInfo.Arguments = normalized;
                Log?.Invoke(this, "JVM 参数已重排：清掉 CmlLib 自带的 GC 选择器，避免「多个垃圾收集器」冲突");
            }
        }

        return process;
    }

    /// <summary>
    /// 拼 JVM 调优参数：GC（按 Java 版本挑）+ 玩家自定义那串。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>用 <c>ExtraJvmArguments</c> 追加，不动 <c>JvmArgumentOverrides</c></b>：
    /// 实测 CmlLib 默认命令行里没有 GC 参数，追加不会有「多个收集器」冲突；
    /// 而 Overrides 会替换整组 JVM 参数，把 <c>--add-exports</c> 那些必需项一起清掉。
    /// </para>
    /// <para>
    /// <b>必须知道实际会用哪个 Java</b>：把 ZGC 塞给 Java 8，JVM 会拒绝启动。
    /// 自动模式下用哪个 Java 是 CmlLib 决定的，只能反过来问它。
    /// 问不到就按「版本不明」处理 —— 那种情况只给最保守的 G1 参数。
    /// </para>
    /// </remarks>
    private async Task<(MArgument[] Arguments, IReadOnlyList<string> GcArguments)> BuildJvmTuningAsync(
        string versionName,
        CancellationToken cancellationToken)
    {
        var mode = LauncherSettings.ResolveGcMode(_instanceRoot);
        var custom = LauncherSettings.ResolveCustomJvmArgs(_instanceRoot);

        var result = new List<MArgument>();

        var javaMajor = await ResolveJavaMajorAsync(versionName, cancellationToken).ConfigureAwait(false);

        var gcArgs = JvmArgumentBuilder.ArgumentsFor(mode, javaMajor);
        if (gcArgs.Count > 0)
        {
            foreach (var arg in gcArgs) result.Add(new MArgument(arg));

            var versionText = javaMajor > 0 ? $"Java {javaMajor}" : "Java 版本未知";
            Log?.Invoke(this, $"JVM：{JvmArgumentBuilder.Describe(mode, javaMajor)}（{versionText}）");
        }
        else if (mode != GcMode.Custom)
        {
            Log?.Invoke(this, "JVM：没注入 GC 参数（Java 版本不明或过旧），交给 JVM 默认");
        }

        if (custom.Length > 0)
        {
            // FromCommandLine 把整串解析成一个能承载多参数的 MArgument ——
            // 自己按空格切会把 "-Dfoo=bar baz" 这种带空格的值切坏
            result.Add(MArgument.FromCommandLine(custom));
            Log?.Invoke(this, $"JVM：追加自定义参数 —— {custom}");
        }

        return (result.ToArray(), gcArgs);
    }

    /// <summary>这次启动实际会用哪个 Java，返回主版本号；判断不出来返回 0。</summary>
    private async Task<int> ResolveJavaMajorAsync(string versionName, CancellationToken cancellationToken)
    {
        // 用户指定了 Java 就一定用它，直接读
        if (LauncherSettings.ResolveJavaPath(_instanceRoot) is { } configured)
            return JavaRuntimeScanner.Describe(configured).Major;

        try
        {
            var version = await Current.Launcher.GetVersionAsync(versionName, cancellationToken).ConfigureAwait(false);
            var javaPath = Current.Launcher.GetJavaPath(version);

            if (!string.IsNullOrWhiteSpace(javaPath) && File.Exists(javaPath))
                return JavaRuntimeScanner.Describe(javaPath).Major;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"JVM：问不出该用哪个 Java（{ex.GetType().Name}），按版本不明处理");
        }

        return 0;
    }

    /// <summary>启动前把 options.txt 钉成全屏（设置里开了才调）。</summary>
    private void EnsureFullscreen(string versionName)    {
        try
        {
            if (GameOptionsLocalizer.EnsureFullscreen(GetGameDir(versionName)) is { } change)
                Log?.Invoke(this, $"全屏：{change}");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"[警告] 写 options.txt 全屏项失败（不影响启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ValidateMemory(int maximumRamMb, int minimumRamMb)
    {
        if (maximumRamMb <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRamMb), "最大内存必须大于 0");

        if (minimumRamMb > maximumRamMb)
            throw new ArgumentException("最小内存不能大于最大内存（CmlLib 会直接抛异常）");
    }

    /// <summary>
    /// 删掉 Fabric 上次重映射失败留下的 <c>.tmp</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fabric 首次启动会把游戏 jar 重映射成 <c>.fabric\remappedJars\…\client-intermediary.jar</c>，
    /// 过程中先写 <c>.tmp</c> 再改名。中途崩掉就留下一个 22 字节的空 zip。
    /// 下次启动 Fabric 想去覆盖它，结果打不开这个坏 zip，直接：
    /// </para>
    /// <code>
    /// java.io.IOException: the jar file …\client-intermediary.jar.tmp can't be written
    /// </code>
    /// <para>
    /// 用户看不懂这句话，也不知道该删哪个文件。所以启动前扫一遍：只删 <c>.tmp</c>，
    /// 已经完成的 <c>.jar</c> 不动（那是几百 MB 的重映射成果，删了要重做几分钟）。
    /// </para>
    /// </remarks>
    private void PurgeStaleRemapTemps(string versionName)
    {
        try
        {
            var root = Path.Combine(GamePath.Versions, versionName, ".fabric", "remappedJars");
            if (!Directory.Exists(root)) return;

            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 删不掉就算了，让 Fabric 自己报错，至少不会因为这一步拦住启动
                }
            }

            if (removed > 0)
                Log?.Invoke(this, $"清掉 {removed} 个上次重映射失败留下的临时文件（Fabric 会重新生成）");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"清理重映射临时文件时出错（不影响启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 语言只能靠改 <c>options.txt</c>（MC 不认启动参数）。游戏每次退出都会重写这个文件，
    /// 所以每次启动前都得检查一遍。写失败不拦启动 —— 大不了玩家自己进设置里改。
    /// </summary>
    private void EnsureChineseLanguage(string versionName)
    {
        try
        {
            if (GameOptionsLocalizer.EnsureChinese(GetGameDir(versionName)) is { } change)
                Log?.Invoke(this, $"游戏语言：{change}");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"[警告] 写 options.txt 失败（不影响启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把启动器做的汉化资源包挂进 <c>options.txt</c>，让它默认生效。
    /// </summary>
    /// <remarks>
    /// 和语言一样必须每次启动前做（游戏退出会重写整个文件）。写失败不拦启动 ——
    /// 玩家自己进「选项 → 资源包」手动启用即可。
    /// </remarks>
    private void EnsureLocalizationPacks(string versionName)
    {
        try
        {
            if (GameOptionsLocalizer.EnsureResourcePacks(GetGameDir(versionName)) is { } change)
                Log?.Invoke(this, $"汉化资源包：{change}");
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"[警告] 启用汉化资源包失败（不影响启动）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 某个版本的游戏目录（也就是 <c>--gameDir</c> 指向的地方）。
    /// </summary>
    /// <remarks>
    /// 隔离开启时是 <c>versions/&lt;版本名&gt;/</c>，否则是整个实例根目录。
    /// 卸载版本前要用它列出「会跟着一起删掉的模组和存档」。
    /// </remarks>
    public string GetGameDir(string versionName) => IsolateVersionGameDir
        ? Path.Combine(GamePath.Versions, versionName)
        : _instanceRoot;

    /// <summary>
    /// 构造启动用的 <see cref="MinecraftPath"/>：只把 <c>--gameDir</c> 挪到版本自己的目录。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>BasePath</c> 决定 <c>--gameDir</c> —— 游戏把 <c>saves</c>、<c>mods</c>、
    /// <c>options.txt</c>、<c>logs</c> 都写在它下面。其余几个路径继续指向实例根，
    /// 所以 <c>libraries</c> 和 <c>assets</c> 不会被复制成多份（那两个加起来 900 MB 起）。
    /// </para>
    /// <para>
    /// ⚠️ 不能直接改 <see cref="GamePath"/>：它是全局单例，安装流程和版本发现都在用。
    /// 一旦被改成某个版本的子目录，<c>GetAllVersionsAsync</c> 就只看得到那一个版本了。
    /// </para>
    /// </remarks>
    private MinecraftPath CreateGamePathFor(string versionName)
    {
        if (!IsolateVersionGameDir)
            return GamePath;

        var gameDir = Path.Combine(GamePath.Versions, versionName);
        Directory.CreateDirectory(gameDir);

        return new MinecraftPath(_instanceRoot)
        {
            BasePath = gameDir,             // ← --gameDir：存档、模组、配置都落在这
            Versions = GamePath.Versions,   // 版本 JSON 留在共享目录，不能跟着挪
            Library = GamePath.Library,
            Assets = GamePath.Assets,
            Runtime = GamePath.Runtime,
            Resource = GamePath.Resource,
        };
    }

    /// <summary>当前实例已装好的版本名。</summary>
    public string[] GetInstalledVersions()    {
        var dir = GamePath.Versions;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException
        || ex.InnerException is not null && IsTransient(ex.InnerException);

    /// <summary>
    /// 两个采样点之间的速度（字节/秒）。
    /// </summary>
    /// <remarks>
    /// 抽出来是为了能单测边界：字节数可能因为重试/回退而变小，那时不能算成负速度去误判。
    /// </remarks>
    public static double SpeedBytesPerSecond(long previousBytes, long currentBytes, double seconds)
    {
        if (seconds <= 0) return double.MaxValue;      // 没法算，当作不慢
        var delta = currentBytes - previousBytes;
        if (delta <= 0) return 0;                       // 没涨就是 0（会算作慢）
        return delta / seconds;
    }

    /// <summary>这一拍的速度是否低于换源阈值。</summary>
    public static bool IsTooSlow(long previousBytes, long currentBytes, double seconds) =>
        SpeedBytesPerSecond(previousBytes, currentBytes, seconds) < SlowBytesPerSecond;

    /// <summary>
    /// 等到 <paramref name="token"/> 被取消，再等 <paramref name="grace"/> 那么久才完成。
    /// </summary>
    /// <remarks>
    /// 用来给「卡死的下载任务」设一个逃生口：正常完成时它永远不会赢，
    /// 只有安装任务在被取消后仍然不吭声时才会先完成。
    /// </remarks>
    private static async Task EscapeAfterCancelAsync(CancellationToken token, TimeSpan grace)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期的路径：看门狗把这一遍掐了
        }

        await Task.Delay(grace).ConfigureAwait(false);
    }

    private static string Short(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "(无消息)";
        var one = message.Split('\n')[0].Trim();
        return one.Length <= 160 ? one : one[..160] + "…";
    }

    /// <summary>
    /// 放掉本服务可能还握着「某个实例目录里的文件」的句柄，好让那个目录能被删掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要这个方法</b>：CmlLib 的 <c>MinecraftLauncher</c> 不实现 <c>IDisposable</c>，
    /// 切换下载源 / 切实例时我们只是「丢掉引用」，它内部打开的文件流（最典型的是
    /// <c>versions\version_manifest_v2.json</c>）要等 GC finalizer 才会关。
    /// </para>
    /// <para>
    /// 于是会撞上这种事：装完整合包立刻删这个实例 → <c>Directory.Delete</c> 抛
    /// <c>IOException: 文件正被另一个进程使用</c>，而「另一个进程」其实就是自己。
    /// 这里先把连接池清掉、再强制走两轮 GC 收掉那些被丢掉的 launcher，句柄就落了。
    /// </para>
    /// <para>
    /// 只用在「删目录」这种低频路径上，不影响正常下载性能。
    /// </para>
    /// </remarks>
    public void ReleaseFileHandles()
    {
        foreach (var bundle in _bundles)
        {
            try { bundle.Http.Dispose(); }
            catch { /* 连接池关掉失败不影响后续，句柄该关的还是会关 */ }
        }

        _bundles.Clear();

        // 两轮：第一轮让可达性分析发现 launcher 已成垃圾并跑 finalizer，
        // 第二轮收掉 finalizer 过程中又被释放出来的对象
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var b in _bundles) b.Http.Dispose();
        _bundles.Clear();
    }

    /// <summary>
    /// 「别再试了」的异常。
    /// </summary>
    /// <remarks>
    /// 必须独立于 IOException / TimeoutException：那两个在 <see cref="IsTransient"/> 里算可重试，
    /// 抛出去会被 catch 吞掉再进下一遍，等于没放弃。
    /// </remarks>
    private sealed class InstallAbortedException : Exception
    {
        public InstallAbortedException(string message) : base(message) { }
    }

    /// <summary>一个下载源所需的全部东西（HttpClient + 镜像处理器 + Launcher）。</summary>
    private sealed class SourceBundle
    {
        public required bool UseMirror { get; init; }
        public required HttpClient Http { get; init; }
        public required BmclMirrorHandler? Mirror { get; init; }
        public required MinecraftLauncher Launcher { get; init; }
    }
}
