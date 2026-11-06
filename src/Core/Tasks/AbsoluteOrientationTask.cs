using System;
using System.Collections.Generic;
using System.Linq;

namespace Photogrammetry.Core.Tasks;

/// <summary>绝对定向卡在哪一步。界面据此决定措辞，不靠匹配消息文本。</summary>
public enum AbsoluteOrientationFailure
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>没有可用的像对，或者像对的两片不在工程里。</summary>
    NoPair,

    /// <summary>这个像对还没有相对定向结果，或者基准片对不上。</summary>
    NoRelative,

    /// <summary>两片里有片没有有效内定向。</summary>
    NoInterior,

    /// <summary>两片都刺过点的控制点不足 3 个。</summary>
    NotEnoughPoints,

    /// <summary>解算本身失败（共线、退化、数值非法）。</summary>
    Solver,
}

/// <summary>
/// 一个控制点的绝对定向观测：它的模型坐标（由两片像点交会得到）与地面坐标。
/// </summary>
public sealed record AbsoluteOrientationObservation(
    string GcpId,
    GroundPointM Model,
    GroundPointM Ground,
    GcpType Type)
{
    /// <summary>参与解算时的不符值；检查点是预测偏差。解算前为 NaN。</summary>
    public double ResidualLength { get; init; } = double.NaN;

    /// <summary>检查点：用七参数把它变到地面之后的位置。</summary>
    public GroundPointM? Transformed { get; init; }
}

/// <summary>解算前的准备结果：凑到了哪些控制点、跳过了谁、还差什么。</summary>
public sealed class AbsoluteOrientationPreparation
{
    public required IReadOnlyList<AbsoluteOrientationObservation> Observations { get; init; }
    public required IReadOnlyList<string> Skipped { get; init; }
    public required AbsoluteOrientationFailure Failure { get; init; }
    public required string Message { get; init; }

    /// <summary>像对的名字，写进结果页与报告。</summary>
    public string PairLabel { get; init; } = string.Empty;

    /// <summary>模型坐标是自由比例尺（相对定向产物），界面上必须标出来。</summary>
    public bool IsModelCoordinates { get; init; } = true;

    public int Count => Observations.Count;

    public bool Ready => Failure == AbsoluteOrientationFailure.None;
}

/// <summary>
/// 一次绝对定向的运行结果。
///
/// <para>注意这里<b>没有</b>"匹配点云"的影子：参与解算的只有已刺点的控制点，
/// 每个控制点用自己的两片像点交会出一个模型坐标，靠点号与地面坐标成对。
/// 匹配出来那几千个模型点是"无名点"，只做成果展示，不进这里。</para>
/// </summary>
public sealed class AbsoluteOrientationRun
{
    public required AbsoluteOrientationFailure Failure { get; init; }

    /// <summary>七参数解。失败在 <see cref="AbsoluteOrientationFailure.Solver"/> 时为 null。</summary>
    public AbsoluteOrientationResult? Result { get; init; }

    /// <summary>参与解算的控制点（不含检查点），<see cref="AbsoluteOrientationObservation.ResidualLength"/> 已填。</summary>
    public IReadOnlyList<AbsoluteOrientationObservation> Used { get; init; } =
        Array.Empty<AbsoluteOrientationObservation>();

    /// <summary>检查点：没参与解算，只用来独立验证。</summary>
    public IReadOnlyList<AbsoluteOrientationObservation> Checks { get; init; } =
        Array.Empty<AbsoluteOrientationObservation>();

    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string Message { get; init; } = string.Empty;

    public string PairLabel { get; init; } = string.Empty;

    public bool Success => Failure == AbsoluteOrientationFailure.None;

    /// <summary>检查点的最大平面（X/Y）偏差；没有检查点时为 NaN。</summary>
    public double CheckPlanimetricMax
    {
        get
        {
            var worst = double.NaN;
            foreach (var c in Checks)
            {
                if (c.Transformed is not { } t) continue;
                var d = Math.Sqrt((t.X - c.Ground.X) * (t.X - c.Ground.X) +
                                  (t.Y - c.Ground.Y) * (t.Y - c.Ground.Y));
                if (double.IsNaN(worst) || d > worst) worst = d;
            }
            return worst;
        }
    }

    /// <summary>检查点的最大高程偏差；没有检查点时为 NaN。</summary>
    public double CheckHeightMax
    {
        get
        {
            var worst = double.NaN;
            foreach (var c in Checks)
            {
                if (c.Transformed is not { } t) continue;
                var d = Math.Abs(t.Z - c.Ground.Z);
                if (double.IsNaN(worst) || d > worst) worst = d;
            }
            return worst;
        }
    }
}

/// <summary>
/// 绝对定向（相对定向-绝对定向法的最后一步）：把模型坐标拉到地面坐标。
///
/// <para>流水线：<b>每个控制点在两片上的像点</b> → 用该像对的相对定向位姿做前方交会
/// （<see cref="NativeAlgorithms.IntersectRays"/>，与 M5 同一个内核） → 该控制点的模型坐标
/// → 与已知地面坐标配对 → 七参数（<see cref="NativeAlgorithms.SolveAbsoluteOrientation"/>）。</para>
///
/// <para>本类不引用界面类型，也不决定文案排版；它只说「成没成、卡在哪、数值是多少」。</para>
/// </summary>
public static class AbsoluteOrientationTask
{
    /// <summary>七参数需要的最少控制点数：3 个点 9 个方程，多余观测 2。</summary>
    public const int MinPoints = 3;

    /// <summary>
    /// 收集能参与绝对定向的控制点观测。两片都得刺过点——只有一片有像点的控制点交会不出模型坐标。
    /// </summary>
    public static AbsoluteOrientationPreparation Prepare(Project project, StereoPair? pair)
    {
        ArgumentNullException.ThrowIfNull(project);

        // 绝对定向的输入就是「模型交会出来的那批控制点」——两步共用同一份交会实现。
        // 否则「两片都刺点了吗」「两片主距一致吗」这类判断要写两遍，改漏一处不会报错。
        var model = ModelIntersectionTask.Run(project, pair);

        var observations = model.Observations
            .Select(o => new AbsoluteOrientationObservation(o.GcpId, o.Model, o.Ground, o.Type))
            .ToList();

        var usable = observations.Count(o => o.Type != GcpType.CheckPoint);
        var failure = model.Failure switch
        {
            ModelIntersectionFailure.None => usable >= MinPoints
                ? AbsoluteOrientationFailure.None
                : AbsoluteOrientationFailure.NotEnoughPoints,
            ModelIntersectionFailure.NoPair => AbsoluteOrientationFailure.NoPair,
            ModelIntersectionFailure.NoRelative => AbsoluteOrientationFailure.NoRelative,
            ModelIntersectionFailure.NoInterior => AbsoluteOrientationFailure.NoInterior,
            _ => AbsoluteOrientationFailure.NotEnoughPoints,
        };

        return new AbsoluteOrientationPreparation
        {
            Observations = observations,
            Skipped = model.Skipped,
            Failure = failure,
            Message = failure switch
            {
                AbsoluteOrientationFailure.None => string.Empty,
                AbsoluteOrientationFailure.NotEnoughPoints =>
                    $"能在像对 {model.PairLabel} 的两片上都刺点的可用控制点只有 {usable} 个" +
                    $"（七参数至少要 {MinPoints} 个）。控制点必须在同一个像对的两片上都有像点，" +
                    "否则交会不出它的模型坐标。",
                _ => model.Message,
            },
            PairLabel = model.PairLabel,
            IsModelCoordinates = model.IsModelCoordinates,
        };
    }

    public static AbsoluteOrientationRun Run(Project project, StereoPair? pair)
    {
        var prep = Prepare(project, pair);
        if (!prep.Ready)
        {
            return new AbsoluteOrientationRun
            {
                Failure = prep.Failure,
                Message = prep.Message,
                Skipped = prep.Skipped,
                PairLabel = prep.PairLabel,
            };
        }

        var used = prep.Observations
            .Where(o => o.Type != GcpType.CheckPoint)
            .Select(o => o)
            .ToList();
        var checks = prep.Observations
            .Where(o => o.Type == GcpType.CheckPoint)
            .ToList();

        var solution = NativeAlgorithms.SolveAbsoluteOrientation(
            used.Select(o => o.Model).ToList(),
            used.Select(o => o.Ground).ToList());

        if (!solution.Success)
        {
            return new AbsoluteOrientationRun
            {
                Failure = AbsoluteOrientationFailure.Solver,
                Message = solution.Message,
                Skipped = prep.Skipped,
                PairLabel = prep.PairLabel,
            };
        }

        var warnings = new List<string>();

        var usedWithResiduals = new List<AbsoluteOrientationObservation>(used.Count);
        for (var k = 0; k < used.Count; k++)
        {
            var residual = k < solution.ResidualLength.Count ? solution.ResidualLength[k] : double.NaN;
            usedWithResiduals.Add(used[k] with { ResidualLength = residual });
        }

        var checksTransformed = new List<AbsoluteOrientationObservation>(checks.Count);
        foreach (var c in checks)
        {
            checksTransformed.Add(c with { Transformed = solution.Transform(c.Model) });
        }

        if (solution.MirrorSuspected)
        {
            warnings.Add("控制点数据像是镜像的（左右手系搞反）：最优的真旋转也贴不上，" +
                         "残差大且呈系统性。检查地面坐标的 X/Y 是否与工程口径一致。");
        }

        var sigma = solution.Sigma0;
        if (sigma > 0)
        {
            var over = usedWithResiduals.Count(o => o.ResidualLength > 2.0 * sigma);
            if (over > 0)
            {
                warnings.Add($"{over} 个控制点的不符值超过 2σ₀，可能是刺点粗差。");
            }
        }

        if (solution.Singular.Count >= 3 && solution.Singular[0] > 0)
        {
            var flatness = solution.Singular[2] / solution.Singular[0];
            if (flatness < 1e-3)
            {
                warnings.Add($"控制点接近共面（σ₃/σ₁ = {flatness:0.0e+0}）：能解，" +
                             "但高程方向全靠倾角外推，尺度与高程的精度会明显偏低。");
            }
        }

        return new AbsoluteOrientationRun
        {
            Failure = AbsoluteOrientationFailure.None,
            Result = solution,
            Used = usedWithResiduals,
            Checks = checksTransformed,
            Skipped = prep.Skipped,
            Warnings = warnings,
            PairLabel = prep.PairLabel,
        };
    }

}
