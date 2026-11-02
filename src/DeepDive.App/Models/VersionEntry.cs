using System.IO;
using System.Text.Json;
using DeepDive.Core.Launcher;

namespace DeepDive.App.Models;

/// <summary>
/// 版本下拉里的一项。远端清单版本和本地已装版本统一成这一个模型。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要合并：加载器装完后写进 <c>versions/</c> 的版本（如 <c>neoforge-21.1.265</c>、
/// <c>fabric-loader-0.20.1-1.21.1</c>）<b>不在 Mojang 的版本清单里</b>。
/// 下拉框如果只绑远端清单，用户装完加载器会发现它根本不出现 —— 装了个寂寞。
/// </para>
/// <para>
/// 反向也成立：远端清单里的版本用户可能已经装好了，标出来就不用重复下载。
/// </para>
/// </remarks>
public sealed class VersionEntry
{
    /// <summary>版本名（= <c>versions/</c> 下的目录名 = 启动时传给 CmlLib 的名字）。</summary>
    public required string Name { get; init; }

    /// <summary>Mojang 的类型标签：release / snapshot / old_beta …（本地独有版本为空）。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Mojang 的发布时间。本地独有版本（加载器版本）没有这个值。</summary>
    public DateTimeOffset ReleaseTime { get; init; }

    /// <summary>本地是否已经有这个版本。</summary>
    public bool IsInstalled { get; init; }

    /// <summary>本地版本 JSON 里的 <c>inheritsFrom</c>（加载器版本才有，原版为空）。</summary>
    public string? InheritsFrom { get; init; }

    /// <summary>
    /// 本地版本的安装时间（取版本目录的最后写入时间）。
    /// </summary>
    /// <remarks>
    /// 排序用它而不是 <see cref="ReleaseTime"/>：加载器版本没有「发布时间」，
    /// 但用户刚装完就一定想第一个看到它。远端版本此值为 default。
    /// </remarks>
    public DateTime InstalledAt { get; init; }

    /// <summary>下拉里的一列：「已装」/「远端」。</summary>
    public string OriginLabel => IsInstalled ? "已装" : "远端";

    /// <summary>
    /// 版本渠道：<c>release</c> / <c>snapshot</c> / <c>old_beta</c> / <c>old_alpha</c>，认不出来就是空串。
    /// </summary>
    /// <remarks>
    /// 远端清单里直接写着 <see cref="Type"/>；加载器版本（<c>neoforge-…</c>）不在 Mojang 清单里，
    /// 得由调用方顺着 <see cref="InheritsFrom"/> 去查它继承的那个原版是什么渠道再填进来 ——
    /// 这个类自己拿不到远端清单，所以不在属性里算。
    /// </remarks>
    public string Channel { get; init; } = string.Empty;

    /// <summary>渠道的中文说法，给筛选按钮和详情区用。</summary>
    public string ChannelLabel => Channel switch
    {
        "release" => "正式版",
        "snapshot" => "测试版",
        "old_beta" or "old_alpha" => "远古版",
        "" or "installed" => "本地",
        _ => Channel,
    };

    /// <summary>原版 / Forge / NeoForge / Fabric。本地版本会按名字认出来。</summary>
    public string KindLabel => DetectKind(Name, InheritsFrom);

    /// <summary>
    /// 这个版本用的是哪个加载器（类型版）。启动前的模组体检要用它判断「模组放错没」。
    /// </summary>
    public ModLoaderKind ModLoader => DetectLoader(Name);

    internal static ModLoaderKind DetectLoader(string name)
    {
        var n = name.ToLowerInvariant();

        // 顺序有讲究：neoforge 里含 forge，先判 neoforge，否则全都算成 Forge
        if (n.StartsWith("fabric-loader")) return ModLoaderKind.Fabric;
        if (n.StartsWith("quilt-loader")) return ModLoaderKind.Vanilla; // 暂不支持 Quilt
        if (n.StartsWith("neoforge")) return ModLoaderKind.NeoForge;
        if (n.Contains("forge")) return ModLoaderKind.Forge;

        return ModLoaderKind.Vanilla;
    }

    /// <summary>下拉里显示的时间：本地独有版本没有发布时间，给个占位。</summary>
    public string TimeLabel => ReleaseTime == default
        ? "—"
        : ReleaseTime.ToString("yyyy-MM-dd");

    internal static string DetectKind(string name, string? inheritsFrom)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("fabric-loader")) return "Fabric";
        if (n.StartsWith("quilt-loader")) return "Quilt";
        if (n.StartsWith("neoforge")) return "NeoForge";
        if (n.Contains("forge")) return "Forge";
        return inheritsFrom is null ? "原版" : "继承";
    }

    /// <summary>
    /// 扫 <c>versions/</c> 目录，把已经装好的版本读出来。
    /// </summary>
    /// <remarks>
    /// 读 JSON 只为拿 <c>inheritsFrom</c>（判断是不是加载器版本、继承自哪个原版）。
    /// 单个版本 JSON 损坏不应该让整个列表崩掉 —— 所以逐个 try，坏的跳过。
    /// </remarks>
    public static List<VersionEntry> LoadInstalled(string versionsDir)
    {
        var result = new List<VersionEntry>();
        if (!Directory.Exists(versionsDir)) return result;

        foreach (var dir in Directory.GetDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) continue;

            var jsonPath = Path.Combine(dir, name + ".json");
            string? inheritsFrom = null;

            if (File.Exists(jsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh))
                        inheritsFrom = inh.GetString();
                }
                catch
                {
                    // JSON 坏了也照样列出来 —— 至少能让用户看见、能删
                }
            }

            result.Add(new VersionEntry
            {
                Name = name,
                Type = "installed",
                IsInstalled = true,
                InheritsFrom = inheritsFrom,
                InstalledAt = SafeLastWrite(dir),
            });
        }

        return result;
    }

    /// <summary>目录的最后写入时间。装完加载器会刷新，用来把刚装好的版本顶到最前面。</summary>
    private static DateTime SafeLastWrite(string dir)
    {
        try { return Directory.GetLastWriteTime(dir); }
        catch { return default; }
    }
}
