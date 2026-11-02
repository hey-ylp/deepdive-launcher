using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepDive.App.Models;
using DeepDive.Core.Launcher;
using DeepDive.Core.Modrinth;

namespace DeepDive.App.ViewModels;

/// <summary>游戏版本标签行的一项。</summary>
public sealed record GameVersionOption(string Value, string Display);

/// <summary>
/// 下载窗口的视图模型：先选游戏版本，再按加载器挑一条，直接下到实例目录。
/// </summary>
/// <remarks>
/// <para>
/// <b>交互照 PCL 的样子做</b>：顶部一排游戏版本标签（1.21.1 / 1.20.1 / …），
/// 下面每个加载器一行（<c>NeoForge 1.21.1</c>、<c>Fabric 1.21.1</c>）。
/// 用户只需要回答「我玩哪个版本、用哪个加载器」，剩下的文件选择由程序定。
/// </para>
/// <para>
/// <b>为什么不把 258 个文件版本平铺出来</b>：Sodium 一个项目就有 258 个版本文件，
/// 平铺等于让人在几百行里找。按「游戏版本 × 加载器」聚成十几行之后，
/// 每行只留「最新」那一个 —— 这也是绝大多数人真正想装的东西。
/// </para>
/// </remarks>
public partial class ContentVersionViewModel : ObservableObject
{
    private readonly ModrinthClient _client;
    private readonly ContentInstaller _installer;
    private readonly ModpackInstaller? _modpackInstaller;
    private readonly GameFolderService _folders;
    private readonly ContentHitView _hit;
    private readonly string _versionName;
    private readonly string? _baseMcVersion;
    private readonly Dispatcher _dispatcher;

    /// <summary>装完整合包后回调（参数是实例名），让主窗口刷新实例下拉。</summary>
    private readonly Action<string>? _onInstanceCreated;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _downloadCts;

    private List<ModrinthVersion> _all = new();
    private Dictionary<string, int> _rank = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>初始化期间改 SelectedGameVersion 会连环重建列表，用它挡住。</summary>
    private bool _suppress;

    public ContentVersionViewModel(
        ModrinthClient client,
        ContentInstaller installer,
        GameFolderService folders,
        ContentHitView hit,
        string versionName,
        string? baseMcVersion,
        ModpackInstaller? modpackInstaller = null,
        Action<string>? onInstanceCreated = null)
    {
        _client = client;
        _installer = installer;
        _folders = folders;
        _hit = hit;
        _versionName = versionName;
        _baseMcVersion = baseMcVersion;
        _modpackInstaller = modpackInstaller;
        _onInstanceCreated = onInstanceCreated;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        IsModpack = hit.TypeInfo.Type == ContentType.Modpack;
        InstanceLoader = InferLoader(versionName);
        NeedsLoaderMatch = hit.TypeInfo.Type == ContentType.Mod;

        // 整合包会新建一个独立实例，跟当前实例没有关系 —— 这时候再谈「装到哪个目录」是误导
        TargetFolder = IsModpack ? string.Empty : ResolveTargetFolder(hit.TypeInfo);

        Title = hit.TitleMain;
        TitleSub = hit.TitleSub;
        Subtitle = IsModpack
            ? "整合包 · 会新建一个独立实例（自带游戏版本和加载器）"
            : $"{hit.TypeLabel} · 装到 {TargetFolder}";
    }

    /// <summary>这条是整合包 —— 走「新建实例」那条完全不同的安装路径。</summary>
    public bool IsModpack { get; }

    /// <summary>
    /// 问用户新实例叫什么。由窗口注入（视图模型不 new 窗口）。
    /// </summary>
    public Func<string, string?>? AskInstanceName { get; set; }

    /// <summary>实例名冲突时自动加序号用。</summary>
    public Func<string, bool>? InstanceExists { get; set; }

    // ── 头部信息 ─────────────────────────────────────────

    public string Title { get; }

    public string TitleSub { get; }

    public bool HasTitleSub => TitleSub.Length > 0;

    public string Subtitle { get; }

    /// <summary>文件最终落到哪。装完找不到文件是最常见的困惑，写出来。</summary>
    public string TargetFolder { get; }

    public string? InstanceLoader { get; }

    public bool NeedsLoaderMatch { get; }

    /// <summary>「1.21.1 · NeoForge」，给筛选说明用。</summary>
    public string InstanceSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_baseMcVersion)) parts.Add(_baseMcVersion);
            if (NeedsLoaderMatch && !string.IsNullOrWhiteSpace(InstanceLoader))
                parts.Add(ContentTypeInfo.LoaderLabel(InstanceLoader));

            return parts.Count == 0 ? "不限版本" : string.Join(" · ", parts);
        }
    }

    /// <summary>「当前实例：1.21.1 · NeoForge」；整合包则说明它另开实例。</summary>
    public string InstanceLine => IsModpack
        ? "整合包会新建实例，不动你现在的存档和模组"
        : $"当前实例：{InstanceSummary}";


    // ── 列表 ─────────────────────────────────────────────

    /// <summary>顶部那排游戏版本标签。</summary>
    public ObservableCollection<GameVersionOption> GameVersions { get; } = new();

    /// <summary>按加载器分组的可下载条目。</summary>
    public ObservableCollection<VersionTargetRow> Targets { get; } = new();

    [ObservableProperty]
    private GameVersionOption? _selectedGameVersion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "读取版本列表…";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _errorText = string.Empty;

    /// <summary>这个项目的文件版本总数（258 之类），界面上说明「为什么只列这么几行」。</summary>
    public int TotalVersions => _all.Count;

    public bool ShowEmpty => !IsBusy && Targets.Count == 0;

    // ── 载入 ─────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCts = cts;

        IsBusy = true;
        ErrorText = string.Empty;
        Status = "读取版本列表…";

        try
        {
            _all = (await _client.GetProjectVersionsAsync(_hit.ProjectId, cts.Token).ConfigureAwait(true))
                .Where(v => v.Files.Count > 0)
                .ToList();

            if (cts.IsCancellationRequested) return;

            // 版本新旧只能按发布时间排（字符串比大小会把 0.8.9 排到 0.8.13 后面）
            var releases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var gameVersions = await _client.GetGameVersionsAsync(cts.Token).ConfigureAwait(true);
                foreach (var item in gameVersions)
                {
                    if (item.VersionType == "release") releases.Add(item.Version);
                    _rank[item.Version] = 0;
                }

                // 序号按发布时间重排一遍（GetGameVersionsAsync 已按时间倒序）
                var ordered = gameVersions.OrderBy(v => v.Date).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    _rank[ordered[i].Version] = i;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 排不了序就按服务器给的顺序显示，不影响下载
                ErrorText = $"版本排序不可用（不影响下载）：{ex.Message}";
            }

            BuildGameVersionTabs(releases);

            Status = _all.Count == 0
                ? "这个项目没有可下载的文件"
                : $"共 {_all.Count} 个文件版本，其中 {GameVersions.Count} 个游戏版本可选";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Status = "读取版本列表失败";
            ErrorText = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsBusy = false;
                _loadCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 把所有出现过的游戏版本做成标签，新的排前面。
    /// </summary>
    /// <param name="releases">正式版游戏版本集合。用来把 <c>26.3-rc-2</c> 这类快照挡在外面。</param>
    /// <remarks>
    /// 不滤掉快照的话，Sodium 会列出 41 个标签（<c>26.3-rc-2</c>、<c>25w31a</c> 全在里面），
    /// 一排标签把窗口占满，而真正有人玩的就那十几个。
    /// 但如果这个项目<b>只</b>支持快照（极少数），滤完是空的，那就退回全显示。
    /// </remarks>
    private void BuildGameVersionTabs(IReadOnlySet<string> releases)
    {
        var all = _all
            .SelectMany(v => v.GameVersions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filtered = releases.Count == 0
            ? all
            : all.Where(v => releases.Contains(v)).ToList();

        if (filtered.Count == 0) filtered = all;

        var versions = filtered
            .OrderByDescending(v => _rank.TryGetValue(v, out var r) ? r : -1)
            .ToList();

        _suppress = true;
        try
        {
            GameVersions.Clear();
            foreach (var version in versions)
                GameVersions.Add(new GameVersionOption(version, version));

            // 默认停在当前实例的版本上 —— 用户点下载，十次有九次是装给自己在玩的版本
            SelectedGameVersion =
                GameVersions.FirstOrDefault(v =>
                    string.Equals(v.Value, _baseMcVersion, StringComparison.OrdinalIgnoreCase))
                ?? GameVersions.FirstOrDefault();
        }
        finally
        {
            _suppress = false;
        }

        RebuildTargets();
    }

    partial void OnSelectedGameVersionChanged(GameVersionOption? value)
    {
        if (_suppress) return;
        RebuildTargets();
    }

    /// <summary>
    /// 按当前选中的游戏版本，把加载器聚成行。
    /// </summary>
    /// <remarks>
    /// 同一个文件版本可能同时标了 fabric 和 quilt，所以这里会拆成两行 ——
    /// 跟 PCL 一样，用户要按「加载器」做决定，而不是按文件。
    /// </remarks>
    private void RebuildTargets()
    {
        Targets.Clear();

        var selected = SelectedGameVersion?.Value;
        if (string.IsNullOrWhiteSpace(selected) || _all.Count == 0)
        {
            OnPropertyChanged(nameof(ShowEmpty));
            DownloadCommand.NotifyCanExecuteChanged();
            return;
        }

        var rows = _all
            .Where(v => v.GameVersions.Contains(selected, StringComparer.OrdinalIgnoreCase))
            .SelectMany(
                v => v.Loaders.Count == 0 ? new[] { string.Empty } : v.Loaders.ToArray(),
                (v, loader) => (Loader: loader, Version: v))
            .GroupBy(x => x.Loader.ToLowerInvariant())
            .Select(g => new VersionTargetRow(
                g.Key,
                selected,
                g.Select(x => x.Version)
                    .DistinctBy(v => v.Id)
                    .OrderByDescending(v => v.DatePublished)
                    .ToList(),
                // 判不出来就当匹配（宁可少拦一次，也不要因为猜不到就把正常的下载挡住）
                // 整合包不看当前实例 —— 它自己就是一套新环境，标「不匹配」纯属吓人
                matchesGameVersion: IsModpack ||
                                    string.IsNullOrWhiteSpace(_baseMcVersion) ||
                                    string.Equals(selected, _baseMcVersion, StringComparison.OrdinalIgnoreCase),
                matchesLoader: IsModpack || !NeedsLoaderMatch ||
                               string.IsNullOrWhiteSpace(InstanceLoader) ||
                               string.Equals(g.Key, InstanceLoader, StringComparison.OrdinalIgnoreCase)))
            // 能直接用的排最前面，其余按名字排，顺序稳定不跳
            .OrderByDescending(r => r.IsInstanceMatch)
            .ThenBy(r => r.LoaderLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var row in rows)
            Targets.Add(row);

        var matched = rows.Count(r => r.IsInstanceMatch);
        Status = rows.Count == 0
            ? $"这个项目没有支持 {selected} 的文件"
            : matched > 0
                ? $"{selected}：{string.Join(" / ", rows.Select(r => r.LoaderLabel))}"
                  + (NeedsLoaderMatch && !string.IsNullOrWhiteSpace(InstanceLoader)
                      ? $"（{LoaderName} 与当前实例匹配，可直接装）"
                      : string.Empty)
                : string.IsNullOrWhiteSpace(_baseMcVersion) ||
                  string.Equals(selected, _baseMcVersion, StringComparison.OrdinalIgnoreCase)
                    ? $"{selected}：都不匹配当前实例的加载器（{LoaderName}），装上不会生效"
                    : $"{selected}：当前实例是 {_baseMcVersion}，装进去不会生效";

        OnPropertyChanged(nameof(ShowEmpty));
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private string LoaderName => string.IsNullOrWhiteSpace(InstanceLoader)
        ? "—"
        : ContentTypeInfo.LoaderLabel(InstanceLoader);

    // ── 下载 ─────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync(VersionTargetRow? row)
    {
        if (row is null || !row.HasFile) return;

        // 整合包是另一条完全不同的路：新建实例、装原版和加载器、下几十个文件
        if (IsModpack)
        {
            await InstallModpackAsync(row);
            return;
        }

        // 装之前先拦一道：版本或加载器对不上就说明白，让用户自己决定要不要硬装。
        // 这一步比事后「游戏崩了」有价值得多 —— 崩的时候报错只会说某个模组加载失败，
        // 没人会想到是版本下错了。
        if (!row.IsInstanceMatch)
        {
            var reason = row.DescribeMismatch(_baseMcVersion, LoaderName);

            var answer = MessageBox.Show(
                $"{reason}\n\n装进去多半不会生效，也可能让游戏直接起不来。\n\n仍然要装吗？",
                "这个文件跟当前实例对不上",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;
        }

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();

        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        row.IsDownloading = true;
        row.StateText = "下载中…";
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        ProgressText = string.Empty;

        var reporter = new Progress<ContentDownloadProgress>(p =>
        {
            ProgressText = p.Display;
            if (p.Fraction >= 0)
            {
                IsProgressIndeterminate = false;
                Progress = p.Fraction;
            }
        });

        try
        {
            var result = await _installer
                .InstallAsync(row.Latest, TargetFolder, reporter, cts.Token)
                .ConfigureAwait(true);

            row.MarkDone(result.AnythingInstalled
                ? $"已装好 {row.LatestNumber} · 重启游戏生效"
                : "本来就有，没重复下载");

            Status = $"{row.Title} · {row.LatestNumber}：{result.Summary}";
            ErrorText = result.Warnings.Count == 0
                ? string.Empty
                : string.Join("；", result.Warnings);

            ProgressText = $"已完成 → {result.Folder}";
            Progress = 1;
            IsProgressIndeterminate = false;
        }
        catch (OperationCanceledException)
        {
            row.MarkFailed("已取消");
            Status = "下载已取消";
        }
        catch (Exception ex)
        {
            row.MarkFailed("失败");
            Status = "下载失败";
            ErrorText = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts))
            {
                IsBusy = false;
                _downloadCts = null;
            }

            cts.Dispose();
        }
    }

    private bool CanDownload(VersionTargetRow? row) => !IsBusy;

    /// <summary>
    /// 装整合包：问名字 → 建实例 → 下原版和加载器 → 下文件 → 解 overrides。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要问实例名</b>：整合包不能塞进现有实例 —— 它自带一整套游戏版本和加载器，
    /// 硬塞进去会跟用户已经装的东西打架。所以每个整合包都单独开一个实例，名字得让人能认出来。
    /// </para>
    /// <para>
    /// 全程可能要好几分钟（原版 + Java + 几十个模组），所以进度分阶段显示，
    /// 而不是一个百分比从头转到尾 —— 用户得知道现在卡在哪一步。
    /// </para>
    /// </remarks>
    private async Task InstallModpackAsync(VersionTargetRow row)
    {
        if (_modpackInstaller is null)
        {
            Status = "整合包安装组件没准备好";
            return;
        }

        var suggested = SuggestInstanceName();

        var name = AskInstanceName?.Invoke(suggested) ?? suggested;
        if (string.IsNullOrWhiteSpace(name)) return; // 用户取消

        name = name.Trim();

        // 名字合不合法由 InstanceManager 说了算（非法字符、保留名、长度都在那儿）
        if (InstanceManager.CheckName(name) is { } problem)
        {
            Status = "实例名不可用";
            ErrorText = problem;
            return;
        }

        // 重名就自动加序号，别让用户为了个名字反复试
        if (InstanceExists?.Invoke(name) == true)
        {
            var baseName = name;
            for (var i = 2; i < 100; i++)
            {
                var candidate = $"{baseName}-{i}";
                if (InstanceExists(candidate)) continue;
                name = candidate;
                break;
            }
        }

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();

        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        row.IsDownloading = true;
        row.StateText = "安装中…";
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        ProgressText = string.Empty;
        ErrorText = string.Empty;

        var reporter = new Progress<ModpackProgress>(p =>
        {
            if (p.IsDeterminate)
            {
                IsProgressIndeterminate = false;
                Progress = p.Fraction;
                ProgressText = $"{p.Stage} {p.Fraction * 100:F0}%";
            }
            else
            {
                IsProgressIndeterminate = true;
                ProgressText = p.Stage;
            }
        });

        try
        {
            var result = await _modpackInstaller
                .InstallAsync(row.Latest, name, reporter, cts.Token)
                .ConfigureAwait(true);

            row.MarkDone($"已装成实例「{result.InstanceName}」");

            Status = result.Summary;
            ErrorText = result.Warnings.Count == 0 ? string.Empty : string.Join("；", result.Warnings);

            Progress = 1;
            IsProgressIndeterminate = false;
            ProgressText = $"实例目录 → {result.InstanceDirectory}";

            // 通知主窗口刷新实例下拉并切过去，否则用户还得自己找
            _onInstanceCreated?.Invoke(result.InstanceName);
        }
        catch (OperationCanceledException)
        {
            row.MarkFailed("已取消");
            Status = "安装已取消（实例可能只装了一半，可以重来或手动删掉）";
        }
        catch (Exception ex)
        {
            row.MarkFailed("失败");
            Status = "整合包安装失败";
            ErrorText = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts))
            {
                IsBusy = false;
                _downloadCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>默认实例名：项目名去掉非法字符，太长就截断。</summary>
    private string SuggestInstanceName()
    {
        var raw = string.IsNullOrWhiteSpace(_hit.Title) ? _hit.Slug : _hit.Title;

        var cleaned = raw;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');

        cleaned = cleaned.Trim();

        if (cleaned.Length > 24) cleaned = cleaned[..24].Trim();
        if (cleaned.Length == 0) cleaned = "整合包";

        // 名字以 . 开头会被当隐藏目录，InstanceManager 也不允许下划线开头
        cleaned = cleaned.TrimStart('.', '_');

        return cleaned.Length == 0 ? "整合包" : cleaned;
    }

    [RelayCommand]
    private void Cancel()
    {
        _downloadCts?.Cancel();
        Status = "正在取消…";
    }

    partial void OnIsBusyChanged(bool value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    public bool CanCancel => IsBusy;

    public void Close()
    {
        _loadCts?.Cancel();
        _downloadCts?.Cancel();
    }

    // ── 路径与加载器 ─────────────────────────────────────

    /// <summary>文件该落到哪个目录（跟「打开模组文件夹」同一套解析）。</summary>
    private string ResolveTargetFolder(ContentTypeInfo typeInfo)
    {
        try
        {
            if (typeInfo.Type == ContentType.Mod)
                return _folders.Resolve(_versionName, GameFolderKind.Mods).Path;

            var versionDir = _folders.Resolve(_versionName, GameFolderKind.Version).Path;
            return Path.Combine(versionDir, typeInfo.InstallFolder);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>从版本目录名猜加载器（名字是我们自己生成的，前缀可靠）。</summary>
    private static string? InferLoader(string versionName)
    {
        var name = versionName.ToLowerInvariant();

        if (name.StartsWith("neoforge", StringComparison.Ordinal)) return "neoforge";
        if (name.StartsWith("forge", StringComparison.Ordinal)) return "forge";
        if (name.StartsWith("fabric", StringComparison.Ordinal)) return "fabric";
        if (name.StartsWith("quilt", StringComparison.Ordinal)) return "quilt";

        return null;
    }
}
