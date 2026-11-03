using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CmlLib.Core.Installers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepDive.App.Models;
using DeepDive.App.Services;
using DeepDive.App.Views;
using DeepDive.Core.Auth;
using DeepDive.Core.Launcher;
using DeepDive.Core.Modrinth;
using DeepDive.Core.Mods;
using DeepDive.Core.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace DeepDive.App.ViewModels;

/// <summary>
/// 设置页 Java 下拉的一项。
/// </summary>
/// <param name="Path"><c>javaw.exe</c> 路径。空串 = 「自动」，交给 CmlLib 按游戏版本挑。</param>
/// <param name="Display">下拉里显示的文字。</param>
public sealed record JavaOption(string Path, string Display)
{
    /// <summary>是不是「自动」那一项。</summary>
    public bool IsAuto => Path.Length == 0;

    public static JavaOption Auto { get; } = new(string.Empty, "自动（按游戏版本挑）");
}

/// <summary>设置页「垃圾回收器」下拉的一项。</summary>
public sealed record GcModeOption(GcMode Mode, string Display, string Note);


/// <summary>
/// 主界面视图模型：选版本 → （可选）装加载器 → 下载 → 启动，全程带进度与日志。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    /// <summary>每写这么多行才检查一次日志大小 —— 每行都 stat 文件太亏。</summary>
    private const int LogRotateCheckEveryLines = 200;

    private readonly MinecraftLauncherService _launcher;
    private readonly LoaderInstallerService _loaderInstaller;
    private readonly VersionRemovalService _removal;
    private readonly GameFolderService _folders;
    private readonly InstanceManager _instances;
    private readonly FileIntegrityChecker _integrity;
    private readonly ModpackInstaller _modpackInstaller;
    private readonly Dispatcher _dispatcher;
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _downloadClock = new();

    /// <summary>EasyTier 联机服务（懒加载，第一次进联机页才要）。</summary>
    private EasyTierService? _easyTier;

    /// <summary>联机服务实例（按需创建，避免启动时就扫描 EasyTier 目录）。</summary>
    public EasyTierService EasyTier => _easyTier ??= CreateEasyTier();

    /// <summary>加入房间时要填的房间码（绑定输入框）。</summary>
    [ObservableProperty]
    private string _joinNetworkName = string.Empty;

    /// <summary>加入房间时要填的密码。</summary>
    [ObservableProperty]
    private string _joinNetworkSecret = string.Empty;

    /// <summary>当前联机会话的状态快照（联机页整体绑它）。</summary>
    public EasyTierSession Session => EasyTier.Session;

    /// <summary>距上次检查日志大小已经写了多少行（见 <see cref="WriteLogLine"/>）。</summary>
    private int _logLinesSinceSizeCheck;

    /// <summary>正在进行的加载器版本查询（切类型/切 MC 版本时掐掉上一次）。</summary>
    private CancellationTokenSource? _loaderVersionCts;

    /// <summary>内存/用户名的落盘节奏控制（拖滑块时别每帧写一次）。</summary>
    private readonly DispatcherTimer _saveSettingsTimer;

    /// <summary>
    /// 远端版本清单缓存。
    /// </summary>
    /// <remarks>
    /// 切实例时只需要按本地已装版本重建列表，没必要为了换个盒子再下一次清单。
    /// 只有用户点「刷新」才重新拉。
    /// </remarks>
    private IReadOnlyList<CmlLib.Core.VersionMetadata.IVersionMetadata>? _remoteVersions;

    /// <summary>正在切换实例 —— 用来挡住下拉框的连环触发。</summary>
    private bool _switchingInstance;

    /// <summary>所有实例（每个是一个独立玩法盒子）。</summary>
    public ObservableCollection<InstanceInfo> Instances { get; } = new();

    /// <summary>当前实例。</summary>
    [ObservableProperty]
    private InstanceInfo? _selectedInstance;

    /// <summary>版本下拉：本地已装版本 + 远端清单版本合并后的一项。</summary>
    public ObservableCollection<VersionEntry> Versions { get; } = new();

    /// <summary>加载器类型下拉：原版 / Fabric / NeoForge / Forge。</summary>
    public ObservableCollection<LoaderChoice> LoaderChoices { get; } = new(LoaderChoice.All);

    /// <summary>加载器要装到哪个原版上（正式版列表）。</summary>
    public ObservableCollection<string> LoaderBaseVersions { get; } = new();

    /// <summary>某个加载器对某个原版可用的构建列表。</summary>
    public ObservableCollection<LoaderVersionInfo> LoaderVersions { get; } = new();

    [ObservableProperty]
    private VersionEntry? _selectedVersion;

    [ObservableProperty]
    private string _username = "Player";

    // ── 账号 ────────────────────────────────────────────────────────────────
    //
    // 账号分两种：离线（只有名字）和微软正版（有 UUID + 令牌）。
    // 列表第一项永远是离线账号，后面是登录过的微软账号 —— 这样「没有一个微软账号」
    // 和「有五个微软账号」在界面上是同一种结构，不用写两套模板。

    /// <summary>账号列表。第 0 项必然是离线账号。</summary>
    public ObservableCollection<AccountItemView> Accounts { get; } = new();

    /// <summary>当前选中的账号。<b>永远不会是 null</b>（最差也有离线账号）。</summary>
    [ObservableProperty]
    private AccountItemView? _selectedAccount;

    /// <summary>微软登录用的 Azure 应用 client id（设置页可改）。</summary>
    [ObservableProperty]
    private string _microsoftClientId = string.Empty;

    /// <summary>账号区的一行状态字（刷新中 / 刷新失败原因）。</summary>
    [ObservableProperty]
    private string _accountStatusText = string.Empty;

    [ObservableProperty]
    private bool _isAccountBusy;

    /// <summary>离线账号那一项。名字跟着 <see cref="Username"/> 走。</summary>
    private AccountItemView? _offlineAccount;

    /// <summary>当前账号头像（离线账号没有 → 界面显示首字母方块）。</summary>
    public ImageSource? CurrentAccountAvatar => SelectedAccount?.Avatar;

    /// <summary>当前账号名字。</summary>
    public string CurrentAccountName => SelectedAccount?.Name ?? Username;

    /// <summary>「微软正版」/「离线」。</summary>
    public string CurrentAccountKindLabel => SelectedAccount?.KindLabel ?? "离线";

    /// <summary>当前选的是不是微软账号（决定界面上要不要显示"正版"徽章）。</summary>
    public bool CurrentAccountIsMicrosoft => SelectedAccount?.IsMicrosoft ?? false;

    /// <summary>当前账号的补充说明。</summary>
    public string CurrentAccountDetail => SelectedAccount?.DetailLabel ?? "离线 · 进不了正版服务器";

    /// <summary>启动前是否把游戏语言自动设成简体中文（设置页的开关）。</summary>
    [ObservableProperty]
    private bool _autoChineseLanguage = true;

    /// <summary>启动前是否强制全屏（设置页的开关）。</summary>
    [ObservableProperty]
    private bool _forceFullscreen;

    /// <summary>版本隔离：每个版本用自己的 mods / saves（设置页的开关）。</summary>
    [ObservableProperty]
    private bool _isolateVersionGameDir = true;

    [ObservableProperty]
    private double _memoryMb = 4096;

    [ObservableProperty]
    private double _maxMemoryMb = 8192;

    /// <summary>
    /// 物理内存算出来的上限。<see cref="MaxMemoryMb"/> 可能比它更小（选了 32 位 Java 时），
    /// 所以两个要分开记 —— 否则切回 64 位 Java 就再也回不到原来的上限了。
    /// </summary>
    private double _physicalMaxMemoryMb = 8192;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "就绪";

    /// <summary>
    /// 现在是「只认识本地」的状态：远端清单拉不到，版本下拉里只有已经装好的。
    /// </summary>
    /// <remarks>
    /// 不叫「没网」是因为不一定真断网 —— DNS 抽风、Mojang 挂了、代理坏了都长这样。
    /// 界面只拿它决定提不提示，别拿它去拦任何操作：本地版本照样能启动。
    /// </remarks>
    [ObservableProperty]
    private bool _isOffline;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private LoaderChoice _selectedLoaderChoice = LoaderChoice.All[0];

    [ObservableProperty]
    private string? _selectedLoaderBaseVersion;

    [ObservableProperty]
    private LoaderVersionInfo? _selectedLoaderVersion;

    [ObservableProperty]
    private bool _isLoadingLoaderVersions;

    public string InstanceRoot => _launcher.InstanceRoot;

    /// <summary>「实例目录：D:\...」一行小字。</summary>
    public string InstancePathLine => $"实例目录：{InstanceRoot}";

    /// <summary>当前下载源显示名。会在「镜像失败自动回退官方源」时实时更新。</summary>
    [ObservableProperty]
    private string _sourceLabel = string.Empty;

    /// <summary>下载源可选值（官方源 / BMCLAPI 镜像）。</summary>
    public IReadOnlyList<SourceChoice> SourceOptions { get; } = SourceChoice.All;

    /// <summary>
    /// 界面上选中的下载源。改动即生效，同时写进 <c>instances\launcher-settings.json</c>。
    /// </summary>
    [ObservableProperty]
    private SourceChoice _selectedSource = SourceChoice.Official;

    /// <summary>测速结果一行字（「0.8 秒可达」/「连不上（6 秒超时）」）。</summary>
    [ObservableProperty]
    private string _sourceProbeText = string.Empty;

    /// <summary>模组搜索源可选值（官方源 / MCIM 镜像）。</summary>
    public IReadOnlyList<ModrinthSourceChoice> ModrinthSourceOptions { get; } = ModrinthSourceChoice.All;

    /// <summary>
    /// 界面上选中的模组搜索源。改动即生效，同时写进设置文件。
    /// </summary>
    [ObservableProperty]
    private ModrinthSourceChoice _selectedModrinthSource = ModrinthSourceChoice.Official;

    /// <summary>设置页：当前模组搜索源的说明（跟着下拉走）。</summary>
    public string ModrinthSourceNote => SelectedModrinthSource?.Note ?? string.Empty;

    /// <summary>下载线程可选档位。</summary>
    public IReadOnlyList<int> ConcurrencyOptions { get; } = MirrorConfigurator.ConcurrencyOptions;

    /// <summary>当前下载线程数。改完立刻重建下载器，下一次下载生效。</summary>
    [ObservableProperty]
    private int _selectedConcurrency = MirrorConfigurator.MaxDownloaders;

    /// <summary>「32 线程」这种显示文本。</summary>
    public string ConcurrencyLabel => $"{MirrorConfigurator.MaxDownloaders} 线程";

    /// <summary>并发调优的说明行，随档位变化（低档位省带宽，高档位抢速度）。</summary>
    public string ConcurrencyNote => SelectedConcurrency switch
    {
        <= 8 => "8 线程最省带宽，不容易打扰同网络下别人，但装 1.21 这种大版本会明显变慢",
        <= 16 => "16 线程：稳定优先。线路差、老是被服务端掐断时用这个",
        <= 32 => "32 线程是默认值。多数家用宽带在这个档位能吃满带宽",
        <= 48 => "48 线程：带宽够（200M 以上）时可以再快点，容易被机房限流",
        _ => "64 线程：只建议在千兆宽带下用。线程开太大反而会被服务端限速，装到一半更容易失败",
    };

    /// <summary>关于页的路径信息。</summary>
    public string SharedRepository => _instances.SharedRepository;

    public string LogFilePath => LauncherPaths.LogFile;

    public string SettingsFilePath => LauncherSettings.PathFor(_launcher.InstanceRoot);

    /// <summary>设置在页头只露文件名，完整路径放 ToolTip —— 一行几十个字符会把标题栏压歪。</summary>
    public string SettingsFileName => System.IO.Path.GetFileName(SettingsFilePath);

    /// <summary>设置页：当前下载源的说明（跟着下拉走）。</summary>
    public string SourceNote => SelectedSource?.Note ?? string.Empty;

    /// <summary>设置页：版本隔离是否开着（只读，改它得动启动器配置，不在界面里放开）。</summary>
    public string IsolateVersionLabel => _launcher.IsolateVersionGameDir
        ? "已开启 —— 每个版本有自己的 mods / saves"
        : "已关闭 —— 所有版本共用 mods / saves";

    /// <summary>设置页：内存滑块的可调范围说明。</summary>
    public string MemoryRangeLabel => $"1024 ~ {MaxMemoryMb:F0} MB";

    // ─────────────────────── Java ───────────────────────

    /// <summary>扫到的所有 Java + 打头的「自动」。下拉框绑这个。</summary>
    public ObservableCollection<JavaOption> JavaOptions { get; } = new();

    /// <summary>当前选中的 Java。<c>IsAuto</c> 为真表示交给 CmlLib 挑。</summary>
    [ObservableProperty]
    private JavaOption? _selectedJava;

    /// <summary>扫描结果原文，界面显示版本/位数要用。</summary>
    private List<JavaRuntimeInfo> _javaRuntimes = new();

    /// <summary>程序里改选中项时的闸门（扫描完回填时不触发落盘）。</summary>
    private bool _suppressJavaChange;

    /// <summary>设置页「当前 Java」那一行的说明。</summary>
    public string JavaInUseLabel
    {
        get
        {
            if (SelectedJava is not { } selected || selected.IsAuto)
            {
                var auto = GuessAutoJava();
                return auto is null
                    ? "自动 —— 启动时按游戏版本挑（还没探测到可用的 Java）"
                    : $"自动 —— 按游戏版本挑，预计用 {auto.Label}";
            }

            var known = FindRuntime(selected.Path);
            return known is null
                ? $"已指定：{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(selected.Path)) ?? string.Empty)}"
                : $"已指定：{known.Label}";
        }
    }

    /// <summary>当前 Java 的完整路径（自动时显示猜测的那个，让人知道实际用的是谁）。</summary>
    public string JavaPathLabel
    {
        get
        {
            if (SelectedJava is { IsAuto: false } selected) return selected.Path;

            var auto = GuessAutoJava();
            return auto?.Path ?? "(尚未探测)";
        }
    }

    /// <summary>
    /// 导航栏底部那行短标签：当前会用的 Java 版本，形如 <c>Java 21.0.7</c>。
    /// </summary>
    /// <remarks>
    /// 放版本号而不是「.NET 8 · WPF」：后者是开发者信息，用户看不出来跟自己有什么关系；
    /// Java 版本则直接影响「这个游戏能不能启动」，值得常驻一眼可见。
    /// </remarks>
    public string JavaShortLabel
    {
        get
        {
            string? label = null;

            if (SelectedJava is { IsAuto: false } selected)
                label = FindRuntime(selected.Path)?.Label;

            label ??= GuessAutoJava()?.Label;

            if (string.IsNullOrWhiteSpace(label)) return "Java 未探测";

            // Label 形如「21.0.7 · 64 位 · 启动器自带」，只留版本号那一段
            var head = label.Split('·')[0].Trim();
            if (head.Length == 0) return "Java 未探测";

            return head.StartsWith("Java", StringComparison.OrdinalIgnoreCase) ? head : $"Java {head}";
        }
    }

    /// <summary>32 位 Java 会压住内存上限，界面上得说清楚为什么。</summary>
    public bool IsJava32Bit =>
        SelectedJava is { IsAuto: false } selected && FindRuntime(selected.Path) is { Is64Bit: false };

    /// <summary>内存上限被 32 位 Java 压低时要显示的提示。</summary>
    public string JavaMemoryHint => IsJava32Bit
        ? $"当前选的是 32 位 Java，堆内存上限被限制在 {JavaRuntimeScanner.MaxHeapFor32Bit} MB。"
          + "想用更大的内存，请换一个 64 位 Java。"
        : string.Empty;

    // ─────────────────────── JVM 参数 / GC ───────────────────────

    /// <summary>垃圾回收器下拉的选项（照 PCL 那四档）。</summary>
    public ObservableCollection<GcModeOption> GcModeOptions { get; } = new()
    {
        new(GcMode.Auto, "自动（按 Java 版本挑）", "Java 21+ 用分代 ZGC，15~20 用 ZGC，更老用 G1GC"),
        new(GcMode.PreferZgc, "优先 ZGC（保守）", "只有 21+ 才用分代 ZGC，其余一律 G1GC"),
        new(GcMode.G1Only, "只用 G1GC", "兼容性最好。出问题往这儿切"),
        new(GcMode.Custom, "不注入（JVM 默认）", "启动器不加任何 GC 参数"),
    };

    [ObservableProperty]
    private GcModeOption? _selectedGcMode;

    /// <summary>程序里回填下拉时的闸门。</summary>
    private bool _suppressGcChange;

    /// <summary>「当前会用哪个 GC」那一行。</summary>
    public string GcHint
    {
        get
        {
            var mode = SelectedGcMode?.Mode ?? GcMode.Auto;
            var major = CurrentJavaMajor();

            var target = major > 0 ? $"按 Java {major} 算" : "Java 版本未定，按保守的来";
            return $"当前会用：{JvmArgumentBuilder.Describe(mode, major)}（{target}）";
        }
    }

    /// <summary>这次实际会用的 Java 主版本。判断不出来返回 0。</summary>
    private int CurrentJavaMajor()
    {
        if (SelectedJava is { IsAuto: false } selected)
            return FindRuntime(selected.Path)?.Major ?? 0;

        return GuessAutoJava()?.Major ?? 0;
    }

    /// <summary>换 GC 策略：立刻落盘。</summary>
    partial void OnSelectedGcModeChanged(GcModeOption? value)
    {
        if (_suppressGcChange || value is null) return;

        try
        {
            var saved = LauncherSettings.SaveGcMode(_launcher.InstanceRoot, value.Mode);
            if (!saved) AppendLog("[设置] GC 策略没能写进设置文件（下次启动会回到自动）");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 保存 GC 策略失败：{ex.GetType().Name}: {ex.Message}");
        }

        OnPropertyChanged(nameof(GcHint));
        AppendLog($"设置：垃圾回收器 → {value.Display}");
    }

    /// <summary>自定义 JVM 参数原文。</summary>
    [ObservableProperty]
    private string _customJvmArgs = string.Empty;

    /// <summary>自定义参数的落盘防抖。</summary>
    private DispatcherTimer? _saveJvmTimer;

    /// <summary>自定义 JVM 参数：改一下就打一次字，防抖 800ms 再落盘。</summary>
    partial void OnCustomJvmArgsChanged(string value)
    {
        if (_saveJvmTimer is null)
        {
            _saveJvmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _saveJvmTimer.Tick += (_, _) =>
            {
                _saveJvmTimer!.Stop();

                try
                {
                    var saved = LauncherSettings.SaveCustomJvmArgs(_launcher.InstanceRoot, CustomJvmArgs);
                    if (!saved) AppendLog("[设置] 自定义 JVM 参数没能写进设置文件");
                }
                catch (Exception ex)
                {
                    AppendLog($"[设置] 保存自定义 JVM 参数失败：{ex.GetType().Name}: {ex.Message}");
                }
            };
        }

        _saveJvmTimer.Stop();
        _saveJvmTimer.Start();
    }

    /// <summary>程序里改 <see cref="SelectedSource"/> 时（初始化、自动回退同步）挡住「用户切换」那条路。</summary>
    private bool _suppressSourceChange;

    /// <summary>程序里改 <see cref="SelectedModrinthSource"/> 时（初始化）挡住「用户切换」那条路。</summary>
    private bool _suppressModrinthSourceChange;

    /// <summary>下载中把线程数拨回去时挡住递归。</summary>
    private bool _suppressConcurrencyChange;

    private CancellationTokenSource? _probeCts;

    public string MemoryLabel => $"{MemoryMb:F0} MB";

    /// <summary>选了具体加载器才需要挑版本号、才能点安装。</summary>
    public bool IsLoaderSelected => SelectedLoaderChoice.NeedsLoaderVersion;

    public string InstallLoaderLabel => SelectedLoaderChoice.NeedsLoaderVersion
        ? $"安装 {SelectedLoaderChoice.Display}"
        : "安装加载器";

    public MainViewModel(
        MinecraftLauncherService launcher,
        LoaderInstallerService loaderInstaller,
        VersionRemovalService removal,
        GameFolderService folders,
        InstanceManager instances,
        FileIntegrityChecker integrity,
        ModpackInstaller modpackInstaller)
    {
        _launcher = launcher;
        _loaderInstaller = loaderInstaller;
        _removal = removal;
        _folders = folders;
        _instances = instances;
        _integrity = integrity;
        _modpackInstaller = modpackInstaller;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        var physical = PhysicalMemoryMb();
        _physicalMaxMemoryMb = Math.Max(2048, physical - 2048);
        _maxMemoryMb = _physicalMaxMemoryMb;
        _memoryMb = Math.Min(4096, _maxMemoryMb);

        // 上次调过的内存 / 用户名要接着用 —— 不然每次开机都得重新拨一遍滑块
        var savedSettings = LauncherSettings.LoadAt(LauncherSettings.InstancesRootFor(launcher.InstanceRoot));
        if (savedSettings.MemoryMb is int savedMemory && savedMemory >= 1024)
            _memoryMb = Math.Min(savedMemory, _maxMemoryMb);
        if (!string.IsNullOrWhiteSpace(savedSettings.Username))
            _username = savedSettings.Username!;

        _autoChineseLanguage = savedSettings.AutoChineseLanguage ?? true;
        _forceFullscreen = savedSettings.ForceFullscreen is true;

        // Java 列表扫描放后台 —— Program Files 下翻目录可能要一两秒，不能卡在这里
        _ = LoadJavaRuntimesAsync(savedSettings.JavaPath);

        // GC 策略和自定义参数：直接读字段，不触发落盘回调
        _suppressGcChange = true;
        var savedGc = LauncherSettings.ResolveGcMode(launcher.InstanceRoot);
        _selectedGcMode = GcModeOptions.FirstOrDefault(o => o.Mode == savedGc) ?? GcModeOptions[0];
        _suppressGcChange = false;

        _customJvmArgs = LauncherSettings.ResolveCustomJvmArgs(launcher.InstanceRoot);

        // 模组汉化：Key 和开关直接读字段，不走落盘回调
        _deepSeekApiKey = LauncherSettings.ResolveDeepSeekApiKey(launcher.InstanceRoot) ?? string.Empty;
        _aiModelName = LauncherSettings.ResolveAiModel(launcher.InstanceRoot) ?? string.Empty;
        _aiLocalizationEnabled = LauncherSettings.ResolveAiLocalization(launcher.InstanceRoot);
        _modPreflightEnabled = LauncherSettings.ResolveModPreflight(launcher.InstanceRoot);
        _lookupModChineseNames = LauncherSettings.ResolveLookupModChineseNames(launcher.InstanceRoot);

        // 账号：读存档。离线账号常驻第一项，微软账号按最近登录时间排。
        // 这里不做网络请求 —— 令牌刷新放到真正要启动的时候，免得开个启动器就先等十秒。
        _microsoftClientId = LauncherSettings.ResolveMicrosoftClientId(launcher.InstanceRoot) ?? string.Empty;
        LoadAccounts();

        // 版本隔离必须在这里落到服务上：它决定 mods / saves 到底在实例根还是 versions/<名字> 下面，
        // 界面读模组数、打开模组目录全都跟着它走，晚一步设就会出现「显示的和实际不是同一个目录」
        _isolateVersionGameDir = savedSettings.IsolateVersionGameDir ?? true;
        _launcher.IsolateVersionGameDir = _isolateVersionGameDir;

        // 滑块拖一下就写一次盘太费，攒 600 ms 再落盘
        _saveSettingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveSettingsTimer.Tick += (_, _) =>
        {
            _saveSettingsTimer.Stop();
            PersistMemoryAndUser();
        };

        _sourceLabel = _launcher.SourceName;

        // 版本列表是异步重建的，谁往里塞都算 —— 直接盯集合变化，省得每处 Refresh 都记得通知一次
        Versions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VersionsSummary));

        // 下拉框的初始值取「服务实际在用的那个」，而不是设置文件里的值 ——
        // 两者可能不一致（比如设置写的是镜像，但环境变量强制了官方源），以实际为准
        _suppressSourceChange = true;
        _selectedSource = SourceChoice.For(_launcher.IsUsingMirror);
        _suppressSourceChange = false;

        // 模组搜索源：读设置、应用到底层 Modrinth 客户端（DI 单例），并同步下拉框。
        // 它跟「游戏下载源」是两码事 —— Modrinth 走 Cloudflare，跟 Mojang 的线路不是同一条。
        var modrinthChoice = LauncherSettings.ResolveModrinthSource(launcher.InstanceRoot);
        _suppressModrinthSourceChange = true;
        _selectedModrinthSource = modrinthChoice;
        _suppressModrinthSourceChange = false;
        ApplyModrinthSource(modrinthChoice, log: false);

        // ⚠️ 进度事件只订 LoaderInstallerService 这一个 —— 它在构造时已经把
        // MinecraftLauncherService 的三个事件转发出来了。两边都订会双份日志。
        _loaderInstaller.FileProgressChanged += OnFileProgress;
        _loaderInstaller.ByteProgressChanged += OnByteProgress;
        _loaderInstaller.Log += (_, message) => AppendLog(message);

        _launcher.SourceChanged += (_, name) => _dispatcher.BeginInvoke(() =>
        {
            SourceLabel = name;

            // 自动回退（镜像挂了退官方源）也要把下拉框拨过去，
            // 否则界面显示「镜像」，实际在走官方源，用户会被这两条信息弄晕
            _suppressSourceChange = true;
            SelectedSource = SourceChoice.For(_launcher.IsUsingMirror);
            _suppressSourceChange = false;

            AppendLog($"下载源已切换为：{name}");
        });

        // 日志只增不减，先看一眼上次那份有没有撑爆（超过 8 MB 就改名成 .1）
        var rotatedAtStartup = LogRotation.RotateIfNeeded(LogFilePath);

        // 构建时间：发版后反复出现「你说改了我这儿没有」，查这一行就能确认跑的是哪个包
        AppendLog($"DeepDive · 构建于 {BuildStamp()} · CmlLib.Core 4.1.0 · 离线账号模式");

        if (rotatedAtStartup)
            AppendLog($"上一份日志超过 {LogRotation.DefaultMaxBytes / 1024 / 1024} MB，"
                      + $"已改名成 {Path.GetFileName(LogRotation.BackupPathFor(LogFilePath))}，本次从新的开始记");

        // 实例列表：InstanceManager 构造时已经恢复过「上次用的实例」，这里只是把结果展示出来
        ReloadInstances();
        AppendLog($"当前实例：{SelectedInstance?.Name ?? "(无)"}  →  {_launcher.InstanceRoot}");
        AppendLog($"共用库目录：{_instances.SharedRepository}");
        AppendLog($"实例列表：{Instances.Count} 个（{string.Join("、", Instances.Select(i => i.Name))}）");

        // 身份 + 临时目录可写性：拒绝访问这类问题全靠这几行定位，别再靠猜
        foreach (var line in App.StartupDiagnostics)
            AppendLog(line);

        AppendLog($"当前临时目录：{Path.GetTempPath()}");

        // 工作目录是「打不开文件夹」类问题的第一嫌疑人（见 OpenInExplorer 的注释），先记下来
        AppendLog($"进程工作目录：{Environment.CurrentDirectory}");
        AppendLog($"本日志同时写入：{LogFilePath}");
        AppendLog($"下载源：{_sourceLabel}");
        AppendLog($"下载线程数：{MirrorConfigurator.MaxDownloaders}（校验 {MirrorConfigurator.MaxCheckers} 线程，"
                  + $"连接池 {MirrorConfigurator.MaxConnectionsPerServer}，队列 {MirrorConfigurator.BoundedCapacity}）");
        AppendLog($"设置文件：{LauncherSettings.PathFor(_launcher.InstanceRoot)}");
        AppendLog($"本机物理内存：{physical} MB（内存上限已按此限制）");
        AppendLog(_launcher.IsolateVersionGameDir
            ? "版本隔离：已开启 —— 每个版本有自己的 mods / saves，卸载版本时一并删除，互不影响"
            : "版本隔离：已关闭 —— 所有版本共用 mods / saves，卸载版本不会删除存档和模组");
        AppendLog("加载器安装提示：Forge 官方依赖下载页广告维持运营，本项目不自动打开广告页，也不代其收费。"
                  + "如果你常用 Forge，建议通过官方渠道支持他们。");

        // 启动就自动拉一次版本列表 —— 否则用户面对一个空下拉框，根本不知道要先点「刷新」
        _ = RefreshVersionsAsync();
    }

    private static int PhysicalMemoryMb()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (int)(bytes / 1024 / 1024);
    }

    /// <summary>
    /// 按物理内存推荐一个堆大小。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取物理内存的<b>四分之一</b>，下限 2048、封顶 8192。
    /// </para>
    /// <para>
    /// 为什么不按一半算：模组整合包在 8 GB 之后，多给的堆基本被 GC 停顿吃掉 ——
    /// 堆越大，一次 Full GC 要扫的对象越多，表现出来就是「内存给得越多反而越卡」。
    /// 8 GB 是这个量级整合包的甜点，再往上收益是负的。
    /// </para>
    /// <para>
    /// 结果对齐到滑块的 512 刻度：滑块开了 <c>IsSnapToTickEnabled</c>，
    /// 不对齐的话设进去会被 WPF 吸附到旁边一格，看起来像「点了没反应」。
    /// </para>
    /// </remarks>
    private static double ComputeRecommendedMemory(int physicalMb, int modCount)
    {
        // 基础：物理内存的四分之一（8 GB 机器给 2 GB，16 GB 给 4 GB，往上封顶 8 GB）
        var baseline = Math.Clamp(physicalMb / 4d, 2048, 8192);

        // 模组包往上抬：加载期要同时读出上百个 jar 的元数据，运行期实体和区块也更多。
        // 光按物理内存算是给原版算的，146 个模组的 NeoForge 包 4 GB 进去就 OOM。
        var floor = modCount switch
        {
            >= 200 => 8192d,
            >= 120 => 6144d,
            >= 60 => 5120d,
            _ => 0d,
        };

        var value = Math.Max(baseline, floor);

        // 别超过物理内存的一半：超了系统自己开始换页，比堆给小还慢
        value = Math.Min(value, Math.Max(2048d, physicalMb / 2d));

        return Math.Round(value / 512d) * 512;
    }

    /// <summary>
    /// 当前版本 <c>mods</c> 里的 jar 数量。
    /// </summary>
    /// <remarks>
    /// 读不到就返回 0：版本没装、目录不存在、路径异常都按「没有模组」处理。
    /// 推荐内存是辅助功能，不该因为读个目录失败就弹异常打断界面。
    /// </remarks>
    public int CurrentModCount
    {
        get
        {
            if (SelectedVersion is not { IsInstalled: true } version) return 0;

            try
            {
                var mods = _folders.Resolve(version.Name, GameFolderKind.Mods).Path;
                return Directory.Exists(mods) ? Directory.GetFiles(mods, "*.jar").Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>推荐的堆大小（按钮上直接显示这个值）。</summary>
    public double RecommendedMemoryMb => ComputeRecommendedMemory(PhysicalMemoryMb(), CurrentModCount);

    /// <summary>按钮文案：把推荐值写出来，点之前就知道会变成多少。</summary>
    public string AutoMemoryLabel => $"自动 {RecommendedMemoryMb:F0}";

    /// <summary>按钮提示：说明依据，免得看起来像个随便的魔法值。</summary>
    public string AutoMemoryHint =>
        $"物理内存 {PhysicalMemoryMb()} MB，取四分之一作基础值；"
        + $"当前版本有 {CurrentModCount} 个模组，按数量上调。封顶 8192 MB —— "
        + "堆再大 Full GC 会变慢，反而不如给小一点";

    /// <summary>底部状态栏：本机物理内存。</summary>
    public string PhysicalMemoryLabel => $"物理 {PhysicalMemoryMb()} MB";

    /// <summary>一键把内存设成推荐值。</summary>
    [RelayCommand]
    private void AutoMemory()
    {
        var recommend = Math.Min(RecommendedMemoryMb, MaxMemoryMb);
        MemoryMb = recommend;

        AppendLog($"内存已设为推荐值 {recommend:F0} MB"
                  + $"（物理 {PhysicalMemoryMb()} MB 的 1/4，封顶 8192，上限 {MaxMemoryMb:F0}）");
    }

    // ─────────────────────── 实例管理（多实例隔离） ───────────────────────

    /// <summary>新机器上兜底用的实例名。</summary>
    private const string FallbackInstanceName = "default";

    /// <summary>
    /// 重建实例下拉，并保住当前选中的那个。
    /// </summary>
    /// <remarks>
    /// <c>Items.Clear()</c> 会让 ComboBox 把 <see cref="SelectedInstance"/> 打成 <c>null</c>，
    /// 而 <c>null</c> 又在集合变更后立刻被设成第一项 —— 这两个动作都会触发
    /// <see cref="OnSelectedInstanceChanged"/>，不挡一下就会莫名其妙切一次实例。
    /// </remarks>
    /// <summary>
    /// 重新扫描实例目录。装完整合包之后由内容窗口调用，不然新实例不会出现在下拉里。
    /// </summary>
    /// <param name="selectName">扫描完切到哪个实例（按名字）。空 = 保持当前。</param>
    public void ReloadInstances(string? selectName = null)
    {
        var currentName = selectName
                          ?? SelectedInstance?.Name
                          ?? Path.GetFileName(_launcher.InstanceRoot.TrimEnd(Path.DirectorySeparatorChar));

        _switchingInstance = true;
        try
        {
            Instances.Clear();
            foreach (var instance in _instances.Scan())
                Instances.Add(instance);

            SelectedInstance = Instances.FirstOrDefault(i =>
                                  string.Equals(i.Name, currentName, StringComparison.OrdinalIgnoreCase))
                              ?? Instances.FirstOrDefault();
        }
        finally
        {
            _switchingInstance = false;
        }

        OnPropertyChanged(nameof(InstanceRoot));
        OnPropertyChanged(nameof(SharedLibraryRoot));
    }

    /// <summary>共用库目录（libraries / assets / runtime 所在）。</summary>
    public string SharedLibraryRoot => _launcher.LibraryRoot;

    partial void OnSelectedInstanceChanged(InstanceInfo? value)
    {
        // 卡片标题就是实例名，切换的中间态也要跟着变（早退之前先通知）
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(DiskFreeLabel));

        if (_switchingInstance || value is null) return;

        // 选中的就是当前实例 —— 不用折腾
        if (string.Equals(value.Directory, _launcher.InstanceRoot, StringComparison.OrdinalIgnoreCase))
            return;

        _ = SwitchInstanceAsync(value);
    }

    /// <summary>切换实例：换路径 → 清列表 → 按新实例的已装版本重建下拉。</summary>
    private async Task SwitchInstanceAsync(InstanceInfo target)
    {
        if (IsBusy)
        {
            AppendLog("[提示] 正在下载或安装，等这一轮完事再切实例。");
            Status = "当前有任务在跑，先别切";
            ReloadInstances();
            return;
        }

        var previous = Instances.FirstOrDefault(i =>
            string.Equals(i.Directory, _launcher.InstanceRoot, StringComparison.OrdinalIgnoreCase));

        try
        {
            _switchingInstance = true;

            _instances.SwitchTo(target);

            AppendLog($"已切换到实例「{target.Name}」 → {target.Directory}");
            if (!string.Equals(target.Directory, _launcher.LibraryRoot, StringComparison.OrdinalIgnoreCase))
                AppendLog($"  库共用自：{_launcher.LibraryRoot}");

            OnPropertyChanged(nameof(InstanceRoot));
            OnPropertyChanged(nameof(SharedLibraryRoot));

            SelectedVersion = null;
            Versions.Clear();

            await ReloadVersionsAsync();

            var installed = Versions.Count(v => v.IsInstalled);
            Status = installed > 0
                ? $"已切到实例「{target.Name}」，{installed} 个已装版本"
                : $"已切到实例「{target.Name}」，这个实例还是空的，先装个版本";
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 切换实例失败：{ex.GetType().Name}: {ex.Message}");
            Status = "切换实例失败";

            // 退回原来那个，别让下拉框显示的和实际用的对不上
            if (previous is not null)
                SelectedInstance = previous;

            MessageBox.Show(
                $"切不到这个实例：{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "切换实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _switchingInstance = false;
        }
    }

    private bool CanManageInstance() => !IsBusy && SelectedInstance is not null;

    private bool CanDeleteInstance() =>
        !IsBusy && SelectedInstance is { IsSharedRepository: false };

    /// <summary>新建实例：给个名字，建个空盒子，然后切过去。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void CreateInstance()
    {
        var name = Views.TextInputWindow.Ask(
            Application.Current?.MainWindow,
            "新建实例",
            "给这个实例起个名字。每个实例的版本、模组、存档都是自己一套，删掉一个不影响别的；"
            + "libraries / assets 由所有实例共用，不会重复占空间。",
            initialValue: SuggestInstanceName(),
            hint: @"名字就是文件夹名，不能包含 \ / : * ? "" < > |",
            validate: ValidateNewInstanceName);

        if (name is null) return;

        try
        {
            var created = _instances.Create(name);
            AppendLog($"已新建实例「{created.Name}」：{created.Directory}");
            Status = $"实例「{created.Name}」建好了，选个版本装进去";

            ReloadInstances();
            SelectedInstance = Instances.FirstOrDefault(i =>
                string.Equals(i.Name, created.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 新建实例失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "新建实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>重命名实例（改目录名）。</summary>
    [RelayCommand(CanExecute = nameof(CanManageInstance))]
    private void RenameInstance()
    {
        var target = SelectedInstance;
        if (target is null) return;

        if (target.IsSharedRepository)
        {
            MessageBox.Show(
                $"实例「{target.Name}」是共用库宿主，libraries / assets / runtime 都在它下面。"
                + Environment.NewLine + Environment.NewLine
                + "改它的名字会让其他实例找不到库，所以这里不给改。",
                "改不了", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = Views.TextInputWindow.Ask(
            Application.Current?.MainWindow,
            "重命名实例",
            $"把「{target.Name}」改成什么名字？",
            initialValue: target.Name,
            hint: @"名字就是文件夹名，不能包含 \ / : * ? "" < > |",
            validate: text => ValidateNewInstanceName(text, target));

        if (name is null || string.Equals(name, target.Name, StringComparison.Ordinal))
            return;

        var wasCurrent = string.Equals(target.Directory, _launcher.InstanceRoot, StringComparison.OrdinalIgnoreCase);

        try
        {
            var renamed = _instances.Rename(target, name);
            AppendLog($"实例「{target.Name}」已改名为「{renamed.Name}」");

            // 改的是当前实例：目录已经搬了，核心还指着旧路径，必须重新切一次
            if (wasCurrent)
                _instances.SwitchTo(renamed);

            ReloadInstances();
            SelectedInstance = Instances.FirstOrDefault(i =>
                string.Equals(i.Name, renamed.Name, StringComparison.OrdinalIgnoreCase));

            Status = $"实例已改名为「{renamed.Name}」";
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 重命名实例失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>删除实例：列出会一起没掉的东西，确认后整个目录删掉。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteInstance))]
    private void DeleteInstance()
    {
        var target = SelectedInstance;
        if (target is null) return;

        InstanceRemovalPreview preview;
        try
        {
            preview = _instances.PreviewRemoval(target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "读取实例信息失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(
                BuildInstanceRemovalText(preview),
                "删除实例",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            // 删的是当前实例：先挪到别的实例上，否则核心指着一个马上不存在的目录
            if (preview.IsCurrent)
            {
                var fallback = Instances.FirstOrDefault(i =>
                    !string.Equals(i.Directory, target.Directory, StringComparison.OrdinalIgnoreCase));

                fallback ??= _instances.Create(FallbackInstanceName);
                _instances.SwitchTo(fallback);
                AppendLog($"已先切到实例「{fallback.Name}」，再删「{target.Name}」");
            }

            _instances.Delete(target);
            AppendLog($"已删除实例「{target.Name}」（{target.Directory}）");

            ReloadInstances();
            SelectedVersion = null;
            Versions.Clear();

            Status = $"实例「{target.Name}」已删除";
            _ = ReloadVersionsAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 删除实例失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(
                $"{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                + "如果是「文件正在被使用」，先把游戏和启动器里的这个实例关掉再试。",
                "删除实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>名字是否能用（新建时查重）。</summary>
    private string? ValidateNewInstanceName(string name) => ValidateNewInstanceName(name, null);

    /// <param name="self">改名时传自己，免得「没改名字」被判成重名。</param>
    private string? ValidateNewInstanceName(string name, InstanceInfo? self)
    {
        var error = InstanceManager.CheckName(name);
        if (error is not null) return error;

        var normalized = name.Trim();
        if (string.Equals(normalized, self?.Name, StringComparison.Ordinal))
            return null;

        return Instances.Any(i => string.Equals(i.Name, normalized, StringComparison.OrdinalIgnoreCase))
            ? $"「{normalized}」已经有一个实例了，换个名字。"
            : null;
    }

    /// <summary>「实例 2」「实例 3」这种不撞车的默认名。</summary>
    private string SuggestInstanceName()
    {
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"实例 {i}";
            if (Instances.All(x => !string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }

        return $"实例 {DateTime.Now:HHmmss}";
    }

    /// <summary>删除实例前的确认文本。</summary>
    private static string BuildInstanceRemovalText(InstanceRemovalPreview preview)
    {
        var lines = new List<string>
        {
            $"要删掉实例「{preview.Name}」吗？",
            string.Empty,
            preview.Directory,
            string.Empty,
        };

        if (preview.Versions.Count > 0)
            lines.Add($"· {preview.Versions.Count} 个版本：{string.Join("、", preview.Versions)}");
        else
            lines.Add("· 这个实例里还没有装过版本");

        lines.Add($"· 模组 {preview.ModCount} 个");
        lines.Add($"· 存档 {preview.SaveCount} 个");

        if (preview.IsCurrent)
            lines.Add("· 这是当前正在用的实例，删完会自动切到另一个");

        lines.Add(string.Empty);
        lines.Add("共用库（libraries / assets）不会动，其他实例照常用。");
        lines.Add("这一步删完就找不回来了，确认？");

        return string.Join(Environment.NewLine, lines);
    }

    private bool CanRun() => !IsBusy;

    /// <summary>卸载的前置条件：没在忙、且选中了一个版本。</summary>
    private bool CanUninstall() => !IsBusy && SelectedVersion is not null;

    /// <summary>安装加载器的前置条件：没在忙、有加载器类型、有原版版本、版本清单查完了。</summary>
    private bool CanInstallLoader() =>
        !IsBusy
        && !IsLoadingLoaderVersions
        && SelectedLoaderChoice.NeedsLoaderVersion
        && !string.IsNullOrEmpty(SelectedLoaderBaseVersion);

    partial void OnIsBusyChanged(bool value)
    {
        RefreshVersionsCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        InstallLoaderCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        OpenGameFolderCommand.NotifyCanExecuteChanged();
        CreateInstanceCommand.NotifyCanExecuteChanged();
        RenameInstanceCommand.NotifyCanExecuteChanged();
        DeleteInstanceCommand.NotifyCanExecuteChanged();
        ImportModpackCommand.NotifyCanExecuteChanged();
        AnalyzeCrashCommand.NotifyCanExecuteChanged();
        CheckFilesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVersionChanged(VersionEntry? value)
    {
        UninstallCommand.NotifyCanExecuteChanged();
        OpenGameFolderCommand.NotifyCanExecuteChanged();
        AnalyzeCrashCommand.NotifyCanExecuteChanged();
        CheckFilesCommand.NotifyCanExecuteChanged();

        // 切版本后存档列表就过期了；就算两边都空，SaveHint 的措辞也可能要变
        // （「还没下载」和「还没有存档」是两句不同的话）
        OnPropertyChanged(nameof(SaveHint));

        OnPropertyChanged(nameof(SelectedVersionHint));
        OnPropertyChanged(nameof(OpenFolderHint));
        OnPropertyChanged(nameof(SelectedBaseMcVersion));

        // 启动卡片：版本一换，图标上的大版本号、加载器徽章、模组数全得跟着换
        OnPropertyChanged(nameof(CardIconText));
        OnPropertyChanged(nameof(CardTitle));
        OnPropertyChanged(nameof(CardMcLabel));
        OnPropertyChanged(nameof(CardLoaderBadge));
        OnPropertyChanged(nameof(CardHasLoader));
        OnPropertyChanged(nameof(CardModLabel));

        // 「自动」时预计用哪个 Java 跟着游戏版本走（1.20.1 要 J17，26.3 要 J21）
        OnPropertyChanged(nameof(JavaInUseLabel));
        OnPropertyChanged(nameof(JavaPathLabel));
        OnPropertyChanged(nameof(JavaShortLabel));

        // 版本页的详情区
        OnPropertyChanged(nameof(SelectedVersionStateLabel));
        OnPropertyChanged(nameof(SelectedVersionInstalledLabel));
        OnPropertyChanged(nameof(SelectedVersionFolder));
        OnPropertyChanged(nameof(SelectedVersionTypeLabel));
        OnPropertyChanged(nameof(SelectedVersionLoaderLabel));

        // 换版本 = 换 mods 目录，推荐内存跟着变（原版 4096，146 模组的包 6144）
        OnPropertyChanged(nameof(CurrentModCount));
        OnPropertyChanged(nameof(RecommendedMemoryMb));
        OnPropertyChanged(nameof(AutoMemoryLabel));
        OnPropertyChanged(nameof(AutoMemoryHint));
    }

    /// <summary>
    /// 当前版本对应的<b>原版</b>版本号（<c>neoforge-26.3.0.99</c> → <c>26.3</c>）。
    /// </summary>
    /// <remarks>
    /// 搜模组要用它做筛选，不能用版本目录名 —— Modrinth 上只有 <c>26.3</c>，
    /// 没有 <c>neoforge-26.3.0.99</c>，拿目录名去筛必然一条都搜不到。
    /// 加载器版本靠 JSON 里的 <c>inheritsFrom</c> 拿原版号；原版版本自己就是原版号。
    /// </remarks>
    public string SelectedBaseMcVersion => SelectedVersion is { } entry
        ? entry.InheritsFrom ?? entry.Name
        : string.Empty;

    /// <summary>启动卡片左上角方块里的字（<c>1.21.4</c> → <c>1.21</c>）。</summary>
    public string CardIconText
    {
        get
        {
            var version = SelectedBaseMcVersion;
            if (string.IsNullOrEmpty(version)) return "?";

            var parts = version.Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : version;
        }
    }

    /// <summary>
    /// 启动卡片的主标题：<b>实例名</b>。
    /// </summary>
    /// <remarks>
    /// 用实例名而不是版本名：一个实例就是一套玩法（整合包），用户记住的是
    /// 「Better One Block」这种名字，不是 <c>neoforge-21.4.135</c>。版本号在副行。
    /// </remarks>
    public string CardTitle =>
        SelectedInstance?.Name is { Length: > 0 } name
            ? name
            : SelectedVersion?.Name ?? "还没选版本";

    /// <summary>卡片副行：原版版本号（搜模组、排错都要看这个）。</summary>
    public string CardMcLabel =>
        string.IsNullOrEmpty(SelectedBaseMcVersion) ? "—" : SelectedBaseMcVersion;

    /// <summary>
    /// 卡片副行里的加载器徽章，如 <c>NeoForge 21.4.135</c>；原版版本返回空串（徽章整个不显示）。
    /// </summary>
    public string CardLoaderBadge =>
        SelectedVersion is { } entry ? DescribeLoader(entry.Name) : string.Empty;

    /// <summary>有没有加载器 —— 决定徽章显不显示（原版没有徽章）。</summary>
    public bool CardHasLoader => CardLoaderBadge.Length > 0;

    /// <summary>页头右上角：本地装了几个、远端可选几个。比「就绪」有用，也不占地方。</summary>
    public string VersionsSummary
    {
        get
        {
            var installed = 0;
            foreach (var entry in Versions)
                if (entry.IsInstalled) installed++;

            return Versions.Count == 0
                ? "还没有版本"
                : $"已装 {installed} 个 · 可选 {Versions.Count - installed} 个";
        }
    }

    /// <summary>卡片副行：模组数量。<b>如实</b>说「没有模组」，不写「0 个模组」这种废话。</summary>
    public string CardModLabel => CurrentModCount > 0 ? $"{CurrentModCount} 个模组" : "没有模组";

    /// <summary>状态栏用 GB 写法（8192 MB → 8 GB）—— 11 号小字里 MB 太长。</summary>
    public string MemoryGbLabel => $"{MemoryMb / 1024d:0.#} GB 内存";

    /// <summary>状态栏：实例所在磁盘还剩多少（下整合包之前先看这个）。</summary>
    public string DiskFreeLabel
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(_launcher.InstanceRoot);
                if (string.IsNullOrEmpty(root)) return string.Empty;

                var freeGb = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
                return $"{root.TrimEnd(Path.DirectorySeparatorChar)} 盘剩余 {freeGb:0} GB";
            }
            catch
            {
                // 驱动器拔了/权限不对都不该让状态栏崩，空着就空着
                return string.Empty;
            }
        }
    }

    /// <summary>版本详情：这个版本是已装还是只存在于远端清单。</summary>
    public string SelectedVersionStateLabel => SelectedVersion switch
    {
        null => "还没选版本",
        { IsInstalled: true } => "已装好，可以直接启动",
        _ => "还没下载，选中后要点「下载」",
    };

    /// <summary>版本详情：安装时间（远端版本没有，显示 —）。</summary>
    public string SelectedVersionInstalledLabel =>
        SelectedVersion is { IsInstalled: true, InstalledAt: var at } && at != default
            ? at.ToString("yyyy-MM-dd HH:mm")
            : "—";

    /// <summary>
    /// 版本详情：版本目录的绝对路径。
    /// </summary>
    /// <remarks>
    /// 故意<b>不走</b> <see cref="GameFolderService.Resolve"/> —— 那个方法会顺手把目录建出来，
    /// 对着一个还没下载的远端版本调用，就会凭空造出一个空的 <c>versions/&lt;名字&gt;</c>，
    /// 看着像装了一半。这里只拼路径，不碰磁盘。
    /// </remarks>
    public string SelectedVersionFolder =>
        SelectedVersion is { } entry
            ? Path.Combine(_launcher.GamePath.Versions, entry.Name)
            : string.Empty;

    /// <summary>版本详情：加载器（原版版本明确写「原版」，不留空白让人猜）。</summary>
    public string SelectedVersionLoaderLabel =>
        CardLoaderBadge is { Length: > 0 } badge ? badge : "原版（没有加载器）";

    /// <summary>版本详情：类型 —— 正式版 / 测试版 / 远古版，外加是不是本地已装。</summary>
    public string SelectedVersionTypeLabel => SelectedVersion switch
    {
        null => "—",
        { IsInstalled: true } version => $"已装 · {version.ChannelLabel}",
        { Channel.Length: > 0 } version => version.ChannelLabel,
        _ => "—",
    };

    /// <summary>
    /// 把加载器版本目录名拆成「加载器 构建号」：<c>neoforge-21.4.135</c> → <c>NeoForge 21.4.135</c>。
    /// </summary>
    /// <remarks>
    /// 原版版本返回空串 —— 调用方靠这个判断要不要显示徽章，
    /// 别返回「原版」再去特判，那种写法很容易在界面上漏出一块「原版」的牌子。
    /// </remarks>
    private static string DescribeLoader(string versionName)
    {
        const string fabricPrefix = "fabric-loader-";
        const string quiltPrefix = "quilt-loader-";

        if (versionName.StartsWith(fabricPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // fabric-loader-0.16.9-1.21.1：中间那段是 loader 版本，后半截是原版版本
            var rest = versionName[fabricPrefix.Length..];
            var dash = rest.IndexOf('-');
            return "Fabric " + (dash > 0 ? rest[..dash] : rest);
        }

        if (versionName.StartsWith(quiltPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = versionName[quiltPrefix.Length..];
            var dash = rest.IndexOf('-');
            return "Quilt " + (dash > 0 ? rest[..dash] : rest);
        }

        if (versionName.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            return "NeoForge " + versionName["neoforge-".Length..];

        if (versionName.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            return "Forge " + versionName["forge-".Length..];

        return string.Empty;
    }

    /// <summary>版本行右边的提示：告诉用户这个版本是已装还是远端、要不要先下载。</summary>
    public string SelectedVersionHint => SelectedVersion switch
    {
        null => string.Empty,
        { IsInstalled: true } => "已装好，直接点「启动游戏」",
        _ => "还没下载，先点「下载」",
    };

    /// <summary>「打开文件夹」的悬停说明 —— 讲清楚隔离开启时目录到底在哪，省得用户去根目录找。</summary>
    public string OpenFolderHint => SelectedVersion switch
    {
        not { IsInstalled: true } => "先选一个已安装的版本",
        _ when _launcher.IsolateVersionGameDir =>
            "版本隔离已开启：每个版本有自己的模组和存档，都在 versions\\「版本名」\\ 下面",
        _ => "版本隔离已关闭：所有版本共用实例根目录下的模组和存档",
    };

    partial void OnMemoryMbChanged(double value)
    {
        OnPropertyChanged(nameof(MemoryLabel));
        OnPropertyChanged(nameof(MemoryGbLabel));
        SchedulePersistMemoryAndUser();
    }

    partial void OnUsernameChanged(string value)
    {
        SchedulePersistMemoryAndUser();

        // 离线账号就是"用这个名字玩"，用户名一改，列表第一项跟着改
        UpdateOfflineAccountName(value);
    }

    // ── 账号：加载 / 切换 / 登录 / 移除 ─────────────────────────────────────

    /// <summary>从存档读账号，离线账号补在第一项，并恢复上次选中的那个。</summary>
    private void LoadAccounts()
    {
        var offlineName = string.IsNullOrWhiteSpace(Username) ? "Player" : Username.Trim();

        _offlineAccount = new AccountItemView(MinecraftAccount.CreateOffline(offlineName));

        var (saved, selectedUuid) = AccountStore.Load();

        Accounts.Clear();
        Accounts.Add(_offlineAccount);

        foreach (var account in saved
                     .Where(a => a.IsMicrosoft)
                     .OrderByDescending(a => a.LastLoginAt))
        {
            Accounts.Add(new AccountItemView(account));
        }

        // 按 UUID 找回上次选的。找不到就回退到离线账号 ——
        // 那个账号可能已经被删了，不能因为存档里还记着它就让启动器没有账号可用
        var picked = string.IsNullOrWhiteSpace(selectedUuid)
            ? null
            : Accounts.FirstOrDefault(a =>
                string.Equals(a.Uuid, selectedUuid, StringComparison.OrdinalIgnoreCase));

        ApplySelection(picked ?? _offlineAccount, persist: false);

        AppendLog($"[账号] 读出 {Accounts.Count - 1} 个微软账号，当前用「{CurrentAccountName}」（{CurrentAccountKindLabel}）");

        // 头像走网络，晚点加载；加载不到就是没头像，不影响功能
        _ = LoadAllAvatarsAsync();
    }

    /// <summary>把某一行标成选中，并刷新界面上的账号摘要。</summary>
    private void ApplySelection(AccountItemView account, bool persist)
    {
        foreach (var item in Accounts)
            item.IsSelected = ReferenceEquals(item, account);

        SelectedAccount = account;

        if (persist) SaveAccounts();

        OnPropertyChanged(nameof(CurrentAccountName));
        OnPropertyChanged(nameof(CurrentAccountKindLabel));
        OnPropertyChanged(nameof(CurrentAccountIsMicrosoft));
        OnPropertyChanged(nameof(CurrentAccountDetail));
        OnPropertyChanged(nameof(CurrentAccountAvatar));
    }

    /// <summary>把微软账号写回存档（离线账号不入库 —— 它的名字就在设置里）。</summary>
    private void SaveAccounts()
    {
        var microsoft = Accounts.Where(a => a.IsMicrosoft).Select(a => a.Account).ToList();

        if (!AccountStore.Save(microsoft, SelectedAccount?.Uuid))
            AppendLog("[账号] 没能写进 accounts.json（这次登录下次开机就没了）");
    }

    /// <summary>用户名改了：更新列表第一项。</summary>
    private void UpdateOfflineAccountName(string name)
    {
        if (_offlineAccount is null) return;

        var trimmed = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();

        _offlineAccount.Update(MinecraftAccount.CreateOffline(trimmed));

        if (ReferenceEquals(SelectedAccount, _offlineAccount))
        {
            OnPropertyChanged(nameof(CurrentAccountName));
            OnPropertyChanged(nameof(CurrentAccountDetail));
        }
    }

    /// <summary>用户在账号窗口里点了某一项。</summary>
    [RelayCommand]
    private void SelectAccount(AccountItemView? account)
    {
        if (account is null) return;

        ApplySelection(account, persist: true);
        AccountStatusText = string.Empty;

        AppendLog($"[账号] 切换为「{account.Name}」（{account.KindLabel}）");
    }

    /// <summary>移除一个微软账号。离线账号不给删（删了就没有账号能启动了）。</summary>
    [RelayCommand]
    private void RemoveAccount(AccountItemView? account)
    {
        if (account is null || !account.IsMicrosoft) return;

        var wasSelected = ReferenceEquals(SelectedAccount, account);

        Accounts.Remove(account);

        AppendLog($"[账号] 移除「{account.Name}」");

        if (wasSelected && _offlineAccount is not null)
            ApplySelection(_offlineAccount, persist: true);
        else
            SaveAccounts();
    }

    /// <summary>打开账号窗口（切换 / 登录 / 移除）。</summary>
    [RelayCommand]
    private void OpenAccounts()
    {
        try
        {
            var window = new AccountWindow(this)
            {
                Owner = Application.Current?.MainWindow is { IsLoaded: true } main ? main : null,
            };

            window.ShowDialog();

            // 窗口里可能改了 client id / 切了账号，摘要要在关窗后重新推一遍
            OnPropertyChanged(nameof(CurrentAccountName));
            OnPropertyChanged(nameof(CurrentAccountKindLabel));
            OnPropertyChanged(nameof(CurrentAccountIsMicrosoft));
            OnPropertyChanged(nameof(CurrentAccountDetail));
            OnPropertyChanged(nameof(CurrentAccountAvatar));
        }
        catch (Exception ex)
        {
            AppendLog($"[账号] 打开账号窗口失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "打不开账号窗口", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 从 Minecraft 官方启动器导入账号令牌。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条路绕开了「Azure 应用要被 Mojang 批准」那道坎：令牌是官方启动器自己登录拿的、
    /// Mojang 自己发的，我们只是从本机文件里读出来用。
    /// </para>
    /// <para>
    /// 代价是<b>令牌 24 小时过期，而且拿不到续期凭据</b>。过期后只能回官方启动器
    /// 启动一次 Java 版，再回来点这个按钮。界面上把这件事写在账号说明里。
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task ImportFromOfficialAsync()
    {
        if (IsAccountBusy) return;

        IsAccountBusy = true;
        AccountStatusText = "正在查找官方启动器的账号…";

        try
        {
            var service = App.Services.GetRequiredService<OfficialLauncherTokenService>();
            service.Log = AppendLog;

            var scan = service.Scan();

            // 两条来源：老版启动器留下的明文文件，或者正在运行的游戏进程。
            // 新版（Store 版）只走后者 —— 它压根不往磁盘写令牌。
            MinecraftAccount? candidate = null;

            if (scan.Found && scan.Account is not null)
            {
                candidate = scan.Account;
                AppendLog($"[账号] 从 {scan.FilePath} 读到令牌");
            }
            else
            {
                AccountStatusText = "文件里没有令牌，正在找运行中的游戏…";

                var running = Core.Auth.RunningGameTokenReader.Scan();
                if (running.Count > 0)
                {
                    var hit = running[0];

                    candidate = new MinecraftAccount
                    {
                        Name = hit.Username,
                        Uuid = Core.Auth.MicrosoftAuthService.AddDashes(hit.Uuid),
                        Kind = AccountKind.Microsoft,
                        AccessToken = hit.AccessToken,
                        AccessTokenExpiresAt = null,

                        // 命令行里拿不到续期凭据，跟文件导入一样是「过期要重来」
                        RefreshToken = null,
                        LastLoginAt = DateTimeOffset.UtcNow,
                    };

                    AppendLog($"[账号] 从运行中的游戏进程（PID {hit.ProcessId}）读到令牌：" +
                              $"{hit.Username}，userType={hit.UserType}");
                }
            }

            if (candidate is null)
            {
                AccountStatusText = "没找到可用的令牌";

                var message =
                    "两条路都没找到正版令牌。\n\n" +
                    "【你装的是新版官方启动器，走这条】\n" +
                    "用官方启动器启动一次游戏，进到主菜单后【别关游戏】，\n" +
                    "切回来点这个按钮。新版启动器只把令牌放在运行中的游戏里，不写到磁盘上。\n\n" +
                    "【装着老版官方启动器，走这条】\n" +
                    "在官方启动器里登录并启动一次 Java 版，然后再点这个按钮。";

                AppendLog($"[账号] 导入失败：{scan.Problem}；运行中的游戏也没找到");

                MessageBox.Show(message, "没找到正版令牌",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                return;
            }

            AccountStatusText = "找到了，正在向 Mojang 验证令牌…";

            // 文件里没有过期时间字段，要问一次才知道能不能用
            var verified = await service.VerifyAsync(candidate);

            var existing = Accounts.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Uuid) &&
                string.Equals(a.Uuid, verified.Uuid, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Update(verified);
                ApplySelection(existing, persist: true);
            }
            else
            {
                var item = new AccountItemView(verified);
                Accounts.Add(item);
                ApplySelection(item, persist: true);
            }

            AccountStatusText = $"已导入：{verified.Name}";
            AppendLog($"[账号] 从官方启动器导入「{verified.Name}」，令牌 24 小时内有效");

            _ = LoadAvatarAsync(SelectedAccount!);
        }
        catch (AuthException ex)
        {
            AccountStatusText = ex.Hint.Split('\n')[0];
            AppendLog($"[账号] 导入失败（{ex.Stage}）：{ex.Message}");

            MessageBox.Show(ex.FullMessage, "导入失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AccountStatusText = $"导入失败：{ex.Message}";
            AppendLog($"[账号] 导入失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    /// <summary>登录一个新的微软账号。成功就加进列表并切过去。</summary>
    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        if (IsAccountBusy) return;

        // 留空就用内置的公共 id —— 实测能走通整条链，用户什么都不用配
        var clientId = string.IsNullOrWhiteSpace(MicrosoftClientId)
            ? MicrosoftAuthService.DefaultClientId
            : MicrosoftClientId.Trim();

        IsAccountBusy = true;
        AccountStatusText = "正在登录…";

        try
        {
            var auth = App.Services.GetRequiredService<MicrosoftAuthService>();
            auth.Log = AppendLog;

            var window = new MicrosoftLoginWindow(auth, clientId)
            {
                Owner = Application.Current?.MainWindow is { IsLoaded: true } main ? main : null,
            };

            var ok = window.ShowDialog() == true && window.Result is not null;

            // 用户在窗口里改了 client id 就顺手存下来
            if (!string.IsNullOrWhiteSpace(window.UsedClientId) &&
                !string.Equals(window.UsedClientId, MicrosoftClientId, StringComparison.Ordinal))
            {
                MicrosoftClientId = window.UsedClientId!;
            }

            if (!ok || window.Result is null)
            {
                AccountStatusText = window.FailureMessage ?? "已取消";
                return;
            }

            var account = window.Result;

            // 同一个账号（UUID 相同）重复登录时替换掉旧的，而不是堆两行
            var existing = Accounts.FirstOrDefault(a =>
                string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Update(account);
                ApplySelection(existing, persist: true);
            }
            else
            {
                var item = new AccountItemView(account);
                Accounts.Add(item);
                ApplySelection(item, persist: true);
            }

            AccountStatusText = $"已登录：{account.Name}";
            AppendLog($"[账号] 登录成功：{account.Name}（{account.Uuid}）");

            _ = LoadAvatarAsync(SelectedAccount!);
        }
        catch (Exception ex)
        {
            AccountStatusText = $"登录失败：{ex.Message}";
            AppendLog($"[账号] 登录失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    // ── 联机（EasyTier）────────────────────────────────────────────────────

    private EasyTierService CreateEasyTier()
    {
        var service = App.Services.GetRequiredService<EasyTierService>();
        service.Log = AppendLog;
        return service;
    }

    /// <summary>创建房间：本机当房主，虚拟 IP 固定 .1。</summary>
    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        if (Session.State is EasyTierState.Starting or EasyTierState.Stopping)
            return;

        AppendLog("[联机] 创建房间…");

        var result = await EasyTier.CreateAsync().ConfigureAwait(true);

        if (result.Ok)
        {
            AppendLog($"[联机] 房间已创建：{result.NetworkName}（密码 {result.NetworkSecret}）");
            OnPropertyChanged(nameof(Session));
        }
        else
        {
            AppendLog($"[联机] 创建失败：{result.Error}");
        }
    }

    /// <summary>加入房间：输入房主给的房间码 + 密码。</summary>
    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (Session.State is EasyTierState.Starting or EasyTierState.Stopping)
            return;

        var name = JoinNetworkName.Trim();
        var secret = JoinNetworkSecret.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(secret))
        {
            Session.StatusText = "房间码和密码都要填。";
            OnPropertyChanged(nameof(Session));
            return;
        }

        AppendLog($"[联机] 加入房间 {name}…");

        var result = await EasyTier.JoinAsync(name, secret).ConfigureAwait(true);

        if (result.Ok)
        {
            AppendLog($"[联机] 已加入 {name}");
            OnPropertyChanged(nameof(Session));
        }
        else
        {
            AppendLog($"[联机] 加入失败：{result.Error}");
        }
    }

    /// <summary>停止联机。</summary>
    [RelayCommand]
    private void StopRoom()
    {
        EasyTier.Stop();
        AppendLog("[联机] 已停止");
        OnPropertyChanged(nameof(Session));
    }

    /// <summary>手动刷新联机页显示的房间码。</summary>
    [RelayCommand]
    private void RefreshRoom()
    {
        OnPropertyChanged(nameof(Session));
    }

    /// <summary>手动刷新当前微软账号的登录状态（令牌过期、或者怀疑失效时点）。</summary>
    [RelayCommand]
    private async Task RefreshAccountAsync()
    {
        if (IsAccountBusy) return;

        if (SelectedAccount is not { IsMicrosoft: true } item)
        {
            AccountStatusText = "当前离线账号，没什么可刷新的";
            return;
        }

        // 官方导入的账号没有续期凭据，刷新这条路走不通 —— 得回去重新导入
        if (item.Account.NeedsReimport && !item.Account.HasUsableToken)
        {
            AccountStatusText = "令牌过期了，去官方启动器启动一次游戏再导入";

            MessageBox.Show(
                "这个账号是从官方启动器导入的，没有续期凭据，没法在启动器里刷新。\n\n" +
                "做法：打开 Minecraft 官方启动器 → 点「开始游戏」启动一次 Java 版" +
                "（到主菜单就行）→ 关掉游戏 → 回来点「从官方启动器导入」。\n\n" +
                "为什么这么麻烦：官方启动器只把 24 小时有效的令牌交给别人，不给能续期的凭据。",
                "需要重新导入",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (item.Account.NeedsReimport)
        {
            // 还有效的话，点刷新就当「再导一遍」，等于重新读文件、重新验证
            await ImportFromOfficialAsync().ConfigureAwait(true);
            return;
        }

        IsAccountBusy = true;
        AccountStatusText = "正在刷新登录状态…";

        try
        {
            var (ok, refreshed) = await TryRefreshAccountAsync(item, interactive: true).ConfigureAwait(true);

            AccountStatusText = ok ? $"刷新成功：{refreshed?.Name}" : AccountStatusText;
        }
        finally
        {
            IsAccountBusy = false;
        }
    }

    /// <summary>尝试刷新一个微软账号的令牌，成功就更新列表并落盘。</summary>
    /// <param name="item">要刷的账号。</param>
    /// <param name="interactive">是不是用户主动点的（决定要不要弹窗说明失败原因）。</param>
    private async Task<(bool Ok, MinecraftAccount? Account)> TryRefreshAccountAsync(
        AccountItemView item, bool interactive)
    {
        var clientId = MicrosoftClientId.Trim();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            AccountStatusText = "缺 client id，刷不了";

            if (interactive)
                MessageBox.Show(
                    "刷新登录状态需要 client id，但设置里没填。\n" +
                    "先在设置页「账号」里填一个，或者直接重新登录。",
                    "缺 client id", MessageBoxButton.OK, MessageBoxImage.Warning);

            return (false, null);
        }

        try
        {
            var auth = App.Services.GetRequiredService<MicrosoftAuthService>();
            auth.Log = AppendLog;

            var refreshed = await auth.RefreshAsync(item.Account, clientId).ConfigureAwait(true);

            item.Update(refreshed);
            SaveAccounts();

            if (ReferenceEquals(SelectedAccount, item))
            {
                OnPropertyChanged(nameof(CurrentAccountName));
                OnPropertyChanged(nameof(CurrentAccountDetail));
            }

            AppendLog($"[账号] 「{refreshed.Name}」登录状态已刷新，{refreshed.AccessTokenExpiresAt:HH:mm} 前有效");
            return (true, refreshed);
        }
        catch (AuthException ex)
        {
            AccountStatusText = ex.Hint;

            AppendLog($"[账号] 刷新失败（{ex.Stage}）：{ex.Message}");

            if (interactive)
            {
                MessageBox.Show(ex.FullMessage, "刷新失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            AccountStatusText = $"刷新失败：{ex.Message}";
            AppendLog($"[账号] 刷新失败：{ex.GetType().Name}: {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>启动前把账号准备好：微软账号令牌过期就先刷新。</summary>
    /// <returns>
    /// <c>Ok=false</c> 表示账号用不了、应当中止启动；
    /// <c>Account</c> 为 <c>null</c> 且 <c>Ok=true</c> 表示走离线账号。
    /// </returns>
    /// <remarks>
    /// 这一步放在「体检之后、拼参数之前」：体检要读本地文件，不用账号；
    /// 而拼参数时账号必须是可用的。<b>不能在这里静默降级成离线</b> ——
    /// 用户以为在玩正版、结果进服务器被踢，比直接告诉他"登录过期了"糟糕得多。
    /// </remarks>
    private async Task<(bool Ok, MinecraftAccount? Account)> ResolveLaunchAccountAsync()
    {
        if (SelectedAccount is not { IsMicrosoft: true } item)
            return (true, null);

        if (item.Account.HasUsableToken)
            return (true, item.Account);

        // 官方导入的账号没有续期凭据：过期后只能回去重新导入，不能走刷新
        if (item.Account.NeedsReimport)
        {
            AppendLog("[账号] 官方启动器令牌已过期");

            var reimport = MessageBox.Show(
                $"账号「{item.Name}」的令牌过期了（从官方启动器导入的令牌只有 24 小时）。\n\n" +
                "先在官方启动器里点「开始游戏」启动一次 Java 版（到主菜单就行），关掉游戏，\n" +
                "然后点「确定」，我来重新读一次。\n\n" +
                "点「取消」则这次用离线账号启动（进不了正版服务器）。",
                "登录状态过期",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (reimport == MessageBoxResult.OK)
            {
                await ImportFromOfficialAsync().ConfigureAwait(true);

                if (SelectedAccount is { Account.HasUsableToken: true } after)
                    return (true, after.Account);
            }

            AppendLog("[账号] 这次改用离线身份启动");
            return (true, null);
        }

        AppendLog("[账号] 微软令牌已过期，启动前先刷新");

        // 刷新要联网，但不能让用户毫无心理准备地卡住
        AccountStatusText = "登录状态过期，正在自动刷新…";

        var (ok, refreshed) = await TryRefreshAccountAsync(item, interactive: false).ConfigureAwait(true);

        if (ok) return (true, refreshed);

        var choice = MessageBox.Show(
            $"账号「{item.Name}」的登录状态过期了，自动刷新也没成功。\n\n{AccountStatusText}\n\n" +
            "要改成离线账号启动吗？（进不了正版服务器，但单机能玩）",
            "登录状态过期",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        return choice == MessageBoxResult.OK
            ? (true, null)
            : (false, null);
    }

    /// <summary>把列表里所有微软账号的头像都拉一遍。</summary>
    private async Task LoadAllAvatarsAsync()
    {
        foreach (var item in Accounts.ToList())
        {
            if (item.IsMicrosoft) await LoadAvatarAsync(item).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 拉一个账号的头像（第三方服务，按 UUID 取 64px 小图）。
    /// </summary>
    /// <remarks>
    /// 拉不到就算了 —— 头像只是好看，不该因为它让账号功能不可用，更不该弹错误。
    /// 所以这里所有异常都吞掉。
    /// </remarks>
    private async Task LoadAvatarAsync(AccountItemView item)
    {
        if (item.Avatar is not null) return;

        var url = item.Account.AvatarUrl;
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(true);
            if (bytes.Length == 0) return;

            // 解码放 UI 线程：后台线程解码 BitmapImage 会失败（这个坑在模组图标那里踩过）
            using var stream = new MemoryStream(bytes);

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            item.Avatar = bitmap;

            if (ReferenceEquals(SelectedAccount, item))
                OnPropertyChanged(nameof(CurrentAccountAvatar));
        }
        catch
        {
            // 头像拉不到就保持占位方块
        }
    }

    /// <summary>只改「微软 client id」这一项。</summary>
    partial void OnMicrosoftClientIdChanged(string value)
    {
        try
        {
            if (!LauncherSettings.SaveMicrosoftClientId(_launcher.InstanceRoot, value))
                AppendLog("[设置] client id 没能写进设置文件");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 保存 client id 失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>「自动中文」开关：改了立刻落盘，下次启动（甚至这次）就生效。</summary>
    partial void OnAutoChineseLanguageChanged(bool value)
    {
        try
        {
            var saved = LauncherSettings.SaveAutoChinese(_launcher.InstanceRoot, value);
            AppendLog(value
                ? "设置：启动前自动把游戏语言设为简体中文"
                : "设置：已关闭自动中文，启动器不再改 options.txt");

            if (!saved)
                AppendLog("[设置] 自动中文开关没能写进设置文件（下次启动会回到默认）");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 保存自动中文开关失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>「启动时全屏」开关。</summary>
    partial void OnForceFullscreenChanged(bool value)
    {
        try
        {
            var saved = LauncherSettings.SaveForceFullscreen(_launcher.InstanceRoot, value);
            AppendLog(value
                ? "设置：启动时强制全屏（会写 options.txt 的 fullscreen 行）"
                : "设置：不再强制全屏，用你游戏里上次的设置");

            if (!saved)
                AppendLog("[设置] 全屏开关没能写进设置文件（下次启动会回到默认）");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 保存全屏开关失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 「版本隔离」开关：立刻切服务里的开关，并落盘。
    /// </summary>
    /// <remarks>
    /// <b>只切开关，不搬文件。</b>两套布局的目录不同（实例根 <c>mods</c> vs
    /// <c>versions\&lt;版本名&gt;\mods</c>），自动搬会变成「搬一半失败，模组和存档全在半路」。
    /// 所以界面上必须写清楚：切完之后游戏看的是另一个目录，旧文件还在原处。
    /// </remarks>
    partial void OnIsolateVersionGameDirChanged(bool value)
    {
        try
        {
            _launcher.IsolateVersionGameDir = value;

            var saved = LauncherSettings.SaveIsolateVersionGameDir(_launcher.InstanceRoot, value);

            // 隔离状态变了，模组数、打开目录的提示、版本目录显示全得跟着重算
            OnPropertyChanged(nameof(IsolateVersionLabel));
            OnPropertyChanged(nameof(OpenFolderHint));
            OnPropertyChanged(nameof(CurrentModCount));
            OnPropertyChanged(nameof(CardModLabel));
            OnPropertyChanged(nameof(RecommendedMemoryMb));

            AppendLog(value
                ? "设置：版本隔离已开启 —— mods / saves 在 versions\\<版本名>\\ 下面，每个版本一套"
                : "设置：版本隔离已关闭 —— 所有版本共用实例根目录下的 mods / saves");

            if (!saved)
                AppendLog("[设置] 版本隔离开关没能写进设置文件（下次启动会回到默认）");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 切换版本隔离失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ─────────────────────── Java 运行时 ───────────────────────

    /// <summary>
    /// 扫一遍 Java 并填进下拉框。扫描放后台 —— Program Files 下翻目录、
    /// 偶尔还要起进程问版本，放主线程会让界面卡一两秒。
    /// </summary>
    /// <param name="preferred">设置文件里记的那个路径（可能已经失效）。</param>
    private async Task LoadJavaRuntimesAsync(string? preferred)
    {
        List<JavaRuntimeInfo> runtimes;
        try
        {
            var sharedRepo = _instances.SharedRepository;
            runtimes = await Task.Run(() => JavaRuntimeScanner.Scan(sharedRepo).ToList()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"[Java] 扫描失败：{ex.GetType().Name}: {ex.Message}");
            runtimes = new List<JavaRuntimeInfo>();
        }

        _javaRuntimes = runtimes;

        _suppressJavaChange = true;
        try
        {
            JavaOptions.Clear();
            JavaOptions.Add(JavaOption.Auto);

            foreach (var runtime in runtimes)
                JavaOptions.Add(new JavaOption(runtime.Path, $"{runtime.Label} — {runtime.Path}"));

            // 设置里指定的那个还在就选它；不在了（文件被删/移动）就退回「自动」，
            // 并且把设置也改回自动，免得每次启动都白找一遍
            var match = preferred is { Length: > 0 }
                ? JavaOptions.FirstOrDefault(o => string.Equals(o.Path, preferred, StringComparison.OrdinalIgnoreCase))
                : null;

            if (preferred is { Length: > 0 } && match is null)
            {
                AppendLog($"[Java] 上次指定的 Java 已经不在了：{preferred}，改回自动");
                LauncherSettings.SaveJavaPath(_launcher.InstanceRoot, null);
            }

            SelectedJava = match ?? JavaOption.Auto;
        }
        finally
        {
            _suppressJavaChange = false;
        }

        ApplyJavaMemoryCap();

        AppendLog(runtimes.Count == 0
            ? "[Java] 没扫到任何 Java —— 首次启动时 CmlLib 会自动下载一个"
            : $"[Java] 扫到 {runtimes.Count} 个：" + string.Join("；", runtimes.Select(r => r.Label)));
    }

    /// <summary>用户换了 Java：立刻落盘，并按位数调整内存上限。</summary>
    partial void OnSelectedJavaChanged(JavaOption? value)
    {
        if (_suppressJavaChange) return;

        var path = value?.Path ?? string.Empty;

        try
        {
            var saved = LauncherSettings.SaveJavaPath(_launcher.InstanceRoot, path);
            if (!saved) AppendLog("[设置] Java 选择没能写进设置文件（下次启动会回到自动）");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 保存 Java 选择失败：{ex.GetType().Name}: {ex.Message}");
        }

        ApplyJavaMemoryCap();

        AppendLog(path.Length == 0
            ? "设置：Java 改为自动（按游戏版本挑）"
            : $"设置：Java 指定为 {path}");

        OnPropertyChanged(nameof(JavaInUseLabel));
        OnPropertyChanged(nameof(JavaPathLabel));
        OnPropertyChanged(nameof(JavaShortLabel));
        OnPropertyChanged(nameof(IsJava32Bit));
        OnPropertyChanged(nameof(JavaMemoryHint));

        // 换 Java 会改变「能用哪个 GC」，提示跟着换
        OnPropertyChanged(nameof(GcHint));
    }

    /// <summary>
    /// 选了 32 位 Java 就把内存上限压到 1024。
    /// </summary>
    /// <remarks>
    /// 32 位 JVM 的地址空间就 4GB，堆开超过 1GB 基本起不来，而且报错很难看
    /// （"Could not reserve enough space for object heap"）。与其让用户点了启动才失败，
    /// 不如在滑块上就挡住。
    /// </remarks>
    private void ApplyJavaMemoryCap()
    {
        var cap = IsJava32Bit
            ? Math.Min(_physicalMaxMemoryMb, JavaRuntimeScanner.MaxHeapFor32Bit)
            : _physicalMaxMemoryMb;

        MaxMemoryMb = cap;

        if (MemoryMb > cap)
        {
            MemoryMb = cap;
            AppendLog($"[Java] 这是 32 位 Java，内存上限压到 {cap:F0} MB");
        }

        OnPropertyChanged(nameof(MemoryRangeLabel));
        OnPropertyChanged(nameof(JavaInUseLabel));
        OnPropertyChanged(nameof(JavaPathLabel));
        OnPropertyChanged(nameof(IsJava32Bit));
        OnPropertyChanged(nameof(JavaMemoryHint));

    }

    private JavaRuntimeInfo? FindRuntime(string path) =>
        _javaRuntimes.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 猜「自动」时 CmlLib 会用哪个：按当前游戏版本需要的 Java 主版本去匹配，
    /// 匹配不到再退到最新的那个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只是给界面一句参考话，不参与实际启动决策 —— CmlLib 是按版本 JSON 的
    /// <c>javaVersion.component</c> 精确挑的，跟这里不完全一样。所以文案写「预计用」。
    /// </para>
    /// <para>
    /// 为什么要按版本推：直接取「最高版本」会显示 25.0.1，但 26.3 实际要的是 J21。
    /// 显示一个用不上的版本号，比不显示更误导。
    /// </para>
    /// </remarks>
    private JavaRuntimeInfo? GuessAutoJava()
    {
        if (_javaRuntimes.Count == 0) return null;

        var needed = RequiredJavaMajor(SelectedBaseMcVersion);
        var exact = _javaRuntimes
            .Where(r => r.Major == needed)
            .OrderByDescending(r => r.Major)
            .FirstOrDefault();

        return exact ?? _javaRuntimes.OrderByDescending(r => r.Major).First();
    }

    /// <summary>
    /// 某个游戏版本需要哪个 Java 主版本。
    /// </summary>
    /// <remarks>
    /// 官方对照表：1.16 及以下 → 8；1.17~1.20.4 → 17；1.20.5 以后 → 21。
    /// 日历版本（26.x 这种）一律按 21 算 —— 它们都比 1.20.5 新。
    /// </remarks>
    private static int RequiredJavaMajor(string? mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return 21;

        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major)) return 21;

        // 不是 1.x 的就是日历版本（26.1 / 26.3 …），都是新的
        if (major != 1) return 21;
        if (!int.TryParse(parts[1], out var minor)) return 21;

        if (minor >= 21) return 21;

        if (minor == 20)
        {
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            return patch >= 5 ? 21 : 17;
        }

        return minor >= 17 ? 17 : 8;
    }

    /// <summary>重新扫一遍（用户刚装了个 JDK，不想重启启动器）。</summary>
    [RelayCommand]
    private Task RescanJava() => LoadJavaRuntimesAsync(SelectedJava is { IsAuto: false } current ? current.Path : null);

    /// <summary>手动挑一个 javaw.exe。</summary>
    [RelayCommand]
    private void BrowseJava()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Java 的 javaw.exe",
            Filter = "Java 运行时 (javaw.exe;java.exe)|javaw.exe;java.exe|所有程序 (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        var info = JavaRuntimeScanner.Describe(dialog.FileName);

        // 不是 javaw 也照样记，但要说一句 —— 用 javaw 才不会附带一个黑框
        if (!Path.GetFileName(dialog.FileName).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            AppendLog("[Java] 选的是 java.exe，启动时会多一个控制台黑框；能选 javaw.exe 就选它");

        if (info.Major == 0)
            AppendLog($"[Java] 读不出这个 Java 的版本号，可能不是标准 JDK/JRE：{dialog.FileName}");

        var option = new JavaOption(info.Path, $"{info.Label} — {info.Path}");

        _javaRuntimes.RemoveAll(r => string.Equals(r.Path, info.Path, StringComparison.OrdinalIgnoreCase));
        _javaRuntimes.Add(info);
        if (!JavaOptions.Any(o => string.Equals(o.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
            JavaOptions.Add(option);

        SelectedJava = JavaOptions.First(o => string.Equals(o.Path, info.Path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>攒一下再写：滑块拖一次会连发几十个值，逐次落盘纯属自虐。</summary>
    private void SchedulePersistMemoryAndUser()    {
        _saveSettingsTimer.Stop();
        _saveSettingsTimer.Start();
    }

    /// <summary>
    /// 把内存和用户名写进 <c>launcher-settings.json</c>。
    /// </summary>
    /// <remarks>
    /// 失败只记日志，不弹窗 —— 设置写不进去最多是「下次开机回到默认值」，
    /// 为这个打断用户不值当。
    /// </remarks>
    private void PersistMemoryAndUser()
    {
        try
        {
            var saved = LauncherSettings.SaveMemoryAndUser(
                _launcher.InstanceRoot,
                (int)Math.Round(MemoryMb),
                Username);

            if (!saved)
                AppendLog("[设置] 内存/用户名没能写进设置文件（下次启动会回到默认值）");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 保存内存/用户名失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    partial void OnIsLoadingLoaderVersionsChanged(bool value)
        => InstallLoaderCommand.NotifyCanExecuteChanged();

    partial void OnSelectedLoaderChoiceChanged(LoaderChoice value)
    {
        OnPropertyChanged(nameof(IsLoaderSelected));
        OnPropertyChanged(nameof(InstallLoaderLabel));
        InstallLoaderCommand.NotifyCanExecuteChanged();

        if (!value.NeedsLoaderVersion)
        {
            // 切回「原版」：清掉加载器版本，别让上次的列表误导人
            CancelLoaderVersionQuery();
            LoaderVersions.Clear();
            SelectedLoaderVersion = null;
            return;
        }

        if (!string.IsNullOrEmpty(SelectedLoaderBaseVersion))
            _ = LoadLoaderVersionsAsync();
    }

    partial void OnSelectedLoaderBaseVersionChanged(string? value)
    {
        InstallLoaderCommand.NotifyCanExecuteChanged();
        if (SelectedLoaderChoice.NeedsLoaderVersion && !string.IsNullOrEmpty(value))
            _ = LoadLoaderVersionsAsync();
    }

    // ─────────────────────────── 命令 ───────────────────────────

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshVersionsAsync()
    {
        // 故意<b>不</b>占 IsBusy。IsBusy 会让「启动」按钮变灰，而拉版本清单
        // 和启动游戏本来就不冲突：断网时远端要重试好几轮（最坏两分钟），
        // 这期间本地版本已经列出来了，用户完全可以直接玩 ——
        // 要是按钮跟着灰两分钟，那这次「先出本地」就白做了。
        Status = "正在拉取版本列表…";
        ProgressPercent = 0;

        try
        {
            await ReloadVersionsAsync(refreshRemote: true);
        }
        catch (Exception ex)
        {
            Status = "拉取版本列表失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 重建版本下拉：本地已装版本 + 远端清单合并。
    /// </summary>
    /// <remarks>
    /// 不碰 <see cref="IsBusy"/> —— 安装加载器完成后要复用它刷新列表，
    /// 那里外层的 IsBusy 还没放开，再嵌套一次会把按钮提前解锁。
    /// <para>
    /// 远端清单默认走缓存。切实例、装完加载器都没必要重下一遍清单（几百 KB、还依赖网络），
    /// 只有用户点「刷新」才真要新的。
    /// </para>
    /// </remarks>
    private async Task ReloadVersionsAsync(bool refreshRemote = false)
    {
        IReadOnlyList<CmlLib.Core.VersionMetadata.IVersionMetadata> remote;
        var needsRemote = refreshRemote || _remoteVersions is null;

        if (!needsRemote)
            remote = _remoteVersions!;
        else
        {
            // ① 先铺本地已装的，一次网络都不碰。
            //    CmlLib 的清单请求失败要重试 3 次、还会换源再来 3 次，一次 20 秒超时 ——
            //    「连着 WiFi 但出不去」这种最坏能拖两分钟。期间下拉框要是空的，
            //    用户就没法启动已经下好的游戏，等于断网 = 启动器废了。
            //    所以本地这遍必须先出，远端的等待放在后面。
            ApplyVersionList(
                Array.Empty<CmlLib.Core.VersionMetadata.IVersionMetadata>(),
                provisional: true);

            // ② 再拉远端清单，拿到了把完整列表补上去
            try
            {
                remote = await _launcher.GetVersionsAsync();
                _remoteVersions = remote;
                IsOffline = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 拉不到远端不代表什么都干不了：本地已装版本的文件一个不少，
                // 启动它们完全不需要联网。降级成「只列本地」，别把异常往上抛。
                remote = Array.Empty<CmlLib.Core.VersionMetadata.IVersionMetadata>();
                IsOffline = true;

                _remoteVersions = null;   // 不缓存失败：下次刷新还能重试
                AppendLog($"[离线] 拉不到版本清单，只显示本地已装版本：{ex.GetType().Name}: {ex.Message}");
            }
        }

        ApplyVersionList(remote, provisional: false);
    }

    /// <summary>
    /// 把「远端清单 + 本地已装」合成下拉列表贴到界面上。
    /// </summary>
    /// <param name="remote">远端版本清单；离线或还没拉到的时候给空数组。</param>
    /// <param name="provisional">
    /// <c>true</c> = 这是不等网络先铺的临时结果：不动状态栏、不动离线标记。
    /// 此时到底拉不拉得到还不知道，先写「离线」是冤枉人家。
    /// </param>
    /// <returns>本地已装的版本数。</returns>
    private int ApplyVersionList(
        IReadOnlyList<CmlLib.Core.VersionMetadata.IVersionMetadata> remote,
        bool provisional)
    {
        var installed = VersionEntry.LoadInstalled(_launcher.GamePath.Versions);

        var remoteByName = new Dictionary<string, CmlLib.Core.VersionMetadata.IVersionMetadata>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var r in remote)
            remoteByName[r.Name] = r;

        var previous = SelectedVersion?.Name;
        var built = new List<VersionEntry>();

        // —— 本地已装的排前面：刚装完加载器，用户下一件事就是启动它 ——
        foreach (var local in installed)
        {
            remoteByName.TryGetValue(local.Name, out var meta);

            // 注意：CmlLib 会把本地目录里扫到的版本也放进版本清单，type 标成 "local"。
            // 所以这里不能只判空 —— neoforge-26.3.0.99 拿到的是 "local" 而不是 null，
            // 不回退到父版本的话，一勾「正式版」它整条就没了（用户会以为版本丢了）。
            var channel = meta?.Type;
            if (!IsRealChannel(channel)
                && local.InheritsFrom is { } parent
                && remoteByName.TryGetValue(parent, out var parentMeta)
                && IsRealChannel(parentMeta.Type))
                channel = parentMeta.Type;

            if (!IsRealChannel(channel)) channel = "local";

            built.Add(new VersionEntry
            {
                Name = local.Name,
                Type = meta?.Type ?? "installed",
                ReleaseTime = meta?.ReleaseTime ?? default,
                IsInstalled = true,
                InheritsFrom = local.InheritsFrom,
                InstalledAt = local.InstalledAt,
                Channel = channel,
            });
        }

        // —— 远端清单里本地没有的，跟在后面 ——
        var installedNames = built.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in remote)
        {
            if (installedNames.Contains(r.Name)) continue;
            built.Add(new VersionEntry
            {
                Name = r.Name,
                Type = r.Type ?? string.Empty,
                ReleaseTime = r.ReleaseTime,
                Channel = r.Type ?? string.Empty,
            });
        }

        built.Sort(CompareEntries);

        Versions.Clear();
        foreach (var entry in built)
            Versions.Add(entry);

        // 加载器要装到哪个原版上：只给正式版，省得列表里混进几百个快照
        var releases = remote
            .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Name)
            .ToList();

        if (releases.Count > 0)
        {
            var previousBase = SelectedLoaderBaseVersion;
            LoaderBaseVersions.Clear();
            foreach (var name in releases)
                LoaderBaseVersions.Add(name);
            SelectedLoaderBaseVersion =
                LoaderBaseVersions.FirstOrDefault(n => n == previousBase) ?? LoaderBaseVersions[0];
        }

        SelectedVersion =
            Versions.FirstOrDefault(v => v.Name == previous)
            ?? Versions.FirstOrDefault()
            ?? null;

        var installedCount = built.Count(v => v.IsInstalled);

        if (provisional) return installedCount;   // 临时结果：别抢状态栏

        if (IsOffline)
        {
            Status = installedCount > 0
                ? $"离线：只列出本地已装的 {installedCount} 个版本（能正常启动）"
                : "离线：拉不到版本清单，本地也没有已安装的版本";
        }
        else
        {
            Status = $"共 {Versions.Count} 个版本（已装 {installedCount} 个），当前选中 {SelectedVersion?.Name ?? "无"}";
        }

        if (installedCount > 0)
            AppendLog($"版本列表已更新：已装 {installedCount} 个（{string.Join(", ", built.Where(v => v.IsInstalled).Select(v => v.Name))}）");

        return installedCount;
    }

    /// <summary>已装的排前面（按安装时间倒序），其余按发布时间倒序。</summary>
    /// <summary>
    /// 是不是一个真正的 Mojang 渠道（而不是 CmlLib 给本地目录标的 <c>local</c>）。
    /// </summary>
    private static bool IsRealChannel(string? channel) =>
        channel is "release" or "snapshot" or "old_beta" or "old_alpha";

    private static int CompareEntries(VersionEntry a, VersionEntry b)
    {
        if (a.IsInstalled != b.IsInstalled) return a.IsInstalled ? -1 : 1;
        if (a.IsInstalled) return b.InstalledAt.CompareTo(a.InstalledAt);
        return b.ReleaseTime.CompareTo(a.ReleaseTime);
    }

    [RelayCommand(CanExecute = nameof(CanInstallLoader))]
    private async Task InstallLoaderAsync()
    {
        var kind = SelectedLoaderChoice.Kind;
        var minecraftVersion = SelectedLoaderBaseVersion;
        if (kind == ModLoaderKind.Vanilla)
            return;

        if (string.IsNullOrEmpty(minecraftVersion))
        {
            // 原来这里是静默 return —— 用户点了按钮什么都不发生，只能以为程序坏了
            Status = IsOffline
                ? "装加载器要先知道有哪些原版版本，这个得联网拉；现在拉不到"
                : "先在上面选一个原版版本";
            return;
        }

        var loaderVersion = SelectedLoaderVersion?.LoaderVersion;

        IsBusy = true;
        ProgressPercent = 0;
        _downloadClock.Restart();

        try
        {
            Status = $"正在安装 {SelectedLoaderChoice.Display} …";
            AppendLog($"=== 安装 {SelectedLoaderChoice.Display} / MC {minecraftVersion} / 加载器 {loaderVersion ?? "自动选最新"} ===");

            // Forge 官方在版本 JSON 里明确写着「请不要自动化安装，我们靠下载页广告维持」。
            // 这里不自动打开广告页（那会骚扰用户），但也不能装作没这回事 —— 就地提示。
            if (kind == ModLoaderKind.Forge)
                AppendLog("提示：Forge 官方依赖下载页广告维持运营。本启动器不自动打开广告页，"
                          + "如果你常用 Forge，建议通过官方渠道支持他们。");

            var result = await _loaderInstaller.InstallAsync(kind, minecraftVersion, loaderVersion);

            _downloadClock.Stop();
            ProgressPercent = 100;
            AppendLog($"✓ 安装完成：{result.VersionName}（{result.Kind} {result.LoaderVersion}，MC {result.MinecraftVersion}）");
            AppendLog($"耗时 {_downloadClock.Elapsed.TotalSeconds:F0} 秒");

            // 刷新列表并选中刚装好的版本 —— 用户下一步就是点启动
            await ReloadVersionsAsync();
            SelectedVersion = Versions.FirstOrDefault(v => v.Name == result.VersionName) ?? SelectedVersion;

            Status = $"已装好 {result.VersionName}，点「下载并启动」即可开玩";
        }
        catch (Exception ex)
        {
            _downloadClock.Stop();

            // 进度条留着不动的话，用户看到的就是「16.7 / 18.2 MB 一直停着」——
            // 和"还在跑"完全一样，只能靠读日志才能发现已经失败了。清零才看得出结束了。
            ProgressPercent = 0;
            Status = "加载器安装失败，看下面的日志";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                AppendLog($"[内层] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 卸载选中的版本，连同它的模组与存档。
    /// </summary>
    /// <remarks>
    /// 先扫一遍报账（多少个模组、多少个世界、占多少空间），用户确认后才动手。
    /// 删除是<b>不可逆</b>的，所以默认按钮是「取消」而不是「确定」。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var entry = SelectedVersion;
        if (entry is null) return;

        // 扫目录算大小可能要一两秒（带模组和存档的版本尤其），别卡住 UI 线程
        RemovalPreview preview;
        IsBusy = true;
        try
        {
            preview = await Task.Run(() => _removal.Preview(entry.Name));
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = "读取版本信息失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
            return;
        }
        IsBusy = false;

        var answer = MessageBox.Show(
            BuildUninstallConfirmText(preview),
            $"卸载 {entry.Name}",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);   // 默认落在「取消」上，防手滑

        if (answer != MessageBoxResult.OK)
        {
            Status = "已取消卸载";
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        try
        {
            Status = $"正在删除 {entry.Name} …";
            AppendLog($"=== 卸载 {entry.Name}（{preview.TotalSizeLabel}）===");

            await Task.Run(() => _removal.Remove(entry.Name));

            AppendLog($"✓ 已删除 {preview.Directory}");
            foreach (var item in preview.Items)
                AppendLog($"   · {item.Label} {item.Count} 个（{item.SizeLabel}）已一并删除");

            SelectedVersion = null;
            await ReloadVersionsAsync();

            ProgressPercent = 100;
            Status = $"已卸载 {entry.Name}";
        }
        catch (Exception ex)
        {
            Status = "卸载失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                AppendLog($"[内层] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildUninstallConfirmText(RemovalPreview preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"确定要卸载「{preview.VersionName}」吗？");
        sb.AppendLine();
        sb.AppendLine($"位置：{preview.Directory}");
        sb.AppendLine($"占用：{preview.TotalSizeLabel}");

        sb.AppendLine();
        if (preview.Items.Count > 0)
        {
            sb.AppendLine("会一起删除：");
            foreach (var item in preview.Items)
                sb.AppendLine($"  · {item.Label} {item.Count} 个（{item.SizeLabel}）");
        }
        else
        {
            sb.AppendLine("存档和模组位于实例根目录（版本隔离未开启），不会被删除。");
        }

        if (preview.HasInheritors)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ " + preview.InheritorWarning);
        }

        sb.AppendLine();
        sb.Append("此操作不可恢复。");
        return sb.ToString();
    }

    // ─────────────────────── 打开文件夹（PCL 式） ───────────────────────

    /// <summary>只在选中了「已装」版本时允许打开 —— 没装的版本目录根本不存在。</summary>
    private bool CanOpenVersionFolder(GameFolderKind kind) =>
        !IsBusy && SelectedVersion is { IsInstalled: true };

    /// <summary>
    /// 打开选中版本的某个文件夹（版本本体 / 模组 / 存档 / 配置 / 日志）。
    /// </summary>
    /// <remarks>
    /// 隔离开启时这几个目录都在 <c>versions/&lt;版本名&gt;/</c> 里面，
    /// 所以路径统一交给 <see cref="GameFolderService"/> 算 —— 不能在这里写死 <c>实例根\mods</c>，
    /// 那会打开一个游戏根本不读的目录，模组放进去不生效。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenVersionFolder))]
    private Task OpenGameFolderAsync(GameFolderKind kind)
    {
        var entry = SelectedVersion;
        if (entry is null) return Task.CompletedTask;

        try
        {
            var target = _folders.Resolve(entry.Name, kind);

            // 直接开内置窗口，不再先试系统 shell。
            // 理由：这台机器 shell 坏了，调 ShellExecute / Shell.Application.Explore
            // 都会先给我一个「Windows 无法访问指定设备、路径或文件」的系统弹框，
            // 用户点一次被弹一次，体验比开不出窗口还差。要看系统窗口，菜单里有单独一项。
            AppendLog(target.Existed
                ? $"打开{target.Label}：{target.Path}"
                : $"打开{target.Label}：{target.Path}（原来没有，刚新建的，现在是空的）");
            Status = $"已打开{target.Label}";
            ShowFolderWindow(target.Path, target.Label);
        }
        catch (Exception ex)
        {
            Status = "打开文件夹失败";
            AppendLog($"[错误] 打开文件夹失败：{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
                AppendLog($"[错误] 内层原因：{inner.GetType().Name}: {inner.Message}");
            MessageBox.Show(ex.Message, "打不开文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 仍然交给系统资源管理器打开（用户明确要求的备用路径）。
    /// </summary>
    /// <remarks>
    /// 系统 shell 正常时这才是顺手的做法 —— 有地址栏、有右键菜单、能拖拽。
    /// 但它坏的时候会弹系统级的错误框，所以不能放在默认路径上。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOpenVersionFolder))]
    private async Task OpenGameFolderExternalAsync(GameFolderKind kind)
    {
        var entry = SelectedVersion;
        if (entry is null) return;

        try
        {
            var target = _folders.Resolve(entry.Name, kind);
            var result = await Task.Run(() => OpenInExplorer(target.Path));

            AppendLog($"已请求资源管理器打开{target.Label}：{target.Path}（通道 {result.Channel}，" +
                      $"窗口{(result.Verified ? "已确认" : "未能确认")}）");
            Status = result.Verified ? $"已用资源管理器打开{target.Label}" : "已请求打开，但没确认到窗口";
        }
        catch (Exception ex)
        {
            Status = "资源管理器打开失败";
            AppendLog($"[错误] 资源管理器打开失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "资源管理器打不开", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 复制当前版本某个文件夹的路径 —— 万一系统就是不肯弹资源管理器，
    /// 至少能把路径粘到地址栏里自己进。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenVersionFolder))]
    private void CopyFolderPath(GameFolderKind kind)
    {
        var entry = SelectedVersion;
        if (entry is null) return;

        try
        {
            var target = _folders.Resolve(entry.Name, kind);
            Clipboard.SetText(target.Path);
            AppendLog($"已复制{target.Label}路径：{target.Path}");
            Status = "路径已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Status = "复制路径失败";
            AppendLog($"[错误] 复制路径失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "复制路径失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>打开当前实例目录 —— 这个实例的 <c>versions</c>、模组、存档都在这。</summary>
    [RelayCommand]
    private Task OpenInstanceFolderAsync()
    {
        var root = _folders.InstanceRoot;
        var name = SelectedInstance?.Name ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));

        AppendLog($"打开实例目录（{name}）：{root}");
        Status = $"已打开实例「{name}」目录";
        ShowFolderWindow(root, $"实例：{name}");

        return Task.CompletedTask;
    }

    /// <summary>打开共用库目录 —— 多个实例共用的 <c>libraries</c> / <c>assets</c> / <c>runtime</c> 在这。</summary>
    [RelayCommand]
    private Task OpenSharedLibraryFolderAsync()
    {
        var root = _launcher.LibraryRoot;

        AppendLog($"打开共用库目录：{root}");
        Status = "已打开共用库目录";
        ShowFolderWindow(root, "共用库（libraries / assets / runtime）");

        return Task.CompletedTask;
    }

    // ─────────────────────── 模组管理 ───────────────────────

    /// <summary>
    /// 打开模组管理窗口（列表 / 搜索 / 启用禁用 / 移除）。
    /// </summary>
    /// <remarks>
    /// 目录取的是<b>当前版本真实的游戏目录</b>（见 <see cref="ResolveCurrentGameDir"/>）：
    /// 版本隔离开着时模组在 <c>versions/&lt;版本名&gt;/mods</c>，
    /// 用实例根那个空壳目录会打开一个「0 个模组」的窗口，而启动器明明显示装了 2 个。
    /// </remarks>
    [RelayCommand]
    private Task OpenModManagerAsync()
    {
        if (SelectedVersion is not { } version)
        {
            Status = "先在「版本」页选一个版本";
            return Task.CompletedTask;
        }

        if (!version.IsInstalled)
        {
            Status = "这个版本还没下载，没东西可管";
            return Task.CompletedTask;
        }

        var gameDir = ResolveCurrentGameDir();
        if (gameDir is null)
        {
            Status = "拿不到这个版本的游戏目录";
            AppendLog("[模组] 打开管理窗口失败：解析不到游戏目录");
            return Task.CompletedTask;
        }

        return OpenModManagerAt(Path.Combine(gameDir, "mods"));
    }

    /// <summary>
    /// 打开指定 mods 目录的管理窗口。
    /// </summary>
    /// <param name="modsDirectory">模组目录。</param>
    /// <remarks>
    /// 从文件夹浏览窗口点「用模组管理打开」时走这条：那里的目录可能是<b>别的版本</b>的
    /// （用户翻到了 <c>versions/1.20.1/mods</c>），不能拿当前选中版本去凑。
    /// </remarks>
    private Task OpenModManagerAt(string modsDirectory)
    {
        try
        {
            // 目录不存在时建出来 —— 管理窗口打开一个不存在的目录只能显示「不存在」，
            // 建一个空的至少能让人把 jar 拖进去
            if (!Directory.Exists(modsDirectory))
                Directory.CreateDirectory(modsDirectory);

            var service = App.Services.GetRequiredService<ModManagerService>();

            // 中文名要联网查，用户可以在设置里关掉（比如流量紧张或者不想被查）
            var nameService = LauncherSettings.ResolveLookupModChineseNames(_launcher.InstanceRoot)
                ? App.Services.GetRequiredService<ModChineseNameService>()
                : null;

            // 标题里带版本名：versions/<版本名>/mods 往上退两级，
            // 非隔离时（实例根/mods）退一级就是实例名
            var versionLabel = DescribeModsDirectory(modsDirectory);

            var viewModel = new ModManagerViewModel(
                service,
                modsDirectory,
                $"模组管理 · {versionLabel}",
                AppendLog,
                message => Status = message,
                nameService);

            var window = new Views.ModManagerWindow(viewModel, modsDirectory);

            if (Application.Current?.MainWindow is { IsLoaded: true } main)
                window.Owner = main;

            window.Show();

            AppendLog($"[模组] 打开管理窗口：{modsDirectory}");
            Status = "模组管理已打开";
        }
        catch (Exception ex)
        {
            Status = "打开模组管理失败";
            AppendLog($"[错误] 打开模组管理失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "打不开模组管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 从 mods 目录倒推一个能放在标题里的名字。
    /// </summary>
    /// <remarks>
    /// <c>versions/neoforge-26.3.0.99/mods</c> → <c>neoforge-26.3.0.99</c>；
    /// 非隔离布局 <c>instances/default/mods</c> → <c>default</c>。
    /// 拿不到就退回目录名，不至于显示空白。
    /// </remarks>
    private static string DescribeModsDirectory(string modsDirectory)
    {
        try
        {
            var mods = new DirectoryInfo(modsDirectory);

            // versions/neoforge-26.3.0.99/mods 的父目录就是版本名；
            // 非隔离布局 instances/default/mods 的父目录是实例名。两种情况都是取父目录。
            return mods.Parent?.Name ?? mods.Name;
        }
        catch
        {
            return "当前版本";
        }
    }

    /// <summary>
    /// 用启动器自己的窗口列目录（不依赖系统 shell）。
    /// </summary>
    /// <remarks>
    /// 故意用 <c>Show</c> 而不是 <c>ShowDialog</c>：用户可以同时开模组夹和存档夹对照着看，
    /// 模态窗口会逼他先关一个。
    /// </remarks>
    private void ShowFolderWindow(string path, string caption)
    {
        try
        {
            // 把「当前版本真正的游戏目录」一起传过去。
            // 版本隔离开启时，mods / saves 在 versions/<版本名>/ 下面，
            // 光看路径猜不出来 —— 窗口里的「跳到 模组」会指到实例根那个空 mods 去。
            var gameDir = ResolveCurrentGameDir();

            var window = new Views.FolderWindow(
                path, caption, gameDir,
                modsDirectory => _ = OpenModManagerAt(modsDirectory));

            if (Application.Current?.MainWindow is { IsLoaded: true } main && !ReferenceEquals(main, window))
                window.Owner = main;

            window.Show();
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 打开内置文件夹窗口失败：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "打不开文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 当前选中版本的实际游戏目录（隔离在 <c>versions/&lt;版本名&gt;</c>，否则是实例根）。
    /// </summary>
    /// <returns>拿不到版本名时返回 <c>null</c>，交给窗口自己按路径猜。</returns>
    private string? ResolveCurrentGameDir()
    {
        try
        {
            if (SelectedVersion is not { } version) return null;

            var dir = _launcher.GetGameDir(version.Name);
            return Directory.Exists(dir) ? dir : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 打开目录，返回「通道名 + 是否确认窗口真的出现了」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>绝不 <c>Process.Start("explorer.exe", path)</c>。</b>
    /// 那是真的拉起一个新的 explorer.exe 进程，而这台机器上新建 explorer 进程会初始化失败，
    /// 弹「应用程序无法正常启动(0xc0000142)」—— 用户会以为是启动器坏了。系统日志里有铁证：
    /// <code>
    /// Application Popup (ID 26)
    /// 弹出应用程序: explorer.exe - 应用程序错误: 应用程序无法正常启动(0xc0000142)
    /// </code>
    /// </para>
    /// <para>
    /// 于是按「尽量不新建进程」的顺序试两条通道：
    /// </para>
    /// <list type="number">
    /// <item><c>Shell.Application</c> COM 的 <c>Explore(path)</c> —— 请求常驻的那一个资源管理器开窗口；</item>
    /// <item><c>ShellExecute</c>（<c>UseShellExecute = true</c> + <c>FileName = 目录</c>）—— 走文件关联。</item>
    /// </list>
    /// <para>
    /// <b>关键改动：不再拿「API 没抛异常」当成功。</b>之前就是 API 返回成功、窗口却一个没出来，
    /// 日志还写着「已打开」，纯属骗自己。现在每条通道跑完都去 <c>ShellWindows</c> 里找有没有
    /// 窗口真的停在目标路径上，找到才算数；找不到就换下一条通道，全不行才抛异常。
    /// </para>
    /// <para>
    /// <c>WorkingDirectory</c> 显式设成系统目录：不设会继承本进程的当前目录，
    /// 那个目录一旦不可访问，ShellExecute 会抛「拒绝访问」，而报错文字指向的是目标路径，极容易误判。
    /// </para>
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    /// <exception cref="InvalidOperationException">所有通道都没能开出窗口。</exception>
    private static (bool Verified, string Channel) OpenInExplorer(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"目录不存在：{path}");

        var failures = new List<string>();

        // 通道 1：COM，不新建进程
        try
        {
            ExploreViaShellCom(path);

            switch (WaitForExplorerWindow(path, TimeSpan.FromMilliseconds(1500)))
            {
                case true:
                    return (true, "Shell.Application COM");
                case null:
                    // 窗口列表都读不到（非交互会话之类），没证据说没开，按成功算，别再开第二个窗口
                    return (false, "Shell.Application COM（未能确认）");
            }

            failures.Add("Shell.Application COM：调用没报错，但 1.5 秒内没等到窗口（常驻资源管理器可能已经卡死）");
        }
        catch (Exception ex)
        {
            failures.Add($"Shell.Application COM：{ex.GetType().Name}: {ex.Message}");
        }

        // 通道 2：ShellExecute
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            });

            switch (WaitForExplorerWindow(path, TimeSpan.FromMilliseconds(1500)))
            {
                case true:
                    return (true, "ShellExecute");
                case null:
                    return (false, "ShellExecute（未能确认）");
            }

            failures.Add("ShellExecute：调用没报错，但 1.5 秒内没等到窗口");
        }
        catch (Exception ex)
        {
            failures.Add($"ShellExecute：{ex.GetType().Name}: {ex.Message}");
        }

        throw new InvalidOperationException(
            "两条通道都没能打开资源管理器窗口。" + Environment.NewLine + Environment.NewLine +
            "路径：" + path + Environment.NewLine + Environment.NewLine +
            "尝试记录：" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Select(f => "  · " + f)) + Environment.NewLine + Environment.NewLine +
            "先用菜单里的「复制路径」，把路径粘到资源管理器地址栏里自己进去。");
    }

    /// <summary>
    /// <c>Shell.Application</c> COM 的 <c>Explore(path)</c>：请常驻资源管理器开一个新窗口。
    /// 用反射走 IDispatch，不需要 <c>dynamic</c>，也不用 Add-Type。
    /// </summary>
    private static void ExploreViaShellCom(string path)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new PlatformNotSupportedException("系统没有注册 Shell.Application 组件");

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                throw new InvalidOperationException("创建 Shell.Application 实例失败");

            shellType.InvokeMember("Explore", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        }
        finally
        {
            ReleaseCom(shell);
        }
    }

    /// <summary>
    /// 轮询等窗口出现。资源管理器开窗口不是同步的，调完立刻查多半还查不到。
    /// </summary>
    /// <returns>
    /// <c>true</c> 找到了；<c>false</c> 查得到窗口列表但里面没有目标目录；
    /// <c>null</c> 连窗口列表都读不到 —— 这种情况没法判断，调用方按「当它开了」处理。
    /// </returns>
    private static bool? WaitForExplorerWindow(string path, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            var state = FindExplorerWindow(path);
            if (state == true) return true;
            if (Environment.TickCount64 >= deadline) return state;
            Thread.Sleep(120);
        }
    }

    /// <summary>
    /// 在 <c>ShellWindows</c> 里找有没有窗口停在 <paramref name="path"/> —— 这是「真的开了」的唯一证据。
    /// </summary>
    private static bool? FindExplorerWindow(string path)
    {
        var wanted = NormalizePath(path);
        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, shell, null);
            if (windows is null) return null;

            var windowsType = windows.GetType();
            var count = Convert.ToInt32(windowsType.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));

            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = windowsType.InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (window is null) continue;

                    if (window.GetType().InvokeMember("LocationURL", BindingFlags.GetProperty, null, window, null) is not string url)
                        continue;

                    if (string.Equals(NormalizePath(url), wanted, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // 单个窗口属性读不到就跳过，别让一个异常窗口打断判断
                }
                finally
                {
                    ReleaseCom(window);
                }
            }

            return false;
        }
        catch
        {
            // 枚举失败（没有交互桌面、shell 服务不可用…）：返回 null 表示「查不了」，不是「没开」
            return null;
        }
        finally
        {
            ReleaseCom(windows);
            ReleaseCom(shell);
        }
    }

    /// <summary>
    /// 归一化成可比较的本地路径。<c>LocationURL</c> 给的是 <c>file:///D:/a/b/</c>，
    /// 直接拿本地路径去比永远不相等 —— 斜杠方向、结尾分隔符、百分号转义、大小写都得抹平。
    /// </summary>
    private static string NormalizePath(string raw)
    {
        var local = raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(raw).LocalPath
            : raw;

        return Uri.UnescapeDataString(local).Replace('/', '\\').TrimEnd('\\');
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject)) return;

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 已经释放过/进程正在退出，无所谓
        }
    }

    /// <summary>「下载」：只把文件补齐，不启动。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DownloadAsync()
    {
        if (SelectedVersion is null)
        {
            Status = "请先选择版本";
            return;
        }

        var versionName = SelectedVersion.Name;

        IsBusy = true;
        ProgressPercent = 0;
        _downloadClock.Restart();

        try
        {
            Status = $"下载 {versionName} …";
            AppendLog($"=== 下载 {versionName}（不启动） ===");

            await _launcher.InstallOnlyAsync(versionName);

            _downloadClock.Stop();
            ProgressPercent = 100;

            // 重新扫一遍本地版本 —— 不然列表上还挂着「未装」，用户以为没下成功
            await ReloadVersionsAsync();
            SelectedVersion = Versions.FirstOrDefault(v => v.Name == versionName) ?? SelectedVersion;

            Status = $"{versionName} 下载完成，点「启动游戏」就能玩";
            AppendLog($"✓ {versionName} 下载完成，耗时 {_downloadClock.Elapsed.TotalSeconds:F0} 秒");
            AppendLog($"URL 重写 {_launcher.RewriteCount} 次 / 重试 {_launcher.RetryCount} 次");
        }
        catch (Exception ex)
        {
            _downloadClock.Stop();

            // 进度条停在中间会让人以为还在跑，实际已经结束了
            ProgressPercent = 0;
            Status = "下载失败，看下面的日志";
            AppendLog($"[错误] 下载 {versionName} 失败：{ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                AppendLog($"[内层] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 这个 exe 是什么时候构建的。
    /// </summary>
    /// <remarks>
    /// 取 exe 自己的最后写入时间。单文件发布时 <see cref="Environment.ProcessPath"/>
    /// 指向真正的 exe（不是 %TEMP% 解压目录），所以拿得到。
    /// 用途只有一个：用户说「没有啊」的时候，看一眼日志就知道他跑的是不是新包。
    /// </remarks>
    private static string BuildStamp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            // 拿不到就算了，日志少一个字段不影响任何功能
        }

        return "未知";
    }

    /// <summary>只启动已经下载好的版本，不碰下载。</summary>
    /// <remarks>
    /// 没下载时不去偷偷下载 —— 用户点的是「启动」，结果等了两分钟进度条，
    /// 会以为按钮点错了。直接说清楚该先点哪个。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LaunchAsync()
    {
        if (SelectedVersion is null)
        {
            Status = "请先选择版本";
            return;
        }

        if (string.IsNullOrWhiteSpace(Username))
            Username = "Player";

        var versionName = SelectedVersion.Name;
        var ram = (int)MemoryMb;

        if (!SelectedVersion.IsInstalled)
        {
            Status = "这个版本还没下载";
            AppendLog($"[提示] 「{versionName}」还没下载到本地，先点「下载」把文件补齐，再点「启动游戏」。");
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        _downloadClock.Restart();

        try
        {
            Status = $"正在启动 {versionName} …";

            // 启动前先快速体检一次（只看存在性和大小，几百毫秒）。
            // 坏文件导致的崩溃，报错都指向很奇怪的地方（zip file is empty / can't be written），
            // 让人根本想不到是「文件坏了」。在这里拦住，比事后看崩溃日志划算得多。
            if (!await EnsureFilesIntactAsync(versionName)) return;

            // 模组体检：缺前置 / 同一个包装两份 / 加载器放错 —— 这三类必崩，
            // 而且游戏给的报错完全看不出是模组的问题。用户点「取消」就不往下走了。
            if (!await RunModPreflightAsync(versionName)) return;

            // 账号：微软账号令牌过期就先刷新。刷不动会问要不要改离线启动，
            // 用户拒绝就整体取消 —— 不静默降级（理由见 ResolveLaunchAccountAsync 注释）。
            var (accountOk, account) = await ResolveLaunchAccountAsync();
            if (!accountOk) return;

            var playerName = account?.Name ?? Username;

            AppendLog($"=== 启动：{versionName} / 账号 {playerName}" +
                      $"{(account is null ? "（离线）" : "（微软正版）")} / -Xmx{ram}M ===");

            Status = $"正在启动 {versionName} …";
            var process = await _launcher.BuildProcessOnlyAsync(versionName, playerName, ram, 0, default, account);

            // 交给 service 启动，它会把 stdout/stderr 接进日志 ——
            // JVM 拒绝启动时那行报错只走 stderr，不接就等于没发生
            var startTime = DateTime.Now;
            _launcher.StartGameProcess(process);

            // 盯着它退出：非 0 退出码就当崩溃，自动跑一遍分析。
            // 用户不用自己去 crash-reports 里翻（那堆文件名和堆栈正是最劝退的地方）
            WatchGameProcess(process, versionName, startTime);

            _downloadClock.Stop();
            ProgressPercent = 100;
            Status = $"已启动 {versionName}（PID {process.Id}）";

            AppendLog($"游戏进程已拉起：PID={process.Id}");
            AppendLog($"启动耗时 {_downloadClock.Elapsed.TotalSeconds:F1} 秒");
        }
        catch (Exception ex)
        {
            _downloadClock.Stop();
            Status = "启动失败";
            AppendLog($"[错误] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                AppendLog($"[内层] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─────────────────────── 导入本地整合包 ───────────────────────

    /// <summary>
    /// 选一个本地整合包文件，装成一个新实例。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三种格式都由 <see cref="ModpackInstaller.InstallFromArchiveAsync"/> 自己识别：
    /// Modrinth 的 <c>.mrpack</c>、CurseForge 的标准 <c>.zip</c>、
    /// 以及别人手动打包的「懒人包」（里面直接是 <c>mods</c> / <c>config</c>，没有清单文件）。
    /// 懒人包的版本和加载器从模组 jar 里反推，推不出来时才问你一句
    /// （见 <see cref="ImportArchiveWithFallbackAsync"/>）。
    /// </para>
    /// <para>
    /// <b>为什么必须新建实例</b>：整合包钉死了游戏版本和加载器版本，
    /// 塞进现有实例会跟已经装的东西打架。
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanImportModpack))]
    private async Task ImportModpackAsync()
    {
        if (IsBusy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip"
                     + "|Modrinth 整合包 (*.mrpack)|*.mrpack"
                     + "|CurseForge 整合包 (*.zip)|*.zip"
                     + "|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true) return;

        var packPath = dialog.FileName;

        var owner = Application.Current?.MainWindow;

        var name = TextInputWindow.Ask(
            owner,
            "导入整合包",
            "给这个整合包新建一个实例（跟现有实例完全独立，存档和模组互不影响）：",
            SuggestInstanceNameFrom(packPath),
            "不能有 \\ / : * ? \" < > | 这些字符；不能以下划线或点开头",
            validate: InstanceManager.CheckName);

        if (string.IsNullOrWhiteSpace(name)) return;

        name = EnsureUniqueInstanceName(name.Trim());

        IsBusy = true;
        Status = "正在读取整合包…";
        ProgressPercent = 0;

        var reporter = new Progress<ModpackProgress>(p =>
        {
            Status = p.IsDeterminate ? $"{p.Stage}（{p.Fraction * 100:F0}%）" : p.Stage;
            if (p.IsDeterminate) ProgressPercent = p.Fraction * 100;
        });

        try
        {
            AppendLog($"[整合包] 开始导入：{packPath}");
            AppendLog($"[整合包] 新实例名：{name}");

            var result = await ImportArchiveWithFallbackAsync(packPath, name, reporter, owner)
                .ConfigureAwait(true);

            if (result is null)
            {
                Status = "导入已取消（这个包缺版本信息）";
                AppendLog("[整合包] 用户没填版本，已取消，没有留下任何实例");
                return;
            }

            foreach (var warning in result.Warnings)
                AppendLog($"   · {warning}");

            AppendLog($"[整合包] {result.Summary}");

            // 刷新实例下拉并切到新装的那个，不然用户还得自己去下拉里找
            ReloadInstances(result.InstanceName);

            ProgressPercent = 100;
            Status = $"已装好：实例「{result.InstanceName}」";

            MessageBox.Show(
                result.Summary + "\n\n"
                + $"实例目录：\n{result.InstanceDirectory}\n\n"
                + (result.Warnings.Count == 0
                    ? "现在可以点「启动游戏」了。"
                    : $"有 {result.Warnings.Count} 条提示，详见日志。"),
                "整合包装好了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Status = "导入已取消";
        }
        catch (Exception ex)
        {
            Status = "整合包导入失败";
            AppendLog($"[整合包] 失败：{ex.GetType().Name}: {ex.Message}");

            MessageBox.Show(
                $"导入失败：\n\n{ex.GetType().Name}: {ex.Message}\n\n"
                + "如果实例已经建了一半，可以在实例下拉里找到后手动删掉重来。",
                "整合包导入失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImportModpack() => !IsBusy;

    /// <summary>
    /// 装压缩包，碰上「懒人包缺版本信息」时问用户补一句再重试。
    /// </summary>
    /// <returns>装好的结果；用户不肯补版本时返回 <c>null</c>（此时一个实例都没建）。</returns>
    /// <remarks>
    /// <para>
    /// 懒人包（里面直接是 <c>mods</c> / <c>config</c>）没有清单文件，版本只能从 jar 里反推。
    /// 反推不出来时 <see cref="ModpackInstaller"/> 会在<b>建实例之前</b>抛
    /// <see cref="ModpackNeedsInfoException"/>，这里接住、问一句、带着答案重来 ——
    /// 所以取消的话磁盘上是干净的，不会剩半个实例。
    /// </para>
    /// <para>
    /// 只问一次：用户填了还失败，那就是别的原因（版本号填错会由装原版时抛出来），
    /// 不能变成「一直弹窗」。
    /// </para>
    /// </remarks>
    private async Task<ModpackInstallResult?> ImportArchiveWithFallbackAsync(
        string packPath,
        string instanceName,
        IProgress<ModpackProgress> reporter,
        Window? owner)
    {
        try
        {
            return await _modpackInstaller
                .InstallFromArchiveAsync(packPath, instanceName, reporter, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (ModpackNeedsInfoException need)
        {
            AppendLog($"[整合包] {need.Message}");
            AppendLog("[整合包] 这是懒人包（没有清单文件），需要你补一个版本号");

            var version = TextInputWindow.Ask(
                owner,
                "这个包没写版本",
                need.Message + "\n\n请填这个整合包对应的 Minecraft 版本"
                + "（下载页、群公告、压缩包名字里一般都有）：",
                string.Empty,
                "只填版本号，例如 1.21.4。不要写 fabric / forge / neoforge",
                validate: ValidateMinecraftVersion);

            if (string.IsNullOrWhiteSpace(version))
                return null;

            var trimmed = version.Trim();
            AppendLog($"[整合包] 按你给的 {trimmed} 重新开始装（之前没建过实例，不用清理）");

            return await _modpackInstaller
                .InstallFromArchiveAsync(packPath, instanceName, reporter, CancellationToken.None, trimmed)
                .ConfigureAwait(true);
        }
    }

    /// <summary>只放行形如 <c>1.21.4</c> / <c>1.20</c> 的版本号。</summary>
    /// <remarks>
    /// 拦在前面是有意义的：写错版本号要到「下载原版」那一步才炸，报错是
    /// 「找不到版本 xxx」这种，用户不容易联想到是自己填错了。
    /// </remarks>
    private static string? ValidateMinecraftVersion(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0) return "版本号不能空着";

        if (!Regex.IsMatch(trimmed, @"^1\.\d{1,2}(\.\d{1,2})?$"))
            return "只填版本号，形如 1.21.4（带 fabric / forge 那些字会装不上）";

        return null;
    }

    /// <summary>从文件名猜实例名（去扩展名、去非法字符、截断）。</summary>
    private static string SuggestInstanceNameFrom(string packPath)
    {
        var raw = Path.GetFileNameWithoutExtension(packPath);

        foreach (var invalid in Path.GetInvalidFileNameChars())
            raw = raw.Replace(invalid, '_');

        raw = raw.Trim().TrimStart('.', '_');
        if (raw.Length > 24) raw = raw[..24].Trim();

        return string.IsNullOrWhiteSpace(raw) ? "整合包" : raw;
    }

    /// <summary>重名就自动加序号，别让用户为了个名字反复试。</summary>
    private string EnsureUniqueInstanceName(string name)
    {
        var existing = new HashSet<string>(
            _instances.Scan().Select(i => i.Name), StringComparer.OrdinalIgnoreCase);

        if (existing.Add(name)) return name;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{name}-{i}";
            if (existing.Add(candidate)) return candidate;
        }

        return $"{name}-{DateTime.Now:HHmmss}";
    }

    // ─────────────────────── 崩溃分析 ───────────────────────

    /// <summary>手动分析当前版本最近一次崩溃。</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyzeCrash))]
    private void AnalyzeCrash()
    {
        if (SelectedVersion is null) return;
        ShowCrashAnalysis(SelectedVersion.Name, auto: false);
    }

    private bool CanAnalyzeCrash() => !IsBusy && SelectedVersion is not null;

    /// <summary>
    /// 给游戏进程挂退出监听，异常退出就自动分析。
    /// </summary>
    /// <remarks>
    /// <b>为什么值得监听</b>：模组崩溃时游戏窗口一闪就没，用户的第一反应是「再点一次」，
    /// 而真正的原因全在 <c>crash-reports</c> 里那几个 tst 中。自动把结论摆到眼前，
    /// 比让人自己去翻日志有用得多。
    /// </remarks>
    private void WatchGameProcess(Process process, string versionName, DateTime startTime)
    {
        try
        {
            process.EnableRaisingEvents = true;

            // Exited 是线程池回调，界面操作必须回 Dispatcher
            process.Exited += (_, _) => _dispatcher.BeginInvoke(() => OnGameExited(process, versionName, startTime));
        }
        catch (Exception ex)
        {
            // 监听失败不影响游戏本身，只是不能自动分析
            AppendLog($"[崩溃分析] 没法监听游戏退出（{ex.GetType().Name}: {ex.Message}），可在启动后点「崩溃分析」手动看");
        }
    }

    private void OnGameExited(Process process, string versionName, DateTime startTime)
    {
        int exitCode;

        try
        {
            exitCode = process.ExitCode;
        }
        catch
        {
            return; // 进程对象已经拿不到退出码，不硬猜
        }

        if (exitCode == 0)
        {
            AppendLog($"[崩溃分析] 游戏正常退出（{versionName}）");
            return;
        }

        var ranFor = DateTime.Now - startTime;

        // 「秒退」= JVM 自己就没起来。这时候读崩溃日志毫无意义 ——
        // logs/latest.log 还是上一次的，分析出来的结论跟这次失败没关系（实测被误导过一次）。
        // 真正的原因在 stderr 里，比如 "Multiple garbage collectors selected"。
        if (ranFor < TimeSpan.FromSeconds(15) && _launcher.LastGameStandardError.Count > 0)
        {
            var stderr = _launcher.LastGameStandardError;

            AppendLog($"[崩溃分析] 游戏没起来就退了（{ranFor.TotalSeconds:F1} 秒，exit code {exitCode}）");
            foreach (var line in stderr.Take(8)) AppendLog($"    · {line}");

            Status = "游戏没能启动，原因见日志";
            MessageBox.Show(
                $"游戏刚启动就退出了（{ranFor.TotalSeconds:F1} 秒）。这通常不是游戏崩溃，"
                + "而是 JVM 参数或环境有问题 —— 游戏根本没跑起来。"
                + Environment.NewLine + Environment.NewLine
                + "启动器抓到的原始报错："
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, stderr.Take(10))
                + Environment.NewLine + Environment.NewLine
                + "（完整内容在启动器日志里，搜「游戏错误」）",
                "游戏没能启动",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        AppendLog($"[崩溃分析] 游戏异常退出（exit code {exitCode}），开始读日志…");
        ShowCrashAnalysis(versionName, auto: true);
    }

    /// <summary>
    /// 分析并展示结论。窗口出任何问题都不能影响主界面（照 IntegrityWindow 的教训包起来）。
    /// </summary>
    private void ShowCrashAnalysis(string versionName, bool auto)
    {
        try
        {
            var gameDir = _launcher.GetGameDir(versionName);
            var analysis = CrashAnalyzer.Analyze(gameDir);

            if (analysis is null)
            {
                AppendLog($"[崩溃分析] 在 {gameDir} 下没找到崩溃报告或 latest.log");

                if (!auto)
                {
                    MessageBox.Show(
                        "这个版本还没有崩溃记录（crash-reports 和 logs/latest.log 都没有）。"
                        + Environment.NewLine + Environment.NewLine
                        + $"看的目录是：{Environment.NewLine}{gameDir}",
                        "没有可分析的日志",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            AppendLog($"[崩溃分析] {analysis.BriefSummary}");

            foreach (var finding in analysis.Findings.Take(3))
                AppendLog($"   · 【{finding.Severity}】{finding.Title}：{finding.Explanation}");

            var viewModel = new CrashViewModel(analysis, versionName, AppendLog, _launcher.InstanceRoot);

            new CrashWindow(viewModel)
            {
                Owner = Application.Current?.MainWindow,
            }.Show();
        }
        catch (Exception ex)
        {
            AppendLog($"[崩溃分析] 分析失败：{ex.GetType().Name}: {ex.Message}");

            if (!auto)
            {
                MessageBox.Show(
                    $"分析崩溃时出错了：{Environment.NewLine}{ex.GetType().Name}: {ex.Message}",
                    "崩溃分析失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// 打开「校验文件」窗口：深度校验当前版本，列出问题并一键修复。
    /// </summary>
    /// <remarks>
    /// 窗口一律包 try/catch —— 上次校验窗口因为一个没定义的转换器直接
    /// <c>XamlParseException</c>，点一下就整个进程退出，日志一个字都没留。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCheckFiles))]
    private void CheckFiles()
    {
        var entry = SelectedVersion;
        if (entry is null) return;

        try
        {
            var viewModel = new IntegrityViewModel(_integrity, _launcher, entry.Name, AppendLog);

            new Views.IntegrityWindow(viewModel)
            {
                Owner = Application.Current?.MainWindow,
            }.Show();

            AppendLog($"[校验] 已打开校验窗口：{entry.Name}");
        }
        catch (Exception ex)
        {
            Status = "校验窗口打不开";
            AppendLog($"[校验] 打不开校验窗口：{ex.GetType().Name}: {ex.Message}");

            MessageBox.Show(
                $"校验窗口打不开：{Environment.NewLine}{ex.GetType().Name}: {ex.Message}",
                "打不开",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool CanCheckFiles() => !IsBusy && SelectedVersion is { IsInstalled: true };

    // ─────────────────────── 存档种子 ───────────────────────

    /// <summary>当前版本下的存档列表（工具页「种子查询」用）。</summary>
    public ObservableCollection<SaveInfo> Saves { get; } = new();

    [ObservableProperty]
    private SaveInfo? _selectedSave;

    partial void OnSelectedSaveChanged(SaveInfo? value)
    {
        OnPropertyChanged(nameof(SelectedSeedText));
        OnPropertyChanged(nameof(HasSelectedSeed));
        OnPropertyChanged(nameof(SaveHint));

        OpenChunkbaseCommand.NotifyCanExecuteChanged();
        CopySeedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>种子的可复制文本。</summary>
    public string SelectedSeedText => SelectedSave?.Seed is { } seed ? seed.ToString() : "—";

    /// <summary>有没有能用的种子（决定「复制 / 打开」两个按钮能不能点）。</summary>
    public bool HasSelectedSeed => SelectedSave?.Seed is not null;

    /// <summary>存档那一行下面的说明文字：把「为什么点不了」直接写出来。</summary>
    public string SaveHint => SelectedSave switch
    {
        // 没装完的版本本来就不该有存档，别用「进游戏新建一个世界」误导人
        null when SelectedVersion is { IsInstalled: false }
            => "这个版本还没下载到本地。等装完、进游戏一次，存档目录才会出现。",
        null => "这个版本下还没有存档。进游戏新建一个世界，回来自动就看见了。",
        { Seed: null } => "读到存档了，但种子取不出来 —— level.dat 可能正被游戏占着，或者文件损坏了。",
        { McVersion: null } => "读到种子了，但存档没记游戏版本，Chunkbase 打开后要自己选一下版本。",
        { McVersion: var version } when ChunkbaseLink.PlatformFor(version) is null
            => $"存档版本 {version}，Chunkbase 认不出这个版本号，打开后在地图上手动选一下。",
        _ => $"存档版本 {SelectedSave.McVersion}，链接里已经带上对应版本。",
    };

    /// <summary>重新扫一遍当前版本的存档目录。</summary>
    [RelayCommand]
    private void ReloadSaves()
    {
        if (SelectedVersion is not { } version)
        {
            Saves.Clear();
            SelectedSave = null;
            return;
        }

        var previous = SelectedSave?.Folder;

        try
        {
            var directory = _folders.Resolve(version.Name, GameFolderKind.Saves).Path;
            var found = WorldSeedReader.ListSaves(directory);

            Saves.Clear();
            foreach (var save in found) Saves.Add(save);

            // 刷新一下不该把用户刚选的那条踢掉
            SelectedSave = Saves.FirstOrDefault(s => s.Folder == previous) ?? Saves.FirstOrDefault();

            AppendLog($"[种子] {version.Name} 下找到 {Saves.Count} 个存档");
        }
        catch (DirectoryNotFoundException)
        {
            // 版本还没装完，本来就没有存档目录 —— 不是错误，
            // 界面上 SaveHint 会说明「还没下载到本地」。
            Saves.Clear();
            SelectedSave = null;
        }
        catch (Exception ex)
        {
            AppendLog($"[种子] 扫存档目录失败：{ex.GetType().Name}: {ex.Message}");
            Saves.Clear();
            SelectedSave = null;
        }
    }

    /// <summary>把种子复制到剪贴板。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedSeed))]
    private void CopySeed()
    {
        if (SelectedSave?.Seed is not { } seed) return;

        try
        {
            Clipboard.SetText(seed.ToString());
            Status = $"种子 {seed} 已复制";
        }
        catch (Exception ex)
        {
            AppendLog($"[种子] 写剪贴板失败：{ex.GetType().Name}: {ex.Message}");
            Status = "复制失败，看看日志";
        }
    }

    /// <summary>上一次点「在 Chunkbase 打开」用的链接（失败时直接摊在界面上给人复制）。</summary>
    [ObservableProperty]
    private string _lastChunkbaseUrl = string.Empty;

    /// <summary>自动打开失败时，才把链接那行显示出来。</summary>
    [ObservableProperty]
    private bool _showChunkbaseUrl;

    /// <summary>用默认浏览器打开 Chunkbase 的种子地图。</summary>
    /// <remarks>
    /// 这里必须等浏览器真的起来才敢说「已打开」——
    /// 之前 <c>Process.Start</c> 返回个进程对象就当成功，结果浏览器秒退，
    /// 界面上写着已打开、屏幕上什么都没有。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelectedSeed))]
    private async Task OpenChunkbaseAsync()
    {
        if (SelectedSave?.Seed is not { } seed) return;

        // 只开站点，不往 hash 里塞种子。
        // 带 #seed= 的链接会让页面在地图初始化那一步多吃一轮计算，首屏本来就慢，
        // 很容易看着就是一片白。种子直接放剪贴板，到页面 Ctrl+V 更快也更稳。
        var url = ChunkbaseLink.SiteUrl;

        LastChunkbaseUrl = url;
        ShowChunkbaseUrl = false;
        Status = "正在打开 Chunkbase…";

        var seedCopied = false;
        try
        {
            Clipboard.SetText(seed.ToString());
            seedCopied = true;
        }
        catch (Exception ex)
        {
            AppendLog($"[种子] 写剪贴板失败：{ex.GetType().Name}: {ex.Message}");
        }

        // 沙箱（低完整性）里浏览器写不了自己的配置目录，得给个我们能写的地方
        var profileRoot = Path.Combine(LauncherPaths.CacheRoot, "browser");

        var result = await UrlLauncher.TryOpenAsync(url, profileRoot);

        if (result.Success)
        {
            AppendLog($"[种子] 打开 Chunkbase（{result.Channel}），种子 {seed} " +
                      (seedCopied ? "已复制" : "没复制上") + $"：{url}");

            Status = seedCopied
                ? $"已打开 Chunkbase，种子 {seed} 在剪贴板里，粘到页面的 Seed 框就行"
                : $"已打开 Chunkbase，种子是 {seed}，手动输一下";
            return;
        }

        // 拉不起来就老实说，同时把链接塞进剪贴板并摊在界面上
        AppendLog($"[种子] 打不开浏览器：{result.Channel}");

        var copied = false;
        try
        {
            Clipboard.SetText(url);
            copied = true;
        }
        catch (Exception ex)
        {
            AppendLog($"[种子] 写剪贴板也失败：{ex.GetType().Name}: {ex.Message}");
        }

        ShowChunkbaseUrl = true;
        Status = copied
            ? "没能自动打开浏览器，链接已复制，也可从下面那行手动复制"
            : "没能自动打开浏览器，链接在下面那行，手动复制一下";
    }

    // ─────────────────────── 模组汉化 ───────────────────────

    /// <summary>当前版本下扫到的模组（工具页「模组汉化」用）。</summary>
    public ObservableCollection<ModLocaleItem> Mods { get; } = new();

    /// <summary>DeepSeek 的 API Key。改动即落盘。</summary>
    [ObservableProperty]
    private string _deepSeekApiKey = string.Empty;

    /// <summary>
    /// DeepSeek 模型名。空 = 用代码里的默认值（<c>deepseek-flash</c>）。
    /// </summary>
    /// <remarks>
    /// 官方改过名的前科（<c>deepseek-chat</c> 弃用、<c>deepseek-v4-flash</c> 退役），
    /// 所以放出来让用户自己改，不用等发新版本。
    /// </remarks>
    [ObservableProperty]
    private string _aiModelName = string.Empty;

    /// <summary>模型名输入框的占位提示（把当前实际生效的名字显示出来）。</summary>
    public string AiModelPlaceholder =>
        $"留空用默认：{Core.Translation.DeepSeekTranslator.DefaultModel}";

    /// <summary>模组汉化总开关。</summary>
    [ObservableProperty]
    private bool _aiLocalizationEnabled;

    /// <summary>启动前是否做模组体检。默认开。</summary>
    [ObservableProperty]
    private bool _modPreflightEnabled = true;

    /// <summary>模组管理里要不要联网查中文名。默认开。</summary>
    [ObservableProperty]
    private bool _lookupModChineseNames = true;

    /// <summary>正在翻译（决定按钮和开关能不能动）。</summary>
    [ObservableProperty]
    private bool _isLocalizing;

    /// <summary>汉化卡片底部那一行状态文字。</summary>
    [ObservableProperty]
    private string _localizeStatus = "点「扫描模组」列出当前版本里能翻的 jar";

    partial void OnDeepSeekApiKeyChanged(string value)
    {
        LauncherSettings.SaveDeepSeekApiKey(_launcher.InstanceRoot, value);

        // 界面绑的是 ApiKeyDisplay，key 变了得让它重算一次（否则启动时读到值也不显示）
        OnPropertyChanged(nameof(ApiKeyDisplay));
    }

    /// <summary>
    /// API Key 输入框当前显示的内容。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不用 PasswordBox</b>：WPF-UI 那个自带睁眼按钮的控件，<c>Password</c>
    /// 虽然声明成了依赖属性，但实测把视图模型里的值灌不进去 —— 界面永远显示占位符，
    /// 看着像「key 丢了」。与其和它的内部同步逻辑较劲，不如一个普通 TextBox 自己管。
    /// </para>
    /// <para>
    /// <b>打码时不显示真实长度</b>：固定 12 个圆点。真按长度画，等于把 key 的长度
    /// 告诉旁边看屏幕的人。
    /// </para>
    /// <para>
    /// <b>打码时输入框是只读的</b>（<see cref="IsApiKeyEditable"/>），防止用户在掩码
    /// 状态下敲字，把一串圆点写进配置。
    /// </para>
    /// </remarks>
    public string ApiKeyDisplay
    {
        get => IsApiKeyVisible
            ? DeepSeekApiKey
            : (string.IsNullOrEmpty(DeepSeekApiKey) ? string.Empty : "sk-" + new string('•', 12));
        set
        {
            // 只读态下编辑器仍可能推送旧值回来，这里挡一道
            if (!IsApiKeyVisible) return;
            if (value == DeepSeekApiKey) return;

            DeepSeekApiKey = value;
        }
    }

    /// <summary>API Key 是否显示明文。</summary>
    [ObservableProperty]
    private bool _isApiKeyVisible;

    /// <summary>打码时禁止编辑，避免把圆点写进配置。</summary>
    public bool IsApiKeyEditable => IsApiKeyVisible;

    /// <summary>睁眼按钮上的图标（Segoe MDL2）：睁眼 = 点了看明文，闭眼 = 点了打码。</summary>
    public string ApiKeyEyeGlyph => IsApiKeyVisible ? "\uED1A" : "\uE7B3";

    partial void OnIsApiKeyVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ApiKeyDisplay));
        OnPropertyChanged(nameof(IsApiKeyEditable));
        OnPropertyChanged(nameof(ApiKeyEyeGlyph));
    }

    /// <summary>点小眼睛：明文 / 打码切换。</summary>
    [RelayCommand]
    private void ToggleApiKeyVisibility() => IsApiKeyVisible = !IsApiKeyVisible;

    partial void OnAiModelNameChanged(string value)
        => LauncherSettings.SaveAiModel(_launcher.InstanceRoot, value);

    partial void OnAiLocalizationEnabledChanged(bool value)
        => LauncherSettings.SaveAiLocalization(_launcher.InstanceRoot, value);

    partial void OnModPreflightEnabledChanged(bool value)
        => LauncherSettings.SaveModPreflight(_launcher.InstanceRoot, value);

    partial void OnLookupModChineseNamesChanged(bool value)
        => LauncherSettings.SaveLookupModChineseNames(_launcher.InstanceRoot, value);

    /// <summary>扫一遍当前版本的 mods 目录。</summary>
    [RelayCommand]
    private void ScanMods()
    {
        if (SelectedVersion is not { } version)
        {
            Mods.Clear();
            LocalizeStatus = "先在版本页选一个版本";
            return;
        }

        try
        {
            var directory = _folders.Resolve(version.Name, GameFolderKind.Mods).Path;

            // 资源包必须和 mods 同一个 gameDir：版本隔离关掉时 gameDir 是实例根，
            // 写到 versions/<名字>/ 下游戏根本看不见
            var packsDirectory = Path.Combine(_launcher.GetGameDir(version.Name), "resourcepacks");

            Mods.Clear();
            foreach (var info in ModLocalizer.Scan(directory, packsDirectory, (_, message) => AppendLog(message)))
            {
                Mods.Add(new ModLocaleItem(info));
            }

            var needWork = Mods.Count(m => m.NeedsWork);
            var done = Mods.Count(m => m.LocalizedByPack);

            LocalizeStatus = Mods.Count == 0
                ? "这个版本没有带界面语言文件的模组"
                : done > 0
                    ? $"扫到 {Mods.Count} 个模组，{done} 个已汉化，{needWork} 个还有英文要翻"
                    : $"扫到 {Mods.Count} 个模组，其中 {needWork} 个有英文要翻";

            AppendLog($"[汉化] 扫描 {version.Name}：{Mods.Count} 个带语言文件的模组，" +
                      $"{done} 个已有汉化包，{needWork} 个待翻");
        }
        catch (Exception ex)
        {
            Mods.Clear();
            LocalizeStatus = $"扫描失败：{ex.Message}";
            AppendLog($"[汉化] 扫描失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>汉化单个模组，生成资源包。</summary>
    [RelayCommand]
    private async Task LocalizeModAsync(ModLocaleItem? item)
    {
        if (item is null || IsLocalizing) return;

        if (SelectedVersion is not { } version)
        {
            LocalizeStatus = "先在版本页选一个版本";
            return;
        }

        var apiKey = LauncherSettings.ResolveDeepSeekApiKey(_launcher.InstanceRoot);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LocalizeStatus = "先去设置页填 DeepSeek API Key";
            AppendLog("[汉化] 没有 API Key，没法翻");
            return;
        }

        item.IsBusy = true;
        IsLocalizing = true;

        try
        {
            var cacheFile = LauncherPaths.TranslationCacheFile;

            // 模型名留空就让翻译器用默认值（deepseek-flash），填了就听用户的
            var model = string.IsNullOrWhiteSpace(AiModelName) ? null : AiModelName.Trim();
            using var translator = new DeepSeekTranslator(apiKey, model);
            var localizer = new ModLocalizer(translator, cacheFile);
            localizer.Log += (_, message) => AppendLog(message);

            var versionDirectory = _folders.Resolve(version.Name, GameFolderKind.Version).Path;
            var packsDirectory = Path.Combine(_launcher.GetGameDir(version.Name), "resourcepacks");
            var packFormat = ModLocalizer.ResolvePackFormat(versionDirectory, version.Name);

            // 已有的汉化包要传进去：一是别重复花钱翻，二是重新生成的包得是完整包
            var existingPacks = await Task.Run(() => ModLocalizer.LocalizationPacks.Load(packsDirectory))
                .ConfigureAwait(true);

            AppendLog($"[汉化] 开始翻 {item.DisplayName}（资源包格式 {packFormat.Major}.{packFormat.Minor}）");

            var progress = new Progress<string>(text => LocalizeStatus = $"{item.DisplayName}：{text}");
            var result = await localizer.LocalizeAsync(item.Info, packsDirectory, packFormat, progress,
                cancellationToken: default, packs: existingPacks);

            if (!result.Success)
            {
                item.StatusText = $"失败：{result.Error}";
                LocalizeStatus = $"{item.DisplayName} 汉化失败：{result.Error}";
                AppendLog($"[汉化] {item.DisplayName} 失败：{result.Error}");
                return;
            }

            item.NeedsWork = false;
            item.LocalizedByPack = true;
            item.StatusText = result.PackPath is { } packPath
                ? $"已汉化：新翻 {result.Translated} 条，复用 {result.Reused} 条 → {Path.GetFileName(packPath)}（下次启动游戏自动生效）"
                : $"已汉化：新翻 {result.Translated} 条，复用 {result.Reused} 条";
            LocalizeStatus = result.Failed > 0
                ? $"{item.DisplayName} 完成，但有 {result.Failed} 条没翻成（详情看日志）"
                : $"{item.DisplayName} 完成：新翻 {result.Translated} 条，复用缓存 {result.Reused} 条，下次启动游戏自动生效";

            if (result.Warnings.Count > 0)
            {
                AppendLog($"[汉化] {item.DisplayName} 有 {result.Warnings.Count} 条提醒：");
                foreach (var warning in result.Warnings.Take(20)) AppendLog($"    · {warning}");
            }

            // 顺手挂进 options.txt：这样即使玩家绕过启动器直接开游戏，汉化也已经生效。
            // 启动流程里还会再做一次（游戏退出会重写这个文件），两边都做才稳。
            try
            {
                if (GameOptionsLocalizer.EnsureResourcePacks(_launcher.GetGameDir(version.Name)) is { } change)
                    AppendLog($"[汉化] {change}");
            }
            catch (Exception ex)
            {
                AppendLog($"[汉化] 自动启用资源包失败（启动游戏时还会再试）：{ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            item.StatusText = $"失败：{ex.Message}";
            LocalizeStatus = $"{item.DisplayName} 汉化出错";
            AppendLog($"[汉化] {item.DisplayName} 出错：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
            IsLocalizing = false;
        }
    }

    // ─────────────────────── 文件完整性 ───────────────────────

    /// <summary>
    /// 启动前快速体检。返回 <c>false</c> 表示「别启动」。
    /// </summary>
    /// <remarks>
    /// 只跑 <see cref="IntegrityDepth.Quick"/>：看存在性和大小，几千个文件几百毫秒。
    /// 算 sha1 的深度校验要读几个 GB，放在启动路径上会让人以为卡住了，
    /// 那个留给界面上的「校验文件」按钮。
    /// </remarks>
    private async Task<bool> EnsureFilesIntactAsync(string versionName)
    {
        Status = "正在检查文件完整…";

        IntegrityReport report;
        try
        {
            report = await _integrity.CheckAsync(versionName, IntegrityDepth.Quick);
        }
        catch (Exception ex)
        {
            // 校验本身出错不该拦住启动
            AppendLog($"[校验] 跳过（{ex.GetType().Name}: {ex.Message}）");
            return true;
        }

        AppendLog($"[校验] {report.Summary}");

        if (report.IsHealthy) return true;

        foreach (var issue in report.Issues.Take(10))
            AppendLog($"   · {issue}");

        var preview = string.Join("\n", report.Issues.Take(6).Select(i => "· " + i));
        if (report.ProblemCount > 6)
            preview += $"\n… 另外还有 {report.ProblemCount - 6} 处";

        var answer = MessageBox.Show(
            $"启动前检查发现 {report.ProblemCount} 处文件问题：\n\n{preview}\n\n"
            + "自动修复？会删掉损坏的文件再重新下载（缺多少下多少，已有的不会重下）。",
            "文件不完整",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            AppendLog("[校验] 用户选择先不修复，继续启动");
            return true;
        }

        return await RepairFilesAsync(report);
    }

    // ─────────────────────── 启动前模组体检 ───────────────────────

    /// <summary>
    /// 启动前的模组体检。返回 <c>false</c> 表示「别启动」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只查三类<b>必然出问题</b>的：缺必需前置、同一个 modId 装了两份、模组和加载器不匹配。
    /// 版本范围对不上只当提醒 —— 解析器看错一句范围就把人挡在门外，比不查还糟。
    /// </para>
    /// <para>
    /// 原版版本直接跳过：没模组可查，别让人白等几百毫秒。
    /// </para>
    /// </remarks>
    private async Task<bool> RunModPreflightAsync(string versionName)
    {
        if (!LauncherSettings.ResolveModPreflight(_launcher.InstanceRoot)) return true;
        if (SelectedVersion is not { } version) return true;
        if (version.ModLoader == ModLoaderKind.Vanilla) return true;

        string modsDirectory;
        try
        {
            // Resolve 对 mods 这类子目录会顺手建出来，所以「一个模组都没装」也能拿到路径
            modsDirectory = _folders.Resolve(versionName, GameFolderKind.Mods).Path;
        }
        catch (Exception ex)
        {
            AppendLog($"[预检] 找不到模组目录，跳过（{ex.GetType().Name}）");
            return true;
        }

        Status = "正在检查模组…";

        ModPreflightReport report;
        try
        {
            report = await ModPreflight.ScanAsync(modsDirectory, version.ModLoader, AppendLog);
        }
        catch (Exception ex)
        {
            // 体检自己坏了不该拦住进游戏
            AppendLog($"[预检] 跳过（{ex.GetType().Name}: {ex.Message}）");
            return true;
        }

        AppendLog($"[预检] {report.Summary}");

        if (!report.HasProblems) return true;

        foreach (var issue in report.Issues)
            AppendLog($"   [{(issue.IsError ? "错误" : "提醒")}] {issue.Title}");

        var window = new PreflightWindow(report)
        {
            Owner = Application.Current?.MainWindow,
        };

        if (window.ShowDialog() == true)
        {
            AppendLog("[预检] 用户选择仍然启动");
            return true;
        }

        AppendLog("[预检] 用户取消了启动");
        Status = "已取消启动";
        return false;
    }

    /// <summary>删掉坏文件 + 重新下载 + 复查。返回 <c>false</c> 表示修到一半就失败了。</summary>
    private async Task<bool> RepairFilesAsync(IntegrityReport report)
    {
        var removed = _integrity.DeleteBrokenFiles(report);
        AppendLog($"[修复] 删掉 {removed} 个损坏文件，开始重新下载…");
        Status = "正在修复文件…";

        try
        {
            await _launcher.InstallAsync(report.VersionName);
        }
        catch (Exception ex)
        {
            AppendLog($"[修复] 下载失败：{ex.GetType().Name}: {ex.Message}");
            Status = "修复失败";
            return false;
        }

        var again = await _integrity.CheckAsync(report.VersionName, IntegrityDepth.Quick);
        AppendLog($"[修复] 复查：{again.Summary}");

        if (again.IsHealthy) return true;

        // 修不掉的通常只剩模组（没有「期望哈希」，也没法自动重下）
        MessageBox.Show(
            $"还有 {again.ProblemCount} 处问题，这些自动修不了（一般是模组文件）：\n\n"
            + string.Join("\n", again.Issues.Take(6)),
            "没完全修好",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        return true;   // 剩下的交给用户判断要不要继续启动
    }

    // ─────────────────────── 加载器版本查询 ───────────────────────

    private void CancelLoaderVersionQuery()
    {
        var cts = _loaderVersionCts;
        _loaderVersionCts = null;
        cts?.Cancel();
        cts?.Dispose();
        IsLoadingLoaderVersions = false;
    }

    private async Task LoadLoaderVersionsAsync()
    {
        var kind = SelectedLoaderChoice.Kind;
        var minecraftVersion = SelectedLoaderBaseVersion;
        if (kind == ModLoaderKind.Vanilla || string.IsNullOrEmpty(minecraftVersion)) return;

        // 切类型/切 MC 版本会连续触发 —— 只留最后一枪
        CancelLoaderVersionQuery();
        var cts = new CancellationTokenSource();
        _loaderVersionCts = cts;

        IsLoadingLoaderVersions = true;
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        Status = $"正在查询 {SelectedLoaderChoice.Display} 支持 {minecraftVersion} 的版本…";

        try
        {
            var list = await _loaderInstaller.GetLoaderVersionsAsync(kind, minecraftVersion, cts.Token);
            if (cts.IsCancellationRequested) return;

            foreach (var item in list)
                LoaderVersions.Add(item);

            // 优先推荐版（Forge 的 promotions 会给），否则取列表第一个（通常是最新）
            SelectedLoaderVersion =
                LoaderVersions.FirstOrDefault(v => v.IsRecommended)
                ?? LoaderVersions.FirstOrDefault();

            Status = LoaderVersions.Count == 0
                ? $"{SelectedLoaderChoice.Display} 没有支持 MC {minecraftVersion} 的版本"
                : $"{SelectedLoaderChoice.Display} · MC {minecraftVersion}：{LoaderVersions.Count} 个版本可选";
        }
        catch (OperationCanceledException)
        {
            // 被新的一次查询顶掉，正常
        }
        catch (Exception ex)
        {
            Status = "查询加载器版本失败";
            AppendLog($"[错误] 查询 {SelectedLoaderChoice.Display} 版本：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loaderVersionCts, cts))
            {
                _loaderVersionCts = null;
                IsLoadingLoaderVersions = false;
            }
            cts.Dispose();
        }
    }

    // ─────────────────────────── 进度与日志 ───────────────────────────

    private void OnFileProgress(object? sender, InstallerProgressChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (e.TotalTasks > 0)
                ProgressPercent = e.ProgressedTasks * 100.0 / e.TotalTasks;

            Status = $"[{e.ProgressedTasks}/{e.TotalTasks}] {e.EventType} {e.Name}";
            AppendLog($"[{e.ProgressedTasks}/{e.TotalTasks}] {e.Name}");
        });
    }

    private void OnByteProgress(object? sender, CmlLib.Core.ByteProgress e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (e.TotalBytes <= 0) return;
            var ratio = e.ToRatio();
            ProgressPercent = ratio * 100.0;
            Status = $"{e.ProgressedBytes / 1024 / 1024.0:F1} / {e.TotalBytes / 1024 / 1024.0:F1} MB";
        });
    }

    // ───────────────────────── 下载源切换 ─────────────────────────

    /// <summary>用户在界面上换了下载源。</summary>
    partial void OnSelectedSourceChanged(SourceChoice value)
    {
        OnPropertyChanged(nameof(SourceNote));

        // 初始化和「自动回退后同步下拉框」都会触发这里，那些不算用户操作
        if (_suppressSourceChange || value is null) return;
        ChangeSource(value);
    }

    /// <summary>用户在界面上换了模组搜索源。</summary>
    partial void OnSelectedModrinthSourceChanged(ModrinthSourceChoice value)
    {
        OnPropertyChanged(nameof(ModrinthSourceNote));

        // 初始化那次不算用户操作
        if (_suppressModrinthSourceChange || value is null) return;
        ApplyModrinthSource(value, log: true);
    }

    /// <summary>把选的源应用到 Modrinth 客户端单例，并写盘。</summary>
    private void ApplyModrinthSource(ModrinthSourceChoice choice, bool log)
    {
        // Modrinth 客户端是 DI 单例，直接改它的 BaseUrl，所有搜索/下载立刻走新源
        var client = App.Services.GetRequiredService<ModrinthClient>();
        client.BaseUrl = choice.BaseUrl;

        var saved = LauncherSettings.SaveModrinthSource(_launcher.InstanceRoot, choice.Key);

        if (log)
        {
            AppendLog(saved
                ? $"模组搜索源 → {choice.Display}（{choice.BaseUrl}）"
                : "[警告] 模组搜索源没能写入设置文件，这次选择只在本次运行内有效");
            Status = saved
                ? $"模组搜索源：{choice.Display}，下次搜索生效"
                : "模组搜索源已切换，但没能写进设置文件";
        }
    }

    /// <summary>模组源测速结果，一行文本。空串时界面不显示这一行。</summary>
    [ObservableProperty]
    private string _modrinthSpeedText = string.Empty;

    /// <summary>给当前两个模组源各测一次速，显示首字节耗时。</summary>
    /// <remarks>
    /// <para>
    /// 两个源并发测，不是串行 —— 串行时后测的那个会沾到「线路已经热了」的光，
    /// 先测的吃亏，比出来的结果不可信。
    /// </para>
    /// <para>
    /// 不用自己写防重入标志：<see cref="AsyncRelayCommand"/> 默认不允许并发执行，
    /// 测速期间按钮自己就是禁用的。
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task TestModrinthSpeedAsync()
    {
        ModrinthSpeedText = "测速中…";
        Status = "模组搜索源：正在测速…";

        try
        {
            var results = await Task.WhenAll(
                ModrinthSourceChoice.All.Select(c => ModrinthSourceSpeedTester.TestAsync(c)));

            var parts = results
                .OrderByDescending(r => r.Key == "official")   // 官方源固定排前面，别让顺序随机跳
                .Select(r => r.Ok ? $"{ShortOf(r)} {r.LatencyMs} ms" : $"{ShortOf(r)} 失败")
                .ToList();

            // 「实测」前缀不能省：这一行正上方就是源的中文说明，两行都是灰字小字，
            // 不标一下会被读成同一段话的续行。Notice 是测出来的，不是介绍。
            ModrinthSpeedText = "实测　" + string.Join("　·　", parts) + DescribeSpeedWinner(results);

            // 只报结果，不替用户切。切源是个有后果的动作（镜像可能数据滞后），
            // 让人看着数字自己决定。
            Status = $"模组搜索源 · {ModrinthSpeedText}";
            AppendLog($"模组源测速：{string.Join("，", parts)}");
        }
        catch (Exception ex)
        {
            ModrinthSpeedText = $"测速失败：{ex.Message}";
            Status = "模组搜索源测速失败";
            AppendLog($"[警告] 模组源测速出错：{ex.Message}");
        }
    }

    /// <summary>比出谁快，差距不够大就明说「差不多」，别把 ±10ms 的抖动说成优势。</summary>
    private static string DescribeSpeedWinner(IReadOnlyList<ModrinthSpeedResult> results)
    {
        var ok = results.Where(r => r.Ok).OrderBy(r => r.LatencyMs).ToList();
        if (ok.Count < 2) return string.Empty;

        var fast = ok[0].LatencyMs!.Value;
        var slow = ok[1].LatencyMs!.Value;
        var diff = slow - fast;

        // 要同时满足「绝对差 30ms 以上」和「相对快 20% 以上」才算真的快
        var decisive = diff >= 30 && diff >= slow * 0.2;

        return decisive
            ? $"　→　选 {ShortOf(ok[0])}（省 {diff} ms）"
            : "　→　两者差不多";
    }

    /// <summary>「官方源（Modrinth）」→「官方源」，一行里放两项时用。</summary>
    private static string ShortOf(ModrinthSpeedResult result)
        => result.Display.Split('（')[0];

    /// <summary>用户换了下载线程数。</summary>
    partial void OnSelectedConcurrencyChanged(int value)
    {
        OnPropertyChanged(nameof(ConcurrencyNote));
        OnPropertyChanged(nameof(ConcurrencyLabel));

        if (_suppressConcurrencyChange || value <= 0) return;
        ChangeConcurrency(value);
    }

    /// <summary>改下载线程数：重建下载器 → 写盘。</summary>
    private void ChangeConcurrency(int threads)
    {
        // 下载中途换并发会把正在用的连接池关掉，那一轮直接报错。
        // 与其冒着中断风险，不如等这一轮跑完再改。
        if (IsBusy)
        {
            Status = "正在下载，等这一轮结束再改线程数";
            AppendLog("下载线程数没改：正在下载，等这一轮结束");

            _suppressConcurrencyChange = true;
            SelectedConcurrency = MirrorConfigurator.MaxDownloaders;
            _suppressConcurrencyChange = false;
            return;
        }

        var changed = _launcher.SetDownloadConcurrency(threads);
        var saved = LauncherSettings.SaveConcurrency(_launcher.InstanceRoot, MirrorConfigurator.MaxDownloaders);

        if (!changed)
        {
            Status = $"下载线程数本来就是 {threads}";
            return;
        }

        Status = saved
            ? $"下载线程数：{MirrorConfigurator.MaxDownloaders}（下一次下载生效）"
            : $"下载线程数：{MirrorConfigurator.MaxDownloaders}，但没能写进设置文件";

        AppendLog(saved
            ? $"下载线程数 → {MirrorConfigurator.MaxDownloaders}，校验 {MirrorConfigurator.MaxCheckers} 线程"
            : "[警告] 下载线程数没能写入设置文件，下次启动回到默认");
    }

    /// <summary>切源 → 写盘 → 测速。三步都要，少一步就会出问题。</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>不写盘：下次启动又回到官方源，用户每次都得重选一遍。</item>
    /// <item>不测速：切到镜像以为好了，点下载才发现镜像也连不上，白等一轮。</item>
    /// </list>
    /// </remarks>
    private void ChangeSource(SourceChoice choice)
    {
        var changed = _launcher.SetSource(choice.UseMirror, "界面切换");

        // 只改 PreferMirror 一项，不能 new 一个新对象去覆盖 —— 同一个文件里还存着「上次用的实例」
        var saved = LauncherSettings.SavePreferMirror(_launcher.InstanceRoot, choice.UseMirror);

        SourceLabel = _launcher.SourceName;

        AppendLog(changed
            ? $"下载源 → {choice.Display}（{choice.Note}）"
            : $"下载源本来就是 {choice.Display}，不重复切换");

        AppendLog(saved
            ? $"下载源已记住：{LauncherSettings.PathFor(_launcher.InstanceRoot)}"
            : "[警告] 下载源没能写入设置文件，这次选择只在本次运行内有效");

        if (!saved)
        {
            Status = "下载源已切换，但没能写进设置文件（下次启动会回到原样）";
            return;
        }

        // 正在下载的时候切源：当前这一批还是走旧源，说清楚免得用户以为没生效
        if (IsBusy)
            AppendLog("当前正在下载，这一轮仍走原来的源；下一轮才用新的");

        _ = RunProbeAsync(choice);
    }

    /// <summary>设置页上的「测速」按钮。</summary>
    [RelayCommand]
    private Task ProbeSource() => RunProbeAsync(SelectedSource ?? SourceChoice.Official);

    /// <summary>测一下刚选的源通不通。失败不影响切源本身。</summary>
    private async Task RunProbeAsync(SourceChoice choice)
    {
        // 连点两下切换就掐掉上一次探测，免得快的那个结果被慢的覆盖
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;

        SourceProbeText = "测速中…";
        Status = $"下载源：{choice.Display}，正在测速…";

        var result = await SourceProbe.ProbeAsync(choice.UseMirror, cts.Token)
            .ConfigureAwait(true);

        if (cts.IsCancellationRequested) return;

        SourceProbeText = result.Describe();

        if (result.Ok)
        {
            Status = $"下载源：{choice.Display} · {result.Describe()}";
            AppendLog($"测速 {choice.Display}：{result.Describe()}");
            return;
        }

        Status = $"下载源：{choice.Display} · {result.Describe()}，建议换成另一个";
        AppendLog($"⚠ 测速 {choice.Display}：{result.Describe()}，下载多半也会卡住");
    }


    /// <remarks>
    /// 公开出来是给「内容下载」窗口用的：那个窗口是独立视图模型，出了问题（搜索卡住、
    /// 版本表没拉到）只有写进同一份日志才查得到 —— 用户截图只能说明「界面空着」。
    /// </remarks>
    public void AppendLog(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _log.AppendLine(line);

        // 同时落盘：日志区是内存的，关掉窗口就没证据了
        WriteLogLine(line);

        // 截断过长的日志
        var text = _log.ToString();
        var lines = text.Split('\n');
        if (lines.Length > MaxLogLines)
        {
            _log.Clear();
            _log.AppendLine(string.Join('\n', lines[^MaxLogLines..]));
            text = _log.ToString();
        }

        LogText = text;
    }

    /// <summary>
    /// 清掉界面上的日志。
    /// </summary>
    /// <remarks>
    /// 只清内存里那份，磁盘上的 <c>launcher.log</c> 不动 —— 出问题时那份是唯一证据，
    /// 用户点「清空」多半只是想看清后续输出，不是想销毁记录。
    /// </remarks>
    public void ClearLog()
    {
        _log.Clear();
        LogText = string.Empty;
        AppendLog("日志区已清空（磁盘上的 launcher.log 保留）");
    }

    /// <summary>
    /// 追加一行到 <c>launcher.log</c>。写失败绝不能影响主流程，所以异常全吞。
    /// </summary>
    /// <remarks>
    /// 长任务（下载几百个文件）会产生上万行，所以这里顺带做大小轮转。
    /// <b>不每行都 stat 文件</b>（那是每行一次系统调用），每 200 行看一次 ——
    /// 200 行的体积远小于阈值，不会出现「刚超出就写了半屏才轮转」的观感问题。
    /// </remarks>
    private void WriteLogLine(string line)
    {
        try
        {
            if (++_logLinesSinceSizeCheck >= LogRotateCheckEveryLines)
            {
                _logLinesSinceSizeCheck = 0;

                if (LogRotation.RotateIfNeeded(LogFilePath))
                {
                    // 直接写文件，不走 AppendLog —— 否则会回到这里再判一次
                    File.AppendAllText(
                        LogFilePath,
                        $"[日志] 超过 {LogRotation.DefaultMaxBytes / 1024 / 1024} MB，"
                        + $"上一份已改名成 {Path.GetFileName(LogRotation.BackupPathFor(LogFilePath))}"
                        + Environment.NewLine,
                        Encoding.UTF8);
                }
            }

            File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志写不进去（目录只读 / 被占用）就算了，不能因此让程序出错
        }
    }
}
