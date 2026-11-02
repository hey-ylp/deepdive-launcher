namespace DeepDive.Core.Launcher;

/// <summary>启动器里能一键打开的几种游戏目录。</summary>
public enum GameFolderKind
{
    /// <summary>版本自己的目录（<c>versions/&lt;名字&gt;</c>），版本 JSON、jar、模组、存档都在里面。</summary>
    Version,

    /// <summary>模组目录（<c>mods</c>）。</summary>
    Mods,

    /// <summary>存档目录（<c>saves</c>）。</summary>
    Saves,

    /// <summary>配置目录（<c>config</c>），模组配置文件在这。</summary>
    Config,

    /// <summary>日志目录（<c>logs</c>），游戏崩了先看这里的 latest.log。</summary>
    Logs,
}

/// <summary>一次目录解析的结果。</summary>
/// <param name="Label">给人看的名字，比如「模组文件夹」。</param>
/// <param name="Path">绝对路径。</param>
/// <param name="Existed">调用前是否已存在（false = 是这次新建的，用来在界面上说清「刚建的，还是空的」）。</param>
public sealed record GameFolderTarget(string Label, string Path, bool Existed);

/// <summary>
/// 解析「某个版本的某个游戏目录在哪」，并在需要时建出来。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不能简单地 <c>Path.Combine(实例根, "mods")</c>：版本隔离开启后，<c>mods</c>、
/// <c>saves</c>、<c>config</c>、<c>logs</c> 都搬到了 <c>versions/&lt;版本名&gt;/</c> 下面，
/// 每个版本一套。路径必须跟 <see cref="MinecraftLauncherService.GetGameDir"/> 走同一个判断，
/// 否则界面上点「打开模组文件夹」会打开一个游戏根本不读的目录 —— 放进去的模组不生效，
/// 用户会以为模组坏了。
/// </para>
/// <para>
/// 版本目录（<see cref="GameFolderKind.Version"/>）不受隔离开关影响，永远是
/// <c>versions/&lt;版本名&gt;/</c>，版本 JSON 和 jar 一直放在那。
/// </para>
/// </remarks>
public sealed class GameFolderService
{
    private readonly MinecraftLauncherService _core;

    public GameFolderService(MinecraftLauncherService core)
        => _core = core ?? throw new ArgumentNullException(nameof(core));

    /// <summary>版本目录（<c>versions/&lt;名字&gt;</c>）。没装就是 null。</summary>
    public string? FindVersionDirectory(string versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName)) return null;

        var dir = Path.Combine(_core.GamePath.Versions, versionName);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// 解析目标目录。<b>没装的版本直接抛异常</b> —— 不替用户凭空造一个空目录出来。
    /// </summary>
    /// <remarks>
    /// 对于 <see cref="GameFolderKind.Mods"/> 这类子目录，游戏没启动过一次时本来就不存在，
    /// 这种情况会顺手建出来（否则资源管理器会报「找不到路径」），并在返回值里
    /// 标 <c>Existed = false</c> 让界面说清楚。
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">版本还没装。</exception>
    public GameFolderTarget Resolve(string versionName, GameFolderKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);

        var versionDir = FindVersionDirectory(versionName)
            ?? throw new DirectoryNotFoundException(
                $"版本「{versionName}」还没装到本地，没有可打开的文件夹。先启动一次，文件下载下来就有了。");

        var (label, path) = kind switch
        {
            GameFolderKind.Version => ("版本文件夹", versionDir),

            // 这几个跟着 --gameDir 走：隔离开启时在版本目录里，关闭时在实例根
            GameFolderKind.Mods => ("模组文件夹", Path.Combine(_core.GetGameDir(versionName), "mods")),
            GameFolderKind.Saves => ("存档文件夹", Path.Combine(_core.GetGameDir(versionName), "saves")),
            GameFolderKind.Config => ("配置文件夹", Path.Combine(_core.GetGameDir(versionName), "config")),
            GameFolderKind.Logs => ("日志文件夹", Path.Combine(_core.GetGameDir(versionName), "logs")),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的目录类型"),
        };

        var existed = Directory.Exists(path);
        if (!existed)
            Directory.CreateDirectory(path);

        return new GameFolderTarget(label, path, existed);
    }

    /// <summary>实例根目录（<c>libraries</c>、<c>assets</c>、<c>runtime</c> 都在这，所有版本共用）。</summary>
    public string InstanceRoot => _core.InstanceRoot;
}
