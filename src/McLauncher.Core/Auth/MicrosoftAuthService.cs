using System.Text.Json;

namespace McLauncher.Core.Auth;

/// <summary>登录流程走到哪一步了。出错时靠它判断该给什么提示。</summary>
public enum AuthStage
{
    /// <summary>跟微软要设备码。</summary>
    DeviceCode,

    /// <summary>等用户在浏览器里输码并同意。</summary>
    WaitingUser,

    /// <summary>拿微软账号的令牌换 Xbox Live 令牌。</summary>
    XboxLive,

    /// <summary>Xbox Live → XSTS（这一步会检查账号是否被封、地区是否支持、是否成年）。</summary>
    Xsts,

    /// <summary>XSTS → Minecraft 服务令牌。这一步可能被 Mojang 白名单拦住。</summary>
    Minecraft,

    /// <summary>查游戏档案（名字 / UUID / 有没有买游戏）。</summary>
    Profile,

    /// <summary>用 refresh token 续期。</summary>
    Refresh,
}

/// <summary>登录失败，并且知道失败在哪一步。</summary>
/// <remarks>
/// 微软这条链路的报错一个比一个抽象（<c>XErr 2148916238</c>、
/// <c>403 Forbidden</c> 什么都没有）。所以除了原始信息，还要带一条
/// <see cref="Hint"/> —— 那句话是给用户看的，必须能直接指导他下一步做什么。
/// </remarks>
public sealed class AuthException : Exception
{
    public AuthException(AuthStage stage, string message, string hint)
        : base(message)
    {
        Stage = stage;
        Hint = hint;
    }

    /// <summary>失败发生在哪一步。</summary>
    public AuthStage Stage { get; }

    /// <summary>给用户看的一句话（能直接告诉他怎么办）。</summary>
    public string Hint { get; }

    /// <summary>拼成一段能直接甩到界面 / 日志里的文字。</summary>
    public string FullMessage => $"{Hint}\n\n（技术信息：{Message}）";
}

/// <summary>
/// 微软账号登录：设备码流程 + Xbox Live → XSTS → Minecraft 服务 四段跳。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写而不靠 CmlLib：</b><c>CmlLib.Core 4.1.0</c> 只有离线会话，
/// 专门做微软登录的 <c>CmlLib.Core.Auth.Microsoft</c> 停在 3.4.0，跟 4.x 的 API 不兼容。
/// 所以整条链路只能自己实现。
/// </para>
/// <para>
/// <b>为什么用设备码（device code）而不是浏览器回调：</b>回调那套要在本机起一个
/// HTTP 监听端口等微软把授权码送回来，防火墙会弹窗、端口可能被占、被杀软拦。
/// 设备码流程只是「显示一串 8 位码 → 用户去网页输 → 这边轮询」，不需要监听任何端口。
/// 代价是要多点一次复制粘贴，但换来的是在任何机器上都能跑。
/// </para>
/// <para>
/// ⚠️ <b>最后一跳可能被 Mojang 拒绝（HTTP 403）：</b>从 2023 年起，
/// 第三方启动器要在 <c>api.minecraftservices.com/authentication/login_with_xbox</c>
/// 通过，需要 Azure 应用被 Mojang 批准。没批准的应用会在这一跳被 403，
/// 而前面微软和 Xbox 都会成功 —— 现象是"明明登录进去了，却说没权限"。
/// 这不是代码 bug，<see cref="AuthStage.Minecraft"/> 阶段的提示会写清楚。
/// </para>
/// </remarks>
public sealed class MicrosoftAuthService : IDisposable
{
    // ── 端点 ────────────────────────────────────────────────────────────────
    //
    // 微软这边用 /consumers 而不是 /common：/common 会把企业账号也拉进来，
    // 而企业账号（AAD）没有 Xbox 档案，走到 XSTS 必然失败。
    // 个人微软账号（outlook/hotmail/live）都能用 /consumers。

    private const string DeviceCodeUrl =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";

    private const string TokenUrl =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private const string XboxLiveUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

    private const string MinecraftLoginUrl =
        "https://api.minecraftservices.com/authentication/login_with_xbox";

    private const string MinecraftProfileUrl =
        "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>要的权限：能登 Xbox Live + 要 refresh token（offline_access）。</summary>
    /// <remarks>
    /// <b>offline_access 千万别漏。</b>漏了就只有 1 小时的 access token，
    /// 过期后必须重新输设备码，等于每次开启动器都要重登一遍。
    /// </remarks>
    private const string Scope = "XboxLive.signin offline_access";

    // ── v1 端点：真正能用的那条路 ───────────────────────────────────────────
    //
    // 2026-11-02 实测结论，别再走弯路：
    //
    //   v2 /consumers 设备码端点我们<b>用不了</b>：
    //     · 自己注册的 Azure 应用：个人账号没有租户，门户直接 AADSTS16000；
    //     · 微软自家的 client id：没注册在个人账户租户里，AADSTS700016。
    //   两条都死在第一步，跟代码无关。
    //
    //   v1 端点 login.live.com <b>不查租户</b>，用 MSA 应用 id 直接通，而且给 refresh token。
    //   实测完整链路：code → MSA token → XBL(200) → XSTS(200) → login_with_xbox(200) → profile(200)。
    //
    // 特别记一笔：<c>login_with_xbox</c> <b>没有</b>把我们拦在 403 ——
    // 之前「第三方必须被 Mojang 批准」的说法在这个 client id 上不成立。
    // 这直接意味着：拿到 refresh token 就能长期续期，不用再折腾官方启动器。

    private const string LiveTokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string LiveAuthorizeUrl = "https://login.live.com/oauth20_authorize.srf";
    private const string LiveRedirectUri = "https://login.live.com/oauth20_desktop.srf";

    /// <summary>
    /// 默认用的 MSA 应用 id（Xbox Live 的公共客户端）。
    /// </summary>
    /// <remarks>
    /// 这是微软自家注册的公开应用 id，不是任何第三方启动器的。用它走 v1 授权，
    /// Mojang 那边看到的是「官方客户端在登录」。风险是万一哪天微软收紧，这个 id 会被封 ——
    /// 所以设置里允许覆盖，别写死在调用点。
    /// </remarks>
    public const string DefaultClientId = "000000004C12AE6F";

    /// <summary>v1 授权页的网址。用户在这个页面登录并同意，跳转后 URL 里带 <c>code=</c>。</summary>
    public static string BuildAuthorizeUrl(string? clientId = null)
    {
        var id = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();

        return LiveAuthorizeUrl
             + $"?client_id={Uri.EscapeDataString(id)}"
             + "&response_type=code"
             + $"&redirect_uri={Uri.EscapeDataString(LiveRedirectUri)}"
             + $"&scope={Uri.EscapeDataString(Scope)}"
             + "&display=touch&locale=zh-CN";
    }

    /// <summary>从跳转回来的网址里抠出 <c>code</c>。抠不到返回 <c>null</c>。</summary>
    /// <remarks>
    /// 两种输入都要能吃：完整网址（<c>...?code=M.C5...</c>），或者用户只复制了码本身。
    /// MSA 的授权码一律以 <c>M.</c> 开头、不含空格和 <c>&amp;</c>，
    /// 拿这个特征做兜底判断足够稳。
    /// </remarks>
    public static string? ExtractCode(string callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl)) return null;

        var text = callbackUrl.Trim().Trim('"', '\'', ' ');

        var mark = text.IndexOf("code=", StringComparison.OrdinalIgnoreCase);

        if (mark >= 0)
        {
            var rest = text[(mark + 5)..];

            var end = rest.IndexOfAny(new[] { '&', ' ', '\r', '\n' });
            if (end >= 0) rest = rest[..end];

            var parsed = Uri.UnescapeDataString(rest.Trim());
            return string.IsNullOrWhiteSpace(parsed) ? null : parsed;
        }

        // 只粘了码本身的情况
        if (text.StartsWith("M.", StringComparison.Ordinal) && !text.Contains(' '))
            return text;

        return null;
    }

    private readonly HttpClient _http;

    /// <summary>日志回调（测试和界面都用得上）。</summary>
    public Action<string>? Log { get; set; }

    public MicrosoftAuthService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient
        {
            // 单次请求超时。设备码的「等待用户」是靠我们自己轮询，
            // 不受这个影响 —— 那一步每轮就是一个普通的短请求。
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    // ── v1：授权码换令牌（当前唯一能走通的入口）──────────────────────────────

    /// <summary>
    /// 用授权码换一个完整的 Minecraft 账号（<b>带 refresh token</b>）。
    /// </summary>
    /// <param name="code">授权页跳回来那条网址里的 <c>code</c>，用 <see cref="ExtractCode"/> 抠。</param>
    /// <param name="clientId">发起授权时用的那个 id，必须和当时一致。传 <c>null</c> 用默认。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <remarks>
    /// 授权码<b>一次性、10 分钟过期</b>。失败最常见的两个原因：重复用同一个码、
    /// 或者用户在授权页上停太久。这两种都只能重新走一遍授权，没有别的办法。
    /// </remarks>
    public async Task<MinecraftAccount> LoginWithCodeAsync(
        string code, string? clientId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new AuthException(
                AuthStage.DeviceCode, "授权码是空的",
                "在授权页登录并点「是」之后，地址栏会跳到 oauth20_desktop.srf?code=...，把整条网址复制过来。");

        var id = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();

        var form = new Dictionary<string, string>
        {
            ["client_id"] = id,
            ["redirect_uri"] = LiveRedirectUri,
            ["grant_type"] = "authorization_code",
            ["code"] = code.Trim(),
            ["scope"] = Scope,
        };

        using var response = await _http.PostAsync(
            LiveTokenUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = ReadString(body, "error") ?? "unknown";

            throw new AuthException(
                AuthStage.DeviceCode,
                $"HTTP {(int)response.StatusCode} {error}: {DescribeOAuthError(body)}",
                error == "invalid_grant"
                    ? "这个授权码已经用过了，或者超过 10 分钟。重新点一次登录，拿新的网址过来。"
                    : "微软拒绝了这次授权。重新登录一次试试。");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var msToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        if (string.IsNullOrEmpty(msToken))
            throw new AuthException(
                AuthStage.DeviceCode, "响应里没有 access_token",
                "微软返回的数据不完整。重试一次。");

        Log?.Invoke($"[登录] 微软令牌到手（{(string.IsNullOrEmpty(refresh) ? "没有" : "有")} refresh token），继续换 Xbox / Minecraft");

        return await ExchangeAsync(msToken!, refresh, cancellationToken).ConfigureAwait(false);
    }

    // ── 第一步：要设备码 ────────────────────────────────────────────────────

    /// <summary>向微软申请一个设备码，界面上把它显示给用户。</summary>
    /// <param name="clientId">Azure 应用的 client id（GUID）。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(
        string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new AuthException(
                AuthStage.DeviceCode,
                "client id 是空的",
                "先填 Azure 应用的 client id。设置页「账号」里有说明怎么申请。");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["scope"] = Scope,
        };

        using var response = await _http.PostAsync(
            DeviceCodeUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(
                AuthStage.DeviceCode,
                $"HTTP {(int)response.StatusCode}: {DescribeOAuthError(body)}",
                "微软不接受这个 client id。检查有没有填错，以及 Azure 应用是不是已经允许「公共客户端流」。");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var deviceCode = root.TryGetProperty("device_code", out var dc) ? dc.GetString() : null;
        var userCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() : null;

        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
            throw new AuthException(
                AuthStage.DeviceCode, "微软的响应里没有 device_code",
                "这是微软那边的问题，稍后重试。");

        var info = new DeviceCodeInfo(
            deviceCode!,
            userCode!,
            root.TryGetProperty("verification_uri", out var vu) ? vu.GetString() ?? string.Empty : string.Empty,
            TimeSpan.FromSeconds(GetInt(root, "expires_in", 900)),
            TimeSpan.FromSeconds(Math.Max(1, GetInt(root, "interval", 5))));

        Log?.Invoke($"[登录] 设备码 {info.UserCode}，{info.ExpiresIn.TotalMinutes:F0} 分钟内有效，" +
                    $"去 {info.VerificationUri} 输入");

        return info;
    }

    // ── 第二步：等用户同意 ──────────────────────────────────────────────────

    /// <summary>轮询直到用户在浏览器里点了「允许」。成功后直接返回可用的账号。</summary>
    /// <param name="clientId">Azure 应用的 client id。</param>
    /// <param name="deviceCode">上一步拿到的设备码。</param>
    /// <param name="interval">轮询间隔，微软给的。</param>
    /// <param name="expiresIn">设备码有效期。</param>
    /// <param name="cancellationToken">取消（用户点「取消」就传这个）。</param>
    /// <param name="onStatus">每轮询一次回调一句状态，界面可以用它显示"还在等"。</param>
    public async Task<MinecraftAccount> WaitForLoginAsync(
        string clientId,
        string deviceCode,
        TimeSpan interval,
        TimeSpan expiresIn,
        CancellationToken cancellationToken = default,
        Action<string>? onStatus = null)
    {
        var deadline = DateTimeOffset.UtcNow + expiresIn;
        var current = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(current, cancellationToken).ConfigureAwait(false);

            var (state, msToken, refreshToken) = await PollTokenAsync(
                clientId, deviceCode, cancellationToken).ConfigureAwait(false);

            switch (state)
            {
                case PollState.Pending:
                    onStatus?.Invoke("还没在网页上确认，继续等…");
                    continue;

                case PollState.SlowDown:
                    // 微软要求降速就真的降 —— 硬顶着会直接给你拒了
                    current += TimeSpan.FromSeconds(5);
                    onStatus?.Invoke($"微软要求放慢一点，改成每 {current.TotalSeconds:F0} 秒问一次");
                    continue;

                case PollState.Expired:
                    throw new AuthException(
                        AuthStage.WaitingUser, "设备码到期了",
                        "每次登录码只有 15 分钟。重新点一次「登录」拿一个新的就行。");

                case PollState.Declined:
                    throw new AuthException(
                        AuthStage.WaitingUser, "用户在网页上拒绝了授权",
                        "你在授权页点了「取消」。想登录的话再点一次「登录」。");

                case PollState.Ready:
                    Log?.Invoke("[登录] 微软授权成功，开始换 Xbox 令牌");
                    return await ExchangeAsync(msToken!, refreshToken, cancellationToken)
                        .ConfigureAwait(false);
            }
        }

        throw new AuthException(
            AuthStage.WaitingUser, "等超时了",
            "登录码过期。重新点一次「登录」拿个新的。");
    }

    private enum PollState { Pending, SlowDown, Expired, Declined, Ready }

    private async Task<(PollState State, string? AccessToken, string? RefreshToken)> PollTokenAsync(
        string clientId, string deviceCode, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId.Trim(),
            ["device_code"] = deviceCode,
        };

        using var response = await _http.PostAsync(
            TokenUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 等用户确认期间微软一直回 400 + error=authorization_pending，这是正常的
        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

            if (string.IsNullOrEmpty(access))
                throw new AuthException(
                    AuthStage.WaitingUser, "微软没给 access_token",
                    "微软那边返回不完整，重试一次。");

            return (PollState.Ready, access, refresh);
        }

        var error = ReadString(body, "error") ?? string.Empty;

        return error switch
        {
            "authorization_pending" => (PollState.Pending, null, null),
            "slow_down" => (PollState.SlowDown, null, null),
            "expired_token" => (PollState.Expired, null, null),
            "authorization_declined" or "access_denied" => (PollState.Declined, null, null),

            "invalid_grant" => throw new AuthException(
                AuthStage.WaitingUser, $"invalid_grant: {DescribeOAuthError(body)}",
                "登录码已经不能用了。重新点一次「登录」。"),

            _ => throw new AuthException(
                AuthStage.WaitingUser, $"HTTP {(int)response.StatusCode} {error}: {DescribeOAuthError(body)}",
                "登录过程中微软返回了意外的错误，把这条技术信息记下来再试。"),
        };
    }

    // ── 刷新：用 refresh token 续期 ─────────────────────────────────────────

    /// <summary>用账号里存的 refresh token 换一套新令牌，返回刷新后的账号副本。</summary>
    /// <param name="account">要刷的账号（不会被修改）。</param>
    /// <param name="clientId">Azure 应用的 client id。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <remarks>
    /// <b>返回的账号里带着新的 refresh token，调用方必须把它存下来。</b>
    /// 微软的 refresh token 是轮换的：每次刷新都会作废旧的那个，
    /// 只存第一次拿到的那个的话，一两周后就会突然提示"登录已过期"。
    /// </remarks>
    public async Task<MinecraftAccount> RefreshAsync(
        MinecraftAccount account, string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
            throw new AuthException(
                AuthStage.Refresh, "账号里没有 refresh token",
                "这个账号没有可续期的凭据，需要重新登录一次。");

        // client id 留空就用默认的公共 id —— refresh token 绑定签发它的 id，
        // 换一个 id 去刷会 invalid_grant。所以正常情况下这里一定是调用方传进来的原 id。
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = effectiveClientId,
            ["refresh_token"] = account.RefreshToken!,
            ["scope"] = Scope,
        };

        // 必须打 v1 端点：refresh token 是 v1 发的，v2 端点不认（会 invalid_grant）。
        // 这两个端点没有互通的凭据体系，别想着换一个试试。
        using var response = await _http.PostAsync(
            LiveTokenUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = ReadString(body, "error") ?? string.Empty;

            // invalid_grant 在这里的含义很具体：refresh token 失效了（太久没用 / 被作废 / 改了密码）
            if (error is "invalid_grant")
            {
                throw new AuthException(
                    AuthStage.Refresh, $"invalid_grant: {DescribeOAuthError(body)}",
                    "登录状态过期了（微软的凭据有效期有限，长时间不用或被改密码都会这样）。重新登录一次就行。");
            }

            throw new AuthException(
                AuthStage.Refresh, $"HTTP {(int)response.StatusCode} {error}: {DescribeOAuthError(body)}",
                "刷新登录状态失败，多半是网络问题。稍后重试，或者重新登录。");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var msToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;

        // 微软会轮换 refresh token。它没给就说明沿用旧的
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        if (string.IsNullOrEmpty(msToken))
            throw new AuthException(
                AuthStage.Refresh, "刷新响应里没有 access_token",
                "微软那边返回不完整，重试一次。");

        Log?.Invoke("[登录] 微软令牌已刷新，继续换 Xbox / Minecraft 令牌");

        var refreshed = await ExchangeAsync(msToken!, newRefresh ?? account.RefreshToken, cancellationToken)
            .ConfigureAwait(false);

        // 用户可能在微软账号里改了名，以最新的 profile 为准
        refreshed.LastLoginAt = DateTimeOffset.UtcNow;

        return refreshed;
    }

    // ── 中间三层：Xbox Live → XSTS → Minecraft ──────────────────────────────

    /// <summary>拿微软令牌换一个填好的 Minecraft 账号。</summary>
    private async Task<MinecraftAccount> ExchangeAsync(
        string msAccessToken, string? refreshToken, CancellationToken cancellationToken)
    {
        var (xblToken, xblUhs) = await XboxLiveAuthenticateAsync(msAccessToken, cancellationToken)
            .ConfigureAwait(false);

        var (xstsToken, xstsUhs) = await XstsAuthorizeAsync(xblToken, cancellationToken).ConfigureAwait(false);

        // XSTS 的 uhs 才是给 Minecraft 用的；XBL 那个只用来进 XSTS
        var uhs = string.IsNullOrEmpty(xstsUhs) ? xblUhs : xstsUhs;

        var (mcToken, expiresIn) = await MinecraftLoginAsync(uhs, xstsToken, cancellationToken)
            .ConfigureAwait(false);

        var (name, uuid) = await MinecraftProfileAsync(mcToken, cancellationToken).ConfigureAwait(false);

        var account = new MinecraftAccount
        {
            Name = name,
            Uuid = uuid,
            Kind = AccountKind.Microsoft,
            AccessToken = mcToken,
            RefreshToken = refreshToken,
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            LastLoginAt = DateTimeOffset.UtcNow,
        };

        Log?.Invoke($"[登录] 成功：{name}（{uuid}）");
        return account;
    }

    private async Task<(string Token, string Uhs)> XboxLiveAuthenticateAsync(
        string msAccessToken, CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                // d= 前缀是给「微软账号 v2 令牌」用的；旧版令牌才用 t=
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + msAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        var body = await PostJsonAsync(XboxLiveUrl, payload, cancellationToken).ConfigureAwait(false);
        var (token, uhs) = ParseXboxToken(body);

        if (string.IsNullOrEmpty(token))
            throw new AuthException(
                AuthStage.XboxLive, "Xbox Live 没返回 Token",
                "这一步失败通常是微软令牌本身有问题，重新登录一次。");

        return (token, uhs);
    }

    private async Task<(string Token, string Uhs)> XstsAuthorizeAsync(
        string xblToken, CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        // XSTS 失败时返回的是 401 + {"Identity":"0","XErr":2148916238,...}，
        // 所以不能按普通 HTTP 错误处理，得把 XErr 解出来看
        var body = await PostJsonAsync(XstsUrl, payload, cancellationToken, allowErrorBody: true)
            .ConfigureAwait(false);

        var xerr = ReadLong(body, "XErr");

        if (xerr is not null)
        {
            throw xerr switch
            {
                // 这个账号还没建过 Xbox 档案。微软账号和 Xbox 账号是两回事，新号要先去建一个
                2148916233 => new AuthException(
                    AuthStage.Xsts, "XErr 2148916233：这个微软账号还没有 Xbox 档案",
                    "先去 xbox.com 用同一个账号登录一次（会自动建档案），再回来登录。"),

                2148916235 => new AuthException(
                    AuthStage.Xsts, "XErr 2148916235：所在地区不支持 Xbox Live",
                    "这个微软账号注册的地区（或当前网络出口）不在 Xbox Live 服务范围内。换网络或换账号。"),

                2148916236 or 2148916237 => new AuthException(
                    AuthStage.Xsts, $"XErr {xerr}：账号需要成人验证",
                    "去 account.microsoft.com 补完成年人验证（一般要绑手机或信用卡），再回来登录。"),

                2148916238 => new AuthException(
                    AuthStage.Xsts, "XErr 2148916238：这是未成年人账号",
                    "未成年账号必须被加进「Microsoft 家庭」才能玩 Minecraft。让家长在 family.microsoft.com 里把你加进去。"),

                2148916227 => new AuthException(
                    AuthStage.Xsts, "XErr 2148916227：这个 Xbox 账号被封禁了",
                    "封禁信息在 enforcement.xbox.com 上能查。封禁期间任何启动器都没法登。"),

                _ => new AuthException(
                    AuthStage.Xsts, $"XErr {xerr}",
                    "Xbox Live 拒绝了这次授权。搜一下这个错误码，或者换个账号试试。"),
            };
        }

        var (token, uhs) = ParseXboxToken(body);

        if (string.IsNullOrEmpty(token))
            throw new AuthException(
                AuthStage.Xsts, $"XSTS 没返回 Token：{Trim(body, 200)}",
                "Xbox 授权失败。重新登录一次；一直不行的话，账号本身可能有问题。");

        return (token, uhs);
    }

    private async Task<(string Token, int ExpiresIn)> MinecraftLoginAsync(
        string uhs, string xstsToken, CancellationToken cancellationToken)
    {
        var payload = new
        {
            // 注意格式：XBL3.0 x=<uhs>;<xsts token>，中间的 `<` `>` 和 `;` 都不能省
            identityToken = $"XBL3.0 x={uhs};{xstsToken}",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, MinecraftLoginUrl)
        {
            Content = JsonContent(payload),
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // 这就是那个「Mojang 没批准你的 Azure 应用」的分支
            throw new AuthException(
                AuthStage.Minecraft,
                $"HTTP 403 Forbidden：{Trim(body, 200)}",
                "微软登录和 Xbox 登录都成功了，但 Mojang 拒绝了最后的换令牌请求。\n" +
                "原因：从 2023 年起，第三方启动器要通过 api.minecraftservices.com 需要一个被 Mojang 批准的 Azure 应用，\n" +
                "个人开发者基本申请不到。这不是代码问题，也不是你的账号问题。\n" +
                "可行的做法：先用离线账号玩；或者看 Azure 应用能不能申请加入批准名单。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(
                AuthStage.Minecraft, $"HTTP {(int)response.StatusCode}: {Trim(body, 200)}",
                "换 Minecraft 令牌失败。稍后重试；一直失败的话，可能是 Mojang 服务在维护。");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var token = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;

        if (string.IsNullOrEmpty(token))
            throw new AuthException(
                AuthStage.Minecraft, "Minecraft 登录响应里没有 access_token",
                "Mojang 返回不完整。重试一次。");

        return (token!, GetInt(root, "expires_in", 86400));
    }

    private async Task<(string Name, string Uuid)> MinecraftProfileAsync(
        string mcToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 没买游戏就是这个分支：令牌是好的，但账号下没有 Minecraft
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Unauthorized)
        {
            throw new AuthException(
                AuthStage.Profile,
                $"HTTP {(int)response.StatusCode}：{Trim(body, 200)}",
                "这个微软账号下没有 Minecraft: Java 版。要么还没买，要么买在了另一个账号上。\n" +
                "先去 minecraft.net 确认这个账号能进游戏。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(
                AuthStage.Profile, $"HTTP {(int)response.StatusCode}: {Trim(body, 200)}",
                "查游戏档案失败。稍后重试。");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
            throw new AuthException(
                AuthStage.Profile, "档案里没有名字或 UUID",
                "Mojang 返回的数据不完整。重试一次。");

        return (name!, AddDashes(id!));
    }

    // ── 小工具 ─────────────────────────────────────────────────────────────

    /// <summary>把 32 位无横线的 UUID 变成标准写法。</summary>
    /// <remarks>
    /// Mojang 的 profile 接口返回的是 <c>069a79f444e94726a5befca90e38aaf5</c> 这种紧凑格式，
    /// 但 CmlLib 的 <c>-uuid</c> 启动参数要带横线的标准格式。
    /// </remarks>
    public static string AddDashes(string uuid)
    {
        var hex = uuid.Replace("-", string.Empty).Trim().ToLowerInvariant();
        if (hex.Length != 32) return uuid;

        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private async Task<string> PostJsonAsync(
        string url, object payload, CancellationToken cancellationToken, bool allowErrorBody = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(payload) };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // XSTS 的失败信息本身就在响应体里（XErr），所以调用方要拿到 body
        if (!response.IsSuccessStatusCode && !allowErrorBody)
        {
            throw new AuthException(
                AuthStage.XboxLive, $"HTTP {(int)response.StatusCode}: {Trim(body, 200)}",
                "连 Xbox 服务失败。检查网络（这一步走的是微软海外服务器，可能需要代理）。");
        }

        return body;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    private static (string Token, string Uhs) ParseXboxToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var token = root.TryGetProperty("Token", out var t) ? t.GetString() ?? string.Empty : string.Empty;

            var uhs = string.Empty;
            if (root.TryGetProperty("DisplayClaims", out var dc)
                && dc.TryGetProperty("xui", out var xui)
                && xui.ValueKind == JsonValueKind.Array
                && xui.GetArrayLength() > 0
                && xui[0].TryGetProperty("uhs", out var u))
            {
                uhs = u.GetString() ?? string.Empty;
            }

            return (token, uhs);
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ReadLong(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var p)) return null;
            return p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 OAuth 错误响应里挖出可读的那句（<c>error_description</c> 往往最有信息量）。</summary>
    private static string DescribeOAuthError(string body)
    {
        var desc = ReadString(body, "error_description");
        if (!string.IsNullOrWhiteSpace(desc)) return desc!;

        var error = ReadString(body, "error");
        if (!string.IsNullOrWhiteSpace(error)) return error!;

        return Trim(body, 160);
    }

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty
        : text.Length <= max ? text
        : text[..max] + "…";

    private static int GetInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var p) && p.TryGetInt32(out var v) ? v : fallback;

    public void Dispose() => _http.Dispose();
}

/// <summary>微软设备码流程第一步的结果，界面拿它显示"去哪、输什么"。</summary>
/// <param name="DeviceCode">内部用的长码，给轮询。</param>
/// <param name="UserCode">给用户输的短码，形如 <c>ABCD-1234</c>。</param>
/// <param name="VerificationUri">让用户打开的网址。</param>
/// <param name="ExpiresIn">多久内有效。</param>
/// <param name="Interval">微软要求的轮询间隔。</param>
public sealed record DeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    TimeSpan ExpiresIn,
    TimeSpan Interval);
