using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeepDive.Core.Launcher;

namespace DeepDive.Core.Mods;

/// <summary>依赖的强制性与方向，对应 Modrinth / Fabric / Forge 三家的语义。</summary>
public enum ModDependencyKind
{
    /// <summary>必需，缺了加载器直接拒绝启动。</summary>
    Required,

    /// <summary>可选，缺了不报错但功能不全。</summary>
    Optional,

    /// <summary>互相冲突，同时存在必崩。</summary>
    Incompatible,

    /// <summary>已内嵌在对方 jar 里，不用单独装。</summary>
    Embedded,
}

/// <summary>一条依赖声明。</summary>
/// <param name="ModId">依赖的 modId（小写，如 <c>fabric-api</c>）。</param>
/// <param name="VersionRange">版本范围原文。<c>*</c> / 空 = 不限。</param>
/// <param name="Kind">强制性。</param>
public sealed record ModDependency(string ModId, string VersionRange, ModDependencyKind Kind);

/// <summary>
/// 从一个 jar 里读出来的模组身份信息。
/// </summary>
/// <param name="ModId">模组 ID。跨加载器的<b>唯一</b>身份，按它判重和配对依赖。</param>
/// <param name="Name">显示名。</param>
/// <param name="Version">模组自己的版本（不是游戏版本）。</param>
/// <param name="Kind">这个 jar 是哪个加载器的。</param>
/// <param name="Dependencies">依赖声明。</param>
/// <param name="FileName">jar 文件名，报错时告诉用户是哪一个。</param>
/// <param name="DeclaredGameVersion">声明支持的游戏版本范围（有就记下来，用于版本不匹配提醒）。</param>
/// <param name="Description">模组简介，管理界面显示用。可能是 null。</param>
/// <param name="Authors">作者，逗号分隔。可能是 null。</param>
/// <param name="IconEntry">jar 内图标文件的条目路径，取不到就是 null。</param>
/// <param name="FullPath">jar 的完整路径。<c>null</c> 表示只做了内存解析（比如从流里读）。</param>
public sealed record ModMetadata(
    string ModId,
    string Name,
    string Version,
    ModLoaderKind Kind,
    IReadOnlyList<ModDependency> Dependencies,
    string FileName,
    string? DeclaredGameVersion,
    string? Description = null,
    string? Authors = null,
    string? IconEntry = null,
    string? FullPath = null);

/// <summary>预检发现的问题有多严重。</summary>
public enum PreflightSeverity
{
    /// <summary>基本必然崩溃，或者加载器会直接拒绝加载。</summary>
    Error,

    /// <summary>可能有问题，值得提醒但拦不住。</summary>
    Warning,
}

/// <summary>一条预检问题。</summary>
/// <param name="Severity">严重程度。</param>
/// <param name="Title">一句话结论（「缺少前置：Fabric API」）。</param>
/// <param name="Detail">细节与建议。</param>
/// <param name="SourceFile">涉及的 jar 文件名，可能为空。</param>
public sealed record ModPreflightIssue(
    PreflightSeverity Severity,
    string Title,
    string Detail,
    string? SourceFile)
{
    public bool IsError => Severity == PreflightSeverity.Error;
}

/// <summary>一次预检的完整结果。</summary>
/// <param name="Issues">发现的问题，Error 排在前面。</param>
/// <param name="ScannedJars">实际读了元数据的 jar 数。</param>
/// <param name="TotalJars">目录里的 jar 总数（含读不动的）。</param>
/// <param name="Mods">读出来的模组清单（给界面展示用）。</param>
public sealed record ModPreflightReport(
    IReadOnlyList<ModPreflightIssue> Issues,
    int ScannedJars,
    int TotalJars,
    IReadOnlyList<ModMetadata> Mods)
{
    public int ErrorCount => Issues.Count(i => i.IsError);
    public int WarningCount => Issues.Count(i => !i.IsError);

    /// <summary>有没有值得拦一下的东西。</summary>
    public bool HasProblems => Issues.Count > 0;

    /// <summary>有没有会导致必然崩溃的问题（用来决定「仍然启动」要不要标红）。</summary>
    public bool HasBlockingProblem => ErrorCount > 0;

    /// <summary>一行摘要（「2 个错误，1 个提醒」/「没查出问题」）。</summary>
    public string Summary => (ErrorCount, WarningCount) switch
    {
        (0, 0) => $"检查了 {ScannedJars} 个模组，没发现问题",
        (0, var w) => $"{w} 个提醒",
        (var e, 0) => $"{e} 个问题会导致启动失败",
        (var e, var w) => $"{e} 个问题会导致启动失败，{w} 个提醒",
    };
}

/// <summary>
/// 启动前的模组体检：读每个 jar 的元数据，把「必然崩」的组合在启动前就指出来。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么值得做</b>：模组缺前置时，游戏给的不是「你缺 Fabric API」，
/// 而是一屏 <c>NoClassDefFoundError</c> 加某个类的完整包名。玩家看不出该装什么，
/// 只能去贴吧问。这里在启动前就说清楚缺谁，省掉整轮排查。
/// </para>
/// <para>
/// <b>为什么不检查所有东西</b>：误报比漏报更烦人 —— 每次启动都被拦一下，
/// 用户很快就学会无脑点「仍然启动」，那这个功能就白做了。所以只查三类
/// <b>确定会出问题</b>的：
/// </para>
/// <list type="number">
///   <item><b>缺必需前置</b>：depends 里 required 的 modId 在本地一个都找不到</item>
///   <item><b>同一个 modId 装了两份</b>：加载器会报 duplicate mod，几乎必崩</item>
///   <item><b>加载器不匹配</b>：Fabric 的 jar 丢进 Forge 版本里（或反过来），
///         这种 jar 根本不会被加载，但用户只会觉得「模组没生效」</item>
/// </list>
/// <para>
/// 版本范围不满足只报 Warning：三家的范围语法差异大（Fabric semver vs Forge Maven），
/// 解析器再小心也可能看错，不该因为一句看错的 <c>~0.5.0</c> 挡住进游戏的路。
/// </para>
/// </remarks>
public static class ModPreflight
{
    /// <summary>
    /// 这些 modId 由加载器/游戏本体提供，不该被当成「缺失的前置」。
    /// </summary>
    /// <remarks>
    /// 漏一个就会天天误报。比如 Fabric 模组常写 <c>"java": "&gt;=17"</c>，
    /// 而 <c>java</c> 不是模组；<c>minecraft</c> 更显然。
    /// </remarks>
    private static readonly HashSet<string> BuiltInModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "forge", "neoforge", "fabricloader", "fabric-loader",
        "quilt_loader", "quiltloader", "quilt_base", "mcp", "minecraftforge",
        "javafml", "lowcodefml", "modlauncher", "mixinextras",
    };

    /// <summary>Fabric 的「Fabric API」在不同版本里 modId 不一样，都要认。</summary>
    private static readonly HashSet<string> FabricApiIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "fabric-api", "fabric_api",
    };

    /// <summary>单次预检最多读多少个 jar。再多就只报数量，不逐个解析（读盘太慢）。</summary>
    private const int MaxJarsToParse = 400;

    /// <summary>
    /// 这些后缀的 jar 不是给游戏加载的，必须跳过。
    /// </summary>
    /// <remarks>
    /// <b>不跳会误报「装了两份」</b>：开发时把 <c>xxx-1.0.jar</c> 和
    /// <c>xxx-1.0-sources.jar</c> 一起扔进 mods 目录很常见，两者 modId 相同 ——
    /// 但它们不会同时被加载（源码包根本没有 class）。
    /// </remarks>
    private static readonly string[] NonRuntimeSuffixes =
    {
        "-sources.jar", "-javadoc.jar", "-dev.jar", "-sources-dev.jar",
    };

    /// <summary>
    /// 扫一个 mods 目录做体检。
    /// </summary>
    /// <param name="modsDirectory">模组目录。不存在 / 空的直接返回空报告。</param>
    /// <param name="expectedLoader">这个版本实际用的加载器，用来查「jar 放错地方」。</param>
    /// <param name="log">日志回调，可以传 null。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <remarks>
    /// <b>这个方法不抛异常</b>（除取消除外）。预检属于「锦上添花」，
    /// 它自己坏掉不该让人连游戏都启动不了 —— 所有读不动的 jar 都只记 Warning。
    /// </remarks>
    public static async Task<ModPreflightReport> ScanAsync(
        string modsDirectory,
        ModLoaderKind expectedLoader,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
            return new ModPreflightReport(Array.Empty<ModPreflightIssue>(), 0, 0, Array.Empty<ModMetadata>());

        string[] files;
        try
        {
            files = Directory.GetFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
                .Where(f => !NonRuntimeSuffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[预检] 读不了模组目录：{ex.GetType().Name}: {ex.Message}");
            return new ModPreflightReport(Array.Empty<ModPreflightIssue>(), 0, 0, Array.Empty<ModMetadata>());
        }

        if (files.Length == 0)
            return new ModPreflightReport(Array.Empty<ModPreflightIssue>(), 0, 0, Array.Empty<ModMetadata>());

        var mods = await Task.Run(
            () => ReadAll(files, expectedLoader, log, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var issues = Check(mods, expectedLoader, files.Length, log);

        return new ModPreflightReport(issues, mods.Count, files.Length, mods);
    }

    // ── 读 jar ──────────────────────────────────────────

    private static List<ModMetadata> ReadAll(
        IReadOnlyList<string> files,
        ModLoaderKind expectedLoader,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var result = new List<ModMetadata>(files.Count);
        var parsed = 0;

        foreach (var path in files)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (parsed >= MaxJarsToParse)
            {
                log?.Invoke($"[预检] jar 太多（{files.Count} 个），只解析前 {MaxJarsToParse} 个");
                break;
            }

            try
            {
                var meta = ReadOne(path);
                if (meta is not null) result.Add(meta);
                parsed++;
            }
            catch (Exception ex)
            {
                // 加密的、损坏的、格式怪异的 —— 记一笔就走，绝不中断整个预检
                log?.Invoke($"[预检] 读不动 {Path.GetFileName(path)}：{ex.GetType().Name}");
            }
        }

        return result;
    }

    /// <summary>读一个 jar。认不出是模组（没有元数据文件）时返回 <c>null</c>。</summary>
    public static ModMetadata? ReadOne(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var fileName = Path.GetFileName(jarPath);

        // NeoForge 的元数据要<b>先于</b> Fabric 判。原因：NeoForge 侧为了兼容 Quilt/Fabric，
        // 常常同时打包一份 fabric.mod.json 存根 —— 但它的真实身份是 neoforge.mods.toml。
        // 若先判 fabric.mod.json，Iris 这类「双元数据」的 jar 会被误判成 Fabric 模组，
        // 在 NeoForge 实例里报「加载器不对」。（实测 iris-neoforge-1.11.7 就是这种。）
        var neoToml = FindEntry(archive, "META-INF/neoforge.mods.toml");
        if (neoToml is not null)
            return ParseToml(neoToml, fileName, ModLoaderKind.NeoForge, ReadManifestVersion(archive), jarPath);

        var fabric = FindEntry(archive, "fabric.mod.json");
        if (fabric is not null)
            return ParseFabric(fabric, fileName, jarPath);

        var forgeToml = FindEntry(archive, "META-INF/mods.toml");
        if (forgeToml is not null)
        {
            // 1.20.1 的 NeoForge 模组也写 mods.toml。文件名带 neoforge 的按 NeoForge 算，
            // 否则会把 NeoForge 模组装进 Forge 实例的警告打成误报
            var looksNeo = fileName.Contains("neoforge", StringComparison.OrdinalIgnoreCase);
            return ParseToml(
                forgeToml,
                fileName,
                looksNeo ? ModLoaderKind.NeoForge : ModLoaderKind.Forge,
                ReadManifestVersion(archive),
                jarPath);
        }

        // 没有元数据文件的「库 jar」：Forge/NeoForge 的 FMLModType=LIBRARY 打包（KotlinForForge、
        // Architectury 之类的前置库）。它们靠 MANIFEST 标记被加载器识别成某个 modId 提供者，
        // 但没有 mods.toml —— 不识别的话，依赖它们的模组会被误报「缺少前置」。
        return ParseLibraryJar(archive, fileName, jarPath);
    }

    /// <summary>
    /// 识别 <c>FMLModType: LIBRARY</c> 的库 jar（Forge/NeoForge 前置库，如 KotlinForForge）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这类 jar 没有 <c>mods.toml</c>，modId 由加载器从 MANIFEST 或文件名推断。
    /// 我们这里按<b>文件名主名</b>推断：<c>kotlinforforge-6.4.0-all.jar</c> → <c>kotlinforforge</c>。
    /// 这足够让「缺前置」检查认得出它已经装上了，不追求和加载器的推断逐字一致。
    /// </para>
    /// <para>
    /// 只有 MANIFEST 明确写了 <c>FMLModType: LIBRARY</c>（或 <c>MOD</c>）才认，
    /// 避免把随便一个依赖库 jar（jankson、kotlin-stdlib 等）误当成模组。
    /// </para>
    /// </remarks>
    private static ModMetadata? ParseLibraryJar(ZipArchive archive, string fileName, string? fullPath)
    {
        var manifest = FindEntry(archive, "META-INF/MANIFEST.MF");
        if (manifest is null) return null;

        string? text;
        try
        {
            text = ReadText(manifest);
        }
        catch
        {
            return null;
        }

        if (text is null) return null;

        // FMLModType 可能是 LIBRARY / MOD / GAMELIBRARY / LANGPROVIDER 等，只认库/模组两类
        var fmlType = Regex.Match(text, @"(?im)^FMLModType\s*:\s*(\S+)");
        if (!fmlType.Success) return null;

        var type = fmlType.Groups[1].Value.Trim();
        if (!string.Equals(type, "LIBRARY", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "MOD", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "GAMELIBRARY", StringComparison.OrdinalIgnoreCase))
            return null;

        var modId = InferLibraryModId(fileName);
        if (modId is null) return null;

        // 库 jar 的版本也从文件名推断，尽量去掉 -all / -shaded 这类后缀
        var version = InferLibraryVersion(fileName) ?? "?";

        return new ModMetadata(
            modId,
            modId,
            version,
            ModLoaderKind.NeoForge,
            Array.Empty<ModDependency>(),
            fileName,
            null,
            FullPath: fullPath);
    }

    /// <summary>从库 jar 文件名推断 modId：取第一个「版本号片段」之前的主名，去掉 <c>-all</c> 等后缀。</summary>
    private static string? InferLibraryModId(string fileName)
    {
        var stem = fileName;
        if (stem.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];

        // 去掉 -all / -shaded / -api 这类常见后缀，保留主名
        stem = Regex.Replace(stem, @"[-.](?:all|shaded|sources|dev|api)$", "", RegexOptions.IgnoreCase);

        // 主名里若含版本号（数字开头的一段），从第一个「-数字」处截断。
        // kotlinforforge-6.4.0 → kotlinforforge；architectury-9.1.12 → architectury
        var cut = Regex.Match(stem, @"^(.+?)-\d");
        if (cut.Success) stem = cut.Groups[1].Value;

        return string.IsNullOrWhiteSpace(stem) ? null : stem;
    }

    /// <summary>从库 jar 文件名推断版本：第一个「-数字」之后的部分，去掉 <c>-all</c>。</summary>
    private static string? InferLibraryVersion(string fileName)
    {
        var stem = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

        var m = Regex.Match(stem, @"-(\d[\w.\-]*)");
        if (!m.Success) return null;

        var ver = m.Groups[1].Value;
        // 去掉尾部的 -all / -shaded
        ver = Regex.Replace(ver, @"-(all|shaded)$", "", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(ver) ? null : ver;
    }

    /// <summary>
    /// 从 <c>META-INF/MANIFEST.MF</c> 里取 <c>Implementation-Version</c>。
    /// </summary>
    /// <remarks>
    /// <b>给 <c>${file.jarVersion}</c> 占位符兜底</b>：Forge 系一直有「toml 里写占位符、
    /// 真版本放 MANIFEST」的写法（AppleSkin 到现在还这样）。不解析的话版本号就是那串
    /// 占位符，依赖方的版本范围判断直接失效。
    /// </remarks>
    private static string? ReadManifestVersion(ZipArchive archive)
    {
        var manifest = FindEntry(archive, "META-INF/MANIFEST.MF");
        if (manifest is null) return null;

        try
        {
            var text = ReadText(manifest);
            if (text is null) return null;

            var match = Regex.Match(text, @"^Implementation-Version:\s*(\S+)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 <c>fabric.mod.json</c>。</summary>
    private static ModMetadata? ParseFabric(ZipArchiveEntry entry, string fileName, string? fullPath = null)
    {
        var json = ReadText(entry);
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            // 模组作者手写 JSON 常带注释和尾逗号，宽松模式能救回一大半
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = doc.RootElement;
        var id = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var deps = new List<ModDependency>();
        string? declaredGame = null;

        // depends: 必需依赖
        if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in depends.EnumerateObject())
            {
                var range = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "*" : "*";
                deps.Add(new ModDependency(p.Name, range, ModDependencyKind.Required));

                if (string.Equals(p.Name, "minecraft", StringComparison.OrdinalIgnoreCase))
                    declaredGame = range;
            }
        }

        // suggests / recommends: 可选
        foreach (var field in new[] { "recommends", "suggests" })
        {
            if (!root.TryGetProperty(field, out var opt) || opt.ValueKind != JsonValueKind.Object) continue;

            foreach (var p in opt.EnumerateObject())
            {
                var range = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "*" : "*";
                deps.Add(new ModDependency(p.Name, range, ModDependencyKind.Optional));
            }
        }

        // breaks: 明确不兼容
        if (root.TryGetProperty("breaks", out var breaks) && breaks.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in breaks.EnumerateObject())
            {
                deps.Add(new ModDependency(p.Name, "*", ModDependencyKind.Incompatible));
            }
        }

        return new ModMetadata(
            id!,
            GetString(root, "name") ?? id!,
            GetString(root, "version") ?? "?",
            ModLoaderKind.Fabric,
            deps,
            fileName,
            declaredGame,
            Description: GetString(root, "description"),
            Authors: ReadFabricAuthors(root),
            IconEntry: ReadFabricIcon(root),
            FullPath: fullPath);
    }

    /// <summary>
    /// <c>fabric.mod.json</c> 的 <c>authors</c> 有两种写法：
    /// 字符串数组 <c>["a","b"]</c>，或对象数组 <c>[{"name":"a"}]</c>（新版 schema）。
    /// 也有直接写成单个字符串的。
    /// </summary>
    private static string? ReadFabricAuthors(JsonElement root)
    {
        if (!root.TryGetProperty("authors", out var authors)) return null;

        if (authors.ValueKind == JsonValueKind.String)
            return authors.GetString();

        if (authors.ValueKind != JsonValueKind.Array) return null;

        var names = new List<string>();

        foreach (var item in authors.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) names.Add(s!);
            }
            else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n))
            {
                var s = n.GetString();
                if (!string.IsNullOrWhiteSpace(s)) names.Add(s!);
            }
        }

        return names.Count == 0 ? null : string.Join("、", names);
    }

    /// <summary>
    /// <c>fabric.mod.json</c> 的 <c>icon</c>：通常是相对 jar 根的 png 路径，
    /// 也可能按尺寸给多个（<c>{"64":"a.png","128":"b.png"}</c>），取最大的。
    /// </summary>
    private static string? ReadFabricIcon(JsonElement root)
    {
        if (!root.TryGetProperty("icon", out var icon)) return null;

        if (icon.ValueKind == JsonValueKind.String)
            return NormalizeIconPath(icon.GetString());

        if (icon.ValueKind != JsonValueKind.Object) return null;

        string? best = null;
        var bestSize = -1;

        foreach (var p in icon.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;

            var size = int.TryParse(p.Name, out var n) ? n : 0;
            if (size <= bestSize) continue;

            bestSize = size;
            best = p.Value.GetString();
        }

        return NormalizeIconPath(best);
    }

    /// <summary>图标路径统一成 jar 内相对路径（去掉开头的 <c>/</c>、统一斜杠）。</summary>
    private static string? NormalizeIconPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // base64 内嵌图标（data:image/png;base64,...）当作没有 —— 解出来还要多一层解码，
        // 而且用的模组极少，不值得为它加分支
        if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        return path.TrimStart('/', '\\').Replace('\\', '/');
    }

    /// <summary>
    /// 把 TOML 的「值」部分洗干净：去引号、去掉行尾注释。
    /// </summary>
    /// <remarks>
    /// <b>行内注释是必须处理的</b>：<c>iconFile = "sodium-icon.png" #optional</c> 这种写法很常见，
    /// 不处理就会得到 <c>sodium-icon.png" #optional</c> 这样一串鬼东西，
    /// 然后图标永远读不到（实测 Sodium 就是这样）。
    /// </remarks>
    private static string CleanTomlValue(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return raw;

        // 带引号：取配对引号之间的内容，后面的注释自动丢掉
        if (raw[0] is '"' or '\'')
        {
            var quote = raw[0];
            var close = raw.IndexOf(quote, 1);
            return close > 0 ? raw[1..close] : raw.Trim(quote);
        }

        // 不带引号：截掉 # 之后
        var hash = raw.IndexOf('#');
        return hash >= 0 ? raw[..hash].Trim() : raw;
    }

    /// <summary>找行内三引号（<c>'''</c> 或 <c>"""</c>）的位置，没有返回 -1。</summary>
    private static int FindTripleQuote(string line)
    {
        var single = line.IndexOf("'''", StringComparison.Ordinal);
        var doubleQuote = line.IndexOf("\"\"\"", StringComparison.Ordinal);

        if (single < 0) return doubleQuote;
        if (doubleQuote < 0) return single;

        return Math.Min(single, doubleQuote);
    }

    /// <summary>
    /// 从 jar 里把图标读成字节（PNG）。取不到就返回 <c>null</c>。
    /// </summary>
    /// <param name="jarPath">jar 完整路径。</param>
    /// <param name="iconEntry">元数据里声明的图标路径（<see cref="ModMetadata.IconEntry"/>）。</param>
    /// <remarks>
    /// <para>
    /// <b>按需读，不在扫描时批量读</b>：一个整合包几百个模组，图标加起来几十 MB，
    /// 全部读进内存毫无必要。列表滚动到哪个读哪个（界面侧做缓存）。
    /// </para>
    /// <para>
    /// 有个常见的坑：<c>logoFile="sodium-icon.png"</c> 这种写的是 <b>jar 根目录</b>下的文件名，
    /// 但也有模组写成 <c>assets/xxx/icon.png</c>；还有的压根不写路径只写文件名而文件在别处。
    /// 所以先按声明路径找，找不到就按文件名在所有条目里搜一遍。
    /// </para>
    /// </remarks>
    public static byte[]? ReadIcon(string jarPath, string? iconEntry)
    {
        if (string.IsNullOrWhiteSpace(jarPath) || string.IsNullOrWhiteSpace(iconEntry)) return null;
        if (!File.Exists(jarPath)) return null;

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            var entry = FindEntry(archive, iconEntry);

            // 声明路径找不到时按文件名兜底（作者改了目录没改元数据的情况不少）
            if (entry is null)
            {
                var name = Path.GetFileName(iconEntry);
                entry = archive.Entries.FirstOrDefault(
                    e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            if (entry is null || entry.Length == 0 || entry.Length > 2 * 1024 * 1024) return null;

            using var stream = entry.Open();
            using var memory = new MemoryStream((int)entry.Length);
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            // 图标读不出来不影响任何功能，静默失败
            return null;
        }
    }

    /// <summary>
    /// 解析 Forge / NeoForge 的 <c>mods.toml</c>。
    /// </summary>
    /// <remarks>
    /// 不引 TOML 库：只需要 <c>modId</c> / <c>version</c> / <c>[[dependencies.*]]</c> 三样，
    /// 逐行扫就够。真正的 TOML 解析器还要处理多行字符串、日期、嵌套表，
    /// 为一个预检功能引一整个库不划算。
    /// </remarks>
    private static ModMetadata? ParseToml(
        ZipArchiveEntry entry,
        string fileName,
        ModLoaderKind kind,
        string? manifestVersion,
        string? fullPath = null)
    {
        var text = ReadText(entry);
        if (text is null) return null;

        var lines = text.Split('\n');

        string? modId = null;
        string? displayName = null;
        string? version = null;
        string? declaredGame = null;
        string? description = null;
        string? authors = null;
        string? logoFile = null;

        var deps = new List<ModDependency>();

        // 当前在哪个段里：null = 顶层；"mods" = [[mods]]；"dep" = [[dependencies.*]]
        string? section = null;

        string? depModId = null;
        string? depRange = null;
        string? depType = null;
        var depMandatory = true;

        // TOML 多行字符串（''' 或 """）必须整段跳过，否则里面的内容会被当成键值对读进来。
        // Sodium 的 description 就是 ''' 包起来的一大段 —— 里面要是出现 modId=xxx，
        // 就会污染解析结果（把描述里的字当成真依赖）。
        //
        // 注意开始记号可能**不在行首**：`description = '''` 才是常见写法，
        // 所以不能用 StartsWith 判，得看整行里有没有。
        string? inMultiline = null;
        string? multilineKey = null;
        var multilineBuffer = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // ── 多行字符串内部 ──
            if (inMultiline is not null)
            {
                var close = line.IndexOf(inMultiline, StringComparison.Ordinal);

                if (close < 0)
                {
                    if (multilineBuffer.Length < 2000) multilineBuffer.AppendLine(line);
                    continue;
                }

                multilineBuffer.Append(line[..close]);
                AssignMultiline(multilineKey, multilineBuffer.ToString());

                inMultiline = null;
                multilineKey = null;
                multilineBuffer.Clear();
                continue;
            }

            // ── 多行字符串开始 ──
            var tripleAt = FindTripleQuote(line);
            if (tripleAt >= 0)
            {
                var delim = line.Substring(tripleAt, 3);

                // 同一行里还出现第二次 = 单行三引号（key = '''abc'''），当普通值处理
                if (line.IndexOf(delim, tripleAt + 3, StringComparison.Ordinal) < 0)
                {
                    multilineKey = line[..tripleAt].Trim().TrimEnd('=').Trim();
                    multilineBuffer.Clear();
                    inMultiline = delim;
                }

                continue;
            }

            // 段头
            if (line.StartsWith('['))
            {
                FlushDependency();

                if (line.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "mods";
                }
                else if (line.StartsWith("[[dependencies.", StringComparison.OrdinalIgnoreCase))
                {
                    section = "dep";
                }
                else
                {
                    // 别的段（[[mixins]] / [modproperties.x] 等）一律忽略
                    section = null;
                }

                continue;
            }

            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = CleanTomlValue(line[(eq + 1)..]);

            switch (section)
            {
                case "mods":
                    switch (key)
                    {
                        case "modId" when modId is null: modId = value; break;
                        case "displayName" when displayName is null: displayName = value; break;
                        case "version" when version is null: version = value; break;
                        case "description" when description is null: description = value; break;
                        case "authors" when authors is null: authors = value; break;

                        // 图标字段有两套写法：NeoForge 用 iconFile，老 Forge 用 logoFile。
                        // 实测 AppleSkin / Sodium 都写 iconFile —— 只认 logoFile 会全部读不到
                        case "iconFile" when logoFile is null: logoFile = value; break;
                        case "logoFile" when logoFile is null: logoFile = value; break;
                    }
                    break;

                case "dep":
                    switch (key)
                    {
                        case "modId": depModId = value; break;
                        case "versionRange": depRange = value; break;

                        // 老写法
                        case "mandatory":
                            depMandatory = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                            break;

                        // 新写法（NeoForge 1.20.5+）：type="required" / "optional" / "incompatible"
                        case "type":
                            depType = value;
                            depMandatory = !value.Equals("optional", StringComparison.OrdinalIgnoreCase)
                                           && !value.Equals("incompatible", StringComparison.OrdinalIgnoreCase);
                            break;
                    }
                    break;
            }
        }

        FlushDependency();

        if (string.IsNullOrWhiteSpace(modId)) return null;

        return new ModMetadata(
            modId!,
            string.IsNullOrWhiteSpace(displayName) ? modId! : displayName!,
            ResolveVersion(version, manifestVersion),
            kind,
            deps,
            fileName,
            declaredGame,
            Description: string.IsNullOrWhiteSpace(description) ? null : description,
            Authors: string.IsNullOrWhiteSpace(authors) ? null : authors,
            IconEntry: NormalizeIconPath(logoFile),
            FullPath: fullPath);

        void AssignMultiline(string? key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            switch (key)
            {
                case "description" when description is null:
                    description = value.Trim();
                    break;
                case "authors" when authors is null:
                    authors = value.Trim();
                    break;
            }
        }

        void FlushDependency()
        {
            if (section != "dep" || string.IsNullOrWhiteSpace(depModId)) return;

            var depKind = depType?.ToLowerInvariant() switch
            {
                "incompatible" => ModDependencyKind.Incompatible,
                "optional" => ModDependencyKind.Optional,
                "embedded" => ModDependencyKind.Embedded,
                "required" => ModDependencyKind.Required,
                _ => depMandatory ? ModDependencyKind.Required : ModDependencyKind.Optional,
            };

            var range = string.IsNullOrWhiteSpace(depRange) ? "*" : depRange!;

            deps.Add(new ModDependency(depModId!, range, depKind));

            if (string.Equals(depModId, "minecraft", StringComparison.OrdinalIgnoreCase))
                declaredGame = range;

            depModId = null;
            depRange = null;
            depType = null;
            depMandatory = true;
        }
    }

    /// <summary>
    /// 定版本号：占位符（<c>${file.jarVersion}</c> 之类）用 MANIFEST 里的真版本替掉。
    /// </summary>
    private static string ResolveVersion(string? version, string? manifestVersion)
    {
        if (string.IsNullOrWhiteSpace(version)) return manifestVersion ?? "?";

        if (!version.Contains("${", StringComparison.Ordinal)) return version;

        return string.IsNullOrWhiteSpace(manifestVersion) ? version : manifestVersion;
    }

    // ── 检查 ────────────────────────────────────────────

    private static List<ModPreflightIssue> Check(
        IReadOnlyList<ModMetadata> mods,
        ModLoaderKind expectedLoader,
        int totalJars,
        Action<string>? log)
    {
        var issues = new List<ModPreflightIssue>();

        // 本地装了哪些 modId（小写索引）
        var installed = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<ModPreflightIssue>();

        foreach (var mod in mods)
        {
            if (installed.TryGetValue(mod.ModId, out var first))
            {
                duplicates.Add(new ModPreflightIssue(
                    PreflightSeverity.Error,
                    $"同一个模组装了两份：{mod.Name}（{mod.ModId}）",
                    $"「{first.FileName}」和「{mod.FileName}」都声明 modId 为 {mod.ModId}。\n" +
                    "加载器会报 duplicate mod 并直接拒绝启动。删掉其中一个（留版本号新的那个）。",
                    mod.FileName));
            }
            else
            {
                installed[mod.ModId] = mod;
            }
        }

        issues.AddRange(duplicates);

        // 加载器不匹配：只在双方都明确时判定
        if (expectedLoader is ModLoaderKind.Fabric or ModLoaderKind.Forge or ModLoaderKind.NeoForge)
        {
            foreach (var mod in mods)
            {
                if (mod.Kind == ModLoaderKind.Vanilla) continue;
                if (mod.Kind == expectedLoader) continue;

                // Forge ↔ NeoForge 互相之间先不报：1.20.1 那段历史包袱太重，
                // 元数据根本分不清，报了大概率是误伤
                if (IsForgeFamily(expectedLoader) && IsForgeFamily(mod.Kind)) continue;

                issues.Add(new ModPreflightIssue(
                    PreflightSeverity.Error,
                    $"模组加载器不对：{mod.Name}",
                    $"「{mod.FileName}」是 {Describe(mod.Kind)} 的模组，但这个版本用的是 {Describe(expectedLoader)}。\n" +
                    $"{Describe(mod.Kind)} 的 jar 在 {Describe(expectedLoader)} 里不会被加载 —— " +
                    "要么换成对应加载器的版本，要么删掉。",
                    mod.FileName));
            }
        }

        // 缺前置
        var missing = new Dictionary<string, (ModMetadata Owner, ModDependency Dep)>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            foreach (var dep in mod.Dependencies)
            {
                if (dep.Kind != ModDependencyKind.Required) continue;
                if (IsBuiltIn(dep.ModId)) continue;
                if (installed.ContainsKey(dep.ModId)) continue;

                // Fabric 的 modId 在历史上有三种写法，任一命中就算装过
                if (FabricApiIds.Contains(dep.ModId) && FabricApiIds.Any(installed.ContainsKey)) continue;

                if (missing.ContainsKey(dep.ModId)) continue;
                missing[dep.ModId] = (mod, dep);
            }
        }

        foreach (var (depId, pair) in missing.OrderBy(m => m.Key))
        {
            issues.Add(new ModPreflightIssue(
                PreflightSeverity.Error,
                $"缺少前置：{depId}",
                $"「{pair.Owner.Name}」（{pair.Owner.FileName}）要求先装 {depId}" +
                (pair.Dep.VersionRange is "*" or "" ? "" : $"（版本 {pair.Dep.VersionRange}）") +
                "。\n缺前置时游戏不会说「少了什么」，只会甩一屏 NoClassDefFoundError。" +
                "去内容页搜这个名字装上即可。",
                pair.Owner.FileName));
        }

        // 版本范围不满足 —— 只提醒，不阻断
        foreach (var mod in mods)
        {
            foreach (var dep in mod.Dependencies)
            {
                if (dep.Kind != ModDependencyKind.Required) continue;
                if (dep.VersionRange is "*" or "") continue;
                if (!installed.TryGetValue(dep.ModId, out var target)) continue;

                if (VersionRange.Matches(dep.VersionRange, target.Version)) continue;

                // 除了 modId 本身，Fabric API 那几种名字也要一起比
                if (FabricApiIds.Contains(dep.ModId) && FabricApiIds.Any(id =>
                        installed.TryGetValue(id, out var alt) && VersionRange.Matches(dep.VersionRange, alt.Version)))
                    continue;

                issues.Add(new ModPreflightIssue(
                    PreflightSeverity.Warning,
                    $"版本可能对不上：{mod.Name} → {dep.ModId}",
                    $"「{mod.FileName}」要 {dep.ModId} 的 {dep.VersionRange}，本地装的是 {target.Version}（{target.FileName}）。\n" +
                    "范围判断只是按文本比大小，模组作者写错范围也常见 —— 能进游戏就不用管。",
                    mod.FileName));
            }
        }

        // 冲突声明：双方都在才报
        foreach (var mod in mods)
        {
            foreach (var dep in mod.Dependencies)
            {
                if (dep.Kind != ModDependencyKind.Incompatible) continue;
                if (!installed.TryGetValue(dep.ModId, out var target)) continue;

                issues.Add(new ModPreflightIssue(
                    PreflightSeverity.Error,
                    $"明确冲突：{mod.Name} 与 {target.Name}",
                    $"「{mod.FileName}」把 {target.Name}（{target.FileName}）列进了不兼容名单。\n" +
                    "两个同时装，轻则功能失效，重则启动即崩。留一个。",
                    mod.FileName));
            }
        }

        if (mods.Count < totalJars)
        {
            var unread = totalJars - mods.Count;
            log?.Invoke($"[预检] {unread} 个 jar 没有可识别的模组元数据（前置库或不是模组，正常）");
        }

        // Error 排前面
        return issues
            .OrderByDescending(i => i.IsError)
            .ThenBy(i => i.Title, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 这个 modId 是不是「由加载器/游戏本体提供」，不当作可缺失的前置。
    /// </summary>
    /// <remarks>
    /// <b>Fabric API 不在这里面</b>：它是真模组，要单独下载安装。
    /// 只是它的 modId 历史上有 <c>fabric</c> / <c>fabric-api</c> / <c>fabric_api</c> 三种写法，
    /// 判「装没装过」时要三个名字都查一遍（见 <see cref="FabricApiIds"/>）。
    /// </remarks>
    private static bool IsBuiltIn(string modId) => BuiltInModIds.Contains(modId);

    private static bool IsForgeFamily(ModLoaderKind kind)
        => kind is ModLoaderKind.Forge or ModLoaderKind.NeoForge;

    private static string Describe(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        ModLoaderKind.Vanilla => "原版",
        _ => kind.ToString(),
    };

    // ── 小工具 ──────────────────────────────────────────

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
        => archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > 4 * 1024 * 1024) return null; // 元数据文件不该这么大

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// 版本范围匹配：同时认 Fabric 的 semver 写法和 Forge 的 Maven 写法。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要自己写</b>：两家语法不一样，标准库都不覆盖。
/// </para>
/// <list type="bullet">
///   <item>Fabric / semver：<c>&gt;=1.20</c>、<c>~1.20.1</c>、<c>^1.20</c>、<c>1.20.x</c>、<c>&gt;=0.5 &lt;0.6</c></item>
///   <item>Forge / Maven：<c>[1.20,1.21)</c>、<c>[1.20.1]</c>、<c>[1.20,)</c></item>
/// </list>
/// <para>
/// <b>判不准时一律返回 true（放行）</b>。预检的价值在于「确定会崩才拦」，
/// 把看不懂的范围当成不满足，会让用户被自己的正常模组挡在门外 ——
/// 那种误报一次就够让人关掉这个功能了。
/// </para>
/// </remarks>
public static class VersionRange
{
    /// <summary>
    /// <paramref name="version"/> 是否落在 <paramref name="range"/> 里。
    /// 范围为空、<c>*</c>、<c>+</c> 都算「不限」。
    /// </summary>
    public static bool Matches(string? range, string? version)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;
        if (string.IsNullOrWhiteSpace(version)) return true;

        range = range.Trim();
        version = version.Trim();

        if (range is "*" or "+" or "x" or "X") return true;

        // Maven 区间写法
        if (range[0] is '[' or '(') return MatchesMaven(range, version);

        return MatchesSemver(range, version);
    }

    // Maven：[1.20,1.21) / [1.20.1] / [1.20,) / (,1.21]
    private static bool MatchesMaven(string range, string version)
    {
        var close = range.IndexOfAny(new[] { ']', ')' });
        if (close < 0) return true; // 写法不成立，放行

        var includeLower = range[0] == '[';
        var includeUpper = range[close] == ']';

        var inner = range[1..close];
        var comma = inner.IndexOf(',');

        try
        {
            if (comma < 0)
            {
                // [1.20.1] = 精确等于
                var exact = inner.Trim();
                return exact.Length > 0 && Compare(version, exact) == 0;
            }

            var lower = inner[..comma].Trim();
            var upper = inner[(comma + 1)..].Trim();

            if (lower.Length > 0)
            {
                var c = Compare(version, lower);
                if (c < 0 || (c == 0 && !includeLower)) return false;
            }

            if (upper.Length > 0)
            {
                var c = Compare(version, upper);
                if (c > 0 || (c == 0 && !includeUpper)) return false;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    // semver：">=1.20 <1.21" / "~1.20.1" / "^1.20" / "1.20.x" / "1.20.1"
    private static bool MatchesSemver(string range, string version)
    {
        // 多个条件用空格分开，全部满足才算
        var parts = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        foreach (var part in parts)
        {
            if (!MatchesOne(part, version)) return false;
        }

        return true;
    }

    private static bool MatchesOne(string cond, string version)
    {
        try
        {
            if (cond is "*" or "") return true;

            // 范围里连一个数字都没有 —— 不是版本范围（作者手滑、或者写的自定义标记），
            // 看不懂就放行。硬判「不满足」会把自己正常的模组挡在门外。
            if (!cond.Any(char.IsDigit)) return true;

            if (cond.StartsWith(">=", StringComparison.Ordinal))
                return Compare(version, cond[2..]) >= 0;

            if (cond.StartsWith("<=", StringComparison.Ordinal))
                return Compare(version, cond[2..]) <= 0;

            if (cond.StartsWith(">", StringComparison.Ordinal))
                return Compare(version, cond[1..]) > 0;

            if (cond.StartsWith("<", StringComparison.Ordinal))
                return Compare(version, cond[1..]) < 0;

            if (cond.StartsWith("=", StringComparison.Ordinal))
                return Compare(version, cond[1..]) == 0;

            // ~1.20.1 → >=1.20.1 <1.21.0（锁次版本）
            if (cond.StartsWith('~'))
            {
                var target = cond[1..].Trim();
                var nums = ParseNumbers(target);
                if (nums.Count < 2) return true;

                var lower = target;
                var upper = nums.Count >= 3
                    ? $"{nums[0]}.{nums[1] + 1}.0"
                    : $"{nums[0]}.{nums[1] + 1}";

                return Compare(version, lower) >= 0 && Compare(version, upper) < 0;
            }

            // ^1.20 → >=1.20 <2.0（锁主版本）
            if (cond.StartsWith('^'))
            {
                var target = cond[1..].Trim();
                var nums = ParseNumbers(target);
                if (nums.Count == 0) return true;

                var upper = $"{nums[0] + 1}.0.0";
                return Compare(version, target) >= 0 && Compare(version, upper) < 0;
            }

            // 1.20.x / 1.20.* → 前缀匹配
            var starAt = cond.IndexOfAny(new[] { 'x', 'X', '*' });
            if (starAt >= 0)
            {
                var prefix = cond[..starAt].TrimEnd('.');
                return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            // 光秃秃的版本号 = 精确匹配（Fabric 里少见，Forge 里偶尔）
            return Compare(version, cond) == 0;
        }
        catch
        {
            return true; // 看不懂就放行，理由见类注释
        }
    }

    /// <summary>
    /// 按数字段比较版本号。返回 &lt;0 / 0 / &gt;0。
    /// </summary>
    /// <remarks>
    /// <b>不能按字符串比</b>：<c>"1.9" &gt; "1.10"</c> 在字符串比较里成立，会得出荒唐结论。
    /// 段数不够的补 0（<c>1.20</c> 等于 <c>1.20.0</c>），非数字段（<c>-beta</c>、<c>+build</c>）截掉。
    /// </remarks>
    public static int Compare(string a, string b)
    {
        var left = ParseNumbers(a);
        var right = ParseNumbers(b);

        var length = Math.Max(left.Count, right.Count);
        for (var i = 0; i < length; i++)
        {
            var l = i < left.Count ? left[i] : 0;
            var r = i < right.Count ? right[i] : 0;

            if (l != r) return l < r ? -1 : 1;
        }

        return 0;
    }

    private static List<int> ParseNumbers(string version)
    {
        var numbers = new List<int>(4);

        // 只看第一段连续的 数字.数字.数字，遇到 - 或 + 就停（那是预发布/构建元数据）
        var stop = version.IndexOfAny(new[] { '-', '+' });
        var core = stop >= 0 ? version[..stop] : version;

        foreach (var piece in core.Split('.'))
        {
            if (int.TryParse(piece, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                numbers.Add(n);
            else
                break; // 遇到非数字段就停，剩下的不算版本主体
        }

        return numbers;
    }
}
