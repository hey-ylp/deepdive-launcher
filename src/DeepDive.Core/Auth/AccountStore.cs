using System.Text.Json;

namespace DeepDive.Core.Auth;

/// <summary>
/// 账号列表的存档（<c>launcher\accounts.json</c>），所有实例共用一份。
/// </summary>
/// <remarks>
/// <para>
/// 放启动器目录而不是实例目录：账号跟"玩哪个实例"没关系。在 A 实例登过的微软账号，
/// 换到 B 实例当然还要能用，不然每建一个实例就得重新登一次。
/// </para>
/// <para>
/// 这台机器上只有一个用户，所以不区分"账号归属"，也不做加密。
/// 要提醒的是：这个文件里有 refresh token，<b>别传给别人</b>。
/// </para>
/// </remarks>
public static class AccountStore
{
    private const string FileName = "accounts.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>存档文件路径（在启动器数据目录下）。</summary>
    public static string FilePath => Path.Combine(Launcher.LauncherPaths.DataRoot, FileName);

    /// <summary>存档的文件结构。</summary>
    private sealed class FileFormat
    {
        public List<MinecraftAccount> Accounts { get; set; } = new();

        /// <summary>上次选中的账号 UUID（不是索引 —— 删掉一个后索引会错位）。</summary>
        public string? SelectedUuid { get; set; }
    }

    /// <summary>读全部账号。文件不存在 / 坏了都返回空列表，不抛异常。</summary>
    /// <remarks>
    /// 坏文件不能抛：账号文件损坏不该让启动器打不开。最坏的后果是"要重新登一次"，
    /// 而抛异常的后果是"程序起不来"。
    /// </remarks>
    public static (List<MinecraftAccount> Accounts, string? SelectedUuid) Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return (new List<MinecraftAccount>(), null);

            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<FileFormat>(json, JsonOptions);

            if (data is null) return (new List<MinecraftAccount>(), null);

            // 反序列化出来的字段可能是 null（老文件 / 手改过），兜一遍
            data.Accounts = (data.Accounts ?? new List<MinecraftAccount>())
                .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Name))
                .ToList();

            return (data.Accounts, data.SelectedUuid);
        }
        catch
        {
            return (new List<MinecraftAccount>(), null);
        }
    }

    /// <summary>写回全部账号。成功返回 <c>true</c>。</summary>
    public static bool Save(IEnumerable<MinecraftAccount> accounts, string? selectedUuid)
    {
        try
        {
            var data = new FileFormat
            {
                Accounts = accounts.ToList(),
                SelectedUuid = selectedUuid,
            };

            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 先写临时文件再替换：写到一半崩掉 / 断电不会把好的存档变成半截 JSON
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, path, overwrite: true);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
