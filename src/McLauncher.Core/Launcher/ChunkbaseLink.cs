using System.Text.RegularExpressions;

namespace McLauncher.Core.Launcher;

/// <summary>拼 Chunkbase 种子地图的链接。</summary>
/// <remarks>
/// 实测格式（网页版自己分享出来的链接就长这样）：
/// <c>https://www.chunkbase.com/apps/seed-map#seed=-5677969439585475100&amp;platform=java_1_21&amp;dimension=overworld</c>
/// <para>
/// <c>platform</c> 只对 1.x 猜得准（<c>java_1_21</c> 这种）。新版号（26.3 之类）Chunkbase 用什么
/// token 我没法验证，就<b>干脆不带</b> —— 宁可让用户在地图上手动选一次版本，
/// 也不要带一个错的 platform 让人看到完全不对的地形。
/// </para>
/// </remarks>
public static class ChunkbaseLink
{
    private const string BaseUrl = "https://www.chunkbase.com/apps/seed-map";

    /// <summary>
    /// 种子地图页面本身，<b>不带任何参数</b>。
    /// </summary>
    /// <remarks>
    /// 带 <c>#seed=</c> 的链接打开后，页面要在地图初始化那一步把种子吃进去，
    /// 首屏本来就重，这一步经常卡很久，看着就是一片白。
    /// 所以默认只开站点，种子交给用户自己粘 —— 反正种子已经在剪贴板里了，
    /// 到页面按 Ctrl+V 比等它自己算快。
    /// </remarks>
    public static string SiteUrl => BaseUrl;

    /// <summary>拼一个带种子的地图链接。版本认不出来时只带 seed 和维度。</summary>
    public static string Build(long seed, string? mcVersion)
    {
        var platform = PlatformFor(mcVersion);

        return platform is null
            ? $"{BaseUrl}#seed={seed}&dimension=overworld"
            : $"{BaseUrl}#seed={seed}&platform={platform}&dimension=overworld";
    }

    /// <summary>
    /// 把 Minecraft 版本号转成 Chunkbase 的 platform 值：<c>1.21.1</c> → <c>java_1_21</c>。
    /// 非 1.x 或认不出返回 <c>null</c>。
    /// </summary>
    public static string? PlatformFor(string? mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion)) return null;

        var match = Regex.Match(mcVersion, @"^(\d+)\.(\d+)");
        if (!match.Success) return null;

        var major = match.Groups[1].Value;
        if (major != "1") return null;

        return $"java_{major}_{match.Groups[2].Value}";
    }
}
