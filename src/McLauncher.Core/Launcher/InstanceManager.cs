using System.Text.Json;

namespace McLauncher.Core.Launcher;

/// <summary>
/// 一个「实例」（一个独立玩法盒子）的信息快照。
/// </summary>
/// <remarks>
/// 只读快照，每次 <see cref="InstanceManager.Scan"/> 重新生成 —— 免得界面拿到的
/// 版本数、库宿主状态是几小时前的旧值。
/// </remarks>
public sealed class InstanceInfo
{
    /// <summary>实例名，同时也是目录名。</summary>
    public required string Name { get; init; }

    /// <summary>实例目录的绝对路径。</summary>
    public required string Directory { get; init; }

    /// <summary>这个实例是不是「共用库宿主」—— <c>libraries</c> / <c>assets</c> / <c>runtime</c> 在它这。</summary>
    public bool IsSharedRepository { get; init; }

    /// <summary>是不是默认实例（<c>default</c>）。只用来排序和兜底。</summary>
    public bool IsDefault { get; init; }

    /// <summary>已安装的版本数量。</summary>
    public int VersionCount { get; init; }

    /// <summary>下拉框里显示的文字。</summary>
    public string Display => IsSharedRepository
        ? $"{Name} · {VersionCount} 个版本 · 共用库"
        : $"{Name} · {VersionCount} 个版本";

    public override string ToString() => Display;
}

/// <summary>
/// 多实例管理：扫描 / 新建 / 重命名 / 删除 / 切换。
/// </summary>
/// <remarks>
/// <para>
/// <b>实例隔离和版本隔离是两回事，这里管的是前者。</b>
/// </para>
/// <list type="table">
/// <item><term>版本隔离</term><description>同一个实例内，每个版本有自己的 <c>mods</c> / <c>saves</c>。</description></item>
/// <item><term>实例隔离</term><description>不同玩法各占一个目录，版本列表、模组、存档全分开。</description></item>
/// </list>
/// <para>
/// <b>库是共用的（用户定的）</b>：<c>libraries</c> / <c>assets</c> / <c>runtime</c> 只有一份，
/// 放在「共用库宿主」实例目录里，其余实例的 <see cref="MinecraftPath"/> 指过去。这样每建一个实例
/// 不用再下 900 MB；代价是删宿主实例会毁掉所有实例，所以 <see cref="Delete"/> 直接拦死。
/// </para>
/// <para>
/// 宿主怎么定：优先 <c>instances\_shared</c>；没有的话退而用已经有 <c>libraries</c> 的老实例
/// （当前就是 <c>default</c>）。<b>故意不做目录搬迁</b> —— 现有 900 MB 的库躺着好好的，
/// 为了目录好看去挪它，收益是零，风险是挪一半失败全家不能玩。
/// </para>
/// <para>
/// 例外：某个实例目录自己就有 <c>libraries</c>（老布局，或者将来做「独立库实例」），
/// 那就用它自己的，不跟别人共享。
/// </para>
/// </remarks>
public sealed class InstanceManager
{
    private const string MetaFileName = "instance.json";
    private const string SharedFolderName = "_shared";
    private const string DefaultInstanceName = "default";

    /// <summary>
    /// 旧版整合包缓存目录名（没有下划线前缀，会被误当成实例）。
    /// </summary>
    /// <remarks>
    /// 现在用 <c>_modpack-cache</c>，下划线开头天然被过滤。这个名字留着只为兼容已有磁盘数据。
    /// </remarks>
    internal const string LegacyModpackCacheName = "modpack-cache";

    private readonly MinecraftLauncherService _core;

    /// <summary>所有实例的父目录（<c>...\instances</c>）。</summary>
    public string InstancesRoot { get; }

    /// <summary>共用库所在的那个目录。</summary>
    public string SharedRepository { get; }

    public InstanceManager(MinecraftLauncherService core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));

        InstancesRoot = Path.GetDirectoryName(core.InstanceRoot.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidOperationException($"实例目录没有父目录，没法做多实例：{core.InstanceRoot}");

        Directory.CreateDirectory(InstancesRoot);
        SharedRepository = ResolveSharedRepository();

        // 恢复到上次用的实例 —— 否则每次开启动器都跳回 default，用户会以为「我建的实例没了」
        RestoreLastInstance();
    }

    /// <summary>当前正在使用的实例目录（= <see cref="MinecraftLauncherService.InstanceRoot"/>）。</summary>
    public string CurrentDirectory => _core.InstanceRoot;

    /// <summary>扫一遍所有实例。默认实例排最前，其余按名字排。</summary>
    public IReadOnlyList<InstanceInfo> Scan()
    {
        var list = new List<InstanceInfo>();

        foreach (var dir in Directory.GetDirectories(InstancesRoot))
        {
            var name = Path.GetFileName(dir);

            // 下划线开头是内部目录（_shared、_modpack-cache），不当作实例
            if (name.StartsWith('_')) continue;

            // 旧版本的整合包缓存目录（没带下划线前缀），也得跳过 ——
            // 不然它会在实例下拉里冒出来一个叫「modpack-cache」的假实例，
            // 用户切过去会看到一个空实例，还以为是自己的存档没了
            if (string.Equals(name, LegacyModpackCacheName, StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(BuildInfo(dir, name));
        }

        // 兜底：一个实例都没有（实例根被指到别处、或者目录刚被删光）
        if (list.Count == 0)
        {
            var name = Path.GetFileName(_core.InstanceRoot.TrimEnd(Path.DirectorySeparatorChar));
            list.Add(BuildInfo(_core.InstanceRoot, name));
        }

        list.Sort((a, b) =>
        {
            if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
        });

        return list;
    }

    /// <summary>新建一个空实例（没有任何版本，版本要自己装）。</summary>
    /// <remarks>
    /// 故意不自动切换 —— 调用方可能只是想建着备用。要切得显式调 <see cref="SwitchTo"/>。
    /// </remarks>
    /// <exception cref="InvalidOperationException">名字不合法或已存在。</exception>
    public InstanceInfo Create(string name)
    {
        var normalized = ValidateName(name);
        var dir = Path.Combine(InstancesRoot, normalized);

        if (Directory.Exists(dir))
            throw new InvalidOperationException($"实例「{normalized}」已经存在了，换个名字。");

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "versions"));
        WriteMeta(dir, new InstanceMeta { CreatedAt = DateTimeOffset.Now });

        return BuildInfo(dir, normalized);
    }

    /// <summary>改实例名（就是改目录名）。</summary>
    /// <exception cref="InvalidOperationException">名字不合法、已存在，或这是共用库宿主。</exception>
    public InstanceInfo Rename(InstanceInfo instance, string newName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var normalized = ValidateName(newName);
        if (string.Equals(normalized, instance.Name, StringComparison.Ordinal))
            return instance;

        if (instance.IsSharedRepository)
        {
            throw new InvalidOperationException(
                $"实例「{instance.Name}」是共用库宿主（libraries / assets / runtime 都在它下面）。" +
                "改它的名字会让所有实例找不到库，所以这里不给改。");
        }

        var target = Path.Combine(InstancesRoot, normalized);
        if (Directory.Exists(target))
            throw new InvalidOperationException($"实例「{normalized}」已经存在了，换个名字。");

        Directory.Move(instance.Directory, target);
        return BuildInfo(target, normalized);
    }

    /// <summary>彻底删除一个实例（版本、模组、存档、配置全删）。</summary>
    /// <exception cref="InvalidOperationException">这是共用库宿主，删了别的实例全废。</exception>
    /// <exception cref="IOException">目录被占用（游戏正在跑）。</exception>
    public void Delete(InstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.IsSharedRepository)
        {
            throw new InvalidOperationException(
                $"实例「{instance.Name}」是共用库宿主，libraries / assets / runtime 都在它下面。" +
                "删掉它，其他所有实例都会因为找不到库而启动失败，所以不允许删。" +
                Environment.NewLine + Environment.NewLine +
                "确实要删，得先把库搬走或改成独立库，这个操作现在没做。");
        }

        if (Directory.Exists(instance.Directory))
            DeleteDirectoryResilient(instance.Directory, instance.Name);
    }

    /// <summary>
    /// 删实例目录，碰上「文件被占用」自己想办法绕过去。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是踩出来的：装完一个整合包马上删那个实例，<c>Directory.Delete</c> 会抛
    /// <c>IOException: 文件正被另一个进程使用</c> —— 而占用者就是<b>本进程自己</b>：
    /// CmlLib 的 launcher 不实现 <c>IDisposable</c>，它打开的
    /// <c>versions\version_manifest_v2.json</c> 要等 GC 才关。
    /// </para>
    /// <para>
    /// 所以顺序是：先直接删（正常情况下一步就好）→ 失败就让核心放掉句柄再试 →
    /// 还不成说明真有外部进程（游戏没关、杀毒软件在扫），这时给一条能照着做的人话，
    /// 而不是甩一个裸 <c>IOException</c>。
    /// </para>
    /// </remarks>
    private void DeleteDirectoryResilient(string directory, string instanceName)
    {
        const int maxAttempts = 4;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return; // 已经不在了，当成功
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;

                if (attempt == maxAttempts) break;

                // 第二次及以后：先让核心松手再重试（第一次失败可能只是刚下完还没刷盘）
                _core.ReleaseFileHandles();
                Thread.Sleep(120 * attempt);
            }
        }

        throw new InvalidOperationException(
            $"实例「{instanceName}」的目录删不掉，有文件正被占用："
            + Environment.NewLine + directory
            + Environment.NewLine + Environment.NewLine
            + $"系统消息：{lastError?.Message}"
            + Environment.NewLine + Environment.NewLine
            + "常见原因（按概率排）："
            + Environment.NewLine
            + "· 这个实例的游戏还开着 —— 关掉游戏窗口再删"
            + Environment.NewLine
            + "· 刚下载 / 校验完，文件句柄还没释放 —— 等几秒再点一次删除"
            + Environment.NewLine
            + "· 杀毒软件正在扫这个目录 —— 加个白名单或者稍后再试",
            lastError);
    }

    /// <summary>切换到某个实例：换 <see cref="MinecraftPath"/> 并记住选择。</summary>
    public void SwitchTo(InstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _core.SwitchInstance(instance.Directory, ResolveLibraryRoot(instance.Directory));
        SaveCurrent(instance.Name);
    }

    /// <summary>
    /// 某个实例的库根：自己有<b>真的库文件</b>才用自己的，否则用共用库。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 判断条件必须是「目录里有东西」，不能只判断目录存在。踩过的坑：
    /// <c>MinecraftLauncherService</c> 构造时 <c>LibraryRoot</c> 默认等于实例根，
    /// 而 <c>GamePath.CreateDirs()</c> 会顺手建出空的 <c>libraries/assets/runtime</c>。
    /// 于是这个实例被打上「自带库」的标签，之后启动时 Java 去
    /// <c>&lt;实例&gt;\runtime\windows-x64\java-runtime-gamma\bin\javaw.exe</c> 找，
    /// 报「系统找不到指定的文件」，同时所有依赖也全缺。
    /// </para>
    /// <para>
    /// 这个错误会自我强化：一旦误判成自带库，程序就更不会往共用库里装东西，
    /// 那个空目录永远空着，误差永远在。
    /// </para>
    /// </remarks>
    public string ResolveLibraryRoot(string instanceDirectory)
    {
        var own = Path.Combine(instanceDirectory, "libraries");

        if (!string.Equals(
                Path.GetFullPath(instanceDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(SharedRepository).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(own) &&
            Directory.EnumerateFileSystemEntries(own).Any())
        {
            return instanceDirectory;
        }

        return SharedRepository;
    }

    /// <summary>这个实例会不会跟着删掉模组和存档（给确认框用的数字）。</summary>
    public InstanceRemovalPreview PreviewRemoval(InstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var versionsDir = Path.Combine(instance.Directory, "versions");
        var versionNames = Directory.Exists(versionsDir)
            ? Directory.GetDirectories(versionsDir).Select(Path.GetFileName).OfType<string>().ToArray()
            : Array.Empty<string>();

        var modCount = 0;
        var saveCount = 0;

        foreach (var version in versionNames)
        {
            var mods = Path.Combine(versionsDir, version, "mods");
            if (Directory.Exists(mods))
                modCount += Directory.GetFiles(mods, "*.jar").Length;

            var saves = Path.Combine(versionsDir, version, "saves");
            if (Directory.Exists(saves))
                saveCount += Directory.GetDirectories(saves).Length;
        }

        return new InstanceRemovalPreview(
            instance.Name,
            instance.Directory,
            versionNames,
            modCount,
            saveCount,
            IsSharedRepository: instance.IsSharedRepository,
            IsCurrent: string.Equals(instance.Directory, _core.InstanceRoot, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── 内部 ─────────────────────────

    private InstanceInfo BuildInfo(string dir, string name) => new()
    {
        Name = name,
        Directory = dir,
        IsSharedRepository = string.Equals(dir, SharedRepository, StringComparison.OrdinalIgnoreCase),
        IsDefault = string.Equals(name, DefaultInstanceName, StringComparison.OrdinalIgnoreCase),
        VersionCount = CountVersions(dir),
    };

    private static int CountVersions(string dir)
    {
        var versions = Path.Combine(dir, "versions");
        return Directory.Exists(versions) ? Directory.GetDirectories(versions).Length : 0;
    }

    private string ResolveSharedRepository()
    {
        var shared = Path.Combine(InstancesRoot, SharedFolderName);
        if (Directory.Exists(shared)) return shared;

        // 老布局：已经有一个实例自带 libraries（现在的 default），直接让它当宿主，不搬目录
        var preferred = Path.Combine(InstancesRoot, DefaultInstanceName);
        if (Directory.Exists(Path.Combine(preferred, "libraries"))) return preferred;

        foreach (var dir in Directory.GetDirectories(InstancesRoot))
        {
            if (Directory.Exists(Path.Combine(dir, "libraries"))) return dir;
        }

        // 全新机器：建一个空的 _shared，以后所有实例都往这放库
        Directory.CreateDirectory(shared);
        return shared;
    }

    /// <summary>
    /// 检查名字合不合法（给输入框实时校验用）。
    /// </summary>
    /// <returns>合法返回 <c>null</c>，否则返回一句能直接显示给用户的错误。</returns>
    public static string? CheckName(string? name)
    {
        var normalized = (name ?? string.Empty).Trim();

        if (normalized.Length == 0)
            return "实例名不能为空。";

        if (normalized.Length > 40)
            return "实例名太长了，40 字以内。";

        var invalid = Path.GetInvalidFileNameChars();
        if (normalized.Any(c => invalid.Contains(c)))
            return @"实例名里不能有这些字符：\ / : * ? "" < > |";

        if (normalized.StartsWith('_'))
            return "实例名不能以下划线开头（下划线留给内部目录）。";

        if (string.Equals(normalized, SharedFolderName, StringComparison.OrdinalIgnoreCase))
            return "这个名字是内部保留的，换一个。";

        return null;
    }

    /// <summary>
    /// 名字校验：不合法就直接抛，别让用户建出一个 Windows 打不开的目录。
    /// </summary>
    private static string ValidateName(string name)
    {
        var error = CheckName(name);
        if (error is not null)
            throw new InvalidOperationException(error);

        return (name ?? string.Empty).Trim();
    }

    private void WriteMeta(string dir, InstanceMeta meta)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(dir, MetaFileName),
                JsonSerializer.Serialize(meta, JsonOptions));
        }
        catch (Exception ex)
        {
            // 元数据写不进去不影响玩，只是少了创建时间
            System.Diagnostics.Debug.WriteLine($"[InstanceManager] 写 {MetaFileName} 失败：{ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void SaveCurrent(string instanceName)
    {
        // 走公共的 LauncherSettings：同一个文件里还有「下载源」那一项，
        // 各写各的模型会把对方字段抹掉（曾经就是两个同名的私有类在抢同一个文件）
        var settings = LauncherSettings.LoadAt(InstancesRoot);
        settings.CurrentInstance = instanceName;

        if (!LauncherSettings.SaveAt(InstancesRoot, settings))
            System.Diagnostics.Debug.WriteLine("[InstanceManager] 保存当前实例失败：写入被拒");
    }

    private void RestoreLastInstance()
    {
        try
        {
            var settings = LauncherSettings.LoadAt(InstancesRoot);
            if (string.IsNullOrWhiteSpace(settings.CurrentInstance)) return;

            var dir = Path.Combine(InstancesRoot, settings.CurrentInstance);
            if (!Directory.Exists(dir)) return;   // 上次那个被删了，安静回落到默认

            _core.SwitchInstance(dir, ResolveLibraryRoot(dir));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InstanceManager] 恢复上次实例失败：{ex.Message}");
        }
    }

    private sealed class InstanceMeta
    {
        public DateTimeOffset CreatedAt { get; set; }
    }
}

/// <summary>删实例之前给用户看的信息。</summary>
public sealed record InstanceRemovalPreview(
    string Name,
    string Directory,
    IReadOnlyList<string> Versions,
    int ModCount,
    int SaveCount,
    bool IsSharedRepository,
    bool IsCurrent);
