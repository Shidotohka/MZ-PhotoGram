using System;
using System.Collections.Generic;
using System.Linq;

namespace Photogrammetry.Core.Tasks;

/// <summary>模型交会卡在哪一步。</summary>
public enum ModelIntersectionFailure
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>没有可用的像对，或者像对的两片不在工程里。</summary>
    NoPair,

    /// <summary>这个像对还没有相对定向结果。</summary>
    NoRelative,

    /// <summary>两片里有片没有有效内定向，或者两片主距不一致。</summary>
    NoInterior,

    /// <summary>没有一个控制点在两片上都有像点，交会不出来。</summary>
    NoPoints,
}

/// <summary>
/// 一个控制点在模型系里的坐标：由它在<b>左、右两片上的像点</b>与该像对的相对定向位姿求交得到。
///
/// <para>注意这不是匹配点云里那种"无名点"。它有控制点号，所以能和地面坐标配对，
/// 绝对定向要的正是它。</para>
/// </summary>
public sealed record ModelPointObservation(
    string GcpId,
    GroundPointM Model,
    GroundPointM Ground,
    GcpType Type,
    ImagePointMm LeftMm,
    ImagePointMm RightMm,
    double ConvergenceAngleDeg);

/// <summary>
/// 一次模型交会的结果：每个控制点一个模型点，外加跳过了谁、为什么。
/// </summary>
public sealed class ModelIntersectionOutcome
{
    public required ModelIntersectionFailure Failure { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>能交会出来的控制点，顺序与工程里的控制点顺序一致。</summary>
    public IReadOnlyList<ModelPointObservation> Observations { get; init; } =
        Array.Empty<ModelPointObservation>();

    /// <summary>被跳过的控制点及原因（只刺了一片、交会失败……）。</summary>
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string PairLabel { get; init; } = string.Empty;

    /// <summary>模型坐标是自由比例尺（$|\mathbf b| = 1$），界面必须标出来。</summary>
    public bool IsModelCoordinates { get; init; } = true;

    /// <summary>
    /// 地面坐标的轴序。模型坐标由像点交会得来、与地面无关，所以这一步只是<b>顺手判出</b>
    /// 文件口径（判据与后方交会是同一个三点叉乘），好让绝对定向在用地面坐标之前换轴。
    /// </summary>
    public GroundAxisOrder GroundOrder { get; init; } = GroundAxisOrder.EastNorth;

    /// <summary>判手性的证据：用的哪三点、两侧正弦、投票数。控制点不足 3 个时判不出。</summary>
    public GroundOrderEvidence? Evidence { get; init; }

    /// <summary>像对的主距，mm。绝对定向用它把「米」折算成「像元」。</summary>
    public double FocalLengthMm { get; init; } = double.NaN;

    /// <summary>像元尺寸，mm/px。同上。</summary>
    public double PixelSizeMm { get; init; } = double.NaN;

    public bool Success => Failure == ModelIntersectionFailure.None;

    /// <summary>能参与绝对定向的点数（检查点不算）。</summary>
    public int UsableCount => Observations.Count(o => o.Type != GcpType.CheckPoint);
}

/// <summary>
/// 模型交会：把<b>控制点</b>的两片像点交会成模型坐标。
///
/// <para>这是"相对定向-绝对定向法"里真实存在的一步，不是匹配点云的替代品：
/// <list type="bullet">
///   <item>相对定向只给两片的相对关系，一个坐标都没有；</item>
///   <item>模型交会把某个点在两片上的像点变成模型坐标（比例尺以基线长为单位）；</item>
///   <item>绝对定向拿这批"有名字的"模型点与地面坐标解七参数。</item>
/// </list></para>
///
/// <para>求交用的是 M5 的同一个内核（<see cref="NativeAlgorithms.IntersectRays"/>），
/// 位姿来自 <see cref="PairGeometryBuilder.FromRelative"/>——模型交会本身没有新的数学。</para>
/// </summary>
public static class ModelIntersectionTask
{
    /// <summary>
    /// 跑一次模型交会。两片都得有内定向、像对得有相对定向结果、控制点得在两片上
    /// <b>都刺过点</b>——只有一片有点的交会不出模型坐标。
    /// </summary>
    public static ModelIntersectionOutcome Run(Project project, StereoPair? pair)
    {
        ArgumentNullException.ThrowIfNull(project);

        var skipped = new List<string>();
        var observations = new List<ModelPointObservation>();
        var warnings = new List<string>();

        if (pair is null)
        {
            return Fail(ModelIntersectionFailure.NoPair, skipped, observations,
                "还没有像对。先在「相对定向」里解出一对相对方位元素。");
        }

        var label = $"{pair.LeftImageId} × {pair.RightImageId}";

        var left = project.Images.FirstOrDefault(i => i.Id == pair.LeftImageId);
        var right = project.Images.FirstOrDefault(i => i.Id == pair.RightImageId);
        if (left is null || right is null)
        {
            return Fail(ModelIntersectionFailure.NoPair, skipped, observations,
                $"像对 {label} 里有影像不在工程里。", label);
        }

        var geometry = PairGeometryBuilder.FromRelative(pair.Relative, left, right);
        if (geometry is null)
        {
            return Fail(ModelIntersectionFailure.NoRelative, skipped, observations,
                $"像对 {label} 还没有相对定向结果。模型交会要用它给的两份位姿。", label);
        }

        if (left.Interior is not { IsValid: true } ioLeft ||
            right.Interior is not { IsValid: true } ioRight)
        {
            return Fail(ModelIntersectionFailure.NoInterior, skipped, observations,
                "两片里有一片还没有有效内定向，像点换不成像平面坐标。", label);
        }

        if (Math.Abs(ioLeft.FocalLength - ioRight.FocalLength) > 1e-6)
        {
            return Fail(ModelIntersectionFailure.NoInterior, skipped, observations,
                $"两片主距不同（{ioLeft.FocalLength:0.###} / {ioRight.FocalLength:0.###} mm），" +
                "同一像对必须同机同焦距。", label);
        }

        var focal = ioLeft.FocalLength;

        foreach (var gcp in project.Gcps)
        {
            var onLeft = left.FindGcpPoint(gcp.Id);
            var onRight = right.FindGcpPoint(gcp.Id);

            if (onLeft is null && onRight is null)
            {
                skipped.Add($"{gcp.Id}：两片都没刺点");
                continue;
            }
            if (onLeft is null)
            {
                skipped.Add($"{gcp.Id}：只在 {right.Id} 上刺了点");
                continue;
            }
            if (onRight is null)
            {
                skipped.Add($"{gcp.Id}：只在 {left.Id} 上刺了点");
                continue;
            }

            var leftMm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(onLeft.PixelX, onLeft.PixelY), ioLeft, left.Width, left.Height);
            var rightMm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(onRight.PixelX, onRight.PixelY), ioRight, right.Width, right.Height);

            onLeft.PositionMm = leftMm;
            onRight.PositionMm = rightMm;

            try
            {
                var intersection = NativeAlgorithms.IntersectRays(
                    geometry.LeftPose, geometry.RightPose, leftMm, rightMm, focal);

                observations.Add(new ModelPointObservation(
                    gcp.Id,
                    intersection.Point,
                    new GroundPointM(gcp.X, gcp.Y, gcp.Z),
                    gcp.Type,
                    leftMm,
                    rightMm,
                    intersection.ConvergenceAngleDeg));
            }
            catch (Exception ex)
            {
                skipped.Add($"{gcp.Id}：交会失败（{ex.Message}）");
            }
        }

        if (observations.Count == 0)
        {
            return new ModelIntersectionOutcome
            {
                Failure = ModelIntersectionFailure.NoPoints,
                Message = "没有一个控制点在两张片上都有像点，交会不出模型坐标。",
                Skipped = skipped,
                PairLabel = label,
            };
        }

        // 交会角太小的点在深度方向不可靠，值得点名而不是只给个总数
        var shallow = observations.Where(o => o.ConvergenceAngleDeg < 15.0).ToList();
        if (shallow.Count > 0)
        {
            warnings.Add($"{shallow.Count} 个点的交会角小于 15°（{string.Join("、", shallow.Take(5).Select(o => o.GcpId))}" +
                         (shallow.Count > 5 ? " 等" : "") + $"，最差 {shallow.Min(o => o.ConvergenceAngleDeg):0.00}°），" +
                         "深度方向不可靠——多半是基线太短或点离像主点太近。");
        }

        // 判地面轴序（手性）：直接调后方交会那个三点叉乘，不另写一份，也不"试解一次看哪个残差小"。
        // 它要的输入是「像点 + 地面坐标」，而这里每个点本来就有左片像点（右片像点此刻还用不上，
        // 两片的像平面口径相同），所以观测形状和后方交会完全一样，一个方法两边通用。
        // 轴序是地面文件的属性，跟模型坐标无关：判不出来照样出结果，只提示下游小心。
        var probes = observations
            .Select(o => new ResectionObservation(
                o.GcpId, o.LeftMm.X, o.LeftMm.Y, o.Ground.X, o.Ground.Y, o.Ground.Z))
            .ToList();
        var evidence = NativeAlgorithms.DetectGroundOrder(probes);
        var order = evidence.Decided ? evidence.Order : GroundAxisOrder.EastNorth;

        if (!evidence.Decided && observations.Count >= 3)
        {
            warnings.Add(evidence.Message + "绝对定向会按东-北口径解算，残差异常大时先回来查这一点。");
        }
        else if (order == GroundAxisOrder.NorthEast)
        {
            warnings.Add("地面文件是左手系（X 北、Y 东）：绝对定向会先把 X/Y 换回东-北再解。模型坐标不受影响。");
        }

        return new ModelIntersectionOutcome
        {
            Failure = ModelIntersectionFailure.None,
            Observations = observations,
            Skipped = skipped,
            Warnings = warnings,
            PairLabel = label,
            IsModelCoordinates = geometry.IsModelCoordinates,
            GroundOrder = order,
            Evidence = evidence,
            FocalLengthMm = focal,
            PixelSizeMm = ioLeft.PixelSizeMm,
        };
    }

    private static ModelIntersectionOutcome Fail(
        ModelIntersectionFailure failure,
        List<string> skipped,
        List<ModelPointObservation> observations,
        string message,
        string label = "")
        => new()
        {
            Failure = failure,
            Message = message,
            Skipped = skipped,
            Observations = observations,
            PairLabel = label,
        };
}
