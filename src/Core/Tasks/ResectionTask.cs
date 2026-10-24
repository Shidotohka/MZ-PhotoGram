using System;
using System.Collections.Generic;

namespace Photogrammetry.Core.Tasks;

/// <summary>后方交会卡在哪一步。界面据此决定措辞与颜色，不靠匹配消息文本。</summary>
public enum ResectionFailure
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>还没有当前影像。</summary>
    NoImage,

    /// <summary>影像没有有效内定向，没有像平面坐标系。</summary>
    NoInterior,

    /// <summary>可用控制点不足 <see cref="NativeAlgorithms.MinControlPoints"/> 个。</summary>
    NotEnoughPoints,

    /// <summary>三点叉乘判不出地面坐标轴序（点太少、近共线或分布太窄）。</summary>
    GroundOrderUnknown,

    /// <summary>解算本身没收敛，或被残差/几何闸门拒收。<see cref="ResectionRun.Result"/> 里的值只能当诊断。</summary>
    Solver,
}

/// <summary>
/// 一次解算的<b>准备</b>结果：拿到了哪些观测、跳过了哪些点、还差什么。
/// 与解算分开，是为了让界面在按下按钮之前就能说清楚「现在能不能解」。
/// </summary>
public sealed class ResectionPreparation
{
    public required IReadOnlyList<ResectionObservation> Observations { get; init; }
    public required IReadOnlyList<string> Skipped { get; init; }
    public required ResectionFailure Failure { get; init; }

    /// <summary>失败说明。成功时为空串。</summary>
    public required string Message { get; init; }

    public int Count => Observations.Count;

    public bool Ready => Failure == ResectionFailure.None;
}

/// <summary>
/// 一次后方交会的运行结果。成功时 <see cref="Result"/> 非空，且外方位与逐点残差<b>已写回</b>影像。
/// </summary>
public sealed class ResectionRun
{
    public required ResectionFailure Failure { get; init; }

    /// <summary>解算结果。失败在 <see cref="ResectionFailure.Solver"/> 时也非空，但只能当诊断值看。</summary>
    public ResectionResult? Result { get; init; }

    /// <summary>手性判定证据。没走到这一步时为 null。</summary>
    public GroundOrderEvidence? Evidence { get; init; }

    public IReadOnlyList<ResectionObservation> Observations { get; init; } =
        Array.Empty<ResectionObservation>();

    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    /// <summary>失败说明。成功时为空串。</summary>
    public string Message { get; init; } = string.Empty;

    public bool Success => Failure == ResectionFailure.None;
}

/// <summary>
/// 后方交会的一次完整执行：收集观测 → 判手性 → 换轴 → 解算 → 写回模型。
/// <para>
/// 这条流水线以前整个住在 <c>MainWindow</c> 里，导致「解算」和「摆界面」两件事绑在一起，
/// 离屏工具想跑一次解算也只能通过窗口。现在流程在这里，窗口只负责显示。
/// </para>
/// <para>
/// 本类不引用任何界面类型，也不决定文案怎么排版——它只说「成没成、卡在哪、数值是多少」。
/// </para>
/// </summary>
public static class ResectionTask
{
    /// <summary>
    /// 收集可参与解算的观测：本影像上已刺点、有内定向、且不是检查点。
    /// </summary>
    /// <param name="image">被解算的影像。</param>
    /// <param name="gcps">工程里的全部控制点，顺序即输出顺序（点表顺序）。</param>
    public static ResectionPreparation Prepare(ImageItem image, IReadOnlyList<Gcp> gcps)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(gcps);

        var skipped = new List<string>();
        var observations = new List<ResectionObservation>();

        if (image.Interior is not { } io || !io.IsValid)
        {
            skipped.Add("当前影像还没有有效内定向");
            return new ResectionPreparation
            {
                Observations = observations,
                Skipped = skipped,
                Failure = ResectionFailure.NoInterior,
                Message = "当前影像还没有内定向结果。没有像平面坐标系就做不了后方交会。",
            };
        }

        foreach (var gcp in gcps)
        {
            if (gcp.Type == GcpType.CheckPoint) continue;

            var point = image.FindGcpPoint(gcp.Id);
            if (point is null)
            {
                skipped.Add($"{gcp.Id} 未刺点");
                continue;
            }

            var mm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(point.PixelX, point.PixelY), io, image.Width, image.Height);
            point.PositionMm = mm;
            observations.Add(new ResectionObservation(gcp.Id, mm.X, mm.Y, gcp.X, gcp.Y, gcp.Z));
        }

        var enough = observations.Count >= NativeAlgorithms.MinControlPoints;
        return new ResectionPreparation
        {
            Observations = observations,
            Skipped = skipped,
            Failure = enough ? ResectionFailure.None : ResectionFailure.NotEnoughPoints,
            Message = enough
                ? string.Empty
                : $"只有 {observations.Count} 个可用的控制点观测，后方交会至少要 " +
                  $"{NativeAlgorithms.MinControlPoints} 个。",
        };
    }

    /// <summary>
    /// 跑一次后方交会。成功时把外方位、影像状态、逐点残差写回 <paramref name="image"/>。
    /// 不碰界面、不抛异常——所有失败都通过返回值表达。
    /// </summary>
    public static ResectionRun Run(ImageItem? image, IReadOnlyList<Gcp> gcps)
    {
        if (image is null)
        {
            return new ResectionRun
            {
                Failure = ResectionFailure.NoImage,
                Message = "尚未载入影像。",
            };
        }

        var prep = Prepare(image, gcps);
        if (!prep.Ready)
        {
            return new ResectionRun
            {
                Failure = prep.Failure,
                Message = prep.Message,
                Observations = prep.Observations,
                Skipped = prep.Skipped,
            };
        }

        // 先判手性（三点叉乘，不解算），判出是左手系就换轴，然后只解一次
        var evidence = NativeAlgorithms.DetectGroundOrder(prep.Observations);
        if (!evidence.Decided)
        {
            return new ResectionRun
            {
                Failure = ResectionFailure.GroundOrderUnknown,
                Message = "判不出地面坐标轴序：" + evidence.Message,
                Evidence = evidence,
                Observations = prep.Observations,
                Skipped = prep.Skipped,
            };
        }

        // 已换到数学系（X 东、Y 北），再让自动初值与已存外方位各解一次，取残差小的，最后查残差与几何
        var result = NativeAlgorithms.SolveResectionRobust(
            NativeAlgorithms.ToMathFrame(prep.Observations, evidence.Order),
            image.Interior!.FocalLength, image.Interior.PixelSizeMm, image.Exterior);

        if (!result.Success)
        {
            return new ResectionRun
            {
                Failure = ResectionFailure.Solver,
                Message = result.Message,
                Result = result,
                Evidence = evidence,
                Observations = prep.Observations,
                Skipped = prep.Skipped,
            };
        }

        Apply(image, result, evidence.Order);

        return new ResectionRun
        {
            Failure = ResectionFailure.None,
            Result = result,
            Evidence = evidence,
            Observations = prep.Observations,
            Skipped = prep.Skipped,
        };
    }

    /// <summary>把解算结果写回模型：外方位、影像状态、地面轴序、逐点残差与像平面坐标。</summary>
    private static void Apply(ImageItem image, ResectionResult result, GroundAxisOrder order)
    {
        image.Exterior = result.ToExterior();
        image.Status = ImageStatus.Oriented;
        image.GroundOrder = order;

        foreach (var residual in result.Residuals)
        {
            var point = image.FindGcpPoint(residual.GcpId);
            if (point is null) continue;

            point.ResidualMm = Math.Sqrt(residual.VxMm * residual.VxMm + residual.VyMm * residual.VyMm);
            point.PositionMm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(point.PixelX, point.PixelY), image.Interior!, image.Width, image.Height);
        }
    }
}
