using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Photogrammetry.App.Services;

/// <summary>
/// 影像解码与缓存：把「文件路径」变成「冻结的位图」，同一条路径只解码一次。
///
/// <para><b>只管位图。</b>不弹窗、不碰工作区、不管忙碌遮罩、不改 <c>ImageItem</c> 的字段。
/// 解码失败就把异常抛出来，由窗口决定说成什么话——「文件不存在」和「文件坏了」用不同的
/// 提示语气，那是界面的判断，不是服务的。</para>
///
/// <para>从 <c>MainWindow</c> 里搬出来的（重构计划第 5 步）。原来解码、缓存、弹窗、忙碌
/// 遮罩、尺寸回填、载入工作区挤在同一个方法里，想单独测解码都做不到。</para>
/// </summary>
internal sealed class ImageLibrary
{
    /// <summary>按影像 Id 缓存。用 Id 而不是路径：同一张片在工程里改了路径也不该重解码两次。</summary>
    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.Ordinal);

    public int Count => _cache.Count;

    /// <summary>查缓存。命中返回 true，不解码。</summary>
    public bool TryGet(string imageId, [NotNullWhen(true)] out BitmapSource? source) =>
        _cache.TryGetValue(imageId, out source);

    /// <summary>
    /// 解码一张影像并放进缓存，返回冻结的位图。已经在缓存里就直接返回。
    /// 文件读不了会抛异常，调用方负责提示。
    /// </summary>
    public async Task<BitmapSource> LoadAsync(string imageId, string path)
    {
        if (_cache.TryGetValue(imageId, out var cached)) return cached;

        var source = await Task.Run(() => Decode(path));
        _cache[imageId] = source;
        return source;
    }

    /// <summary>影像从工程里删掉时把缓存也丢掉，否则同一个 Id 再加上来会拿到旧图。</summary>
    public bool Remove(string imageId) => _cache.Remove(imageId);

    /// <summary>换工程时清空。缓存跨工程留着只会串图。</summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// 从文件解码成位图。先整文件读进内存再解，避免 BitmapImage 延迟加载把文件句柄捏住，
    /// 导致用户想删这张图时删不掉。冻结是为了能在线程池上用、也能被多个视窗共享。
    /// </summary>
    private static BitmapSource Decode(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
