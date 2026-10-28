using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Photogrammetry.Core.Tasks;

/// <summary>相对定向卡在哪一步。界面据此决定措辞与颜色，不靠匹配消息文本。</summary>
public enum RelativeFailure
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>左窗没有影像。</summary>
    NoLeftImage,

    /// <summary>右窗没有影像。</summary>
    NoRightImage,

    /// <summary>两个窗里是同一张影像，没有基线。</summary>
    SameImage,

    /// <summary>某一张没有内定向结果，没有像平面坐标系。</summary>
    NoInterior,

    /// <summary>两张片焦距不同——不是同一个相机。</summary>
    MismatchedFocalLength,

    /// <summary>两片上都有的同名点对不足 <see cref="NativeAlgorithms.MinRelativePairs"/> 对。</summary>
    NotEnoughTiePoints,

    /// <summary>解算本身失败：几何退化、不收敛、法方程秩亏。<see cref="RelativeRun.Result"/> 里的值只能当诊断。</summary>
    Solver,
}

/// <summary>一对同名像点：两片各一个观测，加上它们共同的点号。</summary>
public sealed record RelativePairObservation(string TiePointId, ImagePoint Left, ImagePoint Right);

/// <summary>
/// 一次相对定向的<b>准备</b>结果：攒到了哪些点对、跳过了哪些、还差什么。
/// 与解算分开，是为了让界面在按下按钮之前就能说清楚「现在能不能解」。
/// </summary>
public sealed class RelativePreparation
{
    public required IReadOnlyList<RelativePairObservation> Pairs { get; init; }

    /// <summary>没参与解算的点号与原因，一条一行，直接进界面。</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    public required RelativeFailure Failure { get; init; }

    /// <summary>失败说明。成功时为空串。</summary>
    public required string Message { get; init; }

    public int Count => Pairs.Count;

    public bool Ready => Failure == RelativeFailure.None;
}

/// <summary>
/// 一次相对定向的运行结果。成功时 <see cref="Result"/> 非空，且相对定向元素与逐点上下视差
/// <b>已写回</b>工程（像对与同名点）。
/// </summary>
public sealed class RelativeRun
{
    public required RelativeFailure Failure { get; init; }

    /// <summary>解算结果。失败在 <see cref="RelativeFailure.Solver"/> 时也可能非空，但只能当诊断值看。</summary>
    public RelativeOrientationResult? Result { get; init; }

    public ImageItem? Left { get; init; }
    public ImageItem? Right { get; init; }

    /// <summary>参与解算的点对，顺序与 <see cref="RelativeOrientationResult.ResidualQ"/> 一致。</summary>
    public IReadOnlyList<RelativePairObservation> Pairs { get; init; } =
        Array.Empty<RelativePairObservation>();

    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 解出来了但值得提醒的事：粗差点被剔除、上下视差偏大、法方程病态、基线主分量不是 b_x。
    /// 这些不阻止出结果，但会直接影响能不能用。
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>失败说明。成功时为空串。</summary>
    public string Message { get; init; } = string.Empty;

    public bool Success => Failure == RelativeFailure.None && Result is not null;
}

/// <summary>
/// 相对定向的一次完整执行：攒点对 → 过闸门 → 解共面条件 → 写回工程。
///
/// <para>与 <see cref="ResectionTask"/>、<see cref="IntersectionTask"/> 同一个形状：不碰界面、
/// 不抛异常，失败都从返回值表达；成功时结果已经落进工程，不需要调用方再搬一遍。</para>
///
/// <para><b>前置条件</b>：两张片都要有内定向（像素 → 像平面）。<b>不需要</b>外方位——
/// 这正是相对定向的意义：它不依赖任何地面坐标，约束全部来自内部几何（同名光线对对相交）。</para>
///
/// <para>点对来自工程的同名点表：一个同名点在左片、右片各有一个观测，才算一对。
/// 只在一边有观测的（点了一半就停了）会进 <see cref="RelativePreparation.Skipped"/>，
/// 不参与解算，也不影响其余点对。</para>
/// </summary>
public static class RelativeOrientationTask
{
    /// <summary>
    /// 像点坐标的先验中误差，mm。内定向在真实航摄数据上 σ₀ 是 0.012–0.020 mm，取上限。
    /// 这个值只用于粗差门槛，不影响解算结果本身。
    /// </summary>
    public const double DefaultImageSigmaMm = 0.02;

    /// <summary>上下视差中误差超过「先验中误差 × 这个倍数」就提醒。</summary>
    public const double LargeParallaxFactor = 5.0;

    /// <summary>法方程条件数超过这个值就提醒：几何太弱，元素不可靠。</summary>
    public const double LargeCondition = 1e6;

    /// <summary>
    /// 收集可参与解算的点对：同名点在两张片上各有一个观测。
    /// 这里顺手把像平面坐标按当前内定向重算一遍——像点可能在没做内定向时就点下了，
    /// 那时存的 (0,0) 是没意义的默认值。
    /// </summary>
    public static RelativePreparation Prepare(ImageItem? left, ImageItem? right, Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var skipped = new List<string>();
        var pairs = new List<RelativePairObservation>();

        if (left is null) return Fail(skipped, RelativeFailure.NoLeftImage, "左窗还没有影像。");
        if (right is null) return Fail(skipped, RelativeFailure.NoRightImage, "右窗还没有影像。");

        if (ReferenceEquals(left, right) || left.Id == right.Id)
        {
            return Fail(skipped, RelativeFailure.SameImage,
                $"两个窗里是同一张影像「{left.Id}」。相对定向要两张不同的片。");
        }

        if (left.Interior is not { } li || !li.IsValid)
        {
            return Fail(skipped, RelativeFailure.NoInterior, $"左片「{left.Id}」还没有内定向结果。");
        }

        if (right.Interior is not { } ri || !ri.IsValid)
        {
            return Fail(skipped, RelativeFailure.NoInterior, $"右片「{right.Id}」还没有内定向结果。");
        }

        if (Math.Abs(li.FocalLength - ri.FocalLength) > 1e-6)
        {
            return Fail(skipped, RelativeFailure.MismatchedFocalLength,
                $"两张片的焦距不同（左 {li.FocalLength:0.###} mm、右 {ri.FocalLength:0.###} mm）。" +
                "相对定向要求两张片属于同一个相机。");
        }

        foreach (var tie in project.TiePoints)
        {
            var members = project.TieMembers(tie.Id);
            var mine = members.FirstOrDefault(m => m.Image.Id == left.Id);
            var theirs = members.FirstOrDefault(m => m.Image.Id == right.Id);

            if (mine.Image is null && theirs.Image is null) continue;   // 与本像对无关，不念叨

            if (mine.Image is null)
            {
                skipped.Add($"{tie.Id} 只在「{right.Id}」上有观测，缺左片");
                continue;
            }

            if (theirs.Image is null)
            {
                skipped.Add($"{tie.Id} 只在「{left.Id}」上有观测，缺右片");
                continue;
            }

            // 反算一遍：真实数据里内定向可能是在点完像点之后才做的
            mine.Point.PositionMm = ToMm(mine.Point, left, li);
            theirs.Point.PositionMm = ToMm(theirs.Point, right, ri);

            pairs.Add(new RelativePairObservation(tie.Id, mine.Point, theirs.Point));
        }

        if (pairs.Count < NativeAlgorithms.MinRelativePairs)
        {
            return new RelativePreparation
            {
                Pairs = pairs,
                Skipped = skipped,
                Failure = RelativeFailure.NotEnoughTiePoints,
                Message = pairs.Count == 0
                    ? $"两张片上还没有成对的同名点。相对定向至少要 {NativeAlgorithms.MinRelativePairs} 对。"
                    : $"两片上都有的同名点只有 {pairs.Count} 对，相对定向至少要 " +
                      $"{NativeAlgorithms.MinRelativePairs} 对（5 个未知数 + 1 个多余观测）。" +
                      (skipped.Count > 0 ? "只在一边有观测的点不算。" : ""),
            };
        }

        return new RelativePreparation
        {
            Pairs = pairs,
            Skipped = skipped,
            Failure = RelativeFailure.None,
            Message = string.Empty,
        };
    }

    /// <summary>
    /// 跑一次相对定向。成功时把相对定向元素写进像对、把上下视差与是否剔除写进同名点。
    /// </summary>
    public static RelativeRun Run(Project project, ImageItem? left, ImageItem? right,
        double imageSigmaMm = DefaultImageSigmaMm)
    {
        var prep = Prepare(left, right, project);
        if (!prep.Ready || left is null || right is null)
        {
            return new RelativeRun
            {
                Failure = prep.Failure,
                Message = prep.Message,
                Left = left,
                Right = right,
                Pairs = prep.Pairs,
                Skipped = prep.Skipped,
            };
        }

        var lmm = prep.Pairs.Select(p => p.Left.PositionMm).ToList();
        var rmm = prep.Pairs.Select(p => p.Right.PositionMm).ToList();

        var result = NativeAlgorithms.SolveRelativeOrientation(
            lmm, rmm, left.Interior!.FocalLength, imageSigmaMm);

        if (!result.Success)
        {
            return new RelativeRun
            {
                Failure = RelativeFailure.Solver,
                Message = result.Message,
                Result = result,
                Left = left,
                Right = right,
                Pairs = prep.Pairs,
                Skipped = prep.Skipped,
            };
        }

        Apply(project, left, right, prep.Pairs, result);

        return new RelativeRun
        {
            Failure = RelativeFailure.None,
            Result = result,
            Left = left,
            Right = right,
            Pairs = prep.Pairs,
            Skipped = prep.Skipped,
            Warnings = Warnings(prep.Pairs, result, imageSigmaMm),
        };
    }

    /// <summary>
    /// 写回工程：像对的五个元素 + 每个同名点的 q 与「是否被剔除」。
    /// 没参与解算的点会把上一次的 q 清掉——那份 q 是上一批点解出来的，留着只会误导。
    /// </summary>
    private static void Apply(Project project, ImageItem left, ImageItem right,
        IReadOnlyList<RelativePairObservation> pairs, RelativeOrientationResult result)
    {
        var pair = project.GetOrCreatePair(left.Id, right.Id);

        pair.Relative = new RelativeOrientation
        {
            // 像对的方向由工程记着，可能与本次入参对调。元素属于哪一侧由这个字段说清楚，
            // 不靠「像对的左片就是基准」这种约定——约定一旦被反着用，元素就整体反了。
            ReferenceImageId = left.Id,
            Bx = result.Bx,
            By = result.By,
            Bz = result.Bz,
            Phi2 = result.PhiDeg,
            Omega2 = result.OmegaDeg,
            Kappa2 = result.KappaDeg,
            Gauge = result.Gauge,
            Iterations = result.Iterations,
            UsedCount = result.UsedCount,
            RejectedCount = result.RejectedCount,
            FocalLength = left.Interior!.FocalLength,
            Sigma0Mm = result.Sigma0Mm,
            RmsQ = result.QRmsMm,
            MaxQ = result.QMaxAbsMm,
            Condition = result.Condition,
        };

        for (var k = 0; k < pairs.Count; k++)
        {
            var tie = project.FindTiePoint(pairs[k].TiePointId);
            if (tie is null) continue;

            tie.Q = k < result.ResidualQ.Count ? result.ResidualQ[k] : null;
            tie.Rejected = k < result.Used.Count && !result.Used[k];
        }
    }

    /// <summary>解出来了但可能不能用的几种情况。不阻止出结果，只是必须让人看见。</summary>
    private static List<string> Warnings(IReadOnlyList<RelativePairObservation> pairs,
        RelativeOrientationResult result, double imageSigmaMm)
    {
        var list = new List<string>();

        var rejected = new List<string>();
        for (var k = 0; k < pairs.Count && k < result.Used.Count; k++)
            if (!result.Used[k]) rejected.Add(pairs[k].TiePointId);

        if (rejected.Count > 0)
        {
            list.Add($"{rejected.Count} 个点被判为粗差、没参与解算：" +
                     string.Join("、", rejected) +
                     $"。它们在点表里仍留着（q 照给），修好点位后重新解一次就会回到解里。");
        }

        // 门槛：有先验中误差就按倍数，没有就按绝对量（无噪声数据 σ₀ = 0，不能用 0 当门槛）
        var threshold = imageSigmaMm > 0 ? imageSigmaMm * LargeParallaxFactor : 0.10;
        if (result.QRmsMm > threshold)
        {
            list.Add($"上下视差中误差 {result.QRmsMm:0.0000} mm，超过 {threshold:0.0000} mm。" +
                     "同名点没点准、或者像对里有几张片其实不是同一个相机。");
        }

        if (result.Condition > LargeCondition)
        {
            list.Add($"法方程条件数 {result.Condition:0.0E+0} 偏大（超过 {LargeCondition:0.0E+0}）。" +
                     "点位集中在一小块、或基线太短，五个元素里有几个其实解不准。");
        }

        if (result.Gauge != 0)
        {
            var name = result.Gauge switch { 1 => "b_y", 2 => "b_z", _ => "b_x" };
            list.Add($"本次冻结的基线分量是 {name}（不是航向像对的 b_x）：像对的基线主要不在 x 方向。" +
                     "教程里「b_x = 1」的写法在这里不适用，面板上给的是单位基线方向。");
        }

        return list;
    }

    private static ImagePointMm ToMm(ImagePoint point, ImageItem image, InteriorOrientation io) =>
        CoordinateConverter.PixelToImagePlane(
            new ImagePixel(point.PixelX, point.PixelY), io, image.Width, image.Height);

    private static RelativePreparation Fail(List<string> skipped, RelativeFailure failure, string message) =>
        new()
        {
            Pairs = Array.Empty<RelativePairObservation>(),
            Skipped = skipped,
            Failure = failure,
            Message = message,
        };

    /// <summary>供界面显示的一句话摘要。放在流程层，免得界面各处自己拼。</summary>
    public static string Describe(RelativeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Result is not { } r) return run.Message;

        var gauge = r.Gauge switch { 1 => "b_y", 2 => "b_z", _ => "b_x" };
        return $"{r.Iterations} 轮　q 中误差 {r.QRmsMm:0.0000} mm　σ₀ {r.Sigma0Mm:0.0000} mm　" +
               $"{r.UsedCount}/{run.Pairs.Count} 对（剔除 {r.RejectedCount}）　冻结 {gauge}";
    }
}
