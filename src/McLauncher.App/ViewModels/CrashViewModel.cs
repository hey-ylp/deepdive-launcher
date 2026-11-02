using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using McLauncher.Core.AI;
using McLauncher.Core.Launcher;

namespace McLauncher.App.ViewModels;

/// <summary>
/// 崩溃分析窗口的视图模型：把 <see cref="CrashAnalysis"/> 摊成人能读的样子。
/// </summary>
/// <remarks>
/// 只在窗口里做展示和「一键禁用嫌疑模组」这一件事，分析逻辑全在
/// <see cref="CrashAnalyzer"/> 里 —— 那边能单独测，这边只管画。
/// </remarks>
public partial class CrashViewModel : ObservableObject
{
    private readonly CrashAnalysis _analysis;
    private readonly Action<string>? _log;
    private readonly string? _instanceRoot;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>AI 正在跑（按钮要变成「分析中…」并禁用）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnalyzeWithAi))]
    private bool _isAiBusy;

    /// <summary>AI 按钮上的字。</summary>
    [ObservableProperty]
    private string _aiButtonText = "让 AI 再分析一遍";

    /// <param name="analysis">本地规则引擎的结果。</param>
    /// <param name="versionName">版本名（只用来做窗口标题）。</param>
    /// <param name="log">写日志的回调。</param>
    /// <param name="instanceRoot">
    /// 实例根目录 —— 读 DeepSeek Key 用。<c>null</c> 表示没配上，
    /// 那 AI 按钮就置灰（总比点了才提示「没 Key」好）。
    /// </param>
    public CrashViewModel(
        CrashAnalysis analysis,
        string versionName,
        Action<string>? log = null,
        string? instanceRoot = null)
    {
        _analysis = analysis;
        _log = log;
        _instanceRoot = instanceRoot;

        Title = $"崩溃分析 · {versionName}";

        Findings = new ObservableCollection<CrashFindingView>(
            analysis.Findings.Select(f => new CrashFindingView(f)));

        Subtitle = BuildSubtitle(analysis);
        StatusMessage = BuildStatusMessage(analysis);
    }

    /// <summary>能不能点 AI 分析（有 Key 且当前没在跑）。</summary>
    public bool CanAnalyzeWithAi => !IsAiBusy && HasApiKey;

    /// <summary>设置页填过 DeepSeek Key 没有。</summary>
    private bool HasApiKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_instanceRoot)) return false;
            return !string.IsNullOrWhiteSpace(LauncherSettings.ResolveDeepSeekApiKey(_instanceRoot));
        }
    }

    /// <summary>
    /// 规则没命中时，把现场发给 AI 再判一次。
    /// </summary>
    /// <remarks>
    /// <b>只在用户点的时候发请求</b> —— 每次都要钱，静默调用等于偷钱。
    /// 结论插到列表最上面，标题带「AI：」前缀，让用户分得清哪条是本地规则说的、哪条是模型猜的。
    /// </remarks>
    [RelayCommand]
    private async Task AnalyzeWithAiAsync()
    {
        if (IsAiBusy || string.IsNullOrWhiteSpace(_instanceRoot)) return;

        var apiKey = LauncherSettings.ResolveDeepSeekApiKey(_instanceRoot);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusMessage = "还没填 DeepSeek API Key，去设置页填一个";
            return;
        }

        IsAiBusy = true;
        AiButtonText = "AI 分析中…";
        StatusMessage = "正在把日志发给 DeepSeek，第一次可能要等十几秒…";

        try
        {
            // 模型名从设置里读，没填就用默认 deepseek-flash（官方改名时不用改代码）
            var model = LauncherSettings.ResolveAiModel(_instanceRoot);
            var advisor = new AiCrashAdvisor(apiKey, model);

            var context = CrashAnalyzer.BuildAiContext(_analysis.GameDir);
            var mods = CrashAnalyzer.ListModFiles(_analysis.GameDir);
            var memoryMb = LauncherSettings.ResolveMemoryMb(_instanceRoot);

            _log?.Invoke($"[崩溃分析] 请求 AI 分析（{mods.Count} 个模组，日志 {context.Length} 字符）");

            var advice = await advisor.AnalyzeAsync(_analysis, context, mods, memoryMb);

            if (!advice.Success)
            {
                StatusMessage = $"AI 分析失败：{advice.Error}";
                _log?.Invoke($"[崩溃分析] AI 失败：{advice.Error}");
                return;
            }

            Findings.Insert(0, new CrashFindingView(ToFinding(advice)));

            StatusMessage = advice.Confidence switch
            {
                "high" => "AI 给了一条高置信度的结论（见最上面那条）",
                "low" => "AI 也没把握 —— 结论仅供参考，别急着删模组",
                _ => "AI 给了参考结论（见最上面那条）",
            };

            _log?.Invoke($"[崩溃分析] AI 结论（{advice.Confidence}）：{advice.Cause}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"AI 分析出错：{ex.Message}";
            _log?.Invoke($"[崩溃分析] AI 出错：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsAiBusy = false;
            AiButtonText = "让 AI 再分析一遍";
        }
    }

    /// <summary>把 AI 的回答折成和本地规则同一种结构，好让界面复用同一套模板。</summary>
    private static CrashFinding ToFinding(AiCrashAdvice advice)
    {
        var steps = advice.Steps.Count == 0
            ? "（AI 没给出具体步骤）"
            : string.Join("\n", advice.Steps.Select((s, i) => $"{i + 1}. {s}"));

        var explanation = advice.Explanation.Length == 0
            ? "（AI 没给解释）"
            : advice.Explanation;

        return new CrashFinding(
            "AI：" + advice.Cause,
            advice.Confidence == "high" ? CrashSeverity.Warning : CrashSeverity.Info,
            explanation + $"\n（模型置信度：{advice.Confidence}）",
            steps,
            advice.Evidence,
            Array.Empty<string>());
    }

    /// <summary>
    /// 窗口顶部那句大字。
    /// </summary>
    /// <remarks>
    /// 「没找到能确定的原因」只在<b>真的什么都没读到</b>时才说。
    /// 没有崩溃报告但日志正常的情况要明说「这次是好的」，
    /// 否则用户点了按钮看到一句「没找到原因」，会当这个功能是废的。
    /// </remarks>
    private static string BuildStatusMessage(CrashAnalysis analysis)
    {
        if (analysis.Findings.Count == 0) return "没找到能确定的原因";

        var fatal = analysis.Findings.Count(f => f.Severity == CrashSeverity.Fatal);
        if (fatal > 0)
            return $"找到 {analysis.Findings.Count} 条结论，其中 {fatal} 条致命";

        var warnings = analysis.Findings.Count(f => f.Severity == CrashSeverity.Warning);
        if (warnings > 0)
        {
            return analysis.HasCrashReport
                ? "有崩溃报告，但没有命中致命规则"
                : $"日志里有 {warnings} 处需要注意，但这次没崩";
        }

        return "这次启动没有崩溃";
    }

    /// <summary>窗口标题。</summary>
    public string Title { get; }

    /// <summary>一行说明：读的哪个文件、什么版本、什么 Java。</summary>
    public string Subtitle { get; }

    public ObservableCollection<CrashFindingView> Findings { get; }

    public bool HasFindings => Findings.Count > 0;

    public bool HasSuspects => _analysis.Findings.Any(f => f.Suspects.Count > 0);

    /// <summary>有没有真的崩溃报告文件（决定「打开崩溃报告」按钮能不能点）。</summary>
    public bool HasReportFile => _analysis.HasCrashReport
                                 && _analysis.SourcePath is { Length: > 0 } path
                                 && File.Exists(path);

    public string ReportPath => _analysis.SourcePath ?? string.Empty;

    /// <summary>把结论里所有嫌疑模组挪进 <c>mods-disabled/</c>。</summary>
    [RelayCommand]
    private void DisableSuspects()
    {
        var suspects = _analysis.Findings
            .SelectMany(f => f.Suspects)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (suspects.Count == 0)
        {
            StatusMessage = "这些结论没有定位到具体模组，没法自动禁用";
            return;
        }

        // 禁用模组是「改动用户的东西」，必须先让他看清单再确认
        var confirm = MessageBox.Show(
            "下面这些模组会被挪进 mods-disabled（不删除，随时能挪回来）："
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, suspects.Select(s => "· " + s))
            + Environment.NewLine + Environment.NewLine
            + "挪完需要重新启动游戏才生效。继续吗？",
            "禁用嫌疑模组",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        var moved = CrashAnalyzer.DisableMods(_analysis.GameDir, suspects);

        if (moved.Count == 0)
        {
            StatusMessage = "一个都没挪动（可能文件已经不在了，或者被游戏占着）";
            return;
        }

        foreach (var name in moved)
            _log?.Invoke($"[崩溃分析] 已禁用 {name}（挪进 mods-disabled）");

        StatusMessage = $"已禁用 {moved.Count} 个模组，重启游戏即可生效";

        MessageBox.Show(
            $"已挪走 {moved.Count} 个模组：{Environment.NewLine}{Environment.NewLine}"
            + string.Join(Environment.NewLine, moved.Select(m => "· " + m))
            + Environment.NewLine + Environment.NewLine
            + "它们在 mods-disabled 目录里，想恢复直接挪回 mods 就行。",
            "已禁用",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string BuildSubtitle(CrashAnalysis analysis)
    {
        var parts = new List<string>();

        if (analysis.HasCrashReport && analysis.SourcePath is { Length: > 0 } path)
            parts.Add($"来源：{Path.GetFileName(path)}");
        else
            parts.Add("来源：latest.log（没有崩溃报告，看的是最近一次启动）");

        if (analysis.Description is { Length: > 0 } desc)
            parts.Add($"游戏说的原因：{desc}");

        if (analysis.GameVersion is { Length: > 0 } mc)
            parts.Add($"游戏版本 {mc}");

        if (analysis.JavaVersion is { Length: > 0 } java)
            parts.Add($"Java {java}");

        return string.Join("  ·  ", parts);
    }
}

/// <summary>一条结论在界面上的样子。</summary>
public sealed class CrashFindingView
{
    public CrashFindingView(CrashFinding finding)
    {
        Title = finding.Title;
        Explanation = finding.Explanation;
        Advice = finding.Advice;

        SeverityText = finding.Severity switch
        {
            CrashSeverity.Fatal => "致命",
            CrashSeverity.Warning => "警告",
            _ => "提示",
        };

        // 颜色直接在视图模型里给：XAML 里用 DataTrigger 又要多一个转换器，
        // 而窗口级资源漏定义就是「点一下就崩」的那个坑，能少一处是一处
        SeverityBrush = finding.Severity switch
        {
            CrashSeverity.Fatal => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
            CrashSeverity.Warning => new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
        };

        SeverityBackground = finding.Severity switch
        {
            CrashSeverity.Fatal => new SolidColorBrush(Color.FromArgb(0x1F, 0xC6, 0x28, 0x28)),
            CrashSeverity.Warning => new SolidColorBrush(Color.FromArgb(0x1F, 0xE0, 0x8A, 0x00)),
            _ => new SolidColorBrush(Color.FromArgb(0x1F, 0x8A, 0x8A, 0x8A)),
        };

        EvidenceText = finding.Evidence.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, finding.Evidence);

        HasEvidence = finding.Evidence.Count > 0;

        SuspectsText = finding.Suspects.Count == 0
            ? string.Empty
            : "嫌疑模组：" + string.Join("、", finding.Suspects);

        HasSuspects = finding.Suspects.Count > 0;
    }

    public string Title { get; }
    public string Explanation { get; }
    public string Advice { get; }
    public string SeverityText { get; }
    public Brush SeverityBrush { get; }
    public Brush SeverityBackground { get; }
    public string EvidenceText { get; }
    public bool HasEvidence { get; }
    public string SuspectsText { get; }
    public bool HasSuspects { get; }
}
