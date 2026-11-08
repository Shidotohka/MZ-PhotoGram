using System;
using System.Collections.Generic;
using System.Linq;

namespace Photogrammetry.Core.Tasks;

/// <summary>
/// 把工程里的**结果块**还原成"任务结果对象"（<c>*Run</c> / <c>*Outcome</c>），
/// 好让界面用**同一套排表代码**显示——解算后与重开工程后，表格只有一条生成路径。
///
/// <para><b>为什么不在界面里另写一套"从结果块排表"。</b>那样就有两份格式化、两处小数位、
/// 两套"哪个点偏大"的判断；改一处另一处迟早忘。这里做一次搬运，界面照旧认任务结果对象。</para>
///
/// <para><b>搬运的边界</b>：结果块是"某一次解"的存档，所以凡是解算时算出来的数都以块为准
/// （包括 σ₀、条件数、逐点残差）；只有**能从块里直接算出来**的量才现算（如不符值的 RMS、
/// 多余观测数）。算不出来的（如镜像嫌疑、点位扁平度）也确实存在块里——存不了就只能丢，
/// 那会让重开后的表格少一行，所以宁可存。</para>
///
/// <para>还原出来的对象<b>不是"刚解算完"</b>：状态由调用方标成已入账，
/// <c>OnAccept</c> 留空——它已经进过工程了，不需要再入账一次。</para>
/// </summary>
public static class StoredResults
{
    /// <summary>框标内定向：六系数 + 质量在影像上，还原成原生库那份结果对象（给排表用）。</summary>
    public static FiducialOrientationResult? Interior(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Affine is not { } affine || image.InteriorQuality is not { } quality) return null;

        return new FiducialOrientationResult
        {
            Success = true,
            A0 = affine.A0,
            A1 = affine.A1,
            A2 = affine.A2,
            B0 = affine.B0,
            B1 = affine.B1,
            B2 = affine.B2,
            SxMm = quality.SxMm,
            SyMm = quality.SyMm,
            PixelSizeMm = quality.PixelSizeMm,
            X0Mm = quality.X0Mm,
            Y0Mm = quality.Y0Mm,
            PrincipalI = quality.PrincipalI,
            PrincipalJ = quality.PrincipalJ,
            ThetaIDeg = quality.ThetaIDeg,
            ThetaJDeg = quality.ThetaJDeg,
            Sigma0Mm = quality.Sigma0Mm,
            MaxAbsResidualMm = quality.MaxAbsResidualMm,
            UsedCount = quality.Residuals.Count,
            Redundancy = quality.Redundancy,
            Residuals = quality.Residuals
                .Select(v => new FiducialResidual(v.FiducialId, v.VxMm, v.VyMm))
                .ToList(),
        };
    }

    /// <summary>后方交会：外方位在影像上，质量与逐点残差在 <c>resection</c> 块里。</summary>
    public static ResectionRun? Resection(ImageItem image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Exterior is not { } exterior || image.Resection is not { } quality) return null;

        var sd = quality.ParamSd;

        var result = new ResectionResult
        {
            Success = true,
            Xs = exterior.Xs,
            Ys = exterior.Ys,
            Zs = exterior.Zs,
            PhiDeg = exterior.Phi,
            OmegaDeg = exterior.Omega,
            KappaDeg = exterior.Kappa,
            Iterations = quality.Iterations,
            UsedCount = quality.UsedCount,
            Redundancy = quality.Redundancy,
            Sigma0Mm = quality.Sigma0Mm,
            MaxAbsResidualMm = quality.MaxAbsResidualMm,
            MXs = sd.Length > 0 ? sd[0] : double.NaN,
            MYs = sd.Length > 1 ? sd[1] : double.NaN,
            MZs = sd.Length > 2 ? sd[2] : double.NaN,
            MPhi = sd.Length > 3 ? sd[3] : double.NaN,
            MOmega = sd.Length > 4 ? sd[4] : double.NaN,
            MKappa = sd.Length > 5 ? sd[5] : double.NaN,
            Condition = quality.Condition,
            Residuals = quality.Residuals
                .Select(v => new ResectionResidual(v.GcpId, v.VxMm, v.VyMm))
                .ToList(),
        };

        return new ResectionRun
        {
            Failure = ResectionFailure.None,
            Result = result,
            Observations = Array.Empty<ResectionObservation>(),
        };
    }

    /// <summary>相对定向：五个元素在 <c>pairs[].relative</c>，逐点 q 与剔除标记在同名点上。</summary>
    public static RelativeRun? Relative(Project project, StereoPair pair)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.Relative is not { } relative) return null;

        var left = project.Images.FirstOrDefault(i => i.Id == pair.LeftImageId);
        var right = project.Images.FirstOrDefault(i => i.Id == pair.RightImageId);
        if (left is null || right is null) return null;

        var pairs = new List<RelativePairObservation>();
        var residuals = new List<double>();
        var used = new List<bool>();

        foreach (var tie in project.TiePoints)
        {
            var l = left.Points.FirstOrDefault(p => p.BoundTiePointId == tie.Id);
            var r = right.Points.FirstOrDefault(p => p.BoundTiePointId == tie.Id);
            if (l is null || r is null) continue;

            pairs.Add(new RelativePairObservation(tie.Id, l, r));
            residuals.Add(tie.Q ?? double.NaN);
            used.Add(!tie.Rejected);
        }

        var result = new RelativeOrientationResult
        {
            Success = true,
            Bx = relative.Bx,
            By = relative.By,
            Bz = relative.Bz,
            PhiDeg = relative.Phi2,
            OmegaDeg = relative.Omega2,
            KappaDeg = relative.Kappa2,
            Gauge = relative.Gauge,
            Iterations = relative.Iterations,
            UsedCount = relative.UsedCount,
            RejectedCount = relative.RejectedCount,
            Sigma0Mm = relative.Sigma0Mm,
            QRmsMm = relative.RmsQ,
            QMaxAbsMm = relative.MaxQ,
            Condition = relative.Condition,
            ResidualQ = residuals,
            Used = used,
        };

        return new RelativeRun
        {
            Failure = RelativeFailure.None,
            Result = result,
            Left = left,
            Right = right,
            Pairs = pairs,
        };
    }

    /// <summary>模型交会：模型坐标在 <c>pairs[].model</c>，地面坐标与像点从工程里取。</summary>
    public static ModelIntersectionOutcome? Model(Project project, StereoPair pair)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.Model is not { } model) return null;

        var label = $"{pair.LeftImageId} × {pair.RightImageId}";
        var left = project.Images.FirstOrDefault(i => i.Id == pair.LeftImageId);
        var right = project.Images.FirstOrDefault(i => i.Id == pair.RightImageId);
        if (left is null || right is null) return null;

        var observations = new List<ModelPointObservation>();
        foreach (var point in model.Points)
        {
            var gcp = project.FindGcp(point.GcpId);
            if (gcp is null) continue;

            var l = left.Points.FirstOrDefault(p => p.BoundGcpId == point.GcpId);
            var r = right.Points.FirstOrDefault(p => p.BoundGcpId == point.GcpId);
            if (l is null || r is null) continue;

            observations.Add(new ModelPointObservation(
                point.GcpId,
                new GroundPointM(point.X, point.Y, point.Z),
                new GroundPointM(gcp.X, gcp.Y, gcp.Z),
                gcp.Type,
                l.PositionMm,
                r.PositionMm,
                point.ConvergenceAngleDeg));
        }

        return new ModelIntersectionOutcome
        {
            Failure = ModelIntersectionFailure.None,
            Observations = observations,
            PairLabel = label,
        };
    }

    /// <summary>
    /// 绝对定向：七参数与逐点不符值在 <c>pairs[].absolute</c>。
    ///
    /// <para>两处现算：$H/B$ 与 GSD 存的是块里的值（不再由内方位推），
    /// 而不符值的平面/高程 RMS 由逐点 $d_{xy}$、$d_z$ 按解算时的同一公式重算——
    /// 它们与逐点值同源，重算不会引入不一致。</para>
    /// </summary>
    public static AbsoluteOrientationRun? Absolute(Project project, StereoPair pair)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.Absolute is not { } absolute) return null;

        var label = $"{pair.LeftImageId} × {pair.RightImageId}";
        var model = pair.Model?.Points.ToDictionary(p => p.GcpId, p => p);

        var used = new List<AbsoluteOrientationObservation>();
        var checks = new List<AbsoluteOrientationObservation>();

        foreach (var point in absolute.Points)
        {
            var gcp = project.FindGcp(point.GcpId);
            if (gcp is null) continue;

            var ground = new GroundPointM(gcp.X, gcp.Y, gcp.Z);
            var modelPoint = model is not null && model.TryGetValue(point.GcpId, out var mp)
                ? new GroundPointM(mp.X, mp.Y, mp.Z)
                : default;

            var observation = new AbsoluteOrientationObservation(point.GcpId, modelPoint, ground, gcp.Type)
            {
                ResidualLength = Math.Sqrt(point.Dxy * point.Dxy + point.Dz * point.Dz),
            };

            if (point.Check) checks.Add(observation);
            else used.Add(observation);
        }

        var sumXy = 0.0;
        var sumZ = 0.0;
        var countUsed = 0;
        foreach (var point in absolute.Points)
        {
            if (point.Check) continue;
            sumXy += point.Dxy * point.Dxy;
            sumZ += point.Dz * point.Dz;
            countUsed++;
        }

        var result = new AbsoluteOrientationResult
        {
            Success = true,
            Scale = absolute.Lambda,
            X0 = absolute.Dx,
            Y0 = absolute.Dy,
            Z0 = absolute.Dz,
            PhiDeg = absolute.Phi,
            OmegaDeg = absolute.Omega,
            KappaDeg = absolute.Kappa,
            UsedCount = absolute.UsedCount,
            Redundancy = absolute.Redundancy,
            Sigma0 = absolute.Sigma0,
            Rms = absolute.Rms3D,
            MaxAbsResidual = absolute.MaxAbsResidual,
            // 只为了让「点位配置 σ₃/σ₁」这一行原样回来：这个比值就是表里显示的数
            Singular = double.IsNaN(absolute.Flatness) ? Array.Empty<double>() : new[] { 1.0, 1.0, absolute.Flatness },
            MirrorSuspected = absolute.MirrorSuspected,
        };

        return new AbsoluteOrientationRun
        {
            Failure = AbsoluteOrientationFailure.None,
            Result = result,
            Used = used,
            Checks = checks,
            PairLabel = label,
            GroundOrder = absolute.GroundOrder,
            RmsXy = countUsed > 0 ? Math.Sqrt(sumXy / countUsed) : double.NaN,
            RmsZ = countUsed > 0 ? Math.Sqrt(sumZ / countUsed) : double.NaN,
            GroundSampleDistance = absolute.Gsd,
            DepthAmplification = absolute.HbRatio,
            StoredCheckPlanimetricMax = absolute.CheckPlanimetricMax,
            StoredCheckHeightMax = absolute.CheckHeightMax,
        };
    }
}
