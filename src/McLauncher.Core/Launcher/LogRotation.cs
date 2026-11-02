namespace McLauncher.Core.Launcher;

/// <summary>
/// 给 <c>launcher.log</c> 做大小轮转：超过阈值就改名成 <c>.1</c>，让新日志重新写。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须有</b>：这个日志每启动一次就追加，下载进度、校验、每个文件的结果都记。
/// 实测一天下来能到 9 MB 以上，翻起来难受，出问题让它一路涨也不合适。
/// </para>
/// <para>
/// <b>只留一代备份</b>（<c>launcher.log.1</c>）。留多代的收益很小 ——
/// 排查问题永远是看「这一次」的日志，旧的上一次已经没用了。
/// 所以磁盘最多占 2×阈值。
/// </para>
/// <para>
/// <b>为什么在写入时轮转而不是定时</b>：定时器会跟写日志抢文件；而「写之前看一眼大小」
/// 天然串行，不会出现写到一半被改名的情况。为了不每行都 stat 一次文件，
/// 调用方自己控制频率（见 <c>MainViewModel.WriteLogLine</c> 的计数器）。
/// </para>
/// </remarks>
public static class LogRotation
{
    /// <summary>单个日志文件的上限，默认 8 MB。</summary>
    public const long DefaultMaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 超了就轮转。<b>任何异常都吞掉</b>：日志轮转失败绝不能影响正常使用。
    /// </summary>
    /// <returns>真的轮转了返回 <c>true</c>。</returns>
    public static bool RotateIfNeeded(string logPath, long maxBytes = DefaultMaxBytes)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return false;

        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists || info.Length <= maxBytes) return false;

            var backupPath = logPath + ".1";

            // File.Move 覆盖已有文件要用 Move(..., overwrite: true)，
            // 否则上一代 .1 会把这次轮转挡下来
            File.Move(logPath, backupPath, overwrite: true);

            return true;
        }
        catch
        {
            // 只读目录、文件被别的程序占着、磁盘满 —— 都当没发生
            return false;
        }
    }

    /// <summary>备份文件路径（给界面提示「上一次的日志在 …」用）。</summary>
    public static string BackupPathFor(string logPath) => logPath + ".1";
}
