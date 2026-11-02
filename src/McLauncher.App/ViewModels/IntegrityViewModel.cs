using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McLauncher.Core.Launcher;

namespace McLauncher.App.ViewModels;

/// <summary>
/// 「校验文件」窗口的视图模型：跑深度校验（算 sha1）、列问题、一键修复。
/// </summary>
/// <remarks>
/// 和启动前那次快速校验的区别：这里是用户可以主动点、也能看见每个文件的那种，
/// 所以跑 <see cref="IntegrityDepth.Deep"/>（几 GB 读一遍盘，几十秒）。
/// </remarks>
public partial class IntegrityViewModel : ObservableObject
{
    private readonly FileIntegrityChecker _checker;
    private readonly MinecraftLauncherService _launcher;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private IntegrityReport? _lastReport;

    public IntegrityViewModel(
        FileIntegrityChecker checker,
        MinecraftLauncherService launcher,
        string versionName,
        Action<string>? log = null)
    {
        _checker = checker;
        _launcher = launcher;
        _log = log;
        VersionName = versionName;
    }

    public string VersionName { get; }

    public string Title => $"校验文件 · {VersionName}";

    public ObservableCollection<IntegrityIssue> Issues { get; } = new();

    [ObservableProperty]
    private string _status = "点「开始校验」检查这个版本的文件（会逐个算 sha1，几个 GB 要几十秒）";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _currentFile = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isHealthy;

    /// <summary>「3672 个文件全部正常（12.3 秒）」这种一句话结论。</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _checkedInfo = string.Empty;

    /// <summary>有没有能自动修的问题（缺文件 / 大小不对 / 内容损坏 / 空文件）。</summary>
    public bool CanRepair => !IsBusy && _lastReport is { } r && r.CanRepair;

    [RelayCommand]
    private async Task CheckAsync()
    {
        await RunAsync(IntegrityDepth.Deep).ConfigureAwait(true);
    }

    /// <summary>校验并修复：删掉坏文件 → 重新下载 → 再校验一遍。</summary>
    [RelayCommand]
    private async Task RepairAsync()
    {
        if (_lastReport is null) return;

        if (!_lastReport.CanRepair)
        {
            Status = "这些问题自动修不了（模组文件没有校验基准，得手动换）";
            return;
        }

        var removed = _checker.DeleteBrokenFiles(_lastReport);
        _log?.Invoke($"[完整性] 删除 {removed} 个损坏文件后重新下载");
        Status = $"已删掉 {removed} 个损坏文件，正在重新下载…";

        IsBusy = true;
        try
        {
            await _launcher.InstallAsync(VersionName).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"下载失败：{ex.GetType().Name}: {ex.Message}";
            _log?.Invoke($"[完整性] 修复时下载失败：{ex.Message}");
            IsBusy = false;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        _log?.Invoke("[完整性] 重新下载完成，复查中");
        await RunAsync(IntegrityDepth.Deep).ConfigureAwait(true);
    }

    private async Task RunAsync(IntegrityDepth depth)
    {
        if (IsBusy) return;

        IsBusy = true;
        Issues.Clear();
        Summary = string.Empty;
        CheckedInfo = string.Empty;
        IsHealthy = false;
        ProgressPercent = 0;
        Status = depth == IntegrityDepth.Deep ? "正在逐个算 sha1…" : "正在检查…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var progress = new Progress<IntegrityProgress>(p =>
        {
            ProgressPercent = p.Fraction * 100;
            Status = $"{p.Phase}：{p.Done}/{p.Total}";
            if (!string.IsNullOrEmpty(p.CurrentFile)) CurrentFile = p.CurrentFile;
        });

        try
        {
            var report = await _checker
                .CheckAsync(VersionName, depth, progress, _cts.Token)
                .ConfigureAwait(true);

            _lastReport = report;

            foreach (var issue in report.Issues) Issues.Add(issue);

            IsHealthy = report.IsHealthy;
            Summary = report.Summary;
            CheckedInfo = $"共检查 {report.CheckedFiles} 个文件，用时 {report.Elapsed.TotalSeconds:F1} 秒";
            Status = report.IsHealthy
                ? "文件完整，不需要修复"
                : $"发现 {report.ProblemCount} 处问题" + (report.CanRepair ? "，可以点「修复」自动重下" : "，这些只能手动处理");

            _log?.Invoke($"[完整性] {report.Summary}");
            foreach (var issue in report.Issues.Take(20))
                _log?.Invoke($"   · {issue}");
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception ex)
        {
            Status = $"{ex.GetType().Name}: {ex.Message}";
            _log?.Invoke($"[完整性] 校验失败：{ex}");
        }
        finally
        {
            IsBusy = false;
            CurrentFile = string.Empty;
            ProgressPercent = 0;
            OnPropertyChanged(nameof(CanRepair));
        }
    }

    /// <summary>窗口关掉时掐掉在跑的校验。</summary>
    public void Close()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
    }
}
