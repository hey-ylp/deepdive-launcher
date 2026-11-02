using System.Text.Json.Serialization;

namespace DeepDive.Core.Auth;

/// <summary>这个账号是怎么来的。</summary>
public enum AccountKind
{
    /// <summary>离线账号。随便起个名字就能玩，进不了正版服务器。</summary>
    Offline = 0,

    /// <summary>微软正版账号。有 UUID、有皮肤、能进正版验证的服务器。</summary>
    Microsoft = 1,
}

/// <summary>
/// 一个账号。<b>同时</b>是硬盘上的存档格式，字段改名要考虑老文件还能不能读。
/// </summary>
/// <remarks>
/// <para>
/// 微软账号会在这里<b>明文</b>存 refresh token。理由和 API Key 一样：这是用户自己的机器，
/// 加密了密钥也在同一个目录，只会让人误以为安全。真正该做的是别把这个文件传给别人。
/// </para>
/// <para>
/// 存的是 <b>refresh token</b>（几个月有效、可续），不是 access token（24 小时）。
/// 启动时用它换一对新的，所以启动器隔一个月打开也还是登录状态。
/// </para>
/// </remarks>
public sealed class MinecraftAccount
{
    /// <summary>游戏里显示的名字（正版是 Mojang ID，会随改名变）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>玩家 UUID，<b>带</b>连字符的标准写法。离线账号是名字推出来的假 UUID。</summary>
    public string Uuid { get; set; } = string.Empty;

    public AccountKind Kind { get; set; } = AccountKind.Offline;

    /// <summary>
    /// Minecraft 服务令牌（<c>login_with_xbox</c> 换来的那个）。
    /// </summary>
    /// <remarks>
    /// 这个不等于微软的 access token —— 它只用来调 <c>api.minecraftservices.com</c>，
    /// 有效期 24 小时。启动前要检查是否过期，过期了才走刷新流程，
    /// 没过期就直接用，省一趟网络往返（也省得微软那边抽风时启动不了）。
    /// </remarks>
    public string? AccessToken { get; set; }

    /// <summary>微软的 refresh token。这是账号能不能"记住登录"的关键。</summary>
    public string? RefreshToken { get; set; }

    /// <summary><see cref="AccessToken"/> 什么时候过期（UTC）。</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }

    /// <summary>最近一次成功登录的时间，界面上按它排序。</summary>
    public DateTimeOffset LastLoginAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>是不是微软正版账号。</summary>
    [JsonIgnore]
    public bool IsMicrosoft => Kind == AccountKind.Microsoft;

    /// <summary>令牌是不是还能用。留 5 分钟余量，避免"启动瞬间刚好过期"。</summary>
    [JsonIgnore]
    public bool HasUsableToken =>
        IsMicrosoft
        && !string.IsNullOrEmpty(AccessToken)
        && AccessTokenExpiresAt is { } expires
        && expires > DateTimeOffset.UtcNow.AddMinutes(5);

    /// <summary>
    /// 这个账号是不是「从官方启动器导入」的（没有 refresh token）。
    /// </summary>
    /// <remarks>
    /// 官方启动器只给 Java 令牌，不给续期凭据。所以这类账号过期之后<b>没法自己刷新</b>，
    /// 只能回官方启动器启动一次游戏，再重新导入。界面上必须区分这两种情况，
    /// 否则用户点了「刷新」看到「需要重新登录」会以为启动器坏了。
    /// </remarks>
    [JsonIgnore]
    public bool NeedsReimport => IsMicrosoft && string.IsNullOrEmpty(RefreshToken);

    /// <summary>界面上那行小字：正版 / 离线。</summary>
    [JsonIgnore]
    public string KindLabel => IsMicrosoft ? "微软正版" : "离线";

    /// <summary>
    /// 头像地址（第三方服务，按 UUID 取）。
    /// </summary>
    /// <remarks>
    /// 走 mc-heads.net：路径简单、不需要 API key、C 系（avatars）和头像是同一个 host。
    /// 加载失败就是没头像，界面显示首字母占位，不影响任何功能 —— 所以不为此加依赖。
    /// </remarks>
    [JsonIgnore]
    public string AvatarUrl =>
        string.IsNullOrWhiteSpace(Uuid)
            ? string.Empty
            : $"https://mc-heads.net/avatar/{Uuid.Replace("-", string.Empty)}/64";

    /// <summary>按名字造一个离线账号（UUID 用 <see cref="OfflineUuid"/> 推）。</summary>
    public static MinecraftAccount CreateOffline(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        return new MinecraftAccount
        {
            Name = trimmed,
            Uuid = OfflineUuid(trimmed),
            Kind = AccountKind.Offline,
            LastLoginAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 由用户名推出一个稳定的假 UUID。
    /// </summary>
    /// <remarks>
    /// 用「离线 UUID」的通行算法：<c>UUID.nameUUIDFromBytes("OfflinePlayer:" + name)</c>，
    /// 也就是 MD5 再改成 version 3。这样同一个名字在任何启动器里算出来的 UUID 都一样，
    /// 老存档、老模组配置不会因为换了启动器就"人物重置"。
    /// </remarks>
    public static string OfflineUuid(string name)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + name));

        // version 3（基于 MD5）
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);

        // variant RFC 4122
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>复制一份（刷新令牌时先改副本、成功了再替换，避免刷一半把账号搞坏）。</summary>
    public MinecraftAccount Clone() => new()
    {
        Name = Name,
        Uuid = Uuid,
        Kind = Kind,
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        AccessTokenExpiresAt = AccessTokenExpiresAt,
        LastLoginAt = LastLoginAt,
    };
}
