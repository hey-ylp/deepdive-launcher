using System.Text;
using System.Text.Json;

namespace DeepDive.Core.Translation;

/// <summary>
/// 译文磁盘缓存 —— 一份 JSON，原文当键。
/// </summary>
/// <remarks>
/// <para>
/// <b>这东西不是可选的优化，是必需的。</b> 免费翻译接口 MyMemory 匿名每天只给
/// 5000 词左右，一页 20 条简介就是 2000 字符，搜三次就没了。
/// 但模组简介的重复率极高（热门模组每次搜索都排前面），缓存能把这个额度放大十几倍。
/// </para>
/// <para>
/// 写到 <c>实例根/translation-cache.json</c>，所有实例共用一份 —— 译文跟实例没关系。
/// </para>
/// <para>
/// 内存里是普通字典，<see cref="Set"/> 后立刻落盘。文件几十 KB，写一次一两毫秒，
/// 而且都在后台线程，不值得为它设计延迟刷盘。
/// </para>
/// </remarks>
public sealed class TranslationCache
{
    /// <summary>条目上限。超了就不再新增，防止长期使用后文件无限膨胀。</summary>
    private const int MaxEntries = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // 中文直接存原文，不要转成 \uXXXX —— 出问题时能直接打开文件看
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private bool _loaded;

    /// <summary>写到磁盘失败过几次之后就不再尝试，免得每次搜索都卡在同一个错误上。</summary>
    private int _writeFailures;

    public TranslationCache(string filePath)
    {
        _path = filePath;
    }

    /// <summary>命中条数（给状态栏显示「缓存命中 N 条」用）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                // 必须 EnsureLoaded：不然新建的实例在第一次 Get 之前会报 0 条，
                // 看起来像「磁盘缓存没生效」
                EnsureLoaded();
                return _entries.Count;
            }
        }
    }

    /// <summary>取一条已翻好的。没有就返回 <c>null</c>。</summary>
    public string? Get(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        lock (_gate)
        {
            EnsureLoaded();
            return _entries.TryGetValue(Normalize(text), out var value) ? value : null;
        }
    }

    /// <summary>存一条。已经存在就不覆盖（先翻的先用，谁也别改）。</summary>
    public void Set(string text, string translation)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation)) return;

        var key = Normalize(text);

        lock (_gate)
        {
            EnsureLoaded();

            if (_entries.ContainsKey(key)) return;
            if (_entries.Count >= MaxEntries) return;

            _entries[key] = translation;
            SaveLocked();
        }
    }

    /// <summary>
    /// 缓存键归一化。
    /// </summary>
    /// <remarks>
    /// 压掉连续空白：Modrinth 的简介末尾偶尔带换行，同一段话可能带不同尾巴出现两次，
    /// 不归一化就会重复花额度再翻一遍。
    /// </remarks>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text.Trim())
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (lastWasSpace) continue;
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }

            lastWasSpace = isSpace;
        }

        return sb.ToString();
    }

    /// <summary>第一次访问时把文件读进内存。文件不存在或者坏了都当空缓存。</summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return;

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded is null) return;

            foreach (var pair in loaded)
            {
                if (_entries.Count >= MaxEntries) break;
                _entries[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // 缓存文件坏了不该拦任何事 —— 顶多是重新翻一遍
            _entries.Clear();
        }
    }

    /// <summary>
    /// 落盘。先写 <c>.tmp</c> 再替换，避免写到一半断电留下半截 JSON。
    /// </summary>
    private void SaveLocked()
    {
        if (_writeFailures >= 3) return;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_entries, JsonOptions);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 缓存写不进去不影响功能，只是下次要重新翻。记次数，别一直重试。
            _writeFailures++;
        }
    }
}
