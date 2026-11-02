using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepDive.Core.Translation;

/// <summary>一批翻译的结果。</summary>
/// <param name="Translations">下标 → 译文。翻不出来的条目不会出现在这里。</param>
/// <param name="Error">整批失败的原因（日志用）。<c>null</c> 表示这批发成功了。</param>
/// <param name="Fatal">
/// 认证或余额之类的硬错误。为 <c>true</c> 时调用方应该<b>整批停手</b>，
/// 继续翻剩下的只会一条条失败，还白等。
/// </param>
public sealed record DeepSeekBatch(IReadOnlyDictionary<int, string> Translations, string? Error, bool Fatal);

/// <summary>
/// DeepSeek 的对话接口，专门用来整批翻模组语言文件。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不复用 <see cref="MyMemoryTranslator"/></b>：那个是给搜索结果简介用的，
/// 一条一次请求、每天 5000 词额度。模组语言文件动辄几百条，用它翻要么撞额度，
/// 要么翻一晚上。这里改成<b>一次请求翻一批</b>（JSON 数组进出），
/// 一个 300 条的模组三四次请求就完事。
/// </para>
/// <para>
/// <b>为什么强制 JSON 输出</b>：批量翻译最怕的是条数对不上、顺序错位。
/// 用 <c>{"id":N,"text":"..."}</c> 一一对应，就算模型漏翻几条也能按 id 补回去，
/// 不会出现「第 7 条的译文跑到第 8 条头上」这种没法排查的问题。
/// </para>
/// <para>
/// 术语表写死在 system prompt 里 —— 机翻在 MC 术语上稳定翻车，
/// 「biome」翻成「生物群落」而不是「生物群系」、「nether」翻成「地下空间」而不是「下界」。
/// 这几句提示词基本能把这个坑按住。
/// </para>
/// </remarks>
public sealed class DeepSeekTranslator : IDisposable
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";

    /// <summary>
    /// 默认模型名。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ 不要改回 <c>deepseek-chat</c>。那个名字官方标注 <b>2026-07-24 弃用</b>，
    /// 它只是 <c>deepseek-v4-flash</c> 非思考模式的兼容路由，随时会被关掉。
    /// 真断了之后 API 返回 <c>model not found</c>，看起来跟网络故障一模一样，极难排查。
    /// </para>
    /// <para>
    /// 现在是 <c>deepseek-flash</c>（DeepSeek-V4.1-Flash）。注意它<b>默认开启思考模式</b>，
    /// 所以下面请求体里显式传了 <c>thinking: disabled</c> —— 翻界面文本不需要思维链，
    /// 开着只会让每批慢两三倍、token 费翻倍。
    /// </para>
    /// </remarks>
    public const string DefaultModel = "deepseek-flash";

    /// <summary>
    /// system prompt：一句话说清角色，然后全是硬规则。
    /// </summary>
    /// <remarks>
    /// 最后那句「只输出 JSON」是必须的 —— 不加的话模型喜欢先来一段「好的，我来帮你翻译」，
    /// 解析直接炸。
    /// </remarks>
    private const string SystemPrompt = """
        你是 Minecraft 模组本地化译者，把英文界面文本翻成简体中文。

        硬性要求：
        1. 用 Minecraft 官方中文术语：block=方块，item=物品，mob=生物，biome=生物群系，dimension=维度，
           inventory=物品栏，nether=下界，end=末地，redstone=红石，crafting=合成，smelting=烧炼，
           enchantment=魔咒，potion=药水，spawn=生成，chunk=区块，tick=刻，durability=耐久度。
        2. 文本里形如 [[F0]] [[F1]] [[F2]] 的标记是格式占位符（颜色码、参数），
           必须原样保留，位置可以按中文语序调整，但一个字都不能改、不能删、不能复制。
           原文里的换行可以按中文排版重新安排，但用空行分隔的段落必须保留下来。
        3. 模组名、作者名、物品的专有 ID 不翻译，保留英文。
        4. 简洁，符合游戏界面习惯（按钮就两三个字），不要加解释、不要加标点以外的修饰。
        5. 输出必须是 JSON 对象：{"items":[{"id":<原样的 id>,"text":"<译文>"}]}
           不要输出 markdown 代码块，不要在 JSON 前后写任何话。
        """;

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <param name="apiKey">DeepSeek 的 API Key（sk- 开头）。</param>
    /// <param name="model">模型名，默认 <see cref="DefaultModel"/>。</param>
    /// <param name="httpClient">测试时可以注入一个假的。</param>
    /// <param name="disableThinking">
    /// 是否关掉思考模式。默认关（false）—— 翻译不需要思维链，开着又慢又贵。
    /// </param>
    public DeepSeekTranslator(
        string apiKey,
        string? model = null,
        HttpClient? httpClient = null,
        bool disableThinking = true)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API Key 是空的", nameof(apiKey));

        _apiKey = apiKey.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        DisableThinking = disableThinking;

        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            // 一批几十条，实测 3~10 秒。给足 90 秒，别为了快把大批量掐死
            Timeout = TimeSpan.FromSeconds(90),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepDive/0.1.0 (Minecraft launcher; mod localization)");
    }

    /// <summary>实际用的模型名。</summary>
    public string Model { get; }

    /// <summary>是否关掉了思考模式（请求体里 <c>thinking.type = disabled</c>）。</summary>
    public bool DisableThinking { get; }

    /// <summary>
    /// 翻一批文本。
    /// </summary>
    /// <param name="texts">已做过格式符保护的文本，下标就是 id。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <remarks>
    /// 这个方法<b>从不抛异常</b>（除非取消）。网络断了、Key 错了、模型抽风返回半截 JSON，
    /// 都只是把原因放进返回值 —— 汉化是附加功能，不能把调用方拖崩。
    /// </remarks>
    public async Task<DeepSeekBatch> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return new DeepSeekBatch(new Dictionary<int, string>(), null, false);

        var items = new JsonArray();
        for (var i = 0; i < texts.Count; i++)
        {
            items.Add(new JsonObject { ["id"] = i, ["text"] = texts[i] });
        }

        var payload = new JsonObject
        {
            ["model"] = Model,

            // 关思考：翻界面文本是纯映射活，思维链只会拖慢两三倍、把 token 费翻倍。
            // DeepSeek 的思考模式默认是开的，不显式关就等于默默多花钱。
            ["thinking"] = new JsonObject { ["type"] = DisableThinking ? "disabled" : "enabled" },

            ["temperature"] = 0.2,
            ["max_tokens"] = 8192,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonObject { ["items"] = items }.ToJsonString(),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        HttpResponseMessage response;
        string body;

        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return new DeepSeekBatch(new Dictionary<int, string>(), "请求超时（90 秒没回应）", false);
        }
        catch (Exception ex)
        {
            return new DeepSeekBatch(new Dictionary<int, string>(), $"网络失败：{ex.GetType().Name}: {ex.Message}", false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new DeepSeekBatch(
                    new Dictionary<int, string>(),
                    $"HTTP {(int)response.StatusCode}：{Shorten(ExtractApiError(body) ?? body)}",
                    IsFatalStatus(response.StatusCode));
            }

            var content = ExtractContent(body);
            if (content is null)
            {
                return new DeepSeekBatch(new Dictionary<int, string>(), "响应里没有 choices[0].message.content", false);
            }

            var parsed = ParseItems(content, texts.Count, out var parseError);
            return parsed is null
                ? new DeepSeekBatch(new Dictionary<int, string>(), $"译文解析失败：{parseError}", false)
                : new DeepSeekBatch(parsed, null, false);
        }
    }

    /// <summary>401 是 Key 不对、402 是余额不够 —— 这两种继续翻没有任何意义。</summary>
    private static bool IsFatalStatus(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden;

    /// <summary>从响应体里取 <c>choices[0].message.content</c>。</summary>
    private static string? ExtractContent(string body)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var choices = root?["choices"] as JsonArray;
            var message = choices is { Count: > 0 } ? choices[0]?["message"] as JsonObject : null;
            return message?["content"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>DeepSeek 的错误信息在 <c>error.message</c> 里。</summary>
    private static string? ExtractApiError(string body)
    {
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["error"]?["message"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把模型返回的 JSON 解析成「下标 → 译文」。
    /// </summary>
    /// <remarks>
    /// 宽容处理三种常见毛病：外面裹了 ```json、id 是字符串、漏了几条。
    /// 漏条不报错 —— 少翻一条就少一条，比整批作废强。
    /// </remarks>
    private static Dictionary<int, string>? ParseItems(string content, int expectedCount, out string? error)
    {
        error = null;

        var json = StripCodeFence(content);

        try
        {
            var root = JsonNode.Parse(json);
            var items = root switch
            {
                JsonObject obj => obj["items"] as JsonArray,
                JsonArray array => array,
                _ => null,
            };

            if (items is null)
            {
                error = "返回的 JSON 里没有 items 数组";
                return null;
            }

            var result = new Dictionary<int, string>();

            foreach (var node in items)
            {
                if (node is not JsonObject item) continue;

                var idNode = item["id"];
                var textNode = item["text"];
                if (textNode is null) continue;

                int id;
                if (idNode is JsonValue idValue && idValue.TryGetValue<int>(out var direct))
                {
                    id = direct;
                }
                else if (!int.TryParse(idNode?.ToString(), out id))
                {
                    continue;
                }

                if (id < 0 || id >= expectedCount) continue;

                var text = textNode.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text)) result[id] = text;
            }

            if (result.Count == 0)
            {
                error = "items 里没有一条能用的";
                return null;
            }

            return result;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {Shorten(ex.Message)}";
            return null;
        }
    }

    /// <summary>模型有时候不听话，还是会套一层 ```json 围栏。</summary>
    private static string StripCodeFence(string content)
    {
        var text = content.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0) return text;

        var body = text[(firstBreak + 1)..];

        var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? body[..lastFence].TrimEnd() : body;
    }

    private static string Shorten(string text, int max = 220)
        => text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => _http.Dispose();
}
