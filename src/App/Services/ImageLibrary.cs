using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Photogrammetry.App.Services;

/// <summary>
/// 影像解码与缓存：把「文件路径」变成「冻结的位图」，同一条路径只解码一次，
/// 并按<b>字节预算</b>淘汰最久没用的那些。
///
/// <para><b>为什么要有预算。</b>一张 UltraCam 影像 $11310\times17310$ 解码成 BGRA 就是 783 MB，
/// 双图就 1.5 GB。早先这里是裸的 <c>Dictionary</c>，打开工程后随便翻几张就能把进程顶到墙上。</para>
///
/// <para><b>为什么不用现成的缓存库。</b>试过 <c>Microsoft.Extensions.Caching.Memory</c>
/// （<c>SizeLimit</c> + 淘汰回调），两条实测都不过关，都记在 <c>Snap --cache</c> 的注释与
/// [小项与取舍](../../Docs/小项与取舍.md) X-01 里：
/// ①它的压缩顺序是「后进来的先出」——往已经满了的缓存里再加一张，被扔掉的就是刚加的那张，
/// 而对影像缓存来说那恰好是唯一不能扔的；②淘汰回调不保证同步执行，按回调记的占用会滞后，
/// 于是「腾到装得下」的循环会多淘汰一张。所以这里只用它最朴素的部分：一个哈希表 + 一条最近使用链，
/// 三十行，行为完全可预期。</para>
///
/// <para><b>淘汰不是灾难。</b>正在显示的那张由视窗自己持有引用，缓存把它丢了也照样画；
/// 只是下次再要它的时候要重新解码（大图约 1 s）。所以这里的策略是"宁可变慢，不许变崩"。</para>
///
/// <para><b>只管位图。</b>不弹窗、不碰工作区、不管忙碌遮罩、不改 <c>ImageItem</c> 的字段。
/// 解码失败就把异常抛出来，由窗口决定说成什么话——「文件不存在」和「文件坏了」用不同的
/// 提示语气，那是界面的判断，不是服务的。</para>
/// </summary>
internal sealed class ImageLibrary
{
    /// <summary>
    /// 默认预算：物理内存的 35%，夹在 512 MiB–2 GiB。
    ///
    /// <para>上限 2 GiB：16 GB 机器的 35% 是 5.6 GB，那已经比"两张 UltraCam 大图"多得多，
    /// 再往上只会让别的进程难受。下限 512 MiB：太小的预算换不来缓存，只换来频繁重解码。</para>
    /// </summary>
    public static long DefaultMaxBytes { get; } = ComputeDefaultMaxBytes();

    private readonly object _gate = new();

    /// <summary>Id → 链表节点。节点指向顺序表里的位置，命中时挪到表尾。</summary>
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new(StringComparer.Ordinal);

    /// <summary>最近使用顺序：表头是<b>最久没用</b>的，表尾是刚用过的。</summary>
    private readonly LinkedList<CacheEntry> _order = new();

    private long _bytes;

    /// <param name="maxBytes">测试用的小预算。生产代码不传，用 <see cref="DefaultMaxBytes"/>。</param>
    public ImageLibrary(long? maxBytes = null) => Limit = maxBytes ?? DefaultMaxBytes;

    /// <summary>预算，字节。</summary>
    public long Limit { get; }

    /// <summary>缓存的影像张数。</summary>
    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    /// <summary>当前占用的估算字节。</summary>
    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>查缓存。命中返回 true，不解码。读一次就算"用过"，淘汰时排后面。</summary>
    public bool TryGet(string imageId, [NotNullWhen(true)] out BitmapSource? source)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(imageId, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                source = node.Value.Source;
                return true;
            }
        }

        source = null;
        return false;
    }

    /// <summary>
    /// 解码一张影像并放进缓存，返回冻结的位图。已经在缓存里就直接返回。
    /// 文件读不了会抛异常，调用方负责提示。
    /// </summary>
    public async Task<BitmapSource> LoadAsync(string imageId, string path)
    {
        if (TryGet(imageId, out var cached)) return cached;

        var source = await Task.Run(() => Decode(path));
        var bytes = EstimateBytes(source);

        // 单张就比整个预算还大：不进缓存。位图已经返回给调用方了，照样能显示，
        // 只是下次再要它得重新解码——总比为了它把整个缓存清空还装不下强。
        if (bytes <= Limit)
        {
            lock (_gate)
            {
                EvictUntilFits(bytes);
                AddCore(imageId, source, bytes);
            }
        }

        return source;
    }

    /// <summary>影像从工程里删掉时把缓存也丢掉，否则同一个 Id 再加上来会拿到旧图。返回原先有没有。</summary>
    public bool Remove(string imageId)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(imageId, out var node)) return false;
            Drop(node);
            return true;
        }
    }

    /// <summary>换工程时清空。缓存跨工程留着只会串图。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// 一张影像占多少字节的<b>上限估算</b>。用 4 字节/像元（BGRA32）：JPEG 解码后可能是 Bgr24，
    /// 那样估算会偏大三成——偏大是安全的方向，宁可早淘汰，不要算漏了把内存吃光。
    /// </summary>
    public static long EstimateBytes(BitmapSource source) =>
        (long)source.PixelWidth * source.PixelHeight * 4;

    // ---------- 下面这些都在 lock 里调用 ----------

    private void AddCore(string imageId, BitmapSource source, long bytes)
    {
        // 同一个 Id 已经在了（并发解码）就替换，别让两张图挂在同名上。
        if (_index.TryGetValue(imageId, out var existing)) Drop(existing);

        var node = new LinkedListNode<CacheEntry>(new CacheEntry(imageId, source, bytes));
        _order.AddLast(node);
        _index[imageId] = node;
        _bytes += bytes;
    }

    private void EvictUntilFits(long incomingBytes)
    {
        while (_bytes + incomingBytes > Limit && _order.First is { } oldest)
        {
            Drop(oldest);
        }
    }

    private void Drop(LinkedListNode<CacheEntry> node)
    {
        _order.Remove(node);
        _index.Remove(node.Value.Id);
        _bytes -= node.Value.Bytes;
    }

    private static long ComputeDefaultMaxBytes()
    {
        const long floor = 512L << 20;
        const long ceiling = 2L << 30;
        var budget = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.35);
        return Math.Clamp(budget, floor, ceiling);
    }

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

    private sealed record CacheEntry(string Id, BitmapSource Source, long Bytes);
}
