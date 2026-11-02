using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using McLauncher.Core.Launcher;

namespace McLauncher.App.Models;

/// <summary>
/// 拉 Modrinth 项目图标：下载 → 解码 → 塞进列表项。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用第三方解码库</b>：实测本机（Win11）WPF 自带的 WIC 能直接解 webp
/// （Modrinth 的图标 20 条里 15 条是 webp）。但 Win10 需要装「WebP 图像扩展」才有这个编解码器，
/// 所以解码失败不算错误 —— 退回原来的首字母色块，界面照常能用。
/// </para>
/// <para>
/// <b>为什么要落盘缓存</b>：同一页翻来翻去、同一个模组在不同搜索里重复出现，
/// 每次都去 CDN 拿一遍既慢又没必要。缓存按 URL 哈希存原始字节，不解码后的位图 ——
/// 这样换了解码参数也不用清缓存。
/// </para>
/// <para>
/// <b>并发压到 4</b>：一页 20 条，全放出去就是 20 个同时连接，
/// 对 CDN 不礼貌，弱网下也容易成片超时。
/// </para>
/// </remarks>
public sealed class ContentIconLoader
{
    private const int MaxParallel = 4;

    /// <summary>卡片上图标显示 42px，解码到 64 足够 2 倍屏，再大只是白占内存。</summary>
    private const int DecodeWidth = 64;

    private static readonly HttpClient Http = CreateClient();

    private readonly string _cacheDir;
    private readonly SemaphoreSlim _gate = new(MaxParallel);

    /// <summary>已经失败过的 URL。同一页里失败十几次没有意义，记下来直接跳过。</summary>
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.Ordinal);

    public ContentIconLoader()
        => _cacheDir = Path.Combine(LauncherPaths.CacheRoot, "icons");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("McLauncher/1.0 (Minecraft launcher)");
        return client;
    }

    /// <summary>给一批列表项补图标。失败的项保持色块，不抛异常。</summary>
    public Task FillAsync(IEnumerable<ContentHitView> hits, CancellationToken token)
    {
        var targets = hits.Where(h => !string.IsNullOrWhiteSpace(h.IconUrl)).ToList();
        if (targets.Count == 0) return Task.CompletedTask;

        return Task.WhenAll(targets.Select(h => LoadIntoAsync(h, token)));
    }

    private async Task LoadIntoAsync(ContentHitView hit, CancellationToken token)
    {
        var url = hit.IconUrl!;
        if (_failed.ContainsKey(url)) return;

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var image = await FetchAsync(url, token).ConfigureAwait(false);
            if (image is null)
            {
                _failed[url] = 0;
                return;
            }

            // 冻过的 BitmapSource 可以跨线程。绑定引擎收到 PropertyChanged 会自己切回 UI 线程，
            // 但这里还是显式派发一下，省得以后有人把这个类挪到没有 Dispatcher 的地方用
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                hit.Icon = image;
            else
                dispatcher.BeginInvoke(() => hit.Icon = image);
        }
        catch (OperationCanceledException)
        {
            // 换搜索/翻页把它掐了，正常
        }
        catch (Exception)
        {
            _failed[url] = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BitmapSource?> FetchAsync(string url, CancellationToken token)
    {
        var cacheFile = Path.Combine(_cacheDir, CacheNameFor(url));

        // 缓存命中：省一次网络往返
        if (File.Exists(cacheFile))
        {
            try
            {
                return Decode(await File.ReadAllBytesAsync(cacheFile, token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 缓存读不动就当没有，走网络
            }
        }

        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length == 0) return null;

        var image = Decode(bytes);
        if (image is null) return null;   // 这台机器解不了 webp：不写缓存，下次换机器还能用

        try
        {
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllBytesAsync(cacheFile, bytes, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去不影响显示
        }

        return image;
    }

    /// <summary>解成位图。解不了（比如没有 webp 编解码器）返回 <c>null</c>，由调用方退回首字母色块。</summary>
    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;          // 立刻读完，流关掉也不影响
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = DecodeWidth;                   // 只解需要的大小
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception)
        {
            // NotSupportedException（没 webp codec）、FileFormatException（图坏了）都走这里
            return null;
        }
    }

    /// <summary>缓存文件名：URL 的哈希，够长不会撞，也不受路径长度限制。</summary>
    private static string CacheNameFor(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".bin";
    }
}
