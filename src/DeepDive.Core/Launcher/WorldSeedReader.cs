using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DeepDive.Core.Launcher;

/// <summary>一个存档的摘要，从 <c>level.dat</c> 里读出来。</summary>
public sealed record SaveInfo(
    string Folder,
    string LevelDatPath,
    string DisplayName,
    long? Seed,
    string? McVersion,
    DateTimeOffset? LastPlayed)
{
    /// <summary>种子的可复制文本；读不出来就是空串。</summary>
    public string SeedText => Seed?.ToString() ?? string.Empty;

    /// <summary>下拉里的一行。</summary>
    public string Display => Seed is null
        ? $"{DisplayName}   ·   种子读不出"
        : $"{DisplayName}   ·   {Seed}";
}

/// <summary>
/// 从存档的 <c>level.dat</c> 里挖出世界种子。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不引 NBT 库</b>：只为了读一个 long 值就拖一个完整 NBT 实现进来，不值得。
/// 这里只做「顺路径往下走、认不出的东西整块跳过」，没有外部依赖。
/// </para>
/// <para>
/// <b>为什么用 <see cref="ReadOnlySpan{T}"/> 而不是 <see cref="BinaryReader"/></b>：
/// 第一版用 BinaryReader + <c>Stream.Seek</c>，遇到损坏或认错长度时 Seek 会越界，
/// 而 <c>MemoryStream</c> 允许 Position 超过 Length —— 于是出现「位置 28786、总长 4260
/// 却还能读出字节」这种见鬼的状态，排查起来极费劲。现在每次读取都先断言边界，
/// 越界当场抛带偏移量的异常，错在哪一眼就能看到。
/// </para>
/// <para>
/// <b>种子在三个地方待过</b>，得按版本顺序都试一遍：
/// </para>
/// <list type="number">
/// <item><description><c>level.dat</c> 的 <c>Data.WorldGenSettings.seed</c> —— 1.16 ~ 1.21.x</description></item>
/// <item><description><c>level.dat</c> 的 <c>Data.RandomSeed</c> —— 1.15 及更早</description></item>
/// <item><description><c>data/minecraft/world_gen_settings.dat</c> 的 <c>data.seed</c> —— 26.3 起</description></item>
/// </list>
/// <para>
/// 第三条是实测挖出来的：26.3 把世界生成设置整个从 <c>level.dat</c> 挪进了独立文件，
/// 而且把键名风格从小驼峰改成了全小写下划线（<c>bonus_chest</c>、<c>generate_structures</c>），
/// 根节点也从 <c>Data</c> 变成了小写 <c>data</c>。老解析器到这里会「读到存档但种子读不出」。
/// </para>
/// </remarks>
public static class WorldSeedReader
{
    private const byte TagEnd = 0;
    private const byte TagByte = 1;
    private const byte TagShort = 2;
    private const byte TagInt = 3;
    private const byte TagLong = 4;
    private const byte TagFloat = 5;
    private const byte TagDouble = 6;
    private const byte TagByteArray = 7;
    private const byte TagString = 8;
    private const byte TagList = 9;
    private const byte TagCompound = 10;
    private const byte TagIntArray = 11;
    private const byte TagLongArray = 12;

    /// <summary>嵌套深度上限。正常 level.dat 撑死十几层，超过就是文件坏了。</summary>
    private const int MaxDepth = 64;

    /// <summary>扫一个 <c>saves</c> 目录下的所有存档。读坏的跳过，不影响其它的。</summary>
    public static IReadOnlyList<SaveInfo> ListSaves(string savesDirectory)
    {
        if (!Directory.Exists(savesDirectory)) return Array.Empty<SaveInfo>();

        var list = new List<SaveInfo>();

        foreach (var dir in Directory.EnumerateDirectories(savesDirectory))
        {
            var levelDat = Path.Combine(dir, "level.dat");
            if (!File.Exists(levelDat)) continue;

            try
            {
                list.Add(Read(levelDat));
            }
            catch
            {
                // 存档被游戏占着、文件损坏、格式不认识 —— 都不该让整个列表消失
                var folderName = Path.GetFileName(dir);
                list.Add(new SaveInfo(folderName, levelDat, folderName, null, null, null));
            }
        }

        return list.OrderByDescending(s => s.LastPlayed ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>读一个 <c>level.dat</c>。</summary>
    public static SaveInfo Read(string levelDatPath)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(levelDatPath)) ?? string.Empty;
        var data = Decompress(levelDatPath);

        var pos = 0;
        var state = new State();

        var rootType = ReadByte(data, ref pos);
        if (rootType != TagCompound)
            throw new InvalidDataException($"level.dat 的根标签不是 Compound（是 {rootType}）");

        ReadString(data, ref pos); // 根名字，通常是空串
        ReadCompound(data, ref pos, string.Empty, state, depth: 1);

        // 老格式就在 level.dat 里；26.3 起挪进了独立文件，得再去翻一次
        var seed = state.WorldGenSeed ?? state.RandomSeed ?? TryReadSeparateWorldGenSeed(levelDatPath);

        return new SaveInfo(
            folder,
            levelDatPath,
            string.IsNullOrWhiteSpace(state.LevelName) ? folder : state.LevelName!,
            seed,
            state.McVersion,
            ToTime(state.LastPlayed));
    }

    /// <summary>
    /// 26.3 起世界生成设置搬到 <c>data/&lt;命名空间&gt;/world_gen_settings.dat</c>，去那儿找种子。
    /// </summary>
    /// <returns>找不到返回 <c>null</c>（老版本存档没有这个文件，属于正常情况）。</returns>
    private static long? TryReadSeparateWorldGenSeed(string levelDatPath)
    {
        var saveDirectory = Path.GetDirectoryName(levelDatPath);
        if (saveDirectory is null) return null;

        var dataDirectory = Path.Combine(saveDirectory, "data");
        if (!Directory.Exists(dataDirectory)) return null;

        foreach (var file in Directory.EnumerateFiles(dataDirectory, "world_gen_settings.dat", SearchOption.AllDirectories))
        {
            try
            {
                var buffer = Decompress(file);
                var position = 0;

                if (ReadByte(buffer, ref position) != TagCompound) continue;
                ReadString(buffer, ref position); // 根名字

                if (ScanForSeed(buffer, ref position, depth: 1) is { } seed) return seed;
            }
            catch
            {
                // 这个文件读不了就试下一个，别影响整个存档
            }
        }

        return null;
    }

    /// <summary>
    /// 在任意 compound 里找 <c>seed</c>（long）。会往名字叫 <c>data</c> / <c>Data</c> 的容器里钻。
    /// </summary>
    /// <remarks>
    /// 只用于 <c>world_gen_settings.dat</c> —— 那个文件的根下面是 <c>data</c>（小写）套 <c>seed</c>。
    /// 限定几层深度，避免在 <c>dimensions</c> 这种大结构里白翻。
    /// </remarks>
    private static long? ScanForSeed(ReadOnlySpan<byte> data, ref int position, int depth)
    {
        if (depth > 3) return null;

        while (true)
        {
            var type = ReadByte(data, ref position);
            if (type == TagEnd) return null;

            var name = ReadString(data, ref position);

            if (type == TagLong && string.Equals(name, "seed", StringComparison.OrdinalIgnoreCase))
                return ReadInt64(data, ref position);

            if (type == TagCompound && string.Equals(name, "data", StringComparison.OrdinalIgnoreCase))
            {
                var found = ScanForSeed(data, ref position, depth + 1);
                if (found is not null) return found;
                continue;
            }

            Skip(data, ref position, type, depth + 1);
        }
    }

    private sealed class State
    {
        public string? LevelName;
        public long? WorldGenSeed;
        public long? RandomSeed;
        public string? McVersion;
        public long? LastPlayed;
    }

    private static DateTimeOffset? ToTime(long? millis)
    {
        if (millis is not { } ms || ms <= 0) return null;

        const long MaxMillis = 253402300799000L;
        return ms > MaxMillis ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    /// <summary>level.dat 是 gzip 包着的，整块解到内存 —— 解压后一般几百 KB 到几 MB。</summary>
    private static byte[] Decompress(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var output = new MemoryStream();

        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static void ReadCompound(ReadOnlySpan<byte> data, ref int pos, string path, State state, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT 嵌套过深");

        while (true)
        {
            var type = ReadByte(data, ref pos);
            if (type == TagEnd) return;

            var name = ReadString(data, ref pos);
            var full = path.Length == 0 ? name : path + "." + name;

            // 只往三层复合节点里钻：Data → WorldGenSettings / Version
            if (type == TagCompound
                && (full == "Data" || full == "Data.WorldGenSettings" || full == "Data.Version"))
            {
                ReadCompound(data, ref pos, full, state, depth + 1);
                continue;
            }

            switch (full)
            {
                case "Data.LevelName" when type == TagString:
                    state.LevelName = ReadString(data, ref pos);
                    continue;

                case "Data.WorldGenSettings.seed" when type == TagLong:
                    state.WorldGenSeed = ReadInt64(data, ref pos);
                    continue;

                case "Data.RandomSeed" when type == TagLong:
                    state.RandomSeed = ReadInt64(data, ref pos);
                    continue;

                case "Data.LastPlayed" when type == TagLong:
                    state.LastPlayed = ReadInt64(data, ref pos);
                    continue;

                case "Data.Version.Name" when type == TagString:
                    state.McVersion = ReadString(data, ref pos);
                    continue;
            }

            Skip(data, ref pos, type, depth + 1);
        }
    }

    /// <summary>整块跳过一个标签。不关心的大结构（dimensions、DataPacks 那些）都走这里。</summary>
    private static void Skip(ReadOnlySpan<byte> data, ref int pos, byte type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT 嵌套过深");

        switch (type)
        {
            case TagByte:
                ReadByte(data, ref pos);
                return;

            case TagShort:
                Advance(data, ref pos, 2);
                return;

            case TagInt:
                Advance(data, ref pos, 4);
                return;

            case TagLong:
                Advance(data, ref pos, 8);
                return;

            case TagFloat:
                Advance(data, ref pos, 4);
                return;

            case TagDouble:
                Advance(data, ref pos, 8);
                return;

            case TagByteArray:
                Advance(data, ref pos, ReadInt32(data, ref pos));
                return;

            case TagString:
                Advance(data, ref pos, ReadUInt16(data, ref pos));
                return;

            case TagList:
            {
                var itemType = ReadByte(data, ref pos);
                var count = ReadInt32(data, ref pos);

                // 空列表的 itemType 可能是 0（End），这时一个元素都没有
                if (itemType == TagEnd || count == 0) return;

                for (var i = 0; i < count; i++) Skip(data, ref pos, itemType, depth + 1);
                return;
            }

            case TagCompound:
            {
                while (true)
                {
                    var itemType = ReadByte(data, ref pos);
                    if (itemType == TagEnd) return;

                    Advance(data, ref pos, ReadUInt16(data, ref pos));
                    Skip(data, ref pos, itemType, depth + 1);
                }
            }

            case TagIntArray:
                Advance(data, ref pos, (long)ReadInt32(data, ref pos) * 4);
                return;

            case TagLongArray:
                Advance(data, ref pos, (long)ReadInt32(data, ref pos) * 8);
                return;

            default:
                throw new InvalidDataException(
                    $"不认识的 NBT 标签类型 {type}（位置 {pos} / 总长 {data.Length}）");
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int pos)
    {
        Need(data, pos, 1);
        return data[pos++];
    }

    private static int ReadUInt16(ReadOnlySpan<byte> data, ref int pos)
    {
        Need(data, pos, 2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
        pos += 2;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int pos)
    {
        Need(data, pos, 4);
        var value = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
        pos += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> data, ref int pos)
    {
        Need(data, pos, 8);
        var value = BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8));
        pos += 8;
        return value;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        var length = ReadUInt16(data, ref pos);
        if (length == 0) return string.Empty;

        Need(data, pos, length);
        var value = Encoding.UTF8.GetString(data.Slice(pos, length));
        pos += length;
        return value;
    }

    private static void Advance(ReadOnlySpan<byte> data, ref int pos, long count)
    {
        Need(data, pos, count);
        pos += (int)count;
    }

    /// <summary>读之前先断言范围。所有越界都在这里爆，并且带上位置，省得靠猜。</summary>
    private static void Need(ReadOnlySpan<byte> data, int pos, long count)
    {
        if (count < 0)
            throw new InvalidDataException($"NBT 长度为负（{count}，位置 {pos}）");

        if (pos < 0 || pos + count > data.Length)
            throw new InvalidDataException(
                $"NBT 越界：位置 {pos} 要读 {count} 字节，但总长只有 {data.Length}");
    }
}
