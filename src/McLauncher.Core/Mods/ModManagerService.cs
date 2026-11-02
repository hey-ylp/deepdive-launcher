using McLauncher.Core.Launcher;

namespace McLauncher.Core.Mods;

/// <summary>
/// 模组管理界面里的一行。
/// </summary>
/// <remarks>
/// 和 <see cref="ModMetadata"/> 分开：那个是「jar 里写了什么」（只读事实），
/// 这个是「文件现在是什么状态」（能不能加载、多大、在哪个路径）。
/// 一个模组文件解析失败时 <see cref="Metadata"/> 是 <c>null</c>，但这一行仍然要显示出来 ——
/// 用户得能看见它、能删掉它，否则一个坏 jar 会让游戏永远起不来还找不到原因。
/// </remarks>
public sealed class ManagedMod
{
    /// <summary>jar 里的元数据。解析失败（不是模组、jar 损坏）时是 <c>null</c>。</summary>
    public ModMetadata? Metadata { get; init; }

    /// <summary>文件完整路径（禁用态时带 <c>.disabled</c> 后缀）。</summary>
    public required string FilePath { get; init; }

    /// <summary>文件名（含后缀）。</summary>
    public required string FileName { get; init; }

    /// <summary>是不是启用态。</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>显示名：有元数据用模组名，没有就用文件名。</summary>
    public string DisplayName => Metadata?.Name ?? Path.GetFileNameWithoutExtension(FileName);

    /// <summary>版本号，没有就是空。</summary>
    public string VersionLabel => Metadata is null ? string.Empty : $"v{Metadata.Version}";

    /// <summary>作者，没有就是空。</summary>
    public string AuthorsLabel => Metadata?.Authors ?? string.Empty;

    /// <summary>模组 ID，没有就是空。</summary>
    public string ModIdLabel => Metadata?.ModId ?? string.Empty;

    /// <summary>简介，没有就是空。</summary>
    public string DescriptionLabel => Metadata?.Description ?? string.Empty;

    /// <summary>加载器标签（Fabric / Forge / NeoForge）。解析失败时是「无法识别」。</summary>
    public string LoaderLabel => Metadata?.Kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        null => "无法识别",
        _ => "未知",
    };

    /// <summary>「已启用」/「已禁用」。</summary>
    public string StateLabel => IsEnabled ? "已启用" : "已禁用";

    /// <summary>大小的人话显示。</summary>
    public string SizeLabel => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F0} KB",
        _ => $"{SizeBytes / (1024.0 * 1024):F1} MB",
    };
}

/// <summary>
/// 模组的启用 / 禁用 / 删除。
/// </summary>
/// <remarks>
/// <para>
/// <b>启用禁用的实现方式就是改文件名</b>，和 PCL、HMCL 一样 ——
/// 游戏只认 <c>mods</c> 目录下 <c>.jar</c> 结尾的文件，加个 <c>.disabled</c> 后缀
/// 它就看不见了。好处是零侵入、随时可逆，不需要动任何配置文件。
/// </para>
/// <para>
/// <b>删除不做真删</b>，移到 <c>mods/.trash/</c> 下面。模组文件动辄几十 MB，
/// 用户点错一次就要重新下载；而且如果是整合包自带的模组，来源可能都找不回来了。
/// 移走之后想恢复，把文件从 .trash 拖回来就行 —— 界面上也会提示路径。
/// </para>
/// </remarks>
public sealed class ModManagerService
{
    /// <summary>禁用态的后缀。PCL 用 <c>.disabled</c>，老启动器还有 <c>.old</c>。</summary>
    private static readonly string[] DisabledSuffixes = { ".disabled", ".old" };

    /// <summary>删掉的文件挪到这个名字的子目录里。</summary>
    public const string TrashFolderName = ".trash";

    /// <summary>
    /// 扫一个 mods 目录，读出所有模组（含禁用态）。
    /// </summary>
    /// <param name="modsDirectory">模组目录。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <remarks>
    /// 不抛异常。目录不存在就返回空列表 —— 管理界面打开一个空目录是正常状态，
    /// 不该弹错误框。
    /// </remarks>
    public Task<IReadOnlyList<ManagedMod>> ScanAsync(
        string modsDirectory,
        CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ManagedMod>>(() =>
        {
            if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
                return Array.Empty<ManagedMod>();

            var result = new List<ManagedMod>();

            string[] files;
            try
            {
                files = Directory.GetFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Array.Empty<ManagedMod>();
            }

            foreach (var path in files)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var name = Path.GetFileName(path);

                // 只要 .jar 和被禁用的 .jar.disabled / .jar.old
                var (rawName, enabled) = NormalizeName(name);
                if (!rawName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }

                ModMetadata? meta = null;
                try
                {
                    // 禁用态的 jar 一样能读元数据 —— 内容没变，只是文件名多了一截
                    meta = ModPreflight.ReadOne(path);
                }
                catch
                {
                    // 加密 / 损坏 / 半截下载：留一行「无法识别」，让用户能删
                }

                result.Add(new ManagedMod
                {
                    Metadata = meta,
                    FilePath = path,
                    FileName = name,
                    IsEnabled = enabled,
                    SizeBytes = size,
                });
            }

            return result
                .OrderByDescending(m => m.IsEnabled)
                .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, cancellationToken);

    /// <summary>
    /// 启用 / 禁用。改文件名，成功返回 <c>null</c>，失败返回原因。
    /// </summary>
    /// <param name="mod">要操作的那一行。</param>
    /// <param name="enabled">目标状态。</param>
    public string? SetEnabled(ManagedMod mod, bool enabled)
    {
        if (mod.IsEnabled == enabled) return null; // 已经是目标状态

        // 去掉所有可能的禁用后缀，得到干净的 .jar 名
        var (rawName, _) = NormalizeName(mod.FileName);
        var directory = Path.GetDirectoryName(mod.FilePath);
        if (string.IsNullOrEmpty(directory)) return "拿不到文件所在目录";

        var target = Path.Combine(directory, enabled ? rawName : rawName + ".disabled");

        try
        {
            if (File.Exists(target))
            {
                // 极罕见：同名文件已经存在（比如手动复制过一份）。不能静默覆盖用户文件。
                return $"「{Path.GetFileName(target)}」已经存在，先把那个处理掉再试";
            }

            File.Move(mod.FilePath, target);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 挪到 <c>mods/.trash/</c>。成功返回 <c>null</c>，失败返回原因。
    /// </summary>
    /// <param name="mod">要删的那一行。</param>
    public string? Delete(ManagedMod mod)
    {
        var directory = Path.GetDirectoryName(mod.FilePath);
        if (string.IsNullOrEmpty(directory)) return "拿不到文件所在目录";

        var trash = Path.Combine(directory, TrashFolderName);

        try
        {
            Directory.CreateDirectory(trash);

            var target = Path.Combine(trash, mod.FileName);

            // 同名的已经在垃圾桶里了（删过同一个文件两次），加时间戳区分
            if (File.Exists(target))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(mod.FileName)}.{stamp}{Path.GetExtension(mod.FileName)}");
            }

            File.Move(mod.FilePath, target);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// 拆出「干净的 jar 名」和「是不是启用态」。
    /// </summary>
    /// <remarks>
    /// 要能处理 <c>a.jar.disabled</c> 和 <c>a.jar.old</c> 两种历史写法，
    /// 而且 <c>.disabled</c> 可能被加了多次（用户手动改过）——
    /// 所以循环剥，剥到没有为止。
    /// </remarks>
    private static (string RawName, bool Enabled) NormalizeName(string fileName)
    {
        var current = fileName;
        var enabled = true;

        while (true)
        {
            var matched = false;

            foreach (var suffix in DisabledSuffixes)
            {
                if (!current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                current = current[..^suffix.Length];
                enabled = false;
                matched = true;
                break;
            }

            if (!matched) break;
        }

        return (current, enabled);
    }
}
