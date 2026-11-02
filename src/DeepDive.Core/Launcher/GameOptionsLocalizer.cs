using System.Text;

namespace DeepDive.Core.Launcher;

/// <summary>
/// 在启动前把游戏的 <c>options.txt</c> 语言项钉成简体中文。
/// </summary>
/// <remarks>
/// <para>
/// Minecraft 的语言设置不读启动参数，只认游戏目录里的 <c>options.txt</c>，
/// 格式是一行 <c>lang:zh_cn</c>。所以「进游戏自动中文」只能在启动前把这一行写好。
/// </para>
/// <para>
/// 覆盖策略：<b>只改 <c>en_us</c>、空值、以及整行缺失的情况</b>。
/// 用户如果主动选了 <c>zh_tw</c>、<c>ja_jp</c> 之类，就不动 —— 否则每次启动都被强行改回来，
/// 想玩英文版的人会被烦死。首次进游戏时 MC 默认写的是 <c>en_us</c>，所以「自动中文」照样生效。
/// </para>
/// <para>
/// ⚠️ 游戏退出时会重写整个 <c>options.txt</c>，所以这活儿必须<b>每次启动前</b>做，不能只做一次。
/// </para>
/// </remarks>
public static class GameOptionsLocalizer
{
    /// <summary>简体中文的语言代码（<c>zh_CN</c> 在 options.txt 里写作全小写）。</summary>
    public const string ChineseLanguageCode = "zh_cn";

    private const string OptionsFileName = "options.txt";
    private const string LanguageKey = "lang:";
    private const string ResourcePacksKey = "resourcePacks:";

    /// <summary>MC 首次生成 options.txt 时用的默认语言。</summary>
    private const string DefaultLanguageCode = "en_us";

    /// <summary>
    /// 确保 <paramref name="gameDir"/> 下的 <c>options.txt</c> 是「全屏」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 和语言同样是启动前改文件 —— MC 的全屏状态也只有 <c>options.txt</c> 里的 <c>fullscreen:</c> 一行说了算，
    /// 没有启动参数能覆盖它（<c>--fullscreen</c> 从来不是官方参数）。
    /// </para>
    /// <para>
    /// 和语言不同，这里<b>不做「尊重用户自选」的判断</b>：开关打开就意味着每次都要全屏，
    /// 用户在游戏里按 F11 退出全屏是这一次的事，下次启动仍然按设置来。
    /// </para>
    /// </remarks>
    /// <returns>发生了改动时返回一句人话描述；已经是目标状态返回 <c>null</c>。</returns>
    public static string? EnsureFullscreen(string gameDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);

        Directory.CreateDirectory(gameDir);
        var file = Path.Combine(gameDir, OptionsFileName);
        const string fullscreenKey = "fullscreen:";
        const string wanted = fullscreenKey + "true";

        if (!File.Exists(file))
        {
            File.WriteAllText(
                file,
                wanted + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return $"新建 {OptionsFileName}，设为全屏";
        }

        var lines = File.ReadAllLines(file);
        var index = Array.FindIndex(
            lines,
            line => line.TrimStart().StartsWith(fullscreenKey, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            var merged = new string[lines.Length + 1];
            Array.Copy(lines, merged, lines.Length);
            merged[^1] = wanted;

            WriteAllLines(file, merged);
            return $"{OptionsFileName} 里没有全屏项，已补上";
        }

        var current = lines[index].TrimStart();
        if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            return null;

        lines[index] = wanted;
        WriteAllLines(file, lines);
        return $"全屏由 {current[fullscreenKey.Length..]} 改为 true";
    }

    /// <summary>
    /// 确保 <paramref name="gameDir"/> 下的 <c>options.txt</c> 语言是简体中文。
    /// </summary>
    /// <returns>
    /// 发生了改动时返回一句人话描述（给日志用）；无需改动（已经是中文，或是用户自选的其他语言）返回 <c>null</c>。
    /// </returns>
    /// <exception cref="IOException">文件被占用、只读等。</exception>
    /// <exception cref="UnauthorizedAccessException">没有写入权限。</exception>
    public static string? EnsureChinese(string gameDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);

        Directory.CreateDirectory(gameDir);
        var file = Path.Combine(gameDir, OptionsFileName);

        // 没有 options.txt：直接造一个最小的。MC 读到缺失项会自己补默认值，只写 lang 行是合法的。
        if (!File.Exists(file))
        {
            File.WriteAllText(
                file,
                LanguageKey + ChineseLanguageCode + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return $"新建 {OptionsFileName}，语言设为简体中文（{ChineseLanguageCode}）";
        }

        var lines = File.ReadAllLines(file);

        var index = Array.FindIndex(
            lines,
            line => line.TrimStart().StartsWith(LanguageKey, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            // 文件在，但没有 lang 行（某些版本首次启动前就是这个状态）：追加一行
            var merged = new string[lines.Length + 1];
            Array.Copy(lines, merged, lines.Length);
            merged[^1] = LanguageKey + ChineseLanguageCode;

            WriteAllLines(file, merged);
            return $"{OptionsFileName} 里没有语言项，已补上简体中文";
        }

        var current = lines[index].TrimStart()[LanguageKey.Length..].Trim();

        if (string.Equals(current, ChineseLanguageCode, StringComparison.OrdinalIgnoreCase))
            return null;   // 已经是中文，什么都不做

        if (!string.IsNullOrEmpty(current) &&
            !string.Equals(current, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            // 用户自己选过别的语言，尊重它
            return null;
        }

        lines[index] = LanguageKey + ChineseLanguageCode;
        WriteAllLines(file, lines);

        return $"语言从 {(string.IsNullOrEmpty(current) ? "空" : current)} 改为简体中文";
    }

    /// <summary>
    /// 确保启动器做的汉化资源包在游戏里是<b>启用</b>状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 汉化产出的包只是躺在 <c>resourcepacks/</c> 里，MC 不会自动加载它 ——
    /// 玩家得在「选项 → 资源包」里手动移到右边。不代劳的话，用户点完「汉化」进游戏
    /// 发现还是英文，会以为功能坏了。
    /// </para>
    /// <para>
    /// 同样必须在<b>启动前</b>写：游戏退出时会重写整个 <c>options.txt</c>，
    /// 运行中改的会被覆盖掉。
    /// </para>
    /// <para>
    /// 只追加、不删除。用户主动把某个包移到左边（禁用）后，下次启动会被我们加回来 ——
    /// 这是故意的：这功能的意义就是「汉化包默认生效」。
    /// </para>
    /// </remarks>
    /// <returns>发生了改动时返回一句人话描述；无需改动返回 <c>null</c>。</returns>
    public static string? EnsureResourcePacks(string gameDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);

        var packsDir = Path.Combine(gameDir, "resourcepacks");
        if (!Directory.Exists(packsDir)) return null;

        var ours = Directory
            .EnumerateFiles(packsDir, "*-zh_cn.zip", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ours.Count == 0) return null;

        var file = Path.Combine(gameDir, OptionsFileName);
        var lines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();

        var index = Array.FindIndex(
            lines,
            line => line.TrimStart().StartsWith(ResourcePacksKey, StringComparison.OrdinalIgnoreCase));

        // 解析现有列表（形如 ["vanilla","file/X-zh_cn.zip"]）
        var current = new List<string>();
        if (index >= 0)
        {
            var raw = lines[index].TrimStart()[ResourcePacksKey.Length..].Trim();
            var start = raw.IndexOf('[');
            var end = raw.LastIndexOf(']');

            if (start >= 0 && end > start)
            {
                foreach (var item in raw[(start + 1)..end].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleaned = item.Trim().Trim('"');
                    if (cleaned.Length > 0) current.Add(cleaned);
                }
            }
        }
        else
        {
            current.Add("vanilla");   // 没有这一行时补个最小合法值
        }

        var existingNames = current
            .Select(entry => entry[(entry.LastIndexOf('/') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = ours.Where(name => !existingNames.Contains(name)).ToList();
        if (added.Count == 0) return null;

        // 追加在末尾：MC 里越靠后优先级越高，我们的覆盖包理应压在上面
        current.AddRange(added.Select(name => "file/" + name));

        var built = ResourcePacksKey + "[" +
                    string.Join(",", current.Select(entry => "\"" + entry + "\"")) + "]";

        if (index >= 0)
        {
            lines[index] = built;
        }
        else
        {
            var merged = new string[lines.Length + 1];
            Array.Copy(lines, merged, lines.Length);
            merged[^1] = built;
            lines = merged;
        }

        WriteAllLines(file, lines);

        return $"资源包已启用 {added.Count} 个：{string.Join("、", added)}";
    }

    /// <summary>
    /// 写回 <c>options.txt</c>：UTF-8 无 BOM、<c>\n</c> 换行 —— 与 MC 自己写的格式保持一致。
    /// </summary>
    private static void WriteAllLines(string file, string[] lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        File.WriteAllText(file, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
