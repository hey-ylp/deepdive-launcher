using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McLauncher.App.Models;
using McLauncher.Core.Mods;

namespace McLauncher.App.ViewModels;

/// <summary>模组列表的筛选档位。</summary>
/// <param name="Key">给判断用的标识。</param>
/// <param name="Display">下拉里显示的文字。</param>
public sealed record ModFilterChoice(string Key, string Display)
{
    public static readonly ModFilterChoice All = new("all", "全部");
    public static readonly ModFilterChoice Enabled = new("enabled", "已启用");
    public static readonly ModFilterChoice Disabled = new("disabled", "已禁用");
    public static readonly ModFilterChoice Broken = new("broken", "无法识别");

    public static IReadOnlyList<ModFilterChoice> Options { get; } = new[] { All, Enabled, Disabled, Broken };
}

/// <summary>
/// 模组管理窗口的视图模型。
/// </summary>
/// <remarks>
/// <para>
/// 对标 PCL 的「模组管理」页：列出 mods 目录里的所有 jar，能搜、能筛、能启用禁用、能删。
/// </para>
/// <para>
/// <b>不做「检查更新」</b>：那要给每个模组查一次 Modrinth，几十个模组就是几十个请求，
/// 打开页面就卡住；而且我们本地没有 Modrinth 的项目 ID（jar 里只有 modId，不通用）。
/// 真要做得先反查项目，那是另一个工程量。
/// </para>
/// </remarks>
public partial class ModManagerViewModel : ObservableObject
{
    private readonly ModManagerService _service;
    private readonly ModChineseNameService? _nameService;
    private readonly string _modsDirectory;
    private readonly Action<string>? _log;
    private readonly Action<string>? _notify;

    /// <summary>
    /// UI 线程的调度器。
    /// </summary>
    /// <remarks>
    /// <b>必须显式调度，不能指望 <c>await</c> 自动回来</b>：
    /// 扫目录用了 <c>Task.Run</c>，如果调用方（比如探针、或者某个没捕获上下文的路径）
    /// 在没有 SynchronizationContext 的环境里 await，续体会跑在线程池上，
    /// 这时往 <c>ObservableCollection</c> 里塞东西就会抛
    /// 「不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改」。
    /// </remarks>
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _loadCts;

    public ModManagerViewModel(
        ModManagerService service,
        string modsDirectory,
        string title,
        Action<string>? log = null,
        Action<string>? notify = null,
        ModChineseNameService? nameService = null)
    {
        _service = service;
        _nameService = nameService;
        _modsDirectory = modsDirectory;
        _log = log;
        _notify = notify;
        Title = title;

        if (_nameService is not null) _nameService.Log = log;

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Mods.CollectionChanged += (_, _) => RefreshCounts();
    }

    /// <summary>把动作扔到 UI 线程执行（已经在 UI 线程就直接跑）。</summary>
    private Task OnUiAsync(Action action)
        => _dispatcher.CheckAccess() ? RunNow(action) : _dispatcher.InvokeAsync(action).Task;

    private static Task RunNow(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public string Title { get; }

    /// <summary>列表数据。界面直接绑这个。</summary>
    public ObservableCollection<ModItemView> Mods { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>筛选下拉的选项。</summary>
    public IReadOnlyList<ModFilterChoice> Filters { get; } = ModFilterChoice.Options;

    [ObservableProperty]
    private ModFilterChoice _selectedFilter = ModFilterChoice.All;

    /// <summary>底部统计一行字。</summary>
    [ObservableProperty]
    private string _summaryText = "还没加载";

    /// <summary>搜索框下面的小字（「12 个里显示 5 个」这种）。</summary>
    [ObservableProperty]
    private string _filterHint = string.Empty;

    /// <summary>列表是否为空（显示空状态提示）。</summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>空状态下的提示文字。</summary>
    [ObservableProperty]
    private string _emptyHint = "这个模组文件夹是空的";

    // ── 加载 ──

    /// <summary>重新扫描一遍并刷新界面。</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (IsLoading) return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsLoading = true;

        try
        {
            if (!Directory.Exists(_modsDirectory))
            {
                await OnUiAsync(() =>
                {
                    Mods.Clear();
                    SummaryText = "模组文件夹还不存在";
                    EmptyHint = "这个版本还没启动过，或者从来没装过模组";
                    IsEmpty = true;
                });
                return;
            }

            var models = await _service.ScanAsync(_modsDirectory, cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested) return;

            List<ModItemView> created = new();

            await OnUiAsync(() =>
            {
                Mods.Clear();
                foreach (var model in models)
                {
                    var item = new ModItemView(model);
                    Mods.Add(item);
                    created.Add(item);
                }

                SummaryText = Mods.Count == 0
                    ? "一个模组都没有"
                    : $"{Mods.Count} 个模组文件";

                EmptyHint = "这个模组文件夹是空的\n去「内容」页搜一个装上，或者自己把 .jar 拖进来";
                ApplyFilter();
            });

            // 图标：几十个 jar 各读一张小图（每个几 KB），串行 await 掉。
            // 不 fire-and-forget 是因为那样窗口会先闪一下「?」占位再逐个跳，
            // 而且调用方（含探针）拿不到「加载完成」的时机。
            await LoadIconsAsync(created, cts.Token).ConfigureAwait(false);

            // 中文名：要联网，而且 MC百科那边只能一个一个问（还得限速），
            // 三十个模组得十几秒。所以<b>不 await</b> —— 列表先按英文名用起来，
            // 名字查到一个就补一个。等它会让窗口白屏十几秒，那是不能接受的。
            NameLookupTask = LoadChineseNamesAsync(created, cts.Token);

            _log?.Invoke($"[模组] 扫描 {_modsDirectory}：{Mods.Count} 个文件");
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => SummaryText = "读取失败");
            _log?.Invoke($"[模组] 扫描失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    /// <summary>
    /// 逐个读图标。
    /// </summary>
    /// <remarks>
    /// <b>用 <see cref="BitmapCacheOption.OnLoad"/> + Freeze</b>：直接在后台线程造 BitmapImage
    /// 然后冻结，冻结后就能跨线程用。不 Freeze 的话赋值给 UI 属性会抛「另一个线程拥有该对象」。
    /// </remarks>
    private async Task LoadIconsAsync(IReadOnlyList<ModItemView> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var meta = item.Model.Metadata;
            if (meta?.IconEntry is null || meta.FullPath is null) continue;

            byte[]? bytes;
            try
            {
                bytes = await Task.Run(
                    () => ModPreflight.ReadIcon(meta.FullPath, meta.IconEntry), cancellationToken);
            }
            catch
            {
                continue;
            }

            if (bytes is null || cancellationToken.IsCancellationRequested) continue;

            // 解码放回 UI 线程。在后台线程造 BitmapImage 再 Freeze 这条路看着可行，
            // 实测 EndInit 之后拿到的对象仍然会失败（几十个图标无一成功）。
            // 几十张 64px 的小图，UI 线程解码总共几十毫秒，不值得为它折腾线程亲和性。
            await OnUiAsync(() =>
            {
                var image = TryBuildImage(bytes);
                if (image is not null) item.Icon = image;
            });
        }
    }

    private ImageSource? TryBuildImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            // 有些模组的"图标"其实是 webp / 动图 / 根本不是图片
            _log?.Invoke($"[图标] 解码失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ── 筛选 ──

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(ModFilterChoice value) => ApplyFilter();

    /// <summary>按当前筛选条件刷新每行的可见性和统计。</summary>
    private void ApplyFilter()
    {
        var keyword = SearchText?.Trim() ?? string.Empty;
        var filter = SelectedFilter?.Key ?? "all";

        var visible = 0;

        foreach (var item in Mods)
        {
            var hit = filter switch
            {
                "enabled" => item.IsEnabled,
                "disabled" => !item.IsEnabled,
                "broken" => item.IsBroken,
                _ => true,
            };

            if (hit && keyword.Length > 0)
                hit = Matches(item, keyword);

            item.IsVisible = hit;
            if (hit) visible++;
        }

        IsEmpty = visible == 0;
        if (visible == 0 && Mods.Count > 0)
            EmptyHint = keyword.Length > 0
                ? $"没有匹配「{keyword}」的模组"
                : "当前筛选条件下没有模组";

        FilterHint = Mods.Count == 0
            ? string.Empty
            : visible == Mods.Count
                ? string.Empty
                : $"{Mods.Count} 个里显示 {visible} 个";

        RefreshCounts();
    }

    private static bool Matches(ModItemView item, string keyword)
    {
        static bool Has(string? text, string key)
            => !string.IsNullOrEmpty(text) && text.Contains(key, StringComparison.OrdinalIgnoreCase);

        return Has(item.DisplayName, keyword)      // 有中文名时这里是中文名
               || Has(item.OriginalName, keyword)  // 原名也要能搜到（中文名和英文名都算）
               || Has(item.ModIdLabel, keyword)
               || Has(item.AuthorsLabel, keyword)
               || Has(item.FileName, keyword)
               || Has(item.DescriptionLabel, keyword);
    }

    private void RefreshCounts()
    {
        if (Mods.Count == 0)
        {
            SummaryText = "一个模组都没有";
            return;
        }

        var enabled = Mods.Count(m => m.IsEnabled);
        var broken = Mods.Count(m => m.IsBroken);

        var text = $"{Mods.Count} 个模组 · 启用 {enabled} · 禁用 {Mods.Count - enabled}";
        if (broken > 0) text += $" · {broken} 个无法识别";

        SummaryText = text;
    }

    // ── 操作 ──

    /// <summary>启用 / 禁用（改文件名后缀）。</summary>
    [RelayCommand]
    private async Task ToggleAsync(ModItemView? item)
    {
        if (item is null || item.IsBusy || item.IsBroken) return;

        item.IsBusy = true;

        try
        {
            var target = !item.IsEnabled;
            var error = _service.SetEnabled(item.Model, target);

            if (error is not null)
            {
                _notify?.Invoke($"改不了：{error}");
                _log?.Invoke($"[模组] 切换 {item.FileName} 失败：{error}");
                return;
            }

            // 文件名变了，重新解析一遍拿到新的路径和状态
            var rescan = await _service.ScanAsync(_modsDirectory).ConfigureAwait(false);

            var again = rescan.FirstOrDefault(m =>
                string.Equals(m.DisplayName, item.DisplayName, StringComparison.Ordinal)
                && string.Equals(m.ModIdLabel, item.ModIdLabel, StringComparison.Ordinal));

            if (again is not null)
            {
                await OnUiAsync(() =>
                {
                    item.Update(again);
                    ApplyFilter();
                });
            }
            else
            {
                await ReloadAsync();
            }

            _log?.Invoke($"[模组] {item.DisplayName} → {item.StateLabel}");
        }
        catch (Exception ex)
        {
            _notify?.Invoke($"操作失败：{ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    /// <summary>删掉（挪到 .trash）。</summary>
    [RelayCommand]
    private async Task DeleteAsync(ModItemView? item)
    {
        if (item is null || item.IsBusy) return;

        var answer = MessageBox.Show(
            $"把「{item.DisplayName}」从模组文件夹里移走？\n\n"
            + $"文件：{item.FileName}\n"
            + $"（不会真删，会挪到 mods\\{ModManagerService.TrashFolderName}\\ 里，想找回可以从那儿拖回来）",
            "移除模组",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        item.IsBusy = true;

        try
        {
            var error = _service.Delete(item.Model);
            if (error is not null)
            {
                _notify?.Invoke($"删不掉：{error}");
                return;
            }

            await OnUiAsync(() =>
            {
                Mods.Remove(item);
                ApplyFilter();
            });

            _log?.Invoke($"[模组] 移走 {item.FileName}（在 {ModManagerService.TrashFolderName} 里）");
            _notify?.Invoke($"已移到 .trash：{item.FileName}");
        }
        catch (Exception ex)
        {
            _notify?.Invoke($"操作失败：{ex.Message}");
        }
        finally
        {
            item.IsBusy = false;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 正在跑的中文名查询。窗口关闭时探针/宿主可以等它结束。
    /// </summary>
    /// <remarks>
    /// 列表不等它 —— 十几秒的东西不能挡在打开窗口的路上。
    /// 但关窗口时要能取消，所以留个句柄。
    /// </remarks>
    public Task? NameLookupTask { get; private set; }

    /// <summary>
    /// 挨个查中文名，查到就立刻更新那一行。
    /// </summary>
    /// <remarks>
    /// 用 <c>onFound</c> 逐条回调而不是等整批返回：MC百科是串行查的，
    /// 整批等下来就是「转十几秒，然后名字一起蹦出来」；
    /// 逐条更新则是「一个个冒出来」，用户能看见进展，也不觉得卡。
    /// </remarks>
    private async Task LoadChineseNamesAsync(
        IReadOnlyList<ModItemView> items, CancellationToken cancellationToken)
    {
        if (_nameService is null) return;

        var byModId = items
            .Where(i => i.Model.Metadata is not null)
            .GroupBy(i => i.Model.Metadata!.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (byModId.Count == 0) return;

        var queries = byModId.Values
            .Select(g => new ModNameQuery(g[0].Model.Metadata!.ModId, g[0].Model.Metadata!.Name))
            .ToList();

        var found = 0;

        // 回调可能来自线程池（SCL 是 6 路并发），必须回 UI 线程再改绑定属性
        void OnFound(string modId, string name)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!byModId.TryGetValue(modId, out var targets)) return;

            Interlocked.Increment(ref found);

            _ = OnUiAsync(() =>
            {
                foreach (var item in targets) item.ChineseName = name;

                // 名字变了要重新过一遍搜索过滤：用户可能正按中文名搜
                ApplyFilter();
            });
        }

        try
        {
            await _nameService.LookupAsync(queries, cancellationToken, OnFound).ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                _log?.Invoke($"[模组] 中文名：{queries.Count} 个里查到 {found} 个");
                RefreshCounts();
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[模组] 查中文名失败（不影响别的功能）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>停掉在飞的加载（窗口关闭时调）。</summary>
    public void CancelPending()
    {
        try { _loadCts?.Cancel(); } catch { /* 已经没了就算了 */ }
    }
}
