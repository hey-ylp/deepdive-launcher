namespace DeepDive.Core.Launcher;

/// <summary>
/// 路径约定：启动器把自己、实例、自己的数据三者的相对关系固定下来。
/// </summary>
/// <remarks>
/// <para>
/// <b>目录结构（照 PCL 的思路，游戏数据和启动器数据分开）</b>：
/// </para>
/// <code>
/// DeepDive.exe
/// instances\          游戏数据：每个实例一套 versions / mods / saves / libraries / assets / runtime
/// launcher\           启动器自己的：设置、日志、崩溃日志、各种缓存、临时目录
/// </code>
/// <para>
/// 分开的好处：想备份存档只拷 <c>instances</c>；启动器出问题删掉 <c>launcher</c> 就等于恢复出厂，
/// 游戏和存档一点不受影响；发存档给别人时也不会把自己的日志和浏览器缓存一起打包出去。
/// </para>
/// <para>
/// <b>单文件发布时不能用 <c>AppContext.BaseDirectory</c></b>：那时候它指向
/// <c>%TEMP%\.net\xxx\</c> 那个解压目录，所有相对路径都会落进临时文件夹，关掉就没了。
/// 必须用 <see cref="Environment.ProcessPath"/>（指向真正的 exe）。
/// </para>
/// </remarks>
public static class LauncherPaths
{
    /// <summary>环境变量名：直接覆盖实例根目录（排障、测试用）。</summary>
    public const string InstanceRootEnvVar = "DEEPDIVE_INSTANCE";

    /// <summary>默认实例名。全新安装时用它。</summary>
    public const string DefaultInstanceName = "default";

    /// <summary>实例集合的目录名。</summary>
    private const string InstancesFolderName = "instances";

    /// <summary>启动器自身数据的目录名。</summary>
    private const string DataFolderName = "launcher";

    private const string SettingsFileName = "launcher-settings.json";
    private const string TranslationCacheFileName = "mod-translation-cache.json";

    private const string ModChineseNameCacheFileName = "mod-chinese-names.json";
    private const string ContentTranslationCacheFileName = "translation-cache.json";
    private const string LogFileName = "launcher.log";
    private const string CrashLogFileName = "launcher-crash.log";

    /// <summary>
    /// 启动器 exe 所在目录。<b>单文件发布时也是准的</b>。
    /// </summary>
    public static string AppDirectory
    {
        get
        {
            var exe = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }

            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    /// <summary>
    /// 根目录：<c>instances</c> 和 <c>launcher</c> 都放在这一层。
    /// </summary>
    /// <remarks>
    /// 从 exe 所在目录起往上找，最多 4 层，取第一个<b>已经有 <c>instances</c> 或 <c>launcher</c></b>
    /// 的目录。这样开发时 exe 在 <c>run\</c> 里，数据仍然落在
    /// <c>D:\deepseek\mc-launcher\</c> 下，和之前一致；全新拷到别处就是便携模式，
    /// 两个目录都建在 exe 旁边。
    /// </remarks>
    public static string BaseRoot
    {
        get
        {
            var dir = AppDirectory;

            for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
            {
                if (Directory.Exists(Path.Combine(dir, InstancesFolderName)) ||
                    Directory.Exists(Path.Combine(dir, DataFolderName)))
                {
                    return dir;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return AppDirectory;
        }
    }

    /// <summary><c>instances</c> 目录（多个实例的父目录）。</summary>
    public static string InstancesRoot => Path.Combine(BaseRoot, InstancesFolderName);

    /// <summary><c>launcher</c> 目录（启动器自己的数据，删掉等于恢复出厂）。</summary>
    public static string DataRoot => Path.Combine(BaseRoot, DataFolderName);

    /// <summary>
    /// 当前实例根目录（即 <c>.minecraft</c> 本体所在目录）。
    /// </summary>
    /// <remarks>
    /// 优先级：环境变量 <c>DEEPDIVE_INSTANCE</c> → <see cref="InstancesRoot"/> 下的默认实例。
    /// </remarks>
    public static string InstanceRoot
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable(InstanceRootEnvVar);

            return string.IsNullOrWhiteSpace(fromEnv)
                ? Path.Combine(InstancesRoot, DefaultInstanceName)
                : fromEnv.Trim();
        }
    }

    // ─────────────── 启动器数据的固定位置 ───────────────

    /// <summary>设置文件。</summary>
    public static string SettingsFile => Path.Combine(DataRoot, SettingsFileName);

    /// <summary>翻译缓存（模组汉化用，翻过的不再花钱）。</summary>
    public static string TranslationCacheFile => Path.Combine(DataRoot, TranslationCacheFileName);

    /// <summary>模组中文名缓存（modId → 中文名，查过的不再联网）。</summary>
    public static string ModChineseNameCacheFile => Path.Combine(DataRoot, ModChineseNameCacheFileName);

    /// <summary>启动器日志。</summary>
    public static string LogFile => Path.Combine(DataRoot, LogFileName);

    /// <summary>未处理异常写这里。</summary>
    public static string CrashLogFile => Path.Combine(DataRoot, CrashLogFileName);

    /// <summary>缓存根目录（图标、浏览器临时配置等）。</summary>
    public static string CacheRoot => Path.Combine(DataRoot, "cache");

    // ─────────────── 老布局迁移 ───────────────

    /// <summary>
    /// 把旧版本散落在 <c>instances</c> 里的启动器数据挪进 <c>launcher</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 老布局：设置、日志、缓存都混在 <c>instances\</c> 和 <c>instances\default\</c> 里面。
    /// 用户升级上来不能丢设置和日志，所以启动时搬一次。
    /// </para>
    /// <para>
    /// <b>目标已存在就不动</b>（说明已经迁过或用户新建过），只搬缺失的。
    /// 单个文件搬失败（被占用、没权限）就跳过，绝不让启动器起不来。
    /// </para>
    /// </remarks>
    /// <returns>搬动了哪些东西（给日志用）。</returns>
    public static IReadOnlyList<string> MigrateLegacyData()
    {
        var moved = new List<string>();

        var instances = InstancesRoot;
        var instance = InstanceRoot;
        var data = DataRoot;

        MoveFile(Path.Combine(instances, SettingsFileName), Path.Combine(data, SettingsFileName), moved);
        MoveFile(Path.Combine(instances, TranslationCacheFileName), Path.Combine(data, TranslationCacheFileName), moved);
        MoveFile(Path.Combine(instances, ContentTranslationCacheFileName),
            Path.Combine(data, ContentTranslationCacheFileName), moved);

        foreach (var name in new[] { LogFileName, LogFileName + ".1", CrashLogFileName })
        {
            MoveFile(Path.Combine(instance, name), Path.Combine(data, name), moved);
        }

        // 缓存目录整块搬：里面有图标缓存、浏览器临时配置
        var oldCache = Path.Combine(instance, "cache");
        var newCache = CacheRoot;

        if (Directory.Exists(oldCache) && !Directory.Exists(newCache))
        {
            try
            {
                Directory.CreateDirectory(data);
                Directory.Move(oldCache, newCache);
                moved.Add("cache\\");
            }
            catch
            {
                // 搬不动就算了，缓存丢了顶多重新生成
            }
        }

        // 旧临时目录：新布局改用 launcher\temp，这里只剩下上次安装的残渣（实测 28MB）。
        // 留着只会被存档备份一起打包出去。删不掉（被占用）就算了。
        var oldTemp = Path.Combine(instance, "temp");
        if (Directory.Exists(oldTemp))
        {
            try
            {
                Directory.Delete(oldTemp, recursive: true);
                moved.Add("temp\\");
            }
            catch
            {
                // 游戏还开着、文件被占 —— 下次启动再清
            }
        }

        return moved;
    }

    /// <summary>搬一个文件：源在、目标不在才动手。</summary>
    private static void MoveFile(string source, string target, List<string> moved)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(target)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                File.Move(source, target);
            }
            catch (IOException)
            {
                // 被占用或跨卷：退化成复制 + 删除
                File.Copy(source, target, overwrite: false);
                try { File.Delete(source); } catch { /* 删不掉就留着，反正已经复制过去 */ }
            }

            moved.Add(Path.GetFileName(source));
        }
        catch
        {
            // 单个文件搬不动不影响启动
        }
    }
}
