using System.Text.Json;

namespace McLauncher.Core.Auth;

/// <summary>扫描官方启动器令牌的结果。</summary>
/// <param name="Found">有没有找到可用的令牌文件。</param>
/// <param name="FilePath">找到的那个文件（没找到是 <c>null</c>）。</param>
/// <param name="Account">解析出来的账号（令牌是否还有效要另外验）。</param>
/// <param name="Problem">没找到 / 解析失败的原因，直接给用户看。</param>
public sealed record OfficialTokenScan(
    bool Found,
    string? FilePath,
    MinecraftAccount? Account,
    string? Problem);

/// <summary>
/// 从 <b>Minecraft 官方启动器</b>的存档里读登录令牌。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个：</b>Mojang 从 2023 年起要求第三方启动器的 Azure 应用先被批准，
/// 才能调 <c>login_with_xbox</c>。个人开发者拿不到批准（要注册 Xbox 开发者计划 + 公司实体）。
/// 但官方启动器自己不受限制 —— 它登录后会把 Java 令牌<b>明文</b>写进
/// <c>launcher_accounts.json</c>，令牌本身就是 Mojang 发的，
/// 谁拿着它去开游戏 Mojang 都认。所以读出来用，是唯一能进正版服务器的路。
/// </para>
/// <para>
/// <b>这不是破解。</b>文件是明文的，格式是公开的（社区文档里到处都是），
/// 读的是用户自己机器上、自己账号的令牌。跟「读自己浏览器保存的密码」是一个性质 ——
/// 只在本机、只对本人。
/// </para>
/// <para>
/// <b>局限必须说清楚：</b>令牌 24 小时过期，而且这里<b>拿不到 refresh token</b>
/// （官方启动器不给）。所以过期之后只能回官方启动器再启动一次游戏让它刷新，
/// 我们才能读到新的。这一点在界面上要写明白，不然用户会以为是登录坏了。
/// </para>
/// </remarks>
public sealed class OfficialLauncherTokenService : IDisposable
{
    private readonly HttpClient _http;

    public OfficialLauncherTokenService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>日志回调。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// 官方启动器可能把账号存在这些位置。按「最可能有」排。
    /// </summary>
    /// <remarks>
    /// 三种来源：老的 Java 启动器（<c>.minecraft\launcher_accounts.json</c>）、
    /// 新的 Windows 版启动器（Store 包，路径带随机 Package 名，所以用通配找）、
    /// 以及少数人手动改过游戏目录的情况（跟着 launcher_profiles.json 走）。
    /// </remarks>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var list = new List<string>();

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // ① 老版 Java 启动器 / 大部分教程说的那个位置
        list.Add(Path.Combine(roaming, ".minecraft", "launcher_accounts.json"));

        // ② 新版 Windows 启动器（Store 版）实测用的名字，2026-11 在真机上确认过
        list.Add(Path.Combine(roaming, ".minecraft", "launcher_accounts_microsoft_store.json"));
        list.Add(Path.Combine(roaming, ".minecraft", "launcher_accounts_microsoft.json"));

        // ③ Store 版（UWP）的本地状态目录。Package 名是固定的 GUID 族名，但子目录可能变
        try
        {
            var packages = Path.Combine(local, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var dir in Directory.EnumerateDirectories(packages, "Microsoft.4297127D64EC6_*"))
                {
                    var state = Path.Combine(dir, "LocalState");
                    if (!Directory.Exists(state)) continue;

                    list.Add(Path.Combine(state, "launcher_accounts.json"));
                    list.Add(Path.Combine(state, "launcher_accounts_microsoft_store.json"));
                    list.Add(Path.Combine(state, ".minecraft", "launcher_accounts.json"));
                    list.Add(Path.Combine(state, ".minecraft", "launcher_accounts_microsoft_store.json"));
                }
            }
        }
        catch
        {
            // 包目录读不了就算了，不影响其他候选
        }

        return list;
    }

    /// <summary>
    /// 扫一遍候选路径，返回第一个能解析出账号的。
    /// </summary>
    /// <param name="searchPaths">
    /// 要搜的路径。传 <c>null</c> 用 <see cref="CandidatePaths"/>（正常情况）。
    /// 测试时传自己的路径，免得往用户真实的 <c>.minecraft</c> 里写假文件。
    /// </param>
    /// <remarks>
    /// 遇到文件存在但解析不出东西（比如启动器升级换了格式）时，会继续试下一个候选，
    /// 而不是直接报错 —— 用户装了两个启动器的情况很常见。
    /// </remarks>
    public OfficialTokenScan Scan(IEnumerable<string>? searchPaths = null)
    {
        var tried = new List<string>();
        string? lastProblem = null;

        foreach (var path in searchPaths ?? CandidatePaths())
        {
            if (!File.Exists(path)) continue;

            tried.Add(path);

            var (account, problem) = ParseFile(path);

            if (account is not null)
            {
                Log?.Invoke($"[官方令牌] 从 {path} 读到账号 {account.Name}");
                return new OfficialTokenScan(true, path, account, null);
            }

            lastProblem = problem;
            Log?.Invoke($"[官方令牌] {path} 解析不出账号：{problem}");
        }

        if (tried.Count == 0)
        {
            return new OfficialTokenScan(false, null, null,
                "没找到官方启动器的账号文件。先把 Minecraft 官方启动器装好、登录、\n" +
                "并且启动一次 Java 版（登录本身不够，要启动游戏才会写令牌）。");
        }

        return new OfficialTokenScan(false, tried[0], null,
            lastProblem ?? "账号文件存在，但里面没有可用的登录令牌。\n" +
            "去官方启动器里启动一次 Java 版再试。");
    }

    /// <summary>解析一个 <c>launcher_accounts.json</c>。</summary>
    private (MinecraftAccount? Account, string? Problem) ParseFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return (null, $"读不了文件：{ex.GetType().Name}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(json))
            return (null, "文件是空的");

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = doc.RootElement;

            if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Object)
                return (null, "文件里没有 accounts 段");

            // 优先拿 activeAccountLocalId 指的那个；没有就随便挑一个带令牌的
            string? activeId = null;
            if (root.TryGetProperty("activeAccountLocalId", out var active) && active.ValueKind == JsonValueKind.String)
                activeId = active.GetString();

            MinecraftAccount? fallback = null;

            // 有账号、但 accessToken 是空的。新版 Store 版启动器就是这个状态 ——
            // 令牌只在启动游戏那一刻通过命令行传下去，从不落盘。
            // 记下来，好让报错能说清"我认出你是谁了，就差令牌"。
            var tokenless = new List<string>();
            string? tokenlessExpiry = null;

            foreach (var entry in accounts.EnumerateObject())
            {
                var candidate = ParseAccount(entry.Name, entry.Value);

                if (candidate is null)
                {
                    var (name, expiry) = PeekTokenlessAccount(entry.Value);
                    if (name is not null)
                    {
                        tokenless.Add(name);
                        tokenlessExpiry ??= expiry;
                    }
                    continue;
                }

                if (activeId is not null && string.Equals(entry.Name, activeId, StringComparison.Ordinal))
                    return (candidate, null);

                fallback ??= candidate;
            }

            if (fallback is not null) return (fallback, null);

            if (tokenless.Count > 0)
            {
                var when = string.IsNullOrWhiteSpace(tokenlessExpiry)
                    ? string.Empty
                    : $"（文件里记的到期时间是 {tokenlessExpiry}）";

                return (null,
                    $"认出了正版账号「{string.Join("、", tokenless)}」，但 accessToken 是空的 —— " +
                    $"新版（Store 版）官方启动器不把令牌写进磁盘，只在启动游戏时从命令行传下去。{when}");
            }

            return (null, "accounts 里没有带登录令牌的账号（可能只保存了离线账号）");
        }
        catch (JsonException ex)
        {
            return (null, $"JSON 格式不对：{ex.Message}");
        }
    }

    /// <summary>从 accounts 里的一项解析出账号。</summary>
    private static MinecraftAccount? ParseAccount(string localId, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var token = GetString(element, "accessToken");
        if (string.IsNullOrWhiteSpace(token)) return null;

        string? name = null;
        string? uuid = null;

        // 微软账号（新版）：名字和 UUID 在 minecraftProfile 里
        if (element.TryGetProperty("minecraftProfile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            name = GetString(profile, "name");
            uuid = GetString(profile, "id");
        }

        // 老版 / 某些分支：直接摊在顶层
        name ??= GetString(element, "username") ?? GetString(element, "displayName");
        uuid ??= GetString(element, "uuid") ?? GetString(element, "playerId");

        if (string.IsNullOrWhiteSpace(name)) return null;

        // 官方文件里的 UUID 是 32 位紧凑格式，统一成带横线的
        if (!string.IsNullOrWhiteSpace(uuid))
            uuid = MicrosoftAuthService.AddDashes(uuid!);

        return new MinecraftAccount
        {
            Name = name!,
            Uuid = uuid ?? string.Empty,
            Kind = AccountKind.Microsoft,
            AccessToken = token!.Trim(),

            // 文件里不写过期时间。这里<b>不</b>自己编一个 24 小时：
            // 不知道就是不知道，交给 VerifyAsync 拿真实结果。
            AccessTokenExpiresAt = null,

            // 官方启动器不把 refresh token 给我们 —— 这就是"过期后要重新导入"的根因
            RefreshToken = null,

            LastLoginAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 只取出「名字」和「令牌到期时间」，不管有没有令牌。
    /// </summary>
    /// <remarks>
    /// 专门为「账号在、令牌不在」这种新版启动器的状态服务。拿不到名字就没法在
    /// 提示里告诉用户是哪个人，用户会怀疑我们根本没找到他的账号。
    /// </remarks>
    private static (string? Name, string? ExpiresAt) PeekTokenlessAccount(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return (null, null);

        string? name = null;

        if (element.TryGetProperty("minecraftProfile", out var profile)
            && profile.ValueKind == JsonValueKind.Object)
        {
            name = GetString(profile, "name");
        }

        name ??= GetString(element, "username");
        name ??= GetString(element, "displayName");

        var expiry = GetString(element, "accessTokenExpiresAt");

        return (string.IsNullOrWhiteSpace(name) ? null : name, expiry);
    }

    /// <summary>拿令牌去问一次 Mojang：还能不能用、现在叫什么名字。</summary>
    /// <param name="account">待验证的账号。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <remarks>
    /// <para>
    /// <b>必须验。</b>文件里的令牌可能是几小时前写的、也可能早就过期了，
    /// 而文件本身没有过期时间字段。不验就启动，用户会在游戏里看到
    /// 「登录验证失败」这种完全查不出原因的错误。
    /// </para>
    /// <para>
    /// 验证还能顺便解决另一件事：<b>玩家可能改过名</b>，文件里的名字是旧的。
    /// 以接口返回的为准。
    /// </para>
    /// </remarks>
    public async Task<MinecraftAccount> VerifyAsync(
        MinecraftAccount account, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            throw new AuthException(
                AuthStage.Refresh,
                $"HTTP {(int)response.StatusCode}：{Trim(body, 160)}",
                "官方启动器里的令牌已经过期了（它只有 24 小时）。\n" +
                "打开官方启动器，点「开始游戏」启动一次 Java 版（进到主菜单就行），关掉游戏，\n" +
                "再回来点「从官方启动器导入」。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(
                AuthStage.Profile,
                $"HTTP {(int)response.StatusCode}：{Trim(body, 160)}",
                "向 Mojang 查询账号信息失败。检查网络，稍后重试。");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var name = GetString(root, "name");
        var id = GetString(root, "id");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AuthException(
                AuthStage.Profile, "档案里没有玩家名",
                "Mojang 返回的数据不完整。稍后重试。");
        }

        account.Name = name!;

        if (!string.IsNullOrWhiteSpace(id))
            account.Uuid = MicrosoftAuthService.AddDashes(id!);

        // 验证通过就按 24 小时算。不是瞎猜：Java 令牌的有效期就是 24 小时，
        // 而且我们刚刚确认它现在是有效的。
        account.AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(24);

        Log?.Invoke($"[官方令牌] 验证通过：{account.Name}（{account.Uuid}），24 小时内可用");

        return account;
    }

    private static string? GetString(JsonElement element, string property)
    {
        foreach (var p in element.EnumerateObject())
        {
            if (!string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase)) continue;

            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.ToString(),
                _ => null,
            };
        }

        return null;
    }

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty
        : text.Length <= max ? text
        : text[..max] + "…";

    public void Dispose() => _http.Dispose();
}
