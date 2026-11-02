using System.Text.Json;
using McLauncher.Core.Modrinth;

namespace McLauncher.Core.Launcher;

/// <summary>
/// 全局设置（放在实例集合目录 <c>instances\launcher-settings.json</c>，所有实例共用一份）。
/// </summary>
/// <remarks>
/// <para>
/// 两个使用者：<see cref="InstanceManager"/> 存「上次用的实例」，这里存「下载源」。
/// <b>必须是同一个类、同一个文件</b> —— 各自用自己的模型去写，会把对方的字段覆盖掉
/// （改下载源会丢掉"上次用哪个实例"，反之亦然）。
/// </para>
/// <para>
/// 放集合目录而不是每个实例一份：这两件事都跟网络/程序有关，跟玩哪个实例没关系。
/// 在 A 实例里连不上官方源，换到 B 实例也一样连不上。
/// </para>
/// </remarks>
public sealed class LauncherSettings
{
    /// <summary>设置文件名（在实例集合目录下）。</summary>
    public const string FileName = "launcher-settings.json";

    /// <summary>上次用的实例名（由 <see cref="InstanceManager"/> 维护）。</summary>
    public string? CurrentInstance { get; set; }

    /// <summary>
    /// 是否优先用 BMCLAPI 镜像。
    /// </summary>
    /// <remarks>
    /// <c>null</c> = 没选过，跟随 <see cref="MirrorConfigurator.PreferMirror"/>（官方源优先）。
    /// 用可空而不是 bool，是为了区分「用户明确选了官方源」和「用户没选过」——
    /// 否则以后改默认值时，老用户的旧设置会把它顶回去。
    /// </remarks>
    public bool? PreferMirror { get; set; }

    /// <summary>下载线程数（并发）。<c>null</c> = 没调过，用默认 32。</summary>
    public int? DownloadConcurrency { get; set; }

    /// <summary>
    /// 模组搜索源（Modrinth）的标识，取 <see cref="ModrinthSourceChoice"/> 的 Key。
    /// <c>null</c> = 没选过，默认官方源。
    /// </summary>
    public string? ModrinthSource { get; set; }

    /// <summary>分配给游戏的堆大小（MB）。<c>null</c> = 没调过，用默认 4096。</summary>
    public int? MemoryMb { get; set; }

    /// <summary>离线模式下的玩家名。<c>null</c> = 没设过，用 Player。</summary>
    public string? Username { get; set; }

    /// <summary>
    /// 启动前是否自动把游戏语言设成简体中文。
    /// <c>null</c> = 没选过，按默认「开」处理（老用户升级上来行为不变）。
    /// </summary>
    public bool? AutoChineseLanguage { get; set; }

    /// <summary>启动前是否强制全屏。<c>null</c> / <c>false</c> = 不干预。</summary>
    public bool? ForceFullscreen { get; set; }

    /// <summary>
    /// 版本隔离：每个版本用自己的 <c>mods</c> / <c>saves</c>，而不是整个实例共用一个。
    /// <c>null</c> = 没选过，按默认「开」处理。
    /// </summary>
    public bool? IsolateVersionGameDir { get; set; }

    /// <summary>
    /// 手动指定的 <c>javaw.exe</c> 路径。<c>null</c> / 空 = 交给 CmlLib 按版本自动挑。
    /// </summary>
    /// <remarks>
    /// 存绝对路径而不是索引：Java 列表每次扫描都可能变（多装一个、删一个），
    /// 存索引下次就指到别人身上了，而用户完全看不出来。
    /// 路径失效时（文件被删了）<see cref="ResolveJavaPath"/> 会返回 <c>null</c>，
    /// 自动回到「让 CmlLib 挑」，不会因为一个坏路径启动不了游戏。
    /// </remarks>
    public string? JavaPath { get; set; }

    /// <summary>
    /// GC 策略，对应 <see cref="Launcher.GcMode"/> 的整数值。<c>null</c> = 没选过，按自动。
    /// </summary>
    /// <remarks>
    /// 存 int 不存枚举：设置文件是手写可改的，存 int 至少不会因为以后枚举重命名让老配置失效。
    /// </remarks>
    public int? GcMode { get; set; }

    /// <summary>
    /// 玩家自己写的 JVM 参数，原样追加在末尾。空 = 没有。
    /// </summary>
    /// <remarks>
    /// 按字符串存而不是拆成数组：用户改起来直观，而且参数格式（<c>-Dfoo=bar</c>、
    /// <c>-XX:+Flag</c>、带引号的路径）五花八门，先存原文、启动时再解析最安全 ——
    /// 拆开存等于替用户做了一次可能出错的解析。
    /// </remarks>
    public string? CustomJvmArgs { get; set; }

    /// <summary>
    /// DeepSeek 的 API Key，模组汉化用。空 = 没配，汉化功能不可用。
    /// </summary>
    /// <remarks>
    /// 明文存在本地 settings 里。这是台自己的机器、Key 也是自己填的，
    /// 不做加密 —— 加了密反而让人以为它安全，实际密钥就在同目录。
    /// </remarks>
    public string? DeepSeekApiKey { get; set; }

    /// <summary>
    /// DeepSeek 模型名。<c>null</c> / 空 = 用代码里的默认值。
    /// </summary>
    /// <remarks>
    /// 做成可配置是硬要求。官方改过至少两次名（<c>deepseek-chat</c> → <c>deepseek-flash</c>，
    /// 以及 <c>deepseek-v4-flash</c> 退役），写死的话改一次名全功能静默失效，
    /// 而报出来的是 <c>model not found</c>，看着跟网络故障一模一样。
    /// </remarks>
    public string? AiModel { get; set; }

    /// <summary>模组汉化总开关。默认关着 —— 这是个会长 API 账单的功能，得用户自己打开。</summary>
    public bool? AiLocalizationEnabled { get; set; }

    /// <summary>
    /// 启动前是否做模组体检（缺前置 / 重复 / 加载器放错）。
    /// <c>null</c> = 没选过，默认开。
    /// </summary>
    /// <remarks>
    /// 默认开：这三类问题一旦发生，游戏给的报错基本看不懂，用户只能干瞪眼。
    /// 但一定要留开关 —— 有人就是不喜欢每次启动弹东西。
    /// </remarks>
    public bool? ModPreflightEnabled { get; set; }

    /// <summary>
    /// 模组管理列表里要不要联网查中文名（modId → 「钠」「苹果皮」）。
    /// <c>null</c> = 没选过，默认开。
    /// </summary>
    /// <remarks>
    /// 默认开：PCL 有中文名，用户会期待。查过一次就永远缓存，
    /// 代价是第一次打开模组管理窗口会慢十几秒（列表本身是秒出的，名字陆续补）。
    /// 留开关是因为这确实要往 MC百科发请求 —— 有人会在意。
    /// </remarks>
    public bool? LookupModChineseNames { get; set; }

    /// <summary>
    /// 微软登录用的 Azure 应用 client id（GUID）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须由用户自己填，不能内置。</b>一是不能拿别人的应用去登录；
    /// 二是就算拿了别人的，Mojang 那边也会按应用做白名单，照样 403。
    /// </para>
    /// <para>
    /// 申请是免费的：Azure 门户 → 应用注册 → 新注册 → 支持账户类型选「个人 Microsoft 账户」
    /// → 认证页打开「允许公共客户端流」。不用建客户端密码，也不用填重定向 URI
    /// （设备码流程两样都不要）。
    /// </para>
    /// </remarks>
    public string? MicrosoftClientId { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 设置文件所在目录（<c>launcher\</c>）。
    /// </summary>
    /// <remarks>
    /// 以前设置跟着实例走（写在 <c>instances\launcher-settings.json</c>），现在统一挪到
    /// <see cref="LauncherPaths.DataRoot"/>：设置本来就是全局的，没有「每个实例一套设置」的说法，
    /// 放在 <c>instances</c> 里只会让游戏目录不干净。
    /// <para>
    /// 参数保留是为了不动几十个调用点，<b>实际已不参与路径计算</b>。
    /// </para>
    /// </remarks>
    public static string InstancesRootFor(string instanceRoot) => LauncherPaths.DataRoot;

    /// <summary>设置文件完整路径（参数已不参与计算，见 <see cref="InstancesRootFor"/>）。</summary>
    public static string PathAt(string instancesRoot) => LauncherPaths.SettingsFile;

    /// <summary>设置文件完整路径（参数是某个实例目录）。</summary>
    public static string PathFor(string instanceRoot) => LauncherPaths.SettingsFile;

    /// <summary>读设置。文件不存在、坏了、没权限 —— 一律返回默认值，不抛。</summary>
    /// <remarks>
    /// 设置读失败绝不能让启动器起不来。最坏情况就是「按默认的官方源、默认实例启动」，
    /// 用户还能进界面自己改回来。
    /// </remarks>
    public static LauncherSettings LoadAt(string instancesRoot)
    {
        try
        {
            var path = PathAt(instancesRoot);
            if (!File.Exists(path)) return new LauncherSettings();

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new LauncherSettings();

            return JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions)
                   ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    /// <summary>写设置。先写临时文件再改名，避免写一半断电留下半截 JSON。</summary>
    /// <returns>是否写成功。</returns>
    public static bool SaveAt(string instancesRoot, LauncherSettings settings)
    {
        try
        {
            Directory.CreateDirectory(instancesRoot);

            var path = PathAt(instancesRoot);
            var temp = path + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 只改「下载源」这一项，其他字段原样保留。
    /// </summary>
    /// <remarks>
    /// <b>别改成「new 一个再 Save」</b> —— 那样会把 <see cref="CurrentInstance"/> 抹成 null，
    /// 下次启动就回到 default 实例，用户会以为"我选的实例又没了"。
    /// </remarks>
    public static bool SavePreferMirror(string instanceRoot, bool useMirror)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.PreferMirror = useMirror;
        return SaveAt(root, settings);
    }

    /// <summary>解析「该用哪个源」：设置里选过就用选的，没选过就用编译期默认。</summary>
    public static bool ResolveUseMirror(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).PreferMirror ?? MirrorConfigurator.PreferMirror;

    /// <summary>只改「模组搜索源」这一项，其他字段原样保留。</summary>
    public static bool SaveModrinthSource(string instanceRoot, string? sourceKey)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.ModrinthSource = sourceKey;
        return SaveAt(root, settings);
    }

    /// <summary>解析「模组搜索源」：没选过或认不出来都回官方源。</summary>
    public static ModrinthSourceChoice ResolveModrinthSource(string instanceRoot)
    {
        var key = LoadAt(InstancesRootFor(instanceRoot)).ModrinthSource;
        return ModrinthSourceChoice.For(key);
    }

    /// <summary>只改「下载线程数」，其他字段原样保留。</summary>
    public static bool SaveConcurrency(string instanceRoot, int downloaders)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.DownloadConcurrency = downloaders;
        return SaveAt(root, settings);
    }

    /// <summary>只改「内存」和「用户名」，其他字段原样保留（这两项一起动，一次写盘就够）。</summary>
    /// <remarks>
    /// 同样别 new 一个新的再存：那样会把 <see cref="CurrentInstance"/> 和下载源一起抹掉。
    /// </remarks>
    public static bool SaveMemoryAndUser(string instanceRoot, int memoryMb, string? username)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.MemoryMb = memoryMb;
        settings.Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        return SaveAt(root, settings);
    }

    /// <summary>只改「自动中文」这一项，其他字段原样保留。</summary>
    public static bool SaveAutoChinese(string instanceRoot, bool enabled)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.AutoChineseLanguage = enabled;
        return SaveAt(root, settings);
    }

    /// <summary>该不该在启动前把游戏语言设成简体中文：没选过按「开」。</summary>
    public static bool ResolveAutoChinese(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).AutoChineseLanguage ?? true;

    /// <summary>该不该在启动前强制全屏：只有明确打开过才算。</summary>
    public static bool ResolveForceFullscreen(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).ForceFullscreen is true;

    /// <summary>只改「强制全屏」这一项。</summary>
    public static bool SaveForceFullscreen(string instanceRoot, bool enabled)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.ForceFullscreen = enabled;
        return SaveAt(root, settings);
    }

    /// <summary>版本隔离：没选过按「开」。</summary>
    public static bool ResolveIsolateVersionGameDir(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).IsolateVersionGameDir ?? true;

    /// <summary>只改「版本隔离」这一项。</summary>
    public static bool SaveIsolateVersionGameDir(string instanceRoot, bool enabled)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.IsolateVersionGameDir = enabled;
        return SaveAt(root, settings);
    }

    /// <summary>存 DeepSeek 的 API Key。传空 = 清掉。</summary>
    public static bool SaveDeepSeekApiKey(string instanceRoot, string? apiKey)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.DeepSeekApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        return SaveAt(root, settings);
    }

    /// <summary>读出 API Key，没配返回 <c>null</c>。</summary>
    public static string? ResolveDeepSeekApiKey(string instanceRoot)
    {
        var key = LoadAt(InstancesRootFor(instanceRoot)).DeepSeekApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>
    /// 该用哪个模型名：设置里填过就用填的，没填返回 <c>null</c>（调用方用代码默认值）。
    /// </summary>
    public static string? ResolveAiModel(string instanceRoot)
    {
        var model = LoadAt(InstancesRootFor(instanceRoot)).AiModel;
        return string.IsNullOrWhiteSpace(model) ? null : model.Trim();
    }

    /// <summary>只改「模型名」这一项。传空 = 恢复默认。</summary>
    public static bool SaveAiModel(string instanceRoot, string? model)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.AiModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        return SaveAt(root, settings);
    }

    /// <summary>模组汉化是否开着：默认关。</summary>
    public static bool ResolveAiLocalization(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).AiLocalizationEnabled is true;

    /// <summary>分配的内存（MB）。没设过返回 0，交给调用方当「未知」处理。</summary>
    public static int ResolveMemoryMb(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).MemoryMb ?? 0;

    /// <summary>只改「模组汉化」这一项。</summary>
    public static bool SaveAiLocalization(string instanceRoot, bool enabled)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.AiLocalizationEnabled = enabled;
        return SaveAt(root, settings);
    }

    /// <summary>启动前模组体检是否开着：没选过按「开」。</summary>
    public static bool ResolveModPreflight(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).ModPreflightEnabled ?? true;

    /// <summary>只改「启动前模组体检」这一项。</summary>
    public static bool SaveModPreflight(string instanceRoot, bool enabled)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.ModPreflightEnabled = enabled;
        return SaveAt(root, settings);
    }

    /// <summary>模组中文名要不要联网查：没选过按「要」。</summary>
    public static bool ResolveLookupModChineseNames(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).LookupModChineseNames ?? true;

    /// <summary>只改「查模组中文名」这一项。</summary>
    public static bool SaveLookupModChineseNames(string instanceRoot, bool enabled)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.LookupModChineseNames = enabled;
        return SaveAt(root, settings);
    }

    /// <summary>
    /// 读出微软登录的 client id。没填就用默认的那个 MSA 应用 id。
    /// </summary>
    /// <remarks>
    /// 这里不再返回 <c>null</c>：v1 授权流程有一个实测可用的公共 id，
    /// 用户什么都不填也能登录。想换的话在设置里覆盖，覆盖值优先。
    /// </remarks>
    public static string ResolveMicrosoftClientId(string instanceRoot)
    {
        var id = LoadAt(InstancesRootFor(instanceRoot)).MicrosoftClientId;
        return string.IsNullOrWhiteSpace(id)
            ? McLauncher.Core.Auth.MicrosoftAuthService.DefaultClientId
            : id.Trim();
    }

    /// <summary>只改「微软 client id」这一项。</summary>
    public static bool SaveMicrosoftClientId(string instanceRoot, string? clientId)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);

        settings.MicrosoftClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();

        return SaveAt(root, settings);
    }

    /// <summary>指定用哪个 Java。<c>null</c> / 空 = 恢复「自动挑」。</summary>
    public static bool SaveJavaPath(string instanceRoot, string? javaPath)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.JavaPath = string.IsNullOrWhiteSpace(javaPath) ? null : javaPath.Trim();
        return SaveAt(root, settings);
    }

    /// <summary>
    /// 该用哪个 Java：设置里指定的那个（文件还在才算数），否则 <c>null</c> 交给 CmlLib 挑。
    /// </summary>
    /// <remarks>
    /// <b>文件不存在时必须返回 null，不能硬返回那个路径</b> —— 用户手动选的 Java 被卸载、
    /// 或者整个 JDK 目录被移走之后，硬用它会让游戏「点了没反应」：进程起不来，
    /// 而界面上什么错误都没有。回退到自动挑，至少还能玩。
    /// </remarks>
    public static string? ResolveJavaPath(string instanceRoot)
    {
        var configured = LoadAt(InstancesRootFor(instanceRoot)).JavaPath;

        if (string.IsNullOrWhiteSpace(configured)) return null;

        try
        {
            return File.Exists(configured) ? configured : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GC 策略：没选过按自动。</summary>
    /// <remarks>
    /// 属性 <see cref="GcMode"/> 和枚举类型同名，类内部写 <c>GcMode</c> 会先解析成属性，
    /// 所以这里用完全限定名。
    /// </remarks>
    public static Launcher.GcMode ResolveGcMode(string instanceRoot)
    {
        var raw = LoadAt(InstancesRootFor(instanceRoot)).GcMode;

        // 认不出来的值（手改的、旧版本写的）一律退回自动，不让它把启动搞挂
        return raw is int value && Enum.IsDefined(typeof(Launcher.GcMode), value)
            ? (Launcher.GcMode)value
            : Launcher.GcMode.Auto;
    }

    /// <summary>只改「GC 策略」这一项。</summary>
    public static bool SaveGcMode(string instanceRoot, Launcher.GcMode mode)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.GcMode = (int)mode;
        return SaveAt(root, settings);
    }

    /// <summary>玩家自定义的 JVM 参数（空串 = 没有）。</summary>
    public static string ResolveCustomJvmArgs(string instanceRoot) =>
        LoadAt(InstancesRootFor(instanceRoot)).CustomJvmArgs?.Trim() ?? string.Empty;

    /// <summary>只改「自定义 JVM 参数」这一项。</summary>
    public static bool SaveCustomJvmArgs(string instanceRoot, string? args)
    {
        var root = InstancesRootFor(instanceRoot);
        var settings = LoadAt(root);
        settings.CustomJvmArgs = string.IsNullOrWhiteSpace(args) ? null : args.Trim();
        return SaveAt(root, settings);
    }

    /// <summary>
    /// 把设置里的调优项应用到 <see cref="MirrorConfigurator"/>（启动时调一次）。
    /// </summary>
    /// <remarks>
    /// 必须在第一个 <see cref="MinecraftLauncherService"/> 被构造<b>之前</b>调用，
    /// 否则那个下载器已经按默认并发建好了。
    /// </remarks>
    public static void ApplyTuning(string instanceRoot)
    {
        var settings = LoadAt(InstancesRootFor(instanceRoot));

        if (settings.DownloadConcurrency is int concurrency && concurrency > 0)
            MirrorConfigurator.ApplyConcurrency(concurrency);
    }
}

/// <summary>下载源的两种取值（给界面下拉用）。</summary>
public sealed record SourceChoice(bool UseMirror, string Display, string Note)
{
    public static readonly SourceChoice Official = new(
        false,
        "官方源（Mojang 直连）",
        "直连 Mojang。正常情况下最快，但国内线路不稳时会连不上");

    public static readonly SourceChoice Mirror = new(
        true,
        "BMCLAPI 镜像",
        "国内镜像站。官方源下不动时用它，版本更新可能晚几小时");

    public static IReadOnlyList<SourceChoice> All { get; } = new[] { Official, Mirror };

    public static SourceChoice For(bool useMirror) => useMirror ? Mirror : Official;
}

/// <summary>模组搜索源（Modrinth）的可选值，给界面下拉用。</summary>
public sealed record ModrinthSourceChoice(string Key, string Display, string Note, string BaseUrl)
{
    public static readonly ModrinthSourceChoice Official = new(
        "official",
        "官方源（Modrinth）",
        "直连 Modrinth 官方 API。数据最新最全，但走 Cloudflare，国内线路可能时快时慢",
        ModrinthClient.OfficialBaseUrl);

    public static readonly ModrinthSourceChoice Mirror = new(
        "mcimirror",
        "MCIM 镜像",
        "国内公益镜像，API 结构完全兼容。文件下载会跳回源站，维护方可能更新不及时",
        ModrinthClient.MirrorBaseUrl);

    public static IReadOnlyList<ModrinthSourceChoice> All { get; } = new[] { Official, Mirror };

    /// <summary>短名，给测速结果这种一行要放两项的地方用。</summary>
    public string Short => Key switch
    {
        "official" => "官方",
        "mcimirror" => "MCIM",
        _ => Display,
    };

    /// <summary>按 Key 找，认不出来回官方源。</summary>
    public static ModrinthSourceChoice For(string? key) =>
        All.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Official;
}
