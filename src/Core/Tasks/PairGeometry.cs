using System;

namespace Photogrammetry.Core.Tasks;

/// <summary>
/// 一对影像的几何：两份位姿 + 匹配要的核线输入 + 成果落在哪个坐标系。
///
/// <para>存在的理由：核线约束和前方交会都要「右片相对左片的姿态 + 基线方向」，而这个量有两条来路——
/// 绝对外方位（后方交会的结果），或者相对定向的结果（M6）。两条来路的数学是同一个：
/// 只要把两份位姿放进同一个坐标系，<see cref="PairGeometryBuilder.FromPoses"/> 就能算出 $(\mathbf b,R)$。
/// 所以这里不做两套公式，只做两种「摆位姿」的方式。</para>
///
/// <para>两种方式的区别只在<b>成果是什么坐标</b>：绝对外方位给的是地面坐标（米）；
/// 相对定向给的模型坐标，**比例尺自由**（$|\mathbf b|=1$），要米制必须再做绝对定向（七参数）。</para>
/// </summary>
public sealed class PairGeometry
{
    /// <summary>左片位姿。<b>不一定</b>是相对定向的基准片——按基准片摆，这里只负责对号入座。</summary>
    public required ExteriorOrientation LeftPose { get; init; }

    /// <summary>右片位姿。</summary>
    public required ExteriorOrientation RightPose { get; init; }

    /// <summary>
    /// 喂给 <c>pgm_cv_match</c> 的核线输入：$\mathbf b$ 在左片像空间、单位长；$R$ 是左片像空间 → 右片像空间。
    /// </summary>
    public required (double Bx, double By, double Bz, double[] Rotation) Epipolar { get; init; }

    /// <summary>
    /// true = 交会出的是<b>模型坐标</b>（比例尺未知，$|\mathbf b|=1$，不能当米制引用）；
    /// false = 地面坐标（米）。
    /// </summary>
    public required bool IsModelCoordinates { get; init; }

    /// <summary>几何来源的一句话说明，写进日志用。</summary>
    public required string Source { get; init; }
}

/// <summary>把两条来路都摆成「同一坐标系里的两份位姿」，再统一换算成核线输入。</summary>
public static class PairGeometryBuilder
{
    /// <summary>两个都做完了后方交会：直接用绝对外方位当位姿。</summary>
    public static PairGeometry? FromAbsolute(ImageItem left, ImageItem right)
    {
        if (left.Exterior is not { } eL || right.Exterior is not { } eR) return null;
        return FromPoses(eL, eR, modelCoordinates: false, "绝对外方位（后方交会）");
    }

    /// <summary>
    /// 只有相对定向结果：把基准片摆到原点、姿态为零，另一片摆到 $(\mathbf b,\varphi_2,\omega_2,\kappa_2)$。
    ///
    /// <para><b>基准片可能不是像对的左片</b>——用户可能把两张对调过。所以必须先看
    /// <see cref="RelativeOrientation.ReferenceImageId"/>，再决定谁摆原点。不按它摆，
    /// 核线会整族偏一条线，而且**照样出数**（和 $R$ 方向那个坑同一类）。</para>
    /// </summary>
    public static PairGeometry? FromRelative(RelativeOrientation? relative, ImageItem left, ImageItem right)
    {
        if (relative is null) return null;

        // 基准片是谁：null 是旧工程没记，按左片处理（并在来源里写出来，别装成确定）。
        var reference = relative.ReferenceImageId;
        if (reference is not null && reference != left.Id && reference != right.Id) return null;

        var baseIsLeft = reference != right.Id;

        var basePose = new ExteriorOrientation();   // 原点 + 姿态为零
        var otherPose = new ExteriorOrientation
        {
            Xs = relative.Bx,
            Ys = relative.By,
            Zs = relative.Bz,
            Phi = relative.Phi2,
            Omega = relative.Omega2,
            Kappa = relative.Kappa2,
        };

        var (leftPose, rightPose) = baseIsLeft ? (basePose, otherPose) : (otherPose, basePose);
        var note = baseIsLeft
            ? (reference is null ? "相对定向（基准片未记录，按左片；模型比例尺未知）" : "相对定向（左片为基准；模型比例尺未知）")
            : "相对定向（右片为基准，已调换左右；模型比例尺未知）";

        return FromPoses(leftPose, rightPose, modelCoordinates: true, note);
    }

    /// <summary>
    /// 给定同一坐标系里的两份位姿，算出核线输入。
    ///
    /// <para>$\mathbf b = R_L(X_2-X_1)/|X_2-X_1|$（基线转到左片像空间，单位化——核线只认方向），
    /// $R = R_R R_L^T$（左片像空间 → 右片像空间）。这两条式子对绝对外方位和相对定向都成立，
    /// 所以只有一份实现；绝对外方位那条路已经用真像对验到核线残差 0.0108 mm。</para>
    /// </summary>
    public static PairGeometry FromPoses(
        ExteriorOrientation leftPose, ExteriorOrientation rightPose, bool modelCoordinates, string source)
    {
        ArgumentNullException.ThrowIfNull(leftPose);
        ArgumentNullException.ThrowIfNull(rightPose);

        var rl = Rotation(leftPose);
        var rr = Rotation(rightPose);

        var dx = rightPose.Xs - leftPose.Xs;
        var dy = rightPose.Ys - leftPose.Ys;
        var dz = rightPose.Zs - leftPose.Zs;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 1e-12))
        {
            throw new ArgumentException("两个摄影中心重合，基线长度为零，核线无从定义。", nameof(rightPose));
        }

        var b = new double[3];
        for (var r = 0; r < 3; r++)
        {
            b[r] = (rl[r * 3] * dx + rl[r * 3 + 1] * dy + rl[r * 3 + 2] * dz) / length;
        }

        var rel = new double[9];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var sum = 0.0;
                for (var k = 0; k < 3; k++) sum += rr[r * 3 + k] * rl[c * 3 + k];
                rel[r * 3 + c] = sum;
            }
        }

        return new PairGeometry
        {
            LeftPose = leftPose,
            RightPose = rightPose,
            Epipolar = (b[0], b[1], b[2], rel),
            IsModelCoordinates = modelCoordinates,
            Source = source,
        };
    }

    /// <summary>教材形式的旋转阵 $R=R_\kappa R_\omega R_\phi$（与 `geometry.hpp` 同一套符号）。角度是度。</summary>
    private static double[] Rotation(ExteriorOrientation eo)
    {
        static double[] Mat(double a, double b, double c, double d, double e, double f,
                            double g, double h, double i) => new[] { a, b, c, d, e, f, g, h, i };

        var p = eo.Phi * Math.PI / 180.0;
        var w = eo.Omega * Math.PI / 180.0;
        var k = eo.Kappa * Math.PI / 180.0;

        var cp = Math.Cos(p); var sp = Math.Sin(p);
        var cw = Math.Cos(w); var sw = Math.Sin(w);
        var ck = Math.Cos(k); var sk = Math.Sin(k);

        var rp = Mat(cp, 0, sp, 0, 1, 0, -sp, 0, cp);
        var rw = Mat(1, 0, 0, 0, cw, sw, 0, -sw, cw);
        var rk = Mat(ck, sk, 0, -sk, ck, 0, 0, 0, 1);

        return Multiply(rk, Multiply(rw, rp));
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[9];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                result[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
            }
        }

        return result;
    }
}
