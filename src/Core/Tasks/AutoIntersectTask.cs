using System;
using System.Collections.Generic;
using System.Linq;

namespace Photogrammetry.Core.Tasks;

/// <summary>自动交会卡在哪一步。界面据此决定措辞，不靠消息文本。</summary>
public enum AutoIntersectFailure
{
    None,

    /// <summary>左窗没有影像。</summary>
    NoLeftImage,

    /// <summary>右窗没有影像。</summary>
    NoRightImage,

    /// <summary>两个窗里是同一张影像。</summary>
    SameImage,

    /// <summary>某一张没有内定向结果，量不出像平面坐标。</summary>
    NoInterior,

    /// <summary>
    /// 两片凑不出一套几何：既没有绝对外方位，也没有相对定向结果。
    /// 有一条就能跑——绝对外方位出地面坐标，相对定向出模型坐标。
    /// </summary>
    NoGeometry,

    /// <summary>两张片焦距不一致——不是同一个相机。</summary>
    MismatchedFocalLength,

    /// <summary>匹配模块出错（多半是 photogrammetry_cv.dll 没就位）。</summary>
    Matcher,

    /// <summary>一对都配不上。</summary>
    NoMatches,
}

/// <summary>一个自动交会点：匹配到的像元坐标 + 解出的地面坐标。</summary>
public sealed record AutoIntersectPoint(
    int Index,
    double LeftI, double LeftJ,
    double RightI, double RightJ,
    ImagePointMm LeftMm, ImagePointMm RightMm,
    IntersectionResult Ground,
    double Score,
    double EpipolarMm);

/// <summary>一次「自动匹配 + 逐点交会」的结果。</summary>
public sealed class AutoIntersectRun
{
    public required AutoIntersectFailure Failure { get; init; }

    /// <summary>失败说明。成功时为空串。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>解出来的点。按匹配质量排序，顺序与匹配输出一致。</summary>
    public IReadOnlyList<AutoIntersectPoint> Points { get; init; } = Array.Empty<AutoIntersectPoint>();

    /// <summary>匹配上的对数。</summary>
    public int Matched { get; init; }

    /// <summary>RANSAC 内点数。</summary>
    public int Inliers { get; init; }

    /// <summary>两片检出特征数取小者。</summary>
    public int Detected { get; init; }

    /// <summary>匹配上了但交会失败的对数（视线平行、投影系数为负等）。</summary>
    public int Failed { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// true = 上面的点是<b>模型坐标</b>（几何来自相对定向，比例尺未知，不能当米制引用）；
    /// false = 地面坐标（几何来自绝对外方位）。
    /// </summary>
    public bool IsModelCoordinates { get; init; }

    /// <summary>几何从哪来：绝对外方位，还是相对定向（含基准片是哪张）。写日志用。</summary>
    public string GeometrySource { get; init; } = string.Empty;

    public bool Success => Failure == AutoIntersectFailure.None && Points.Count > 0;
}

/// <summary>
/// 「自动匹配 → 逐点前方交会」的流程控制。
///
/// <para>它做的事就是原来手工刺点的替代：SIFT 找出成千上万个候选，核线把候选压成一条线，
/// RANSAC 复核，剩下的每一对都走一次 <see cref="NativeAlgorithms.IntersectRays"/>。
/// 一次调用把整幅图交给 C++，不在 C# 里逐像素循环（§8.2）。</para>
///
/// <para>影像解码、灰度转换、结果排版都不在这里——那些要碰 BitmapSource 与 WPF（§8.1）。</para>
/// </summary>
public static class AutoIntersectTask
{
    /// <summary>
    /// 跑一次自动交会。
    /// </summary>
    /// <param name="left">左片（要有内定向与绝对外方位）。</param>
    /// <param name="right">右片，条件同上。</param>
    /// <param name="leftPixels">左片灰度缓冲，8 位，长度至少 leftHeight*leftStep。</param>
    /// <param name="rightPixels">右片灰度缓冲。</param>
    /// <param name="leftStep">左片每行字节数。</param>
    /// <param name="rightStep">右片每行字节数。</param>
    /// <param name="capacity">最多输出多少对。</param>
    /// <param name="ratio">Lowe 比值阈值。</param>
    /// <param name="toleranceMm">核线距离容差 mm。</param>
    /// <param name="relative">
    /// 这对片的相对定向结果，没有就传 null。给了它，即使两张片都没有绝对外方位也能跑
    /// （成果是模型坐标，比例尺未知）；两张都有绝对外方位时优先用外方位。
    /// </param>
    public static AutoIntersectRun Run(
        ImageItem? left,
        ImageItem? right,
        byte[] leftPixels,
        int leftStep,
        byte[] rightPixels,
        int rightStep,
        int capacity = 2000,
        double ratio = 0.8,
        double toleranceMm = 0.10,
        RelativeOrientation? relative = null)
    {
        if (left is null) return Fail(AutoIntersectFailure.NoLeftImage, "左窗还没有影像。");
        if (right is null) return Fail(AutoIntersectFailure.NoRightImage, "右窗还没有影像。");
        if (ReferenceEquals(left, right) || left.Id == right.Id)
        {
            return Fail(AutoIntersectFailure.SameImage,
                "两个窗里是同一张影像，交会角为零，做不了。");
        }

        if (left.Interior is not { IsValid: true } li || right.Interior is not { IsValid: true } ri)
        {
            var which = left.Interior is not { IsValid: true } ? left.Id : right.Id;
            return Fail(AutoIntersectFailure.NoInterior, $"「{which}」还没有内定向结果。");
        }

        // 几何有两条来路：绝对外方位（地面坐标）或相对定向（模型坐标）。有一条就够。
        PairGeometry? geometry;
        try
        {
            geometry = PairGeometryBuilder.FromAbsolute(left, right)
                       ?? PairGeometryBuilder.FromRelative(relative, left, right);
        }
        catch (ArgumentException ex)
        {
            return Fail(AutoIntersectFailure.NoGeometry, ex.Message);
        }

        if (geometry is null)
        {
            return Fail(AutoIntersectFailure.NoGeometry,
                "两片既没有绝对外方位、也没有相对定向结果：先给它们做一次相对定向，" +
                "或者分别在「后方交会」里解算（后者能直接出地面坐标）。");
        }


        if (Math.Abs(li.FocalLength - ri.FocalLength) > 1e-6)
        {
            return Fail(AutoIntersectFailure.MismatchedFocalLength,
                $"两张片的焦距不同（左 {li.FocalLength:0.###} mm、右 {ri.FocalLength:0.###} mm）。");
        }

        if (leftPixels.Length < left.Height * leftStep || rightPixels.Length < right.Height * rightStep)
        {
            return Fail(AutoIntersectFailure.Matcher, "灰度缓冲比影像小，解码那一步没给全。");
        }

        var f = li.FocalLength;

        // 匹配内核完全不关心 (b,R) 是怎么来的——它只要一个旋转阵和一个单位基线方向。
        var epipolar = (geometry.Epipolar.Bx, geometry.Epipolar.By, geometry.Epipolar.Bz,
                        geometry.Epipolar.Rotation, f);

        NativeAlgorithms.MatchOutcome match;
        try
        {
            match = NativeAlgorithms.MatchFeatures(
                leftPixels, left.Width, left.Height, leftStep,
                rightPixels, right.Width, right.Height, rightStep,
                li.PixelSizeMm,
                Principal(li, left),
                Principal(ri, right),
                epipolar,
                capacity,
                ratio,
                toleranceMm);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(AutoIntersectFailure.Matcher, ex.Message);
        }

        if (match.Points.Count == 0)
        {
            return Fail(AutoIntersectFailure.NoMatches,
                match.Detected < 50
                    ? $"只检出 {match.Detected} 个特征，图太平坦或两张片没有重叠。"
                    : "配不上一对（纹理重复，或者两张片根本不是同一块地方）。");
        }

        var poseLeft = geometry.LeftPose;
        var poseRight = geometry.RightPose;
        var points = new List<AutoIntersectPoint>(match.Points.Count);
        var warnings = new List<string>();
        var failed = 0;
        var shallow = 0;

        for (var k = 0; k < match.Points.Count; k++)
        {
            var m = match.Points[k];
            var lmm = CoordinateConverter.PixelToImagePlane(m.LeftI, m.LeftJ, li, left.Width, left.Height);
            var rmm = CoordinateConverter.PixelToImagePlane(m.RightI, m.RightJ, ri, right.Width, right.Height);

            try
            {
                var ground = NativeAlgorithms.IntersectRays(poseLeft, poseRight, lmm, rmm, f);
                points.Add(new AutoIntersectPoint(k, m.LeftI, m.LeftJ, m.RightI, m.RightJ,
                                                  lmm, rmm, ground, m.Score, m.EpipolarMm));
                if (ground.ConvergenceAngleDeg < 15.0) shallow++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        if (shallow > 0)
        {
            warnings.Add($"有 {shallow} 个点的交会角小于 15°，深度方向不可靠（核线方向拉不开，多半是基线太短）。");
        }

        if (failed > 0)
        {
            warnings.Add($"{failed} 对匹配交会失败（视线平行或投影系数为负），已跳过。");
        }

        if (points.Count == 0)
        {
            return Fail(AutoIntersectFailure.NoMatches, "配上的点一个都没交会成功。");
        }

        return new AutoIntersectRun
        {
            Failure = AutoIntersectFailure.None,
            Points = points,
            Matched = match.Points.Count,
            Inliers = match.Inliers,
            Detected = match.Detected,
            Failed = failed,
            Warnings = warnings,
            IsModelCoordinates = geometry.IsModelCoordinates,
            GeometrySource = geometry.Source,
        };
    }

    /// <summary>主点在像元坐标里的位置：像元 → 像平面换算把主点放在中心，再加 x0/y0 的偏移。</summary>
    private static (double Cx, double Cy) Principal(InteriorOrientation io, ImageItem image) =>
        (image.Width / 2.0 + io.X0 / io.PixelSizeMm,
         image.Height / 2.0 - io.Y0 / io.PixelSizeMm);

    private static AutoIntersectRun Fail(AutoIntersectFailure failure, string message) =>
        new() { Failure = failure, Message = message };
}
