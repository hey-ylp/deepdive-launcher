using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McLauncher.App.Models;
using McLauncher.Core.Launcher;
using McLauncher.Core.Modrinth;
using McLauncher.Core.Translation;

namespace McLauncher.App.ViewModels;

/// <summary>加载器下拉的一项。<see cref="Value"/> 为空 = 不限。</summary>
public sealed record LoaderOption(string Value, string Display);

/// <summary>游戏版本下拉的一项。<see cref="Value"/> 为空 = 不限。</summary>
public sealed record VersionOption(string Value, string Display);

/// <summary>排序下拉的一项。</summary>
public sealed record SortOption(string Value, string Display);

/// <summary>
/// 内容下载窗口的视图模型：按类型 / 游戏版本 / 加载器搜 Modrinth。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么类型一变，加载器选项就整个换掉</b>：Modrinth 的加载器不是一套通用的标签。
/// 光影包只有 iris / optifine / canvas，资源包只有 minecraft，模组才有 fabric / forge / neoforge / quilt。
/// 把五类东西的加载器混在一个下拉里，用户选了「Forge 光影包」只会搜出空结果，
/// 而界面上没有任何线索告诉他为什么是空的。
/// </para>
/// <para>
/// 这套映射来自 <c>GET /tag/loader</c> 的 <c>supported_project_types</c> 字段，不是猜的。
/// </para>
/// </remarks>
public partial class ContentBrowserViewModel : ObservableObject
{
    /// <summary>每页条数。Modrinth 单次上限 100，20 条在 1280 宽窗口里正好一屏。</summary>
    public const int PageSize = 20;

    private const string AnyLoader = "";
    private const string AnyVersion = "";

    private readonly ModrinthClient _client;
    private readonly GameFolderService _folders;
    private readonly TranslationService _translator;
    private readonly Dispatcher _dispatcher;
    private readonly string _versionName;
    private readonly ContentIconLoader _icons;

    /// <summary>把关键节点写进 launcher.log 的通道（由主窗口传进来）。</summary>
    private readonly Action<string>? _log;

    /// <summary>整合包装完之后通知主窗口刷新实例下拉（参数是实例名）。</summary>
    public Action<string>? OnInstanceCreated { get; }

    private void Log(string message) => _log?.Invoke($"[内容下载] {message}");

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(未知)" : value;

    private readonly Dictionary<string, int> _rank = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;

    /// <summary>当前这一页的翻译任务。切页或者重新搜索就掐掉。</summary>
    private CancellationTokenSource? _translateCts;

    /// <summary>
    /// 订阅单例翻译服务日志用的委托。
    /// </summary>
    /// <remarks>
    /// <b>必须存下来，关窗口时退订。</b> <see cref="TranslationService"/> 是单例，
    /// 直接写匿名 lambda 订阅会让事件一直握着这个视图模型 —— 每开一次内容窗口泄漏一个，
    /// 用户开开关关十几次就开始莫名占内存。
    /// </remarks>
    private readonly Action<object, string> _translatorLogHandler;

    /// <summary>
    /// 订阅 Modrinth 客户端日志用的委托（同样是单例，必须存下来退订）。
    /// </summary>
    /// <remarks>
    /// <b>这个订阅是为了让「搜索中…」别是死水一潭</b>：网络抽风时客户端会自动重试
    /// （12 秒超时 + 3 次重试），一句提示都不给的话，用户只能对着转圈等半分钟，
    /// 以为程序卡死了。实测 2026-10-29 就出现过一次搜索接口 20 秒无响应。
    /// </remarks>
    private readonly Action<object, string> _clientLogHandler;

    /// <summary>初始化期间把选项一个个塞进去会连环触发搜索，用这个闸门挡住。</summary>
    private bool _suppress;

    public ContentBrowserViewModel(
        ModrinthClient client,
        GameFolderService folders,
        TranslationService translator,
        string versionName,
        string baseMcVersion,
        Action<string>? log = null,
        Action<string>? onInstanceCreated = null)
    {
        _client = client;
        _folders = folders;
        _translator = translator;
        _versionName = versionName;
        _log = log;
        OnInstanceCreated = onInstanceCreated;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _icons = new ContentIconLoader();

        Log($"内容下载：实例版本 {versionName}，原版 {Describe(baseMcVersion)}");

        _translatorLogHandler = (_, message) =>
            _dispatcher.BeginInvoke(() => TranslationHint = message);

        _translator.Log += _translatorLogHandler;

        _clientLogHandler = (_, message) =>
            _dispatcher.BeginInvoke(() =>
            {
                NetworkHint = message;

                // 出了结果就把重试提示擦掉，别让一句「重试中」挂在那儿误导人
                if (!IsBusy) NetworkHint = string.Empty;
            });

        _client.Log += _clientLogHandler;

        Types = new ObservableCollection<ContentTypeInfo>(ContentTypeInfo.All);
        _selectedType = ContentTypeInfo.Get(ContentType.Mod);

        LoaderOptions = new ObservableCollection<LoaderOption>();
        VersionOptions = new ObservableCollection<VersionOption> { new(AnyVersion, "不限版本") };

        SortOptions = new ObservableCollection<SortOption>
        {
            new("relevance", "相关度"),
            new("downloads", "下载量"),
            new("updated", "最近更新"),
            new("newest", "最新发布"),
        };
        _selectedSort = SortOptions[0];

        // 「版本」这个下拉框必须给初值。不给的话 ComboBox 既没选项也没文字，是个空白框 ——
        // 用户第一眼看到会以为是坏掉了，而旁边「不限加载器」「相关度」都是正常有字的。
        // 先停在「不限版本」，等版本表拉回来再按当前实例的版本覆盖（见 InitializeAsync）。
        _selectedVersion = VersionOptions[0];

        _defaultVersion = baseMcVersion;

        RebuildLoaders();
    }

    /// <summary>构造时记下主窗口选的版本，标签表拉回来后再选中它。</summary>
    private readonly string _defaultVersion;

    public ObservableCollection<ContentTypeInfo> Types { get; }

    public ObservableCollection<LoaderOption> LoaderOptions { get; }

    public ObservableCollection<VersionOption> VersionOptions { get; }

    public ObservableCollection<SortOption> SortOptions { get; }

    public ObservableCollection<ContentHitView> Results { get; } = new();

    /// <summary>当前窗口对应的版本目录名（下载要落到它的 mods 里）。</summary>
    public string VersionName => _versionName;

    /// <summary>当前实例的原版版本号（<c>1.21.1</c>），版本选择用来判匹配。</summary>
    public string? BaseMcVersion => _defaultVersion;

    [ObservableProperty]
    private ContentTypeInfo _selectedType;

    [ObservableProperty]
    private LoaderOption? _selectedLoader;

    [ObservableProperty]
    private VersionOption? _selectedVersion;

    [ObservableProperty]
    private SortOption? _selectedSort;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _status = "输入关键词，或直接按下载量翻一翻";

    /// <summary>出错的完整原因，红色显示在状态栏下面。正常时为空。</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _totalHits;

    [ObservableProperty]
    private int _offset;

    /// <summary>
    /// 要不要把简介翻成中文。默认开。
    /// </summary>
    /// <remarks>
    /// 给开关是因为译文是机翻水平 —— 有人宁可看原文。关掉只是不显示，
    /// 已经存进缓存的译文不会丢，重新打开立刻就能用。
    /// </remarks>
    [ObservableProperty]
    private bool _translateDescriptions = true;

    /// <summary>翻译进度一行字（「正在翻译 12 条简介…」）。</summary>
    [ObservableProperty]
    private string _translationHint = string.Empty;

    /// <summary>
    /// 中文关键词被换成英文之后，在这里如实说明换了什么。
    /// </summary>
    /// <remarks>
    /// 不说的话用户会以为搜的就是「机械动力」，结果里出现 Create 会莫名其妙。
    /// </remarks>
    [ObservableProperty]
    private string _queryHint = string.Empty;

    /// <summary>正在翻译中 —— 界面显示一个小转圈。</summary>
    [ObservableProperty]
    private bool _isTranslating;

    /// <summary>
    /// 网络层提示（「网络不通，4 秒后重试（第 2/3 次）」）。
    /// </summary>
    /// <remarks>
    /// 单独一行而不是塞进 <see cref="Status"/>：搜索失败和网络重试是两件事，
    /// 混在一起会让状态栏一会儿一变，读起来更乱。
    /// </remarks>
    [ObservableProperty]
    private string _networkHint = string.Empty;

    /// <summary>当前类型的说明（「装进 mods 目录…」）。</summary>
    public string TypeNote => SelectedType.Note;

    /// <summary>安装目标目录。模组/光影/资源包各自不同，直接写出来省得用户猜。</summary>
    public string InstallTarget
    {
        get
        {
            try
            {
                return SelectedType.Type switch
                {
                    ContentType.Modpack => "整合包安装会新建一个独立实例（下一步实现）",
                    ContentType.Mod => _folders.Resolve(_versionName, GameFolderKind.Mods).Path,
                    ContentType.Shader => Path.Combine(
                        _folders.Resolve(_versionName, GameFolderKind.Version).Path, "shaderpacks"),
                    ContentType.ResourcePack => Path.Combine(
                        _folders.Resolve(_versionName, GameFolderKind.Version).Path, "resourcepacks"),
                    _ => string.Empty,
                };
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>「第 1–20 条，共 1234 条」。</summary>
    public string PageText
    {
        get
        {
            if (TotalHits <= 0) return string.Empty;
            var from = Results.Count == 0 ? 0 : Offset + 1;
            var to = Offset + Results.Count;
            return $"第 {from}–{to} 条，共 {TotalHits:N0} 条";
        }
    }

    public bool CanPrevPage => !IsBusy && Offset > 0;

    public bool CanNextPage => !IsBusy && Offset + Results.Count < TotalHits;

    /// <summary>没有结果且不在加载中 —— 显示空状态提示。</summary>
    public bool ShowEmpty => !IsBusy && Results.Count == 0 && TotalHits == 0;

    /// <summary>当前这一页里，和所选游戏版本兼容的有几条。</summary>
    public string CompatSummary
    {
        get
        {
            if (Results.Count == 0) return string.Empty;
            if (string.IsNullOrEmpty(SelectedVersion?.Value)) return string.Empty;

            var ok = Results.Count(r => r.IsCompatible);
            return ok == Results.Count
                ? "本页全部兼容当前版本"
                : $"本页 {ok} / {Results.Count} 个兼容 {SelectedVersion.Value}";
        }
    }

    /// <summary>
    /// 拉标签表（游戏版本 + 序号），把版本下拉填好。
    /// </summary>
    /// <remarks>
    /// 失败不抛 —— 没有版本表也能搜，只是版本徽章会退化成「版本不匹配」。
    /// 搜索框能用比「整页空白 + 一个错误」重要得多。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var releases = await _client.GetReleaseVersionsAsync(cancellationToken);

            _suppress = true;
            try
            {
                foreach (var version in releases)
                    VersionOptions.Add(new VersionOption(version, version));

                // 主窗口选的版本可能是 26.3 这种刚出的，未必在 release 表里 —— 单独补一个
                if (!string.IsNullOrWhiteSpace(_defaultVersion) &&
                    !releases.Contains(_defaultVersion, StringComparer.OrdinalIgnoreCase))
                {
                    VersionOptions.Add(new VersionOption(_defaultVersion, _defaultVersion));
                }

                SelectedVersion = string.IsNullOrWhiteSpace(_defaultVersion)
                    ? VersionOptions[0]
                    : VersionOptions.FirstOrDefault(v =>
                          string.Equals(v.Value, _defaultVersion, StringComparison.OrdinalIgnoreCase))
                      ?? VersionOptions[0];

                var rank = await _client.GetVersionRankAsync(cancellationToken);
                foreach (var pair in rank) _rank[pair.Key] = pair.Value;
            }
            finally
            {
                _suppress = false;
            }

            Status = string.IsNullOrWhiteSpace(_defaultVersion)
                ? "输入关键词开始搜"
                : $"已按当前版本 {_defaultVersion} 筛选，可随时改成不限";

            Log($"版本表就绪：{VersionOptions.Count - 1} 个正式版，筛选停在「{SelectedVersion?.Display ?? "无"}」");
        }
        catch (OperationCanceledException)
        {
            // 窗口关了就关了吧
            return;
        }
        catch (Exception ex)
        {
            _suppress = false;
            Log($"版本表拉取失败：{ex.GetType().Name}: {ex.Message}（版本下拉只剩「不限版本」）");
            ErrorText = $"读取版本表失败（不影响搜索，只是版本徽章会不准）：{ex.Message}";
            Status = "版本表没拉到，可以直接搜";
        }

        // 一打开窗口就先给一屏东西 —— 空列表框会让人以为功能没做好
        await RunSearchAsync(0);
    }

    // ── 选项联动 ─────────────────────────────────────────

    partial void OnSelectedTypeChanged(ContentTypeInfo value)
    {
        RebuildLoaders();
        OnPropertyChanged(nameof(TypeNote));
        OnPropertyChanged(nameof(InstallTarget));

        if (_suppress) return;
        _ = RunSearchAsync(0);
    }

    partial void OnSelectedLoaderChanged(LoaderOption? value)
    {
        if (_suppress) return;
        _ = RunSearchAsync(0);
    }

    partial void OnSelectedVersionChanged(VersionOption? value)
    {
        OnPropertyChanged(nameof(CompatSummary));
        if (_suppress) return;
        _ = RunSearchAsync(0);
    }

    partial void OnIsBusyChanged(bool value)
    {
        SearchCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPrevPage));
        OnPropertyChanged(nameof(CanNextPage));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnTranslateDescriptionsChanged(bool value)
    {
        if (_suppress) return;

        if (value)
        {
            _ = StartTranslationAsync();
            return;
        }

        _translateCts?.Cancel();
        foreach (var view in Results) view.ClearTranslation();
        IsTranslating = false;
        TranslationHint = string.Empty;
    }

    // ── 翻译 ─────────────────────────────────────────────

    /// <summary>
    /// 把这一页的简介翻成中文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分两步：<b>先同步吃缓存</b>（零网络，命中立刻显示），剩下的才排队去打接口。
    /// 这样重复搜索时是瞬间出中文的，用户感觉不到等待。
    /// </para>
    /// <para>
    /// 全程吞异常 —— 翻译挂了只是没有中文，搜索结果照常能用。
    /// </para>
    /// </remarks>
    private async Task StartTranslationAsync()
    {
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = null;

        if (!TranslateDescriptions) return;

        var views = Results.ToList();
        if (views.Count == 0)
        {
            TranslationHint = string.Empty;
            return;
        }

        var pending = new List<TranslationRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var view in views)
        {
            if (string.IsNullOrWhiteSpace(view.Description)) continue;

            if (_translator.FromCache(view.Description) is { } cached)
            {
                view.ApplyTranslation(cached);
                continue;
            }

            // 带上项目名：机翻会把 Sodium 当成化学元素「钠」，
            // 有了这份名单就会用占位符绕过去
            if (seen.Add(view.Description))
                pending.Add(TranslationRequest.Create(view.Description, view.Title));
        }

        if (pending.Count == 0)
        {
            var cachedCount = views.Count(v => v.HasDescriptionZh);
            TranslationHint = cachedCount > 0 ? $"{cachedCount} 条简介来自本地缓存（不耗流量）" : string.Empty;
            return;
        }

        var cts = new CancellationTokenSource();
        _translateCts = cts;

        IsTranslating = true;
        TranslationHint = $"正在翻译 {pending.Count} 条简介…";

        try
        {
            await _translator.TranslateBatchAsync(
                pending,
                (original, translated) => _dispatcher.BeginInvoke(() =>
                {
                    foreach (var view in views)
                    {
                        if (view.HasDescriptionZh) continue;
                        if (string.Equals(view.Description, original, StringComparison.Ordinal))
                            view.ApplyTranslation(translated);
                    }
                }),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 切页或关窗口，正常
            return;
        }
        catch (Exception ex)
        {
            TranslationHint = $"翻译出错（不影响搜索）：{ex.GetType().Name}: {ex.Message}";
            return;
        }
        finally
        {
            if (ReferenceEquals(_translateCts, cts))
            {
                IsTranslating = false;
                _translateCts = null;
            }

            cts.Dispose();
        }

        var done = views.Count(v => v.HasDescriptionZh);
        TranslationHint = _translator.QuotaExhausted
            ? $"免费翻译额度今天用完了，{views.Count - done} 条暂时显示英文。明天自动恢复。"
            : $"已翻译 {done} / {views.Count} 条简介";
    }

    /// <summary>按当前类型重建加载器下拉。</summary>
    private void RebuildLoaders()
    {
        var wasSuppressing = _suppress;
        _suppress = true;

        try
        {
            LoaderOptions.Clear();
            LoaderOptions.Add(new LoaderOption(AnyLoader, "不限加载器"));

            foreach (var loader in SelectedType.Loaders)
                LoaderOptions.Add(new LoaderOption(loader, ContentTypeInfo.LoaderLabel(loader)));

            SelectedLoader = LoaderOptions[0];
        }
        finally
        {
            _suppress = wasSuppressing;
        }
    }

    // ── 命令 ─────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => RunSearchAsync(0);

    private bool CanSearch() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private Task NextPageAsync() => RunSearchAsync(Offset + PageSize);

    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    private Task PrevPageAsync() => RunSearchAsync(Math.Max(0, Offset - PageSize));

    /// <summary>
    /// 把项目链接复制到剪贴板。
    /// </summary>
    /// <remarks>
    /// 不直接 <c>Process.Start(链接)</c>：这台机器的 shell 处在半死状态，
    /// 打开外部 URL 会弹「Windows 无法访问指定设备」。复制链接一定能成，
    /// 用户粘到浏览器里效果一样。
    /// </remarks>
    [RelayCommand]
    private void CopyLink(ContentHitView? hit)
    {
        if (hit is null) return;

        var url = $"https://modrinth.com/{UrlSegment(hit.TypeInfo.Type)}/{hit.Slug}";

        try
        {
            Clipboard.SetText(url);
            Status = $"链接已复制：{url}";
        }
        catch (Exception ex)
        {
            Status = "复制失败";
            ErrorText = $"{ex.GetType().Name}: {ex.Message}（链接：{url}）";
        }
    }

    private static string UrlSegment(ContentType type) => type switch
    {
        ContentType.Mod => "mod",
        ContentType.Modpack => "modpack",
        ContentType.Shader => "shader",
        ContentType.ResourcePack => "resourcepack",
        _ => "mod",
    };

    [RelayCommand]
    private async Task ResetFiltersAsync()
    {
        _suppress = true;
        try
        {
            SearchText = string.Empty;
            SelectedLoader = LoaderOptions.FirstOrDefault();
            SelectedVersion = VersionOptions.FirstOrDefault();
        }
        finally
        {
            _suppress = false;
        }

        await RunSearchAsync(0);
    }

    // ── 搜索 ─────────────────────────────────────────────

    /// <summary>
    /// 真正发请求的地方。同一时刻只有一个搜索在跑，新的一来就把旧的掐掉。
    /// </summary>
    private async Task RunSearchAsync(int offset)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsBusy = true;
        QueryHint = string.Empty;
        NetworkHint = string.Empty;

        // 不清 ErrorText：「版本表没拉到」这类提示得留着，否则用户永远不知道
        // 为什么版本下拉只有「不限版本」
        Status = string.IsNullOrWhiteSpace(SearchText) ? "搜索中…" : $"搜索「{SearchText.Trim()}」中…";

        try
        {
            var keyword = SearchText?.Trim() ?? string.Empty;
            var queryText = await ResolveQueryAsync(keyword, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            var query = new ModrinthSearchQuery
            {
                Query = queryText,
                Type = SelectedType.Type,
                GameVersion = SelectedVersion?.Value,
                Loader = SelectedLoader?.Value,
                Offset = offset,
                Limit = PageSize,
                SortBy = SelectedSort?.Value ?? "relevance",
            };

            Log($"搜索：关键词「{query.Query}」类型 {query.Type} 版本 {query.GameVersion ?? "不限"} "
                + $"加载器 {query.Loader ?? "不限"} 偏移 {query.Offset}");
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var result = await _client.SearchAsync(query, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            watch.Stop();

            Offset = result.Offset;
            TotalHits = result.TotalHits;

            Results.Clear();
            foreach (var hit in result.Hits)
                Results.Add(new ContentHitView(hit, SelectedType, query.GameVersion, _rank));

            // 图标异步补：列表先用首字母色块显示，图片下完再换。
            // 翻译有独立的取消令牌，图标就复用同一个搜索令牌 —— 翻页/换搜索时一起掐掉，
            // 不然旧一页的图标会在新一页出来后还往后贴。
            _ = _icons.FillAsync(Results, cts.Token);

            // 搜到了就说明网络是通的，把重试提示擦干净。
            // 注意<b>不清</b> ErrorText —— 那里可能有「版本表没拉到」这种一直成立的提示
            NetworkHint = string.Empty;

            Status = Results.Count == 0
                ? "没有匹配的内容"
                : $"{SelectedType.DisplayName}：这一页 {Results.Count} 条";

            Log($"搜索完成：{result.TotalHits} 条，本页 {Results.Count}，用时 {watch.ElapsedMilliseconds} ms");

            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(CompatSummary));

            // 列表先在英文状态下显示出来，译文随后一条条补 —— 不能等翻译完再显示，
            // 20 条串行机翻要七八秒，那段时间界面空着，用户会以为搜索挂了
            _ = StartTranslationAsync();
        }
        catch (OperationCanceledException)
        {
            // 被新搜索顶掉，或者窗口关了。都不是错误。
        }
        catch (Exception ex)
        {
            Status = "搜索失败";
            Log($"搜索失败：{ex.GetType().Name}: {ex.Message}");
            ErrorText = $"{ex.GetType().Name}: {ex.Message}"
                        + "\n如果一直这样，多半是 api.modrinth.com 连不上。点「搜索」重试，或者换个网络再试。";
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsBusy = false;
                OnPropertyChanged(nameof(CanPrevPage));
                OnPropertyChanged(nameof(CanNextPage));
                OnPropertyChanged(nameof(PageText));
                OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    /// <summary>
    /// 把用户敲的关键词变成 Modrinth 能认的英文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Modrinth 只认英文</b>。搜「优化」它一条都不会给，因为它搜的是项目名和简介里的
    /// 英文字符串。所以中文关键词必须换一道。
    /// </para>
    /// <para>
    /// 两条路，按「先免费后付费」排：
    /// </para>
    /// <list type="number">
    ///   <item><b>本地词表</b> —— 「优化」→ optimization、「机械动力」→ create，
    ///         瞬时完成、不耗翻译额度，而且译法固定可控。</item>
    ///   <item><b>机翻兜底</b> —— 词表覆盖不到的自由文本（「能自动种地的模组」）
    ///         交给 MyMemory 翻成英文。一次一个请求，翻过就缓存。</item>
    /// </list>
    /// <para>
    /// 两条都失败就照原样搜，并在界面上说清楚 —— 让用户看着零条结果猜原因，
    /// 比直接告诉他「没翻成」糟糕得多。
    /// </para>
    /// </remarks>
    private async Task<string> ResolveQueryAsync(string keyword, CancellationToken cancellationToken)
    {
        if (keyword.Length == 0 || !NameLocalizer.HasChinese(keyword)) return keyword;

        if (NameLocalizer.EnglishQuery(keyword) is { } mapped)
        {
            QueryHint = $"中文关键词已换成英文检索「{mapped}」";
            return mapped;
        }

        try
        {
            var translated = await _translator.TranslateQueryAsync(keyword, cancellationToken)
                .ConfigureAwait(true);

            // 机翻给的是整句，压成几个实词再搜 —— 长句会把 Modrinth 的相关性拉偏
            if (NameLocalizer.TrimToKeywords(translated) is { } keywords)
            {
                QueryHint = $"中文关键词已换成英文检索「{keywords}」";
                return keywords;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            QueryHint = $"中文关键词没翻成（{ex.GetType().Name}），只能按原文搜";
            return keyword;
        }

        QueryHint = "这个中文词没能翻成英文，Modrinth 只认英文关键词，结果可能不准";
        return keyword;
    }

    /// <summary>窗口关闭时掐掉在飞的请求，并退订单例服务的事件。</summary>
    public void CancelPendingSearch()
    {
        _searchCts?.Cancel();
        _translateCts?.Cancel();

        // 不退订的话，单例的 TranslationService 会一直握着这个视图模型
        _translator.Log -= _translatorLogHandler;
        _client.Log -= _clientLogHandler;
    }
}
