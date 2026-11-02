using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DeepDive.Core.Launcher;

namespace DeepDive.Core.AI;

/// <summary>AI 给出的一次诊断。</summary>
/// <param name="Success">拿到了一份能解析的回答。</param>
/// <param name="Cause">一句话原因。</param>
/// <param name="Explanation">为什么这么判断。</param>
/// <param name="Steps">照着做的步骤。</param>
/// <param name="Evidence">日志里的原文依据。</param>
/// <param name="Confidence"><c>high</c> / <c>medium</c> / <c>low</c>。</param>
/// <param name="Error">失败原因（<c>Success=false</c> 时才有）。</param>
/// <param name="FatalError">Key 错了 / 没钱了 —— 再试也没用。</param>
public sealed record AiCrashAdvice(
    bool Success,
    string Cause,
    string Explanation,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Evidence,
    string Confidence,
    string? Error = null,
    bool FatalError = false);

/// <summary>
/// 规则引擎没命中时，把上下文丢给 DeepSeek 兜底。
/// </summary>
/// <remarks>
/// <para>
/// <b>和 <see cref="Launcher.CrashAnalyzer"/> 的分工</b>：那边是本地规则，秒回、零成本、
/// 结论能精确到 jar；这边只处理「规则没见过」的怪问题，所以必须<b>由用户点一下</b>才发请求 ——
/// 每次点都花钱，静默调用等于偷钱。
/// </para>
/// <para>
/// <b>上下文要克制</b>：日志动辄几 MB，只截尾部 + 崩溃报告，控制在
/// <see cref="MaxContextChars"/> 以内。模组列表只给文件名，不给版本哈希。
/// </para>
/// <para>
/// <b>要明说隐私</b>：日志会上传，界面上必须先告知（见 <see cref="PrivacyNote"/>）。
/// </para>
/// </remarks>
public sealed class AiCrashAdvisor
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";

    /// <summary>
    /// 默认模型名。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不要改回 <c>deepseek-chat</c>。官方标注它 <b>2026-07-24 弃用</b>，
    /// 只是 <c>deepseek-v4-flash</c> 非思考模式的兼容路由。真断了会返回
    /// <c>model not found</c>，这错看着跟网络故障没区别，极难查。
    /// </remarks>
    public const string DefaultModel = "deepseek-flash";

    /// <summary>发给模型的最大字符数，超出截尾。</summary>
    public const int MaxContextChars = 14_000;

    /// <summary>给用户看的隐私提示。</summary>
    public const string PrivacyNote = "会把游戏日志和模组列表上传到 DeepSeek 分析，单次费用约 ¥0.01 以内";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>模型名（可配置，官方改名时不用改代码）。</summary>
    public string Model { get; }

    /// <param name="apiKey">DeepSeek 的 API Key（<c>sk-</c> 开头）。</param>
    /// <param name="model">模型名，默认 <see cref="DefaultModel"/>。</param>
    /// <param name="httpClient">测试时注入假的。</param>
    /// <param name="disableThinking">
    /// 是否关掉思考模式。默认关（false）。
    /// </param>
    /// <remarks>
    /// 崩溃分析<b>必须</b>关思考：<c>max_tokens</c> 只给了 1600，思考内容会和正文抢配额，
    /// 开着的话很可能思维链烧完 1600 个 token，正文是空的 —— 界面上表现为「AI 什么都没说」。
    /// 而且常见崩溃本地规则引擎已经能判，AI 只兜剩下的，不需要长链推理。
    /// </remarks>
    public AiCrashAdvisor(
        string apiKey,
        string? model = null,
        HttpClient? httpClient = null,
        bool disableThinking = true)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API Key 不能为空", nameof(apiKey));

        _apiKey = apiKey.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        DisableThinking = disableThinking;

        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>是否关掉了思考模式。</summary>
    public bool DisableThinking { get; }

    /// <summary>
    /// 让模型看一眼这份现场。
    /// </summary>
    /// <remarks>
    /// 和其他网络功能一样：<b>除取消除外不抛异常</b>，失败原因放进返回值。
    /// 崩溃分析窗口本来就该在崩溃时还能用，它自己绝不能再炸。
    /// </remarks>
    public async Task<AiCrashAdvice> AnalyzeAsync(
        CrashAnalysis analysis,
        string logExcerpt,
        IReadOnlyList<string> modFileNames,
        int memoryMb,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["model"] = Model,

            // 关思考，理由见构造函数的 remarks：1600 的 max_tokens 会被思维链吃光
            ["thinking"] = new JsonObject { ["type"] = DisableThinking ? "disabled" : "enabled" },

            ["temperature"] = 0.2,
            ["max_tokens"] = 1600,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["stream"] = false,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = BuildUserContent(analysis, logExcerpt, modFileNames, memoryMb),
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
            return Fail("请求超时（90 秒没回应），可能是网络不稳");
        }
        catch (Exception ex)
        {
            return Fail($"网络失败：{ex.GetType().Name}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var fatal = response.StatusCode is HttpStatusCode.Unauthorized
                    or HttpStatusCode.PaymentRequired
                    or HttpStatusCode.Forbidden;

                return Fail(
                    $"HTTP {(int)response.StatusCode}：{ExtractApiError(body) ?? Shorten(body)}",
                    fatal);
            }

            var content = ExtractContent(body);
            if (content is null) return Fail("响应里没有 choices[0].message.content");

            return Parse(content);
        }
    }

    /// <summary>
    /// 把现场拼成一段给模型看的文本。
    /// </summary>
    /// <remarks>
    /// 结构固定（版本信息 → 内存 → 模组 → 规则结论 → 日志），这样模型好抓重点，
    /// 也方便以后换模型时对比效果。
    /// </remarks>
    private static string BuildUserContent(
        CrashAnalysis analysis,
        string logExcerpt,
        IReadOnlyList<string> modFileNames,
        int memoryMb)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## 环境");
        sb.AppendLine($"- Minecraft 版本：{Value(analysis.GameVersion)}");
        sb.AppendLine($"- Java 版本：{Value(analysis.JavaVersion)}");
        sb.AppendLine($"- 分配内存：{(memoryMb > 0 ? memoryMb + " MB" : "未知")}");
        sb.AppendLine($"- 有没有崩溃报告：{(analysis.HasCrashReport ? "有" : "没有（只有 latest.log）")}");

        if (analysis.Description is { Length: > 0 } desc)
            sb.AppendLine($"- 崩溃报告 Description：{desc}");

        sb.AppendLine();
        sb.AppendLine($"## 已装模组（{modFileNames.Count} 个）");
        sb.AppendLine(modFileNames.Count == 0
            ? "（没有模组）"
            : string.Join("、", modFileNames.Take(200)));

        sb.AppendLine();
        sb.AppendLine("## 本地规则引擎的结论");
        if (analysis.Findings.Count == 0)
        {
            sb.AppendLine("（一条都没命中）");
        }
        else
        {
            foreach (var finding in analysis.Findings)
                sb.AppendLine($"- [{finding.Severity}] {finding.Title}");
        }

        sb.AppendLine();
        sb.AppendLine("## 日志（可能已截断，靠后的更新）");
        sb.AppendLine(logExcerpt.Length <= MaxContextChars
            ? logExcerpt
            : logExcerpt[^MaxContextChars..]);

        return sb.ToString();
    }

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "未知" : text;

    /// <summary>system prompt：约束输出成 JSON，别写散文。</summary>
    private const string SystemPrompt = """
        你是 Minecraft 整合包崩溃排查专家。用户会给你一次游戏启动的现场：
        版本信息、模组列表、本地规则引擎的结论、以及日志。

        请判断最可能的原因，只输出一个 JSON 对象，字段如下：
        {
          "cause": "一句话原因（中文，20 字以内）",
          "explanation": "为什么这么判断，2-4 句中文，要提到你依据的日志现象",
          "steps": ["第一步", "第二步"],
          "evidence": ["日志里的原文片段"],
          "confidence": "high"
        }

        硬要求：
        1. evidence 只能摘日志里**真实存在**的原文片段，不许编造类名、文件名、行号。
        2. steps 最多 4 条，每一条都要能直接照着做（比如「把 xxx.jar 从 mods 挪到 mods-disabled」），
           不要写「检查模组兼容性」这种空话。
        3. confidence 取 high / medium / low。日志里根本没有崩溃迹象时，用 low，
           并在 explanation 里直说「这次日志看不出崩溃」。
        4. 不确定就说不确定，不要为了给答案硬编一个原因。宁可少说，不能说错 ——
           用户会真的去删模组。
        """;

    /// <summary>解析模型返回的 JSON。</summary>
    private static AiCrashAdvice Parse(string content)
    {
        try
        {
            // 有些模型会把 JSON 包在 ```json 里
            var text = content.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start) text = text[(start + 1)..end].Trim();
            }

            if (JsonNode.Parse(text) is not JsonObject root)
                return Fail("模型返回的不是 JSON 对象");

            var cause = root["cause"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(cause))
                return Fail("模型的回答里没有 cause 字段");

            return new AiCrashAdvice(
                true,
                cause,
                root["explanation"]?.GetValue<string>()?.Trim() ?? string.Empty,
                ReadStrings(root["steps"]),
                ReadStrings(root["evidence"]),
                (root["confidence"]?.GetValue<string>() ?? "medium").Trim().ToLowerInvariant());
        }
        catch (Exception ex)
        {
            return Fail($"解析模型的回答失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<string> ReadStrings(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray array) return list;

        foreach (var item in array)
        {
            var text = item?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text);
        }

        return list;
    }

    private static AiCrashAdvice Fail(string error, bool fatal = false)
        => new(false, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>(), "low", error, fatal);

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

    private static string Shorten(string text)
        => text.Length <= 200 ? text : text[..200] + "…";
}
