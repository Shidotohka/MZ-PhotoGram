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

    /// <summary>
    /// 地面坐标的轴序，由模型交会顺手判出（三点叉乘，与后方交会同一个判据）。
    /// <see cref="GroundAxisOrder.NorthEast"/> 表示文件是左手系，解算前必须换轴。
    /// </summary>
    public GroundAxisOrder GroundOrder { get; init; } = GroundAxisOrder.EastNorth;

    /// <summary>判手性的证据，供界面/报告引用；控制点不足 3 个时为空。</summary>
    public GroundOrderEvidence? Evidence { get; init; }

    /// <summary>像对的主距与像元尺寸，用来把地面上的「米」折算成「像元」。缺内方位时为 NaN。</summary>
    public double FocalLengthMm { get; init; } = double.NaN;

    public double PixelSizeMm { get; init; } = double.NaN;

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

    /// <summary>
    /// 解算时采用的地面轴序。<see cref="GroundAxisOrder.NorthEast"/> 表示文件是左手系、
    /// 解算前已把 X/Y 换回东-北；<see cref="AbsoluteOrientationObservation.Ground"/> 里仍是文件原值。
    /// </summary>
    public GroundAxisOrder GroundOrder { get; init; } = GroundAxisOrder.EastNorth;

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
                // 变换结果在数学系，文件里的地面坐标要先换成同一口径再比
                var g = GroundAxes.ToMathFrame(c.Ground, GroundOrder);
                var d = Math.Sqrt((t.X - g.X) * (t.X - g.X) + (t.Y - g.Y) * (t.Y - g.Y));
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
                // 换轴只动 X/Y，Z 不变，所以这里不用换
                var d = Math.Abs(t.Z - c.Ground.Z);
                if (double.IsNaN(worst) || d > worst) worst = d;
            }
            return worst;
        }
    }

    /// <summary>
    /// 控制点不符值的<b>平面</b>分量 RMS：$\sqrt{\sum(\Delta x^2+\Delta y^2)/n}$，与地面坐标同单位。
    ///
    /// <para>为什么必须单列：交会角小时高程误差被 $H/B$ 放大，平面和高程能差一个量级。
    /// 只报三维合量（<see cref="AbsoluteOrientationResult.Rms"/>）等于把这件事埋掉——
    /// 一个 2.4 m 的合量看起来像"整片都不准"，拆开才发现平面只有 0.4 m。</para>
    /// </summary>
    public double RmsXy { get; init; } = double.NaN;

    /// <summary>控制点不符值的高程分量 RMS：$\sqrt{\sum\Delta z^2/n}$。</summary>
    public double RmsZ { get; init; } = double.NaN;

    /// <summary>
    /// 地面采样间隔，m/px：$H\delta/f$，其中 $H=\lambda\,|Z_{\text{model}}|$（模型以基线为单位，
    /// 所以 $|Z|$ 就是"景深有几个基线"）。缺内方位时为 NaN。
    /// </summary>
    public double GroundSampleDistance { get; init; } = double.NaN;

    /// <summary>
    /// 深度放大倍数 $H/B$。模型坐标以基线为单位时它就等于平均 $|Z_{\text{model}}|$。
    /// 平面误差乘上它才是高程误差——这是小交会角像对的"罪证数字"。
    /// </summary>
    public double DepthAmplification { get; init; } = double.NaN;
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
            GroundOrder = model.GroundOrder,
            Evidence = model.Evidence,
            FocalLengthMm = model.FocalLengthMm,
            PixelSizeMm = model.PixelSizeMm,
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

        // 地面坐标先换到数学系（X 东、Y 北）再解。左手系文件在这一步换掉，与后方交会同一条规矩；
        // 判断已经由模型交会的三点叉乘给出，所以这里不试解、不比较残差。
        var groundFrame = used
            .Select(o => GroundAxes.ToMathFrame(o.Ground, prep.GroundOrder))
            .ToList();

        var solution = NativeAlgorithms.SolveAbsoluteOrientation(
            used.Select(o => o.Model).ToList(),
            groundFrame);

        if (!solution.Success)
        {
            return new AbsoluteOrientationRun
            {
                Failure = AbsoluteOrientationFailure.Solver,
                Message = solution.Message,
                Skipped = prep.Skipped,
                PairLabel = prep.PairLabel,
                GroundOrder = prep.GroundOrder,
            };
        }

        var warnings = new List<string>();

        if (prep.GroundOrder == GroundAxisOrder.NorthEast)
        {
            warnings.Add("地面文件是左手系（X 北、Y 东）：解算前已把 X/Y 换回东-北（右手系），" +
                         "结果里的地面坐标按东-北口径。");
        }

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

        // 平面与高程分开统计：两者的精度差一个 H/B，合成一个数就把差异藏起来了。
        // 逐轴不符值直接从 Transform 里取，不必再下到 ABI 去要 residual_xyz。
        double sumXy = 0.0, sumZ = 0.0;
        foreach (var o in used)
        {
            var t = solution.Transform(o.Model);
            var g = GroundAxes.ToMathFrame(o.Ground, prep.GroundOrder);
            var dx = t.X - g.X;
            var dy = t.Y - g.Y;
            sumXy += dx * dx + dy * dy;
            sumZ += (t.Z - g.Z) * (t.Z - g.Z);
        }

        var rmsXy = used.Count > 0 ? Math.Sqrt(sumXy / used.Count) : double.NaN;
        var rmsZ = used.Count > 0 ? Math.Sqrt(sumZ / used.Count) : double.NaN;

        // 模型以基线为单位，所以 |Z_model| 直接就是"景深有几个基线"，也就是 H/B
        var depth = used.Count > 0 ? used.Average(o => Math.Abs(o.Model.Z)) : double.NaN;
        var gsd = !double.IsNaN(depth) && prep.FocalLengthMm > 0 && prep.PixelSizeMm > 0
            ? solution.Scale * depth * prep.PixelSizeMm / prep.FocalLengthMm
            : double.NaN;

        if (solution.MirrorSuspected)
        {
            warnings.Add(prep.Evidence is { Decided: true }
                ? "七参数仍怀疑镜像：轴序已经按三点叉乘核过并换过轴，所以更可能是控制点号与地面坐标配错，" +
                  "或某个点的地面坐标有粗差。"
                : "控制点数据像是镜像的（左右手系搞反）：最优的真旋转也贴不上，" +
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
            GroundOrder = prep.GroundOrder,
            RmsXy = rmsXy,
            RmsZ = rmsZ,
            GroundSampleDistance = gsd,
            DepthAmplification = depth,
        };
    }

    /// <summary>
    /// 入账：把七参数、质量指标与逐点不符值写进像对。失败什么都不做。
    ///
    /// <para><b>解算本身不写工程</b>（[工程文件格式 §9.6](../../Docs/工程文件格式.md)）：
    /// 能不能算由闸门判，写不写由操作员按「接受」判。</para>
    ///
    /// <para>逐点不符值在这里<b>按平面与高程拆开</b>算（不重算三维合量）：$H/B$ 放大的是高程，
    /// 拆开才看得出问题在哪一头。文件里的地面坐标要先换成数学系再比——换轴只动 X/Y，Z 不变。</para>
    /// </summary>
    public static void Commit(StereoPair pair, AbsoluteOrientationRun run)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(run);
        if (!run.Success || run.Result is not { } result) return;

        var solution = new AbsoluteSolution
        {
            Lambda = result.Scale,
            Phi = result.PhiDeg,
            Omega = result.OmegaDeg,
            Kappa = result.KappaDeg,
            Dx = result.X0,
            Dy = result.Y0,
            Dz = result.Z0,
            GroundOrder = run.GroundOrder,
            Sigma0 = result.Sigma0,
            Gsd = run.GroundSampleDistance,
            HbRatio = run.DepthAmplification,
            TimeUtc = DateTimeOffset.UtcNow,
        };

        foreach (var o in run.Used)
        {
            AddDeviation(solution, o.GcpId, result.Transform(o.Model), o.Ground, run.GroundOrder, false);
        }

        foreach (var c in run.Checks)
        {
            // 检查点的"变换后位置"在解算时已经算好，直接用，别拿七参数再算一遍
            var transformed = c.Transformed ?? result.Transform(c.Model);
            AddDeviation(solution, c.GcpId, transformed, c.Ground, run.GroundOrder, true);
        }

        pair.Absolute = solution;
    }

    private static void AddDeviation(AbsoluteSolution solution, string gcpId, GroundPointM transformed,
        GroundPointM groundInFile, GroundAxisOrder order, bool isCheck)
    {
        var ground = GroundAxes.ToMathFrame(groundInFile, order);
        var dx = transformed.X - ground.X;
        var dy = transformed.Y - ground.Y;

        solution.Points.Add(new AbsoluteSolutionPoint
        {
            GcpId = gcpId,
            Dxy = Math.Sqrt(dx * dx + dy * dy),
            Dz = transformed.Z - ground.Z,
            Check = isCheck,
        });
    }
}
