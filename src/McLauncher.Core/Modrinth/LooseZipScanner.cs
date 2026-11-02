using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using McLauncher.Core.Launcher;

namespace McLauncher.Core.Modrinth;

/// <summary>
/// 「懒人包」的识别结果 —— 版本和加载器全靠从 <c>mods/*.jar</c> 里反推。
/// </summary>
/// <param name="Kind">加载器。一个 mod 都没有时是 <see cref="ModLoaderKind.Vanilla"/>。</param>
/// <param name="MinecraftVersion">猜出来的游戏版本，可能为 null（猜不出来）。</param>
/// <param name="JarCount">扫描到的模组 jar 数。</param>
/// <param name="Evidence">猜的依据，写进日志给人看（例：<c>fabric.mod.json × 23</c>）。</param>
public sealed record LooseZipGuess(
    ModLoaderKind Kind,
    string? MinecraftVersion,
    int JarCount,
    string? Evidence);

/// <summary>
/// 认「懒人包」—— 别人手动打包的压缩包，里面直接是 <c>mods/</c> <c>config/</c> <c>saves/</c>，
/// 没有 <c>modrinth.index.json</c> 也没有 <c>manifest.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>难点：这种包不写任何版本信息</b>。要知道「用什么游戏版本、什么加载器」，
/// 只能翻 jar 里的元数据 —— 模组作者为了能被加载器认，必须把这两样写在 jar 里：
/// </para>
/// <list type="bullet">
///   <item><c>fabric.mod.json</c> → Fabric，<c>depends.minecraft</c> 写游戏版本</item>
///   <item><c>META-INF/neoforge.mods.toml</c> → NeoForge，<c>modId="minecraft"</c> 那条写版本范围</item>
///   <item><c>META-INF/mods.toml</c> → Forge（老版本 NeoForge 也用这个，靠文件名里的 neoforge 纠偏）</item>
/// </list>
/// <para>
/// <b>为什么还要看文件名</b>：<c>sodium-neoforge-0.6.13+mc1.21.4.jar</c> 这种名字里
/// 明摆着写了 <c>mc1.21.4</c>，比解析 toml 快得多也稳得多。两条路都走，投票取多数。
/// </para>
/// <para>
/// <b>为什么不扫全部 jar</b>：一个包一两百个 jar，全扫要读几百 MB。取前
/// <see cref="MaxJarsToScan"/> 个、并且在「加载器和版本各拿到足够票数」后立刻停，
/// 实测几十毫秒就能定下来 —— 同一个整合包里的模组版本必然一致。
/// </para>
/// </remarks>
public static class LooseZipScanner
{
    /// <summary>最多翻多少个 jar。够投票了，再多只是白读磁盘。</summary>
    private const int MaxJarsToScan = 40;

    /// <summary>加载器和版本各有这么多票就不再翻了。</summary>
    private const int EnoughVotes = 5;

    /// <summary>
    /// 「内容目录」—— 这些目录出现在压缩包里，就说明它是个可以直接解压玩的包。
    /// </summary>
    /// <remarks>
    /// 列表要够长，否则「只有 resourcepacks 的包」会被误判成认不出来。
    /// <b>故意不含 <c>logs</c> / <c>crash-reports</c></b>：那是作者自己玩出来的垃圾，
    /// 既不能证明这是个包，也不该解到用户的游戏目录里。
    /// </remarks>
    private static readonly HashSet<string> ContentDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "defaultconfigs", "saves", "resourcepacks", "shaderpacks",
        "datapacks", "kubejs", "scripts", "screenshots", "local",
    };

    /// <summary>
    /// 散在压缩包根上的游戏文件 —— 不在任何目录里，但确实是游戏要用的。
    /// </summary>
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "servers.dat", "servers.dat_old",
        "usercache.json", "iris.properties", "servers.dat.bak",
    };


    /// <summary>
    /// 从任意文本里抠「看起来像 Minecraft 版本号」的片段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 认 <c>1.6 ~ 1.29</c>，顺带排除 <c>0.16.10</c>（Fabric 加载器版本）、
    /// <c>21.4.135</c>（NeoForge 版本）这类干扰项。前后不允许紧贴数字或点，
    /// 免得从 <c>1.21.4</c> 里抠出半个 <c>21.4</c>。
    /// </para>
    /// <para>
    /// <b>下界放到 1.6 是有原因的</b>：<c>1.7.10</c> 和 <c>1.12.2</c> 是国内整合包的两大主力
    /// （工业 2、暮色森林那一批），只认 1.16+ 会把老包全判成「版本未知」。
    /// </para>
    /// <para>
    /// 版本范围（<c>[1.21.4,1.22)</c>、<c>&gt;=1.20.1 &lt;1.21</c>）取<b>第一个</b>命中，
    /// 正好是下界 —— 那才是这个模组支持的主版本。
    /// </para>
    /// </remarks>
    private static readonly Regex VersionPattern = new(
        @"(?<![0-9.])(1\.(?:[6-9]|1[0-9]|2[0-9])(?:\.\d{1,2})?)(?![0-9])",
        RegexOptions.Compiled);

    /// <summary>
    /// 相对路径是不是「该解到游戏目录里的内容」。
    /// </summary>
    /// <remarks>
    /// 白名单而不是黑名单：压缩包里常混着作者放的说明、预览图、旧配置备份，
    /// 黑名单永远漏。只放行<b>已知的游戏目录</b>和<b>根目录下的已知游戏文件</b>。
    /// </remarks>
    public static bool IsContentPath(string relativePath)
    {
        var slash = relativePath.IndexOf('/');

        // 根目录上的单个文件（options.txt 这类）
        if (slash < 0) return RootFiles.Contains(relativePath);

        return ContentDirectories.Contains(relativePath[..slash]);
    }


    /// <summary>压缩包里像不像一个能直接解压玩的包。</summary>
    public static bool LooksLikeLoosePack(IReadOnlyList<string> entryNames)
        => entryNames.Any(name => FindContentDirectory(name) is not null);

    /// <summary>
    /// 算出「内容目录在第几层」的前缀，解压时先剥掉它。
    /// </summary>
    /// <remarks>
    /// 三种常见形态都要认：
    /// <c>mods/...</c>（无前缀）、<c>MyPack/mods/...</c>（作者套了一层文件夹）、
    /// <c>MyPack/.minecraft/mods/...</c>（直接从 .minecraft 目录打的包）。
    /// 取出现次数最多的那个前缀 —— 混着放几个文件的包也不怕。
    /// </remarks>
    public static string DetectContentPrefix(IReadOnlyList<string> entryNames)
    {
        var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in entryNames)
        {
            var segments = name.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!ContentDirectories.Contains(segments[i])) continue;

                var prefix = string.Join('/', segments.Take(i));
                prefixes[prefix] = prefixes.TryGetValue(prefix, out var count) ? count + 1 : 1;
                break;
            }
        }

        return prefixes.Count == 0
            ? string.Empty
            : prefixes.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Length).First().Key;
    }

    /// <summary>翻 jar 猜加载器和游戏版本。<paramref name="log"/> 用来把依据写进日志。</summary>
    public static LooseZipGuess Scan(string zipPath, string prefix, Action<string>? log = null)
    {
        var loaderVotes = new Dictionary<ModLoaderKind, int>();
        var versionVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var jarCount = 0;
        var scanned = 0;

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

            var full = entry.FullName.Replace('\\', '/');

            // 只认 mods/ 下面的，别把 libraries 里的东西当模组
            if (!IsUnderMods(full, prefix)) continue;

            jarCount++;

            // 文件名先投票：这条最便宜，而且「sodium-neoforge-...-mc1.21.4.jar」这种名字信息量最大
            VoteFromFileName(entry.Name, loaderVotes, versionVotes);

            if (scanned >= MaxJarsToScan) continue;
            if (loaderVotes.Values.Sum() >= EnoughVotes && versionVotes.Values.Sum() >= EnoughVotes)
            {
                // 票数够了就停 —— 后面的 jar 只是重复同一件事
                scanned = MaxJarsToScan;
                continue;
            }

            try
            {
                VoteFromJarMetadata(entry, loaderVotes, versionVotes);
                scanned++;
            }            catch (Exception ex)
            {
                // 单个 jar 读不动（加密、损坏、超大）不该影响整体判断
                log?.Invoke($"   跳过读不动的 jar：{entry.Name}（{ex.GetType().Name}）");
            }
        }

        var kind = PickKind(loaderVotes);
        var version = PickVersion(versionVotes);

        var evidence = BuildEvidence(loaderVotes, versionVotes, jarCount);
        return new LooseZipGuess(kind, version, jarCount, evidence);
    }

    // ── 投票来源 ────────────────────────────────────────

    private static void VoteFromFileName(
        string fileName,
        Dictionary<ModLoaderKind, int> loaderVotes,
        Dictionary<string, int> versionVotes)
    {
        var lower = fileName.ToLowerInvariant();

        // 顺序有讲究：先判 neoforge，否则 "neoforge" 会被 "forge" 也命中
        if (lower.Contains("neoforge", StringComparison.Ordinal))
            Bump(loaderVotes, ModLoaderKind.NeoForge);
        else if (lower.Contains("fabric", StringComparison.Ordinal))
            Bump(loaderVotes, ModLoaderKind.Fabric);
        else if (lower.Contains("forge", StringComparison.Ordinal))
            Bump(loaderVotes, ModLoaderKind.Forge);

        foreach (Match match in VersionPattern.Matches(fileName))
            Bump(versionVotes, match.Groups[1].Value);
    }

    private static void VoteFromJarMetadata(
        ZipArchiveEntry jarEntry,
        Dictionary<ModLoaderKind, int> loaderVotes,
        Dictionary<string, int> versionVotes)
    {
        using var buffer = new MemoryStream();
        using (var stream = jarEntry.Open())
            stream.CopyTo(buffer);

        buffer.Position = 0;

        using var jar = new ZipArchive(buffer, ZipArchiveMode.Read);

        var fabricJson = FindEntry(jar, "fabric.mod.json");
        if (fabricJson is not null)
        {
            Bump(loaderVotes, ModLoaderKind.Fabric);

            var json = ReadText(fabricJson);
            if (json is not null)
                VoteFromDependency(json, "minecraft", versionVotes);

            return;
        }

        var neoToml = FindEntry(jar, "META-INF/neoforge.mods.toml");
        if (neoToml is not null)
        {
            Bump(loaderVotes, ModLoaderKind.NeoForge);
            VoteFromToml(ReadText(neoToml), versionVotes);
            return;
        }

        var forgeToml = FindEntry(jar, "META-INF/mods.toml");
        if (forgeToml is not null)
        {
            // 1.20.1 的 NeoForge 也写 META-INF/mods.toml，光看文件名分不出来；
            // 但 jar 名字里带 neoforge 的肯定不是 Forge，按名字纠偏，别把 NeoForge 包装成 Forge
            var looksNeoForge = jarEntry.Name.Contains("neoforge", StringComparison.OrdinalIgnoreCase);
            Bump(loaderVotes, looksNeoForge ? ModLoaderKind.NeoForge : ModLoaderKind.Forge);

            VoteFromToml(ReadText(forgeToml), versionVotes);
        }
    }

    /// <summary>从 <c>fabric.mod.json</c> 里读 <c>depends.minecraft</c>（或 <c>breaks</c> 之外的近似字段）。</summary>
    private static void VoteFromDependency(string json, string modId, Dictionary<string, int> versionVotes)
    {
        // 不引 JSON 库：这段只要「找到 minecraft 后面那一小段文本」，
        // 用正则比反序列化宽容（mod 作者手写 JSON 常有小毛病）
        var pattern = $"\"{Regex.Escape(modId)}\"\\s*:\\s*\"([^\"]*)\"";
        var match = Regex.Match(json, pattern);
        if (!match.Success) return;

        var versionMatch = VersionPattern.Match(match.Groups[1].Value);
        if (versionMatch.Success)
            Bump(versionVotes, versionMatch.Groups[1].Value);
    }

    /// <summary>从 mods.toml 里找 <c>modId="minecraft"</c> 那条依赖的 <c>versionRange</c>。</summary>
    private static void VoteFromToml(string? toml, Dictionary<string, int> versionVotes)
    {
        if (string.IsNullOrEmpty(toml)) return;

        var anchor = Regex.Match(toml, @"modId\s*=\s*""minecraft""", RegexOptions.IgnoreCase);
        if (!anchor.Success) return;

        // versionRange 通常就在同一段（锚点往后几百字符内）
        var tail = toml[anchor.Index..Math.Min(toml.Length, anchor.Index + 600)];

        var range = Regex.Match(tail, @"versionRange\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        if (!range.Success) return;

        var versionMatch = VersionPattern.Match(range.Groups[1].Value);
        if (versionMatch.Success)
            Bump(versionVotes, versionMatch.Groups[1].Value);
    }

    // ── 投票结果 ────────────────────────────────────────

    private static ModLoaderKind PickKind(Dictionary<ModLoaderKind, int> votes)
        => votes.Count == 0
            ? ModLoaderKind.Vanilla
            : votes.OrderByDescending(v => v.Value).First().Key;

    /// <summary>
    /// 版本取「多数派」：先按 <c>1.21</c> 这样的两段号分组选出主版本，
    /// 再在组里挑出现最多的完整号（<c>1.21.4</c> 胜过 <c>1.21</c>，因为更具体）。
    /// </summary>
    private static string? PickVersion(Dictionary<string, int> votes)
    {
        if (votes.Count == 0) return null;

        static string Minor(string version)
        {
            var parts = version.Split('.');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
        }

        var bestMinor = votes
            .GroupBy(v => Minor(v.Key))
            .OrderByDescending(g => g.Sum(v => v.Value))
            .ThenBy(g => g.Key)
            .First()
            .Key;

        var inMinor = votes.Where(v => Minor(v.Key) == bestMinor).ToList();

        // 同票时优先带第三段的（1.21.4 比 1.21 精确）
        return inMinor
            .OrderByDescending(v => v.Value)
            .ThenByDescending(v => v.Key.Count(c => c == '.'))
            .First()
            .Key;
    }

    private static string BuildEvidence(
        Dictionary<ModLoaderKind, int> loaderVotes,
        Dictionary<string, int> versionVotes,
        int jarCount)
    {
        var loaders = loaderVotes.Count == 0
            ? "无"
            : string.Join("、", loaderVotes.OrderByDescending(v => v.Value).Select(v => $"{v.Key}×{v.Value}"));

        var versions = versionVotes.Count == 0
            ? "无"
            : string.Join("、", versionVotes.OrderByDescending(v => v.Value).Take(3).Select(v => $"{v.Key}×{v.Value}"));

        return $"从 {jarCount} 个 jar 反推：加载器投票 [{loaders}]，版本投票 [{versions}]";
    }

    // ── 小工具 ──────────────────────────────────────────

    private static void Bump<TKey>(Dictionary<TKey, int> votes, TKey key) where TKey : notnull
        => votes[key] = votes.TryGetValue(key, out var count) ? count + 1 : 1;

    private static bool IsUnderMods(string fullPath, string prefix)
    {
        if (prefix.Length > 0)
        {
            var prefixWithSlash = prefix + "/";
            if (!fullPath.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = fullPath[prefixWithSlash.Length..];
        }

        return fullPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive jar, string name)
        => jar.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > 4 * 1024 * 1024) return null; // 元数据文件不该这么大

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>找路径里第一个「内容目录」的名字，没有返回 null。</summary>
    private static string? FindContentDirectory(string entryName)
    {
        var segments = entryName.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (ContentDirectories.Contains(segments[i])) return segments[i];
        }

        return null;
    }
}
