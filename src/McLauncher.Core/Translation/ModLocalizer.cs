using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace McLauncher.Core.Translation;

/// <summary>一个模组 jar 里的语言文件现状。</summary>
/// <param name="JarPath">jar 的完整路径。</param>
/// <param name="DisplayName">显示名（从 mods.toml / fabric.mod.json 里读，读不到就用文件名）。</param>
/// <param name="ModIds">jar 里出现过的所有 modid（一个 jar 可能带多个）。</param>
/// <param name="EnglishKeys"><c>en_us.json</c> 里的键总数。</param>
/// <param name="ChineseKeys"><c>zh_cn.json</c> 已经有的键数（可能和英文键不完全重合）。</param>
/// <param name="MissingKeys">英文有、中文没有的键数 —— 也就是这次要翻的条数。</param>
/// <param name="PackName">
/// 中文来自哪个资源包（<c>resourcepacks/</c> 下的文件名）。
/// <c>null</c> 表示中文是模组自己带的，或者是还没有中文。
/// </param>
/// <param name="PackEnabled">那个资源包在游戏里有没有被启用。</param>
public sealed record ModLocaleInfo(
    string JarPath,
    string DisplayName,
    IReadOnlyList<string> ModIds,
    int EnglishKeys,
    int ChineseKeys,
    int MissingKeys,
    string? PackName = null,
    bool PackEnabled = false)
{
    /// <summary>文件名（界面显示用）。</summary>
    public string FileName => Path.GetFileName(JarPath);

    /// <summary>自带中文，不用管。</summary>
    public bool HasChinese => ChineseKeys > 0;

    /// <summary>有得翻。</summary>
    public bool NeedsWork => MissingKeys > 0;

    /// <summary>该翻的都翻了，而且是我们做的资源包补上的。</summary>
    public bool LocalizedByPack => MissingKeys == 0 && PackName is not null;

    /// <summary>一行状态描述，直接给界面用。</summary>
    public string StatusText => EnglishKeys == 0
        ? "没有界面语言文件"
        : MissingKeys != 0
            ? ChineseKeys == 0
                ? $"纯英文，{EnglishKeys} 条待翻"
                : $"部分中文（{ChineseKeys}/{EnglishKeys}），{MissingKeys} 条待翻"
            : PackName is null
                ? $"已有完整中文（{ChineseKeys}/{EnglishKeys}）"
                : PackEnabled
                    ? $"已汉化：{EnglishKeys}/{EnglishKeys}（{PackName}）"
                    : $"已汉化：{EnglishKeys}/{EnglishKeys}，但游戏里没启用";
}

/// <summary>一个模组的汉化结果。</summary>
/// <param name="Success">资源包是否生成成功。</param>
/// <param name="PackPath">生成的资源包路径。</param>
/// <param name="Translated">成功翻译并写入的条数。</param>
/// <param name="Reused">命中缓存、没花钱的条数。</param>
/// <param name="Skipped">判定为不需要翻译的条数（纯符号、已经是中文等）。</param>
/// <param name="Failed">翻译失败或校验没过的条数。</param>
/// <param name="Warnings">值得用户看一眼的问题（太长、格式符丢了之类）。</param>
/// <param name="Error">整体失败原因。</param>
public sealed record ModLocalizeResult(
    bool Success,
    string? PackPath,
    int Translated,
    int Reused,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Warnings,
    string? Error = null);

/// <summary>
/// 把模组的英文语言文件翻成中文，产出一个资源包。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接改 jar</b>：资源包是 MC 原生的零侵入覆盖机制 ——
/// 不动原文件、用户可以随时禁用、模组更新也不会被覆盖掉。CFPA 汉化组也是这么干的。
/// </para>
/// <para>
/// <b>三条铁律</b>：
/// </para>
/// <list type="number">
///   <item>格式符（<c>§a</c> <c>{0}</c> <c>%s</c> 换行）翻译前后必须一字不差，否则游戏里直接崩。</item>
///   <item>已有中文的键<b>绝不覆盖</b> —— 官方译名永远比机翻好，我们只补缺的。</item>
///   <item>机翻结果缓存在本地，同一个模组翻第二遍不花一分钱。</item>
/// </list>
/// </remarks>
public sealed class ModLocalizer
{
    /// <summary>一批多少条。30 条大约 2000 字符，DeepSeek 一次吃得下，出错也只丢一小批。</summary>
    private const int BatchSize = 30;

    /// <summary>一批的字数上限，防止单个模组里有超长的 tooltip 把一批撑爆。</summary>
    private const int BatchMaxChars = 2600;

    /// <summary>译文超过原文这个倍数就算「太长」，界面上要提醒会被截断。</summary>
    private const double MaxLengthRatio = 1.8;

    /// <summary>
    /// 语言文件路径。<b>两个斜杠都认</b> —— zip 规范要求正斜杠，但确实有打包工具
    /// 写出反斜杠的条目（实测 PowerShell 的 Compress-Archive 就会），这种包 MC 读不到，
    /// 我们至少不该跟着瞎。
    /// </summary>
    private static readonly Regex LangPathPattern = new(
        @"^assets[/\\](?<modid>[^/\\]+)[/\\]lang[/\\](?<file>en_us|zh_cn)\.json$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>判断一条值值不值得翻。</summary>
    private static readonly Regex HasLatinLetter = new("[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex HasCjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    /// <summary>
    /// 写 json 用的选项。
    /// </summary>
    /// <remarks>
    /// <b>必须显式给 <c>TypeInfoResolver</c></b>：.NET 8 里 <c>JsonNode.ToJsonString(options)</c>
    /// 一旦拿到没设解析器的 options，会抛
    /// <c>JsonSerializerOptions instance must specify a TypeInfoResolver setting</c>。
    /// 另外 <c>UnsafeRelaxedJsonEscaping</c> 是让中文原样写进去，不然满屏 <c>\u53d2</c>，
    /// 虽然游戏能读，但人看着没法校对。
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly DeepSeekTranslator _translator;
    private readonly TranslationCache _cache;

    /// <summary>日志回调，签名和其它服务保持一致。</summary>
    public event Action<object, string>? Log;

    /// <param name="translator">DeepSeek 翻译器。</param>
    /// <param name="cacheFile">翻译缓存文件路径。建议放 <c>instances/mod-translation-cache.json</c>，所有实例共用。</param>
    public ModLocalizer(DeepSeekTranslator translator, string cacheFile)
    {
        _translator = translator;
        _cache = new TranslationCache(cacheFile);
    }

    /// <summary>缓存里已有多少条译文。</summary>
    public int CacheCount => _cache.Count;

    // ───────────────────────── 扫描 ─────────────────────────

    /// <summary>
    /// <c>resourcepacks/</c> 下已有的中文，按 modid 建索引。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 有这个索引，扫描结果才<b>不会翻脸不认账</b>：汉化产出的资源包在游戏里生效，
    /// 但它不在 jar 里，只数 jar 的话永远显示「105 条待翻」，让人以为白花钱了。
    /// </para>
    /// <para>
    /// 认的是资源包<b>内容</b>（<c>assets/&lt;modid&gt;/lang/zh_cn.json</c>）而不是文件名 ——
    /// 用户重命名、或者装的是别家（CFPA 之类）的汉化包，一样算数。
    /// </para>
    /// </remarks>
    public sealed class LocalizationPacks
    {
        private readonly Dictionary<string, Dictionary<string, string>> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _packs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>扫到几个资源包（诊断用）。</summary>
        public int PackCount { get; private set; }

        /// <summary>这个 modid 的中文（键 → 译文）。</summary>
        public bool TryGet(string modId, out Dictionary<string, string> entries, out string? packName)
        {
            var found = _entries.TryGetValue(modId, out var map);
            entries = map ?? new Dictionary<string, string>(StringComparer.Ordinal);
            packName = _packs.GetValueOrDefault(modId);
            return found;
        }

        /// <summary>这个 modid 对应的资源包有没有在游戏里启用。</summary>
        public bool IsEnabled(string modId)
        {
            var pack = _packs.GetValueOrDefault(modId);
            return pack is not null && _enabled.Contains(pack);
        }

        /// <summary>
        /// 读<paramref name="optionsTxt"/> 里已启用的资源包。
        /// </summary>
        /// <remarks>
        /// 光有 zip 不算生效 —— 玩家还得在「选项 → 资源包」里把它移到右边。
        /// 不读这个的话，界面上写着「已汉化」而游戏里还是英文，纯属骗人。
        /// 行格式：<c>resourcePacks:["vanilla","file/Sodium-zh_cn.zip"]</c>
        /// </remarks>
        private static HashSet<string> LoadEnabledPacks(string? optionsTxt)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (optionsTxt is null || !File.Exists(optionsTxt)) return result;

            try
            {
                foreach (var line in File.ReadLines(optionsTxt))
                {
                    if (!line.StartsWith("resourcePacks:", StringComparison.Ordinal)) continue;

                    var start = line.IndexOf('[');
                    var end = line.LastIndexOf(']');
                    if (start < 0 || end <= start) continue;

                    foreach (var raw in line[(start + 1)..end].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var item = raw.Trim().Trim('"');
                        var slash = item.LastIndexOf('/');
                        result.Add(slash >= 0 ? item[(slash + 1)..] : item);
                    }
                }
            }
            catch
            {
                // options.txt 被游戏占着读不了就算了，最多是提示不准确
            }

            return result;
        }

        /// <summary>读一个目录下所有 <c>*.zip</c>，把它们的中文键收进索引。</summary>
        public static LocalizationPacks Load(string? resourcePacksDirectory, Action<object?, string>? log = null)
        {
            var index = new LocalizationPacks();

            if (string.IsNullOrWhiteSpace(resourcePacksDirectory) ||
                !Directory.Exists(resourcePacksDirectory))
            {
                return index;
            }

            // options.txt 和 resourcepacks 平级（都在版本目录下）
            var versionDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(resourcePacksDirectory));
            var optionsTxt = versionDir is null ? null : Path.Combine(versionDir, "options.txt");
            index._enabled.UnionWith(LoadEnabledPacks(optionsTxt));

            foreach (var pack in Directory.EnumerateFiles(resourcePacksDirectory, "*.zip",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(pack);
                    var name = Path.GetFileName(pack);
                    var hit = false;

                    foreach (var entry in archive.Entries)
                    {
                        var match = LangPathPattern.Match(entry.FullName);
                        if (!match.Success) continue;
                        if (!match.Groups["file"].Value.Equals("zh_cn", StringComparison.OrdinalIgnoreCase)) continue;

                        var text = ReadEntryText(entry);
                        if (text is null) continue;

                        var map = ParseStringMap(text);
                        if (map is null || map.Count == 0) continue;

                        var modId = match.Groups["modid"].Value;

                        if (!index._entries.TryGetValue(modId, out var existing))
                        {
                            existing = new Dictionary<string, string>(StringComparer.Ordinal);
                            index._entries[modId] = existing;
                        }

                        foreach (var (key, value) in map) existing[key] = value;
                        index._packs[modId] = name;   // 同名 modid 撞车时后读到的胜出，够用了
                        hit = true;
                    }

                    if (hit) index.PackCount++;
                }
                catch (Exception ex)
                {
                    log?.Invoke(null, $"[汉化] 读不了资源包 {Path.GetFileName(pack)}：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return index;
        }
    }

    /// <summary>
    /// 扫一个 mods 目录，列出每个 jar 的语言文件情况。
    /// </summary>
    /// <param name="modsDirectory">要扫的 mods 目录。</param>
    /// <param name="resourcePacksDirectory">
    /// 同版本的 <c>resourcepacks/</c>。给了就把里面已有的汉化包算进中文覆盖率，
    /// 汉化过的模组不会每次都显示「待翻」。
    /// </param>
    /// <param name="log">出错时的回话通道。</param>
    /// <remarks>
    /// 不是模组的 jar（比如某些库）、以及没有 <c>assets/*/lang/en_us.json</c> 的 jar
    /// 会直接跳过 —— 列出来也只是噪音。
    /// </remarks>
    public static IReadOnlyList<ModLocaleInfo> Scan(
        string modsDirectory,
        string? resourcePacksDirectory = null,
        Action<object?, string>? log = null)
    {
        var result = new List<ModLocaleInfo>();

        if (!Directory.Exists(modsDirectory)) return result;

        var packs = LocalizationPacks.Load(resourcePacksDirectory, log);

        foreach (var jar in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = InspectJar(jar, packs);
                if (info is not null) result.Add(info);
            }
            catch (Exception ex)
            {
                log?.Invoke(null, $"[汉化] 读不了 {Path.GetFileName(jar)}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        return result.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>打开一个 jar，数一数里面有多少可翻的键。</summary>
    /// <param name="jarPath">jar 路径。</param>
    /// <param name="packs">已有资源包的中文索引，<c>null</c> 表示只数 jar 自带的。</param>
    public static ModLocaleInfo? InspectJar(string jarPath, LocalizationPacks? packs = null)
    {
        using var archive = ZipFile.OpenRead(jarPath);

        var files = CollectLangFiles(archive);
        if (files.Count == 0) return null;

        var english = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // modid → 键
        var chinese = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var keys = ParseKeys(file.Json);
            if (keys is null) continue;

            var bucket = file.IsEnglish ? english : chinese;
            if (!bucket.TryGetValue(file.ModId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                bucket[file.ModId] = set;
            }

            set.UnionWith(keys);
        }

        if (english.Count == 0) return null;

        // 资源包里的中文并进来 —— 但只在 jar 自己没翻的那些键上补，不覆盖
        var packHint = (string?)null;
        var packHintModId = (string?)null;
        if (packs is not null)
        {
            foreach (var (modId, keys) in english)
            {
                if (!packs.TryGet(modId, out var packEntries, out var packName) || packEntries.Count == 0) continue;

                if (!chinese.TryGetValue(modId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    chinese[modId] = set;
                }

                set.UnionWith(packEntries.Keys);

                if (packHint is null)
                {
                    packHint = packName;
                    packHintModId = modId;
                }
            }
        }

        var totalEnglish = english.Values.Sum(s => s.Count);

        // 中文覆盖率按「英文键里有几个已经有中文」算，而不是 zh_cn 的条目数 ——
        // 有些模组的 zh_cn 里塞了英文版没有的旧键，直接相减会得出负数
        var covered = english.Sum(pair =>
            chinese.TryGetValue(pair.Key, out var zh) ? pair.Value.Count(zh.Contains) : 0);

        var modIds = english.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        return new ModLocaleInfo(
            jarPath,
            ReadDisplayName(archive, jarPath, modIds),
            modIds,
            totalEnglish,
            covered,
            Math.Max(0, totalEnglish - covered),
            packHint,
            packHintModId is not null && packs is not null && packs.IsEnabled(packHintModId));
    }

    /// <summary>jar 里的一份语言文件。</summary>
    /// <param name="ModId">命名空间。</param>
    /// <param name="IsEnglish">是 <c>en_us</c> 还是 <c>zh_cn</c>。</param>
    /// <param name="Json">文件原文。</param>
    private sealed record LangFile(string ModId, bool IsEnglish, string Json);

    /// <summary>
    /// 把一个 jar 里所有语言文件找出来，<b>包括嵌套 jar 里的</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 现在的 NeoForge 模组大量使用 <b>Jar-in-Jar (JIJ)</b>：外层 jar 只是个壳，
    /// 真正的代码和资源塞在 <c>META-INF/jarjar/xxx.jar</c> 里。
    /// </para>
    /// <para>
    /// 实测 Sodium 0.9.3（mc26.3）就是这样：外层只有 99 个条目、全是 <c>net/</c> 的类文件，
    /// 语言文件在嵌套的
    /// <c>META-INF/jarjar/net.caffeinemc.sodium-neoforge-...-mod.jar</c> 里（1.2 MB）。
    /// 只看外层的话，这类模组一个都扫不出来。
    /// </para>
    /// <para>
    /// 最多往下钻 2 层 —— 再深的嵌套现实中不存在，防的是恶意/畸形 jar 把内存吃光。
    /// </para>
    /// </remarks>
    private static List<LangFile> CollectLangFiles(ZipArchive archive, int depth = 0)
    {
        var result = new List<LangFile>();
        if (depth > 2) return result;

        foreach (var entry in archive.Entries)
        {
            var match = LangPathPattern.Match(entry.FullName);
            if (match.Success)
            {
                var text = ReadEntryText(entry);
                if (text is null) continue;

                result.Add(new LangFile(
                    match.Groups["modid"].Value,
                    match.Groups["file"].Value.Equals("en_us", StringComparison.OrdinalIgnoreCase),
                    text));

                continue;
            }

            if (!IsNestedJar(entry)) continue;

            try
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;

                using var nested = new ZipArchive(memory, ZipArchiveMode.Read);
                result.AddRange(CollectLangFiles(nested, depth + 1));
            }
            catch
            {
                // 嵌套 jar 坏了就跳过，不能把整个模组的汉化拖垮
            }
        }

        return result;
    }

    /// <summary>是不是嵌套 jar。NeoForge 用 <c>META-INF/jarjar/</c>，Fabric 用 <c>META-INF/jars/</c>。</summary>
    private static bool IsNestedJar(ZipArchiveEntry entry)
    {
        var path = entry.FullName.Replace('\\', '/');

        if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;

        return path.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读一个 zip 条目的文本内容。</summary>
    private static string? ReadEntryText(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 json 里取所有的键。</summary>
    private static List<string>? ParseKeys(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject obj
                ? obj.Select(pair => pair.Key).ToList()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按路径找条目，把反斜杠当正斜杠比（见 <see cref="LangPathPattern"/> 的注释）。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.Equals(normalized, path, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    /// <summary>从 mods.toml / fabric.mod.json 里抠显示名，抠不到就用文件名。</summary>
    private static string ReadDisplayName(ZipArchive archive, string jarPath, IReadOnlyList<string> modIds)
    {
        foreach (var path in new[]
                 {
                     "META-INF/neoforge.mods.toml",
                     "META-INF/mods.toml",
                     "fabric.mod.json",
                     "quilt.mod.json",
                 })
        {
            var entry = FindEntry(archive, path);
            if (entry is null) continue;

            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(text, @"displayName\s*=\s*""([^""]+)""");
                    if (match.Success) return match.Groups[1].Value.Trim();
                }
                else
                {
                    var match = Regex.Match(text, @"""name""\s*:\s*""([^""]+)""");
                    if (match.Success) return match.Groups[1].Value.Trim();
                }
            }
            catch
            {
                // 读不了就下一个
            }
        }

        // 兜底：文件名去掉版本号。appleskin-neoforge-mc26.3-3.0.10 → appleskin
        var name = Path.GetFileNameWithoutExtension(jarPath);
        var dash = name.IndexOf('-');
        return dash > 0 ? name[..dash] : name;
    }

    // ───────────────────────── 汉化 ─────────────────────────

    /// <summary>
    /// 汉化一个模组，把结果打成资源包丢进 <paramref name="resourcePacksDirectory"/>。
    /// </summary>
    /// <param name="info">要处理的模组。</param>
    /// <param name="resourcePacksDirectory">版本目录下的 <c>resourcepacks</c>。</param>
    /// <param name="packFormat">资源包的 <c>pack_format</c>，见 <see cref="ResolvePackFormat"/>。</param>
    /// <param name="progress">进度文字。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<ModLocalizeResult> LocalizeAsync(
        ModLocaleInfo info,
        string resourcePacksDirectory,
        PackFormat packFormat,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        LocalizationPacks? packs = null)
    {
        var warnings = new List<string>();

        try
        {
            Directory.CreateDirectory(resourcePacksDirectory);

            // 1) 把 jar 里所有语言文件读出来
            var bundles = ReadLangBundles(info.JarPath);
            if (bundles.Count == 0)
            {
                return new ModLocalizeResult(false, null, 0, 0, 0, 0, warnings, "jar 里没找到语言文件");
            }

            // 1.5) 把已有资源包的中文并进来。
            // 目的有两个：① 已经翻过的键不再花钱；② 重新生成的包是「完整包」而不是
            // 只含新条的残包 —— 否则重复点一次「汉化」会把上次的译文冲掉。
            if (packs is not null)
            {
                foreach (var bundle in bundles)
                {
                    if (!packs.TryGet(bundle.ModId, out var packEntries, out _)) continue;

                    foreach (var (key, chinese) in packEntries)
                    {
                        // 只认英文版里真有的键，且不覆盖 jar 自带的中文
                        if (bundle.English.ContainsKey(key) && !bundle.Chinese.ContainsKey(key))
                        {
                            bundle.Chinese[key] = chinese;
                        }
                    }
                }
            }

            // 2) 逐个 modid 算出「要翻哪些」
            var pending = new List<PendingText>();
            var skipped = 0;

            foreach (var bundle in bundles)
            {
                foreach (var (key, english) in bundle.English)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 已有中文就用它，官方译名永远比机翻好
                    if (bundle.Chinese.ContainsKey(key)) continue;

                    if (!ShouldTranslate(english))
                    {
                        skipped++;
                        continue;
                    }

                    pending.Add(new PendingText(bundle.ModId, key, english));
                }
            }

            if (pending.Count == 0)
            {
                // 没有要翻的 —— 照样生成资源包（可能只需要把手写的 zh_cn 合并进去）
                var emptyPath = WriteResourcePack(info, bundles, resourcePacksDirectory, packFormat, warnings);
                return new ModLocalizeResult(true, emptyPath, 0, 0, skipped, 0, warnings);
            }

            progress?.Report($"准备翻译 {pending.Count} 条…");

            // 3) 缓存先筛一遍，能白嫖的一分钱不花
            var translations = new Dictionary<(string ModId, string Key), string>();
            var reused = 0;

            foreach (var item in pending)
            {
                var cached = _cache.Get(item.English);
                if (cached is null) continue;

                translations[(item.ModId, item.Key)] = cached;
                reused++;
            }

            var toTranslate = pending.Where(p => !translations.ContainsKey((p.ModId, p.Key))).ToList();

            // 4) 分批翻
            var translated = 0;
            var failed = 0;

            var batches = Chunk(toTranslate);
            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = batches[i];
                progress?.Report($"翻译中 {i + 1}/{batches.Count}（{batch.Count} 条）…");

                var (guarded, maps) = ProtectAll(batch);

                var outcome = await _translator.TranslateBatchAsync(guarded, cancellationToken).ConfigureAwait(false);

                if (outcome.Fatal)
                {
                    return new ModLocalizeResult(
                        false, null, translated, reused, skipped, failed + (toTranslate.Count - translated),
                        warnings, outcome.Error ?? "DeepSeek 拒绝了请求（Key 或余额有问题）");
                }

                if (outcome.Error is not null)
                {
                    Log?.Invoke(this, $"[汉化] 一批没翻成：{outcome.Error}");
                    failed += batch.Count;
                    continue;
                }

                for (var j = 0; j < batch.Count; j++)
                {
                    if (!outcome.Translations.TryGetValue(j, out var raw))
                    {
                        failed++;
                        continue;
                    }

                    var restored = FormatTokenGuard.Restore(raw, maps[j]);
                    var problem = FormatTokenGuard.Validate(batch[j].English, restored);

                    if (problem is not null)
                    {
                        warnings.Add($"{batch[j].Key}：{problem}，已丢弃");
                        failed++;
                        continue;
                    }

                    // 中文比英文宽一倍，超太多的译文进游戏会被截断，先提醒
                    if (restored.Length > batch[j].English.Length * MaxLengthRatio &&
                        restored.Length > 6)
                    {
                        warnings.Add($"{batch[j].Key}：译文偏长（{restored.Length} vs 原文 {batch[j].English.Length}），可能被 UI 截断");
                    }

                    translations[(batch[j].ModId, batch[j].Key)] = restored;
                    _cache.Set(batch[j].English, restored);
                    translated++;
                }
            }

            // 5) 合并回语言文件并打包
            foreach (var bundle in bundles)
            {
                foreach (var ((modId, key), text) in translations)
                {
                    if (modId != bundle.ModId) continue;
                    bundle.Chinese[key] = text;
                }
            }

            var packPath = WriteResourcePack(info, bundles, resourcePacksDirectory, packFormat, warnings);

            progress?.Report($"完成：新翻 {translated} 条，复用 {reused} 条");
            Log?.Invoke(this, $"[汉化] {info.DisplayName}：新翻 {translated}，复用 {reused}，跳过 {skipped}，失败 {failed} → {packPath}");

            return new ModLocalizeResult(true, packPath, translated, reused, skipped, failed, warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"[汉化] {info.DisplayName} 失败：{ex.GetType().Name}: {ex.Message}");
            return new ModLocalizeResult(false, null, 0, 0, 0, 0, warnings, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 资源包该按哪套格式写。
    /// </summary>
    /// <param name="Major">主版本号（旧格式的 <c>pack_format</c>，新格式的 <c>min_format</c> 第一段）。</param>
    /// <param name="Minor">次版本号，只有 1.21.9+ 才有意义。</param>
    /// <param name="HasMinorVersion">
    /// 是不是 1.21.9（25w31a）之后那套新规则。
    /// </param>
    /// <remarks>
    /// <para>
    /// 2026 年官方改了元数据格式，两套规则<b>互斥</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item>1.21.9+：必须写 <c>min_format</c> / <c>max_format</c>，
    ///         而 <c>supported_formats</c> <b>写了就判非法</b>（除非还声明支持 65 以下的老版本）。</item>
    ///   <item>1.21.8-：只认 <c>pack_format</c>，写新字段等于没写。</item>
    /// </list>
    /// <para>
    /// 实测各版本：1.20.1 = 15、1.21.1 = 34、26.3 = 97.1（<c>resource_major</c> 97 + <c>resource_minor</c> 1）。
    /// </para>
    /// </remarks>
    public readonly record struct PackFormat(int Major, int Minor, bool HasMinorVersion);

    /// <summary>一批待翻的文本。</summary>
    private sealed record PendingText(string ModId, string Key, string English);

    /// <summary>按条数和字数双重切批。</summary>
    private static List<List<PendingText>> Chunk(IReadOnlyList<PendingText> items)
    {
        var batches = new List<List<PendingText>>();
        var current = new List<PendingText>();
        var chars = 0;

        foreach (var item in items)
        {
            if (current.Count >= BatchSize || (chars + item.English.Length > BatchMaxChars && current.Count > 0))
            {
                batches.Add(current);
                current = new List<PendingText>();
                chars = 0;
            }

            current.Add(item);
            chars += item.English.Length;
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>整批做格式符保护，返回保护后的文本和各自的还原表。</summary>
    private static (List<string> Guarded, List<IReadOnlyList<string>> Maps) ProtectAll(List<PendingText> batch)
    {
        var guarded = new List<string>(batch.Count);
        var maps = new List<IReadOnlyList<string>>(batch.Count);

        foreach (var item in batch)
        {
            var (text, map) = FormatTokenGuard.Protect(item.English);
            guarded.Add(text);
            maps.Add(map);
        }

        return (guarded, maps);
    }

    /// <summary>
    /// 这条值不值得翻。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>复用 <see cref="TranslationTextCleaner.WorthTranslating"/> ——
    /// 那条规则是为「模组简介」定的（至少 12 个字、必须含空格），
    /// 而语言文件里全是 <c>Back</c>、<c>Durability</c>、<c>Auto-Save</c> 这种短词，
    /// 照搬过来九成都被挡掉，等于没做事。
    /// </remarks>
    public static bool ShouldTranslate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();

        // 纯数字 / 纯符号 / 只有格式码的键：翻了也没意义
        if (!HasLatinLetter.IsMatch(trimmed)) return false;

        // 已经是中文的（有些模组会把中文直接写进 en_us.json）
        if (HasCjk.IsMatch(trimmed)) return false;

        return true;
    }

    // ───────────────────────── 读写 ─────────────────────────

    /// <summary>一个 modid 的语言文件。</summary>
    private sealed class LangBundle
    {
        public required string ModId { get; init; }
        public required Dictionary<string, string> English { get; init; }
        public required Dictionary<string, string> Chinese { get; init; }
    }

    /// <summary>把 jar（含嵌套 jar）里所有语言文件读成内存对象。</summary>
    private static List<LangBundle> ReadLangBundles(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);

        var english = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var chinese = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var file in CollectLangFiles(archive))
        {
            var map = ParseStringMap(file.Json);
            if (map is null) continue;

            var bucket = file.IsEnglish ? english : chinese;
            if (!bucket.TryGetValue(file.ModId, out var existing))
            {
                existing = new Dictionary<string, string>(StringComparer.Ordinal);
                bucket[file.ModId] = existing;
            }

            foreach (var (key, value) in map) existing[key] = value;
        }

        var result = new List<LangBundle>();

        foreach (var (modId, en) in english)
        {
            result.Add(new LangBundle
            {
                ModId = modId,
                English = en,
                Chinese = chinese.TryGetValue(modId, out var zh)
                    ? new Dictionary<string, string>(zh, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal),
            });
        }

        return result;
    }

    /// <summary>把 json 文本解析成键值对。值不是字符串的（数字、布尔）转成字符串。</summary>
    private static Dictionary<string, string>? ParseStringMap(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject obj) return null;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in obj)
            {
                map[key] = value switch
                {
                    null => string.Empty,
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    _ => value.ToJsonString(),
                };
            }

            return map;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成资源包 zip。</summary>
    /// <remarks>
    /// 资源包放在 <c>resourcepacks/</c> 下，文件名带 <c>-zh_cn</c> 后缀，
    /// 用户在游戏里一眼能认出这是启动器做的，随时可以关掉。
    /// </remarks>
    private static string WriteResourcePack(
        ModLocaleInfo info,
        List<LangBundle> bundles,
        string resourcePacksDirectory,
        PackFormat packFormat,
        List<string> warnings)
    {
        var fileName = $"{SafeFileName(info.DisplayName)}-zh_cn.zip";
        var path = Path.Combine(resourcePacksDirectory, fileName);

        if (File.Exists(path)) File.Delete(path);

        var written = 0;

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "pack.mcmeta", BuildPackMeta(packFormat, info.DisplayName));
            written++;

            foreach (var bundle in bundles)
            {
                if (bundle.Chinese.Count == 0) continue;

                WriteEntry(archive, $"assets/{bundle.ModId}/lang/zh_cn.json", SerializeLang(bundle.Chinese));
                written++;
            }
        }

        // 一个 modid 都没写进去说明中文是空的，这种包别留在那儿误导人
        if (written <= 1)
        {
            File.Delete(path);
            throw new InvalidOperationException("没有可写入的中文条目，资源包未生成");
        }

        return path;
    }

    /// <summary>
    /// 组装 <c>pack.mcmeta</c>。
    /// </summary>
    /// <remarks>
    /// 两套规则互斥，见 <see cref="PackFormat"/> 的说明 —— 新版本写 <c>supported_formats</c>
    /// 会被判定为格式非法，整个资源包不加载，所以这里必须分开写。
    /// </remarks>
    private static string BuildPackMeta(PackFormat format, string displayName)
    {
        var pack = new JsonObject
        {
            ["description"] = $"McLauncher AI 汉化 · {displayName}",
        };

        if (format.HasMinorVersion)
        {
            // 1.21.9+：min_format 写全 [97, 0]；max_format 写单个整数表示「该主版本的任何次版本」
            pack["min_format"] = new JsonArray { format.Major, 0 };
            pack["max_format"] = format.Major;
        }
        else
        {
            // 1.21.8-：只认 pack_format
            pack["pack_format"] = format.Major;
        }

        return new JsonObject { ["pack"] = pack }.ToJsonString(WriteOptions);
    }

    /// <summary>序列化语言文件。键排序，方便 diff。</summary>
    private static string SerializeLang(Dictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            obj[key] = value;
        }

        return obj.ToJsonString(WriteOptions);
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    /// <summary>文件系统不认的字符换掉，免得建包失败。</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var clean = new string(chars).Trim();

        return string.IsNullOrEmpty(clean) ? "mod" : clean;
    }

    // ───────────────────────── pack_format ─────────────────────────

    /// <summary>
    /// 问出这个版本该用哪套资源包格式。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 读游戏自己的 <c>version.json</c>（原版 jar 里就有一份，NeoForge 的 jar 也是原版 jar 的副本，
    /// 同样带这个文件）—— 这是唯一不会过时的来源，硬编码版本号对照表迟早跟不上新编号。
    /// </para>
    /// <para>
    /// 读不到就退到 34（1.21 的值）。MC 对格式不匹配的资源包只是提示「为旧版本制作」，
    /// 不会拒绝加载，所以退路是安全的。
    /// </para>
    /// </remarks>
    public static PackFormat ResolvePackFormat(string versionDirectory, string versionName)
    {
        var fallback = new PackFormat(34, 0, false);

        try
        {
            var jar = Path.Combine(versionDirectory, versionName + ".jar");
            if (!File.Exists(jar)) return fallback;

            using var archive = ZipFile.OpenRead(jar);

            var entry = FindEntry(archive, "version.json");
            if (entry is null) return fallback;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject root) return fallback;
            if (root["pack_version"] is not JsonObject packVersion) return fallback;

            // 1.21.9+：{ "resource_major": 97, "resource_minor": 1 }
            if (packVersion["resource_major"] is JsonValue majorNode &&
                majorNode.TryGetValue<int>(out var major) && major > 0)
            {
                var minor = packVersion["resource_minor"] is JsonValue minorNode &&
                            minorNode.TryGetValue<int>(out var parsedMinor)
                    ? parsedMinor
                    : 0;

                return new PackFormat(major, minor, true);
            }

            // 1.21.8-：{ "resource": 34 }
            if (packVersion["resource"] is JsonValue legacyNode &&
                legacyNode.TryGetValue<int>(out var legacy) && legacy > 0)
            {
                return new PackFormat(legacy, 0, false);
            }

            return fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
