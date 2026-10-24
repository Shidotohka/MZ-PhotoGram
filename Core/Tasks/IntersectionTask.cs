using System;
using System.Collections.Generic;

namespace Photogrammetry.Core.Tasks;

/// <summary>前方交会卡在哪一步。界面据此决定措辞与颜色，不靠匹配消息文本。</summary>
public enum IntersectionFailure
{
    None,

    /// <summary>左窗没有影像。</summary>
    NoLeftImage,

    /// <summary>右窗没有影像。</summary>
    NoRightImage,

    /// <summary>两个窗里是同一张影像，交会角为零，做不了。</summary>
    SameImage,

    /// <summary>某一张没有内定向结果，没有像平面坐标系。</summary>
    NoInterior,

    /// <summary>某一张没有绝对外方位（还没做后方交会）。</summary>
    NoExterior,

    /// <summary>两张片的焦距不一致——不是同一个相机，不能用同一个 f 交会。</summary>
    MismatchedFocalLength,

    /// <summary>还没在左片上点同名像点。</summary>
    LeftNotMeasured,

    /// <summary>还没在右片上点同名像点。</summary>
    RightNotMeasured,

    /// <summary>像点落在框幅之外。</summary>
    PixelOutsideImage,

    /// <summary>交会本身失败：视线平行、投影系数为负、参数非法。</summary>
    Solver,
}

/// <summary>
/// 一次前方交会的运行结果。成功时 <see cref="Result"/> 非空，
/// <see cref="Point"/> 是数学系（X 东、Y 北、Z 天顶）的地面坐标。
/// </summary>
public sealed class IntersectionRun
{
    public required IntersectionFailure Failure { get; init; }

    /// <summary>失败说明。成功时为空串。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>解算结果。失败在 <see cref="IntersectionFailure.Solver"/> 时也为 null。</summary>
    public IntersectionResult? Result { get; init; }

    /// <summary>参与解算的两张影像，成功时为非空。</summary>
    public ImageItem? Left { get; init; }
    public ImageItem? Right { get; init; }

    /// <summary>参与解算的两个像平面坐标，mm。</summary>
    public ImagePointMm LeftMm { get; init; }
    public ImagePointMm RightMm { get; init; }

    /// <summary>
    /// 解出来了但值得提醒的事：交会角太小、两条视线差得远、上下视差偏大。
    /// 这些不阻止出结果，但会直接影响能不能用。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool Success => Failure == IntersectionFailure.None && Result is not null;

    /// <summary>地面点，m，数学系。未成功时为零点。</summary>
    public GroundPointM Point => Result?.Point ?? default;
}

/// <summary>
/// 前方交会的一次完整执行：检查两张片的可用性 → 像素换像平面坐标 → 交会 → 过闸门。
///
/// <para>与 <see cref="ResectionTask"/> 同一个形状：不碰界面、不抛异常，失败都从返回值表达。
/// 界面只负责把结果显示出来。</para>
///
/// <para><b>前置条件</b>：两张片都要有内定向（像素 → 像平面）与<b>绝对</b>外方位
/// （各自做过后方交会）。缺任何一项都会在 <see cref="Failure"/> 里说清楚是哪一张缺什么。</para>
/// </summary>
public static class IntersectionTask
{
    /// <summary>
    /// 像点坐标的先验中误差，mm。内定向在真实航摄数据上 σ₀ 是 0.012–0.020 mm，取上限。
    /// 这个值只影响 m_X / m_Y / m_Z 的量级，不影响交会点本身。
    /// </summary>
    public const double DefaultImageSigmaMm = 0.02;

    /// <summary>
    /// 交会角低于这个值就提醒（VL-03）。15° 时 m_Z 已经是 m_X 的约 3.7 倍，
    /// 再小就没有实用价值了。
    /// </summary>
    public const double SmallAngleDeg = 15.0;

    /// <summary>上下视差超过这个值就提醒，mm。</summary>
    public const double LargeVerticalParallaxMm = 0.10;

    /// <summary>
    /// 跑一次前方交会。像素坐标由调用方给出（用户在两张片上各点的位置）。
    /// </summary>
    public static IntersectionRun Solve(
        ImageItem? left,
        ImageItem? right,
        ImagePixel? leftPixel,
        ImagePixel? rightPixel,
        double imageSigmaMm = DefaultImageSigmaMm)
    {
        if (left is null) return Fail(IntersectionFailure.NoLeftImage, "左窗还没有影像。");
        if (right is null) return Fail(IntersectionFailure.NoRightImage, "右窗还没有影像。");
        if (ReferenceEquals(left, right) || left.Id == right.Id)
            return Fail(IntersectionFailure.SameImage,
                $"两个窗里是同一张影像「{left.Id}」。前方交会要两张不同的片。");

        if (left.Interior is not { } li || !li.IsValid)
            return Fail(IntersectionFailure.NoInterior, $"左片「{left.Id}」还没有内定向结果。");
        if (right.Interior is not { } ri || !ri.IsValid)
            return Fail(IntersectionFailure.NoInterior, $"右片「{right.Id}」还没有内定向结果。");

        if (left.Exterior is null)
            return Fail(IntersectionFailure.NoExterior,
                $"左片「{left.Id}」还没有绝对外方位：先在「后方交会」里解算它。");
        if (right.Exterior is null)
            return Fail(IntersectionFailure.NoExterior,
                $"右片「{right.Id}」还没有绝对外方位：先在「后方交会」里解算它。");

        // 两张片必须同一个相机。焦距不一致时用同一个 f 交会会得到系统性偏移，
        // 与其给出一个看着像结果的数，不如直接说清楚。
        if (Math.Abs(li.FocalLength - ri.FocalLength) > 1e-6)
            return Fail(IntersectionFailure.MismatchedFocalLength,
                $"两张片的焦距不同（左 {li.FocalLength:0.###} mm、右 {ri.FocalLength:0.###} mm）。" +
                "前方交会要求两张片属于同一个相机。");

        if (leftPixel is not { } lp) return Fail(IntersectionFailure.LeftNotMeasured, "先在左片上点一个同名像点。");
        if (rightPixel is not { } rp) return Fail(IntersectionFailure.RightNotMeasured, "先在右片上点一个同名像点。");

        if (!Inside(lp, left) || !Inside(rp, right))
            return Fail(IntersectionFailure.PixelOutsideImage, "像点落在框幅之外。");

        var leftMm = CoordinateConverter.PixelToImagePlane(lp, li, left.Width, left.Height);
        var rightMm = CoordinateConverter.PixelToImagePlane(rp, ri, right.Width, right.Height);

        var result = NativeAlgorithms.IntersectRays(
            left.Exterior, right.Exterior, leftMm, rightMm, li.FocalLength, imageSigmaMm);

        if (!result.Success)
        {
            return new IntersectionRun
            {
                Failure = IntersectionFailure.Solver,
                Message = result.Message,
                Left = left,
                Right = right,
                LeftMm = leftMm,
                RightMm = rightMm,
            };
        }

        return new IntersectionRun
        {
            Failure = IntersectionFailure.None,
            Result = result,
            Left = left,
            Right = right,
            LeftMm = leftMm,
            RightMm = rightMm,
            Warnings = Warnings(result),
        };
    }

    /// <summary>解出来了但可能不能用的几种情况。不阻止出结果，只是必须让人看见。</summary>
    private static List<string> Warnings(IntersectionResult r)
    {
        var list = new List<string>();

        if (r.ConvergenceAngleDeg < SmallAngleDeg)
        {
            // 深度方向的精度放大倍数 = 1/tan(交会角)，这是「交会角小」到底小在哪里的量化说法
            var ratio = r.ConvergenceAngleDeg > 1e-6
                ? 1.0 / Math.Tan(r.ConvergenceAngleDeg * Math.PI / 180.0)
                : double.PositiveInfinity;
            list.Add($"交会角只有 {r.ConvergenceAngleDeg:0.00}°（低于 {SmallAngleDeg:0}°）。" +
                     $"高程精度不可靠：m_Z 已经是 m_X / m_Y 的约 {ratio:0.0} 倍。" +
                     "换个重叠更好的像对，或者接受这个精度。");
        }

        // 两条视线不相交的量：正常情况下它应该是毫米级。到米级就说明不是同一个点。
        var tolerance = Math.Max(0.5, r.BaseLength * 1e-3);
        if (r.MissDistance > tolerance)
            list.Add($"两条视线的最近距离 {r.MissDistance:0.000} m（基线 {r.BaseLength:0.0} m）。" +
                     "差得这么多，通常不是同一个点——先查是不是点错了片或点错了位置。");

        if (Math.Abs(r.VerticalParallaxMm) > LargeVerticalParallaxMm)
            list.Add($"剩余上下视差 {r.VerticalParallaxMm:+0.000;-0.000} mm（超过 {LargeVerticalParallaxMm:0.00} mm）。" +
                     "两片同名点的高度对不上，或外方位有问题。");

        return list;
    }

    private static bool Inside(ImagePixel p, ImageItem image) =>
        p.I >= 0 && p.J >= 0 && p.I < image.Width && p.J < image.Height;

    private static IntersectionRun Fail(IntersectionFailure failure, string message) =>
        new() { Failure = failure, Message = message };
}
