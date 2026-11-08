using System;
using System.Collections.Generic;

namespace Photogrammetry.Core.Tasks;

/// <summary>
/// 任务结果 → 工程里那一块结果（`images[].interior / resection`、`pairs[].relative / model / absolute`）。
///
/// <para><b>为什么要有这一层。</b>结果有两个来路，最后都得是同一张表：
/// ① 刚解完——手里是任务返回的 <c>*Run</c> 对象；② 重开工程——手里是 <c>.pht</c> 里的结果块。
/// 如果两边各自往表格里填数，就会有两套排版，迟早一处改了另一处忘了。
/// 所以先统一成"结果块"（纯数值），再让界面只认结果块这一种输入。</para>
///
/// <para>这里的方法<b>只做映射</b>：不碰工程、不写盘、不排版。写进工程由调用方决定
/// （见[工程文件格式 §9.6](../../Docs/工程文件格式.md)：任务不写工程，接受才写）。</para>
/// </summary>
public static class ResultBlocks
{
    /// <summary>框标内定向：六系数之外的那些数（反算内参 + 评价 + 逐框标残差）。</summary>
    public static InteriorQuality Interior(FiducialOrientationResult r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var quality = new InteriorQuality
        {
            SxMm = r.SxMm,
            SyMm = r.SyMm,
            PixelSizeMm = r.PixelSizeMm,
            X0Mm = r.X0Mm,
            Y0Mm = r.Y0Mm,
            PrincipalI = r.PrincipalI,
            PrincipalJ = r.PrincipalJ,
            ThetaIDeg = r.ThetaIDeg,
            ThetaJDeg = r.ThetaJDeg,
            Sigma0Mm = r.Sigma0Mm,
            MaxAbsResidualMm = r.MaxAbsResidualMm,
            Redundancy = r.Redundancy,
        };

        foreach (var v in r.Residuals) quality.Residuals.Add(new FiducialResidualValue(v.Id, v.VxMm, v.VyMm));
        return quality;
    }

    /// <summary>框标内定向的六系数。</summary>
    public static AffineInterior Affine(FiducialOrientationResult r) => new()
    {
        A0 = r.A0,
        A1 = r.A1,
        A2 = r.A2,
        B0 = r.B0,
        B1 = r.B1,
        B2 = r.B2,
    };

    /// <summary>后方交会：质量指标 + 逐点残差。外方位不在这里（它是输入的一部分，另由 Commit 写）。</summary>
    public static ResectionQuality Resection(ResectionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Result is not { } result) throw new ArgumentException("没有解算结果", nameof(run));

        var quality = new ResectionQuality
        {
            Sigma0Mm = result.Sigma0Mm,
            ParamSd = new[]
            {
                result.MXs, result.MYs, result.MZs,
                result.MPhi, result.MOmega, result.MKappa,
            },
            Condition = result.Condition,
            Iterations = result.Iterations,
            UsedCount = result.UsedCount,
            Redundancy = result.Redundancy,
            MaxAbsResidualMm = result.MaxAbsResidualMm,
            RejectedCount = run.Skipped.Count,
            TimeUtc = DateTimeOffset.UtcNow,
        };

        foreach (var v in result.Residuals) quality.Residuals.Add(new ResectionResidualValue(v.GcpId, v.VxMm, v.VyMm));
        return quality;
    }

    /// <summary>模型交会：每个控制点一个模型坐标 + 交会角。</summary>
    public static ModelSolution Model(ModelIntersectionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var solution = new ModelSolution { TimeUtc = DateTimeOffset.UtcNow };
        foreach (var o in outcome.Observations)
        {
            solution.Points.Add(new ModelSolutionPoint
            {
                GcpId = o.GcpId,
                X = o.Model.X,
                Y = o.Model.Y,
                Z = o.Model.Z,
                ConvergenceAngleDeg = o.ConvergenceAngleDeg,
            });
        }

        return solution;
    }

    /// <summary>
    /// 绝对定向：七参数 + 质量 + 逐点不符值。
    ///
    /// <para>逐点在<b>数学系</b>里比（文件里的地面坐标先换轴）——换轴只动 X/Y，Z 不变；
    /// 平面与高程分开算，不存三维合量：$H/B$ 放大的是高程，拆开才看得出问题在哪一头。</para>
    /// </summary>
    public static AbsoluteSolution Absolute(AbsoluteOrientationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Result is not { } result) throw new ArgumentException("没有解算结果", nameof(run));

        var singular = result.Singular;
        var flatness = singular.Count >= 3 && singular[0] > 0
            ? singular[2] / singular[0]
            : double.NaN;

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
            Rms3D = result.Rms,
            MaxAbsResidual = result.MaxAbsResidual,
            Redundancy = result.Redundancy,
            UsedCount = run.Used.Count,
            CheckCount = run.Checks.Count,
            Flatness = flatness,
            MirrorSuspected = result.MirrorSuspected,
            CheckPlanimetricMax = run.CheckPlanimetricMax,
            CheckHeightMax = run.CheckHeightMax,
            Gsd = run.GroundSampleDistance,
            HbRatio = run.DepthAmplification,
            TimeUtc = DateTimeOffset.UtcNow,
        };

        foreach (var o in run.Used)
        {
            Add(solution, o.GcpId, result.Transform(o.Model), o.Ground, run.GroundOrder, false);
        }

        foreach (var c in run.Checks)
        {
            // 检查点的"变换后位置"解算时已经算好，直接用，别拿七参数再算一遍
            var transformed = c.Transformed ?? result.Transform(c.Model);
            Add(solution, c.GcpId, transformed, c.Ground, run.GroundOrder, true);
        }

        return solution;
    }

    private static void Add(AbsoluteSolution solution, string gcpId, GroundPointM transformed,
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
