using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace McLauncher.App.Views;

/// <summary>
/// 内置文件夹浏览窗口 —— 系统资源管理器打不开时的兜底。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由很现实：<c>ShellExecute</c> / <c>Shell.Application.Explore</c> 打开目录
/// 依赖系统的 shell 组件，那个东西一旦坏了（本机就坏过：抛「Windows 无法访问指定设备、路径或文件」），
/// 用户在启动器里点「打开模组文件夹」就只剩一个错误框，什么也干不了 —— 而他要的其实只是
/// 「把我的模组文件列出来」。
/// </para>
/// <para>
/// 所以这里不依赖 shell：直接用 <see cref="Directory"/> 列目录、自己画列表。
/// 双击文件夹进入下一层，双击文件才交给系统（那一步失败会明确告诉用户复制路径）。
/// </para>
/// </remarks>
public partial class FolderWindow : FluentWindow
{
    private readonly ObservableCollection<FsEntry> _entries = new();

    private string _currentPath = string.Empty;

    /// <summary>最近一次尝试交给系统的结果，失败时给用户看。</summary>
    private string _lastShellError = string.Empty;

    /// <summary>
    /// 调用方给的「当前版本真正的游戏目录」，快捷跳转以它为准。
    /// </summary>
    /// <remarks>
    /// <b>必须有这个</b>：版本隔离开启时 <c>mods</c> 在 <c>versions/&lt;版本名&gt;/</c> 下，
    /// 而实例根下也有一个 <c>mods</c>（老布局留下的空壳）。光看路径根本分不出哪个是游戏真正读的 ——
    /// 实测就踩过：启动器显示「2 个模组」，点「模组」跳过去却是空的。
    /// </remarks>
    private readonly string? _gameDirHint;

    /// <summary>
    /// 「用模组管理打开」的回调。只有当前目录是 <c>mods</c> 时才会显示那个按钮。
    /// </summary>
    /// <remarks>
    /// 加这个是因为实际用起来发现：用户想看「装了哪些模组」，第一反应是打开 mods 文件夹，
    /// 然后对着 <c>sodium-neoforge-0.9.3-beta.1+mc26.3.jar</c> 这种文件名发愁。
    /// 这个窗口是文件浏览器，本来就不该显示中文名和图标 —— 但得在用户困惑的地方
    /// 给他一条正确的路。
    /// </remarks>
    private readonly Action<string>? _openModManager;

    public FolderWindow(
        string path,
        string caption,
        string? gameDirHint = null,
        Action<string>? openModManager = null)
    {
        InitializeComponent();

        WindowTitleBar.Title = caption;
        FileList.ItemsSource = _entries;
        _gameDirHint = gameDirHint;
        _openModManager = openModManager;

        Navigate(path);
    }

    /// <summary>当前目录就是 mods 时，把「用模组管理打开」露出来。</summary>
    private void UpdateManageModsButton()
    {
        var isMods = string.Equals(
            Path.GetFileName(_currentPath.TrimEnd(Path.DirectorySeparatorChar)),
            "mods",
            StringComparison.OrdinalIgnoreCase);

        ManageMods.Visibility = isMods && _openModManager is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnManageModsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _openModManager?.Invoke(_currentPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开模组管理失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void Navigate(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusText.Text = $"目录不存在：{path}";
            return;
        }

        _currentPath = path;
        PathText.Text = path;
        Title = $"{System.IO.Path.GetFileName(path.TrimEnd('\\'))} — McLauncher";

        UpdateManageModsButton();

        _entries.Clear();

        var dirCount = 0;
        var fileCount = 0;
        var denied = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                var entry = FsEntry.FromDirectory(dir);
                if (entry is null) { denied++; continue; }
                _entries.Add(entry);
                dirCount++;
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                var entry = FsEntry.FromFile(file);
                if (entry is null) { denied++; continue; }
                _entries.Add(entry);
                fileCount++;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取目录失败：{ex.GetType().Name}: {ex.Message}";
            return;
        }

        // 文件夹在前、文件在后，各自按名字排 —— 资源管理器的习惯顺序
        var sorted = _entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _entries.Clear();
        foreach (var entry in sorted)
            _entries.Add(entry);

        StatusText.Text = $"{dirCount} 个文件夹，{fileCount} 个文件" +
                          (denied > 0 ? $"，{denied} 项读不到（权限不足）" : string.Empty) +
                          _lastShellError;

        UpdateEmptyHint(fileCount);
        UpdateJumpButtons();
    }

    /// <summary>
    /// 空目录给一句人话，别留一片空白让人以为没加载出来。
    /// </summary>
    private void UpdateEmptyHint(int fileCount)
    {
        if (_entries.Count > 0)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        // 空 mods 目录是最常见的情况，直接告诉用户下一步干什么
        var isMods = string.Equals(System.IO.Path.GetFileName(_currentPath), "mods",
            StringComparison.OrdinalIgnoreCase);

        EmptyHint.Text = isMods
            ? "这个模组文件夹是空的\n去「内容」页搜一个装上，或者自己把 .jar 拖进来"
            : "这个目录是空的";

        EmptyHint.Visibility = Visibility.Visible;

        _ = fileCount;
    }

    // ── 快捷跳转 ────────────────────────────────────────

    /// <summary>
    /// 从当前路径往上找「游戏目录根」，用来给快捷按钮定位。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>优先用调用方给的 <c>_gameDirHint</c></b>。只有在没给的时候才按路径猜 ——
    /// 版本隔离开着时 <c>mods</c> 在 <c>versions/&lt;版本名&gt;/mods</c>，
    /// 而实例根下那个 <c>mods</c> 是老布局的空壳，猜错就会跳进一个空目录。
    /// </para>
    /// <para>
    /// 猜的规则：<c>options.txt</c>、<c>versions</c>、<c>mods</c>、<c>saves</c>
    /// 出现任一个就认为是游戏目录。最多往上走 4 层，防止一路爬到盘符根。
    /// </para>
    /// </remarks>
    private string? FindGameDirectory()
    {
        // 调用方知道当前版本的真实目录就用它 —— 这个最准，别去猜
        if (!string.IsNullOrWhiteSpace(_gameDirHint) && Directory.Exists(_gameDirHint))
            return _gameDirHint;

        var dir = new DirectoryInfo(_currentPath);

        for (var i = 0; i < 4 && dir is not null; i++)
        {
            try
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "options.txt")))
                    return dir.FullName;

                foreach (var marker in new[] { "versions", "mods", "saves", "resourcepacks" })
                {
                    if (Directory.Exists(System.IO.Path.Combine(dir.FullName, marker)))
                        return dir.FullName;
                }
            }
            catch
            {
                return null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// 刷新快捷按钮状态。
    /// </summary>
    /// <remarks>
    /// <b>不做成「目录不存在就禁用」</b>：禁用之后用户点不动，也不知道为什么 ——
    /// 是启动器坏了还是本来就没有？宁可让它能点，点了明说「还没这个文件夹，
    /// 游戏启动过一次才有」。已经在里面的那个按钮压暗，表示你现在就在这。
    /// </remarks>
    private void UpdateJumpButtons()
    {
        var root = FindGameDirectory();
        var insideGameDir = root is not null;
        var current = System.IO.Path.GetFileName(_currentPath.TrimEnd('\\'));

        void Apply(System.Windows.Controls.Button button, string folder)
        {
            button.IsEnabled = insideGameDir;
            button.Opacity = string.Equals(current, folder, StringComparison.OrdinalIgnoreCase)
                ? 0.45
                : 1.0;
        }

        Apply(JumpMods, "mods");
        Apply(JumpSaves, "saves");
        Apply(JumpConfig, "config");
        Apply(JumpResourcePacks, "resourcepacks");
        Apply(JumpShaderPacks, "shaderpacks");
        Apply(JumpLogs, "logs");
    }

    private void JumpTo(string folder)
    {
        var root = FindGameDirectory();
        if (root is null)
        {
            StatusText.Text = "这个位置看起来不是游戏目录，找不到模组/存档那些子文件夹";
            return;
        }

        var target = System.IO.Path.Combine(root, folder);
        if (!Directory.Exists(target))
        {
            StatusText.Text = $"还没有「{folder}」文件夹 —— 游戏启动过一次、或者装过东西才会有";
            return;
        }

        _lastShellError = string.Empty;
        Navigate(target);
    }

    private void OnJumpMods(object sender, RoutedEventArgs e) => JumpTo("mods");
    private void OnJumpSaves(object sender, RoutedEventArgs e) => JumpTo("saves");
    private void OnJumpConfig(object sender, RoutedEventArgs e) => JumpTo("config");
    private void OnJumpResourcePacks(object sender, RoutedEventArgs e) => JumpTo("resourcepacks");
    private void OnJumpShaderPacks(object sender, RoutedEventArgs e) => JumpTo("shaderpacks");
    private void OnJumpLogs(object sender, RoutedEventArgs e) => JumpTo("logs");

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent is not null)
            Navigate(parent.FullName);
        else
            StatusText.Text = "已经到顶了";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _lastShellError = string.Empty;
        Navigate(_currentPath);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_currentPath);
            StatusText.Text = $"路径已复制：{_currentPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>还是先试系统资源管理器 —— 它好的时候比这个窗口好用。</summary>
    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = _currentPath,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            });
            StatusText.Text = "已请求系统打开，如果没反应就是资源管理器本身有问题";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"系统拒绝打开：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FsEntry entry) return;

        if (entry.IsDirectory)
        {
            _lastShellError = string.Empty;
            Navigate(entry.FullPath);
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = entry.FullPath,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            });
            _lastShellError = string.Empty;
            StatusText.Text = $"已交给系统打开：{entry.Name}";
        }
        catch (Exception ex)
        {
            // 打不开就老老实实说，并把路径复制好 —— 用户至少能自己想办法
            _lastShellError = $"，上次打开「{entry.Name}」失败：{ex.Message}";
            StatusText.Text = StatusText.Text + _lastShellError;
            try { Clipboard.SetText(entry.FullPath); } catch { /* 复制不了就算了 */ }
        }
    }
}

/// <summary>列表里的一行。目录只显示类型和时间，大小留空（算目录大小要遍历，很慢）。</summary>
public sealed class FsEntry
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public string KindLabel { get; init; } = string.Empty;

    public string SizeLabel { get; init; } = string.Empty;

    public string ModifiedLabel { get; init; } = string.Empty;

    /// <summary>是不是模组 jar（给名字加粗、换图标用）。</summary>
    public bool IsModJar { get; init; }

    public static FsEntry? FromDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);

            return new FsEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = true,
                KindLabel = "文件夹",
                SizeLabel = string.Empty,
                ModifiedLabel = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
            };
        }
        catch
        {
            return null;
        }
    }

    public static FsEntry? FromFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var ext = info.Extension.TrimStart('.').ToUpperInvariant();
            var isJar = string.Equals(ext, "JAR", StringComparison.OrdinalIgnoreCase);

            return new FsEntry
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = false,
                KindLabel = string.IsNullOrEmpty(ext) ? "文件" : $"{ext} 文件",
                SizeLabel = HumanSize(info.Length),
                ModifiedLabel = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                IsModJar = isJar,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string HumanSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";

        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
