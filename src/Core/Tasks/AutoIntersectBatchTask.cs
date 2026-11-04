using System;
using System.Collections.Generic;
using System.Linq;

namespace Photogrammetry.Core.Tasks;

/// <summary>一张被跳过的影像：为什么跳过。</summary>
public sealed record SkippedImage(string ImageId, string Reason);

/// <summary>一对影像的匹配 + 交会结果。</summary>
public sealed record AutoIntersectPair(ImageItem Left, ImageItem Right, AutoIntersectRun Run);

/// <summary>一次「全工作区自动交会」的结果。</summary>
public sealed class AutoIntersectBatchRun
{
    /// <summary>实际跑过匹配的像对（包括一对都没配上的）。</summary>
    public IReadOnlyList<AutoIntersectPair> Pairs { get; init; } = Array.Empty<AutoIntersectPair>();

    /// <summary>所有像对解出来的点，已拼成一个表。</summary>
    public IReadOnlyList<AutoIntersectPoint> Points { get; init; } = Array.Empty<AutoIntersectPoint>();

    /// <summary>直接跳过的影像（缺内定向、缺外方位、读不到文件…）。</summary>
    public IReadOnlyList<SkippedImage> Skipped { get; init; } = Array.Empty<SkippedImage>();

    /// <summary>像对级的提醒：能跑但一对都没配上、或者交会失败得多。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int Matched { get; init; }
    public int Failed { get; init; }

    public bool Success => Points.Count > 0;
}

/// <summary>
/// 把工作区里所有影像两两匹配再逐点交会。
///
/// <para>与 <see cref="AutoIntersectTask"/> 同一个内核，区别是「谁来挑像对、谁来跳过谁」：
/// 单对版本由界面指定两张，这里把全部影像过一遍，条件不够的影像直接跳过并给一句原因，
/// 一对都配不上的像对也只记一句——不需要人盯着某一张。</para>
///
/// <para>影像解码仍在界面层做（要碰 BitmapSource），这里只收灰度缓冲。</para>
/// </summary>
public static class AutoIntersectBatchTask
{
    /// <summary>一张影像连同它的灰度缓冲。</summary>
    public readonly record struct PreparedImage(ImageItem Image, byte[] Pixels, int Step);

    /// <summary>
    /// 跑一批。没有可用的影像时 <see cref="AutoIntersectBatchRun.Points"/> 为空，
    /// <see cref="AutoIntersectBatchRun.Skipped"/> 里会写清每张为什么不能用。
    /// </summary>
    /// <param name="images">影像与灰度缓冲。</param>
    /// <param name="stereoPairs">
    /// 工程里的像对列表，用来找相对定向结果。给了它，即使两张片都没做后方交会也能跑
    /// （成果是模型坐标）；不给或没解过相对定向，就只认绝对外方位。
    /// </param>
    public static AutoIntersectBatchRun Run(
        IReadOnlyList<PreparedImage> images,
        IReadOnlyList<StereoPair>? stereoPairs = null,
        int capacity = 2000,
        double ratio = 0.8,
        double toleranceMm = 0.10)
    {
        ArgumentNullException.ThrowIfNull(images);

        var usable = new List<PreparedImage>(images.Count);
        var skipped = new List<SkippedImage>();

        foreach (var item in images)
        {
            var reason = Check(item);
            if (reason is null)
            {
                usable.Add(item);
            }
            else
            {
                skipped.Add(new SkippedImage(item.Image.Id, reason));
            }
        }

        var pairs = new List<AutoIntersectPair>();
        var points = new List<AutoIntersectPoint>();
        var warnings = new List<string>();
        var matched = 0;
        var failed = 0;

        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = i + 1; j < usable.Count; j++)
            {
                var left = usable[i];
                var right = usable[j];

                var run = AutoIntersectTask.Run(
                    left.Image, right.Image, left.Pixels, left.Step, right.Pixels, right.Step,
                    capacity, ratio, toleranceMm,
                    FindRelative(stereoPairs, left.Image.Id, right.Image.Id));

                pairs.Add(new AutoIntersectPair(left.Image, right.Image, run));
                matched += run.Matched;
                failed += run.Failed;

                if (run.Success)
                {
                    points.AddRange(run.Points);
                    warnings.AddRange(run.Warnings.Select(w => $"{left.Image.Id} × {right.Image.Id}：{w}"));
                }
                else
                {
                    warnings.Add($"{left.Image.Id} × {right.Image.Id}：{Short(run)}");
                }
            }
        }

        return new AutoIntersectBatchRun
        {
            Pairs = pairs,
            Points = points,
            Skipped = skipped,
            Warnings = warnings,
            Matched = matched,
            Failed = failed,
        };
    }

    /// <summary>返回跳过原因；能用时返回 null。措辞要短，日志里一行一条。</summary>
    private static string? Check(PreparedImage item)
    {
        var image = item.Image;

        if (image.Width <= 0 || image.Height <= 0)
        {
            return "影像尺寸未知";
        }

        if (item.Pixels.Length < image.Height * item.Step)
        {
            return "灰度缓冲不完整";
        }

        if (image.Interior is not { IsValid: true })
        {
            return "缺少内定向";
        }

        // 外方位不在这里查：有没有几何是**像对级**的事（绝对外方位或这对片的相对定向），
        // 某一张缺外方位不代表所有配对都跑不了——只要和它对偶的那张有相对定向就行。
        return null;
    }

    /// <summary>
    /// 找这对片的相对定向结果。像对在工程里存的方向可能与本次配对相反，两个方向都要试；
    /// 元素属于哪一侧由 <see cref="RelativeOrientation.ReferenceImageId"/> 决定，不靠存的方向。
    /// </summary>
    private static RelativeOrientation? FindRelative(
        IReadOnlyList<StereoPair>? pairs, string leftId, string rightId)
    {
        if (pairs is null) return null;

        foreach (var pair in pairs)
        {
            if (pair.Relative is null) continue;
            if (pair.LeftImageId == leftId && pair.RightImageId == rightId) return pair.Relative;
            if (pair.LeftImageId == rightId && pair.RightImageId == leftId) return pair.Relative;
        }

        return null;
    }

    /// <summary>像对失败的短句：只留一句，别把整段错误堆进日志。</summary>
    private static string Short(AutoIntersectRun run) => run.Failure switch
    {
        AutoIntersectFailure.NoMatches => "一对都没配上（多半没有重叠，或纹理太重复）",
        AutoIntersectFailure.Matcher => run.Message,
        _ => run.Message,
    };
}
