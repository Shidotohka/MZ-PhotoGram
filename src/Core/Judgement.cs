using System;

namespace Photogrammetry.Core;

/// <summary>
/// 「这个数算不算偏大」——判据集中在这一处。
///
/// <para><b>为什么要有它。</b>同一份结果会出现在两个地方：结果窗口（逐份细看）与
/// 成果报告（汇总成表）。两边各写一套阈值，迟早对不上——窗口里是绿的、报告里染红，
/// 谁也不知道该信哪个。阈值是数据，不是排版，所以放 Core；颜色与小数位仍归界面。</para>
///
/// <para>每条阈值后面都写着"为什么是这个数"。改之前先想清楚是判据错了，还是数据真差了。</para>
/// </summary>
public static class Judgement
{
    // ---------- 内定向 ----------

    /// <summary>
    /// 框标内定向的 σ₀ 上限，mm。真实航摄数据上 σ₀ 是 0.012–0.020 mm（见路线图 M2），
    /// 超过 0.02 说明框标量测本身有问题，六系数再准也没意义。
    /// </summary>
    public const double InteriorSigma0Mm = 0.02;

    /// <summary>
    /// 单个框标残差的上限，以 σ₀ 为单位。2n−6 个多余观测下 3σ 太松（正常残差本来就在
    /// 3σ 以内），5σ₀ 才是"这一个框标可能点错了"的量级。
    /// </summary>
    public const double FiducialResidualSigmas = 5.0;

    // ---------- 后方交会 ----------

    /// <summary>控制点残差上限，以 σ₀ 为单位。超过 3σ₀ 的点先怀疑刺点。</summary>
    public const double ResectionResidualSigmas = 3.0;

    // ---------- 前方交会 ----------

    /// <summary>
    /// 交会角下限，度。15° 时 $m_Z$ 已经是 $m_X$ 的约 3.7 倍，再小深度就没有实用价值
    /// （与 <c>IntersectionTask.SmallAngleDeg</c> 同值：那里写日志，这里染红）。
    /// </summary>
    public const double SmallConvergenceAngleDeg = 15.0;

    /// <summary>
    /// 上下视差上限，mm。理想值是 0（同名点在同一核线上），超过约一个像元就是这里有毛病。
    ///
    /// <para>比 <c>IntersectionTask.LargeVerticalParallaxMm</c>（0.10 mm）严：那个是
    /// 解算时"值得提醒一句"的阈值，这里是"表里该染红"的阈值，两者用途不同。</para>
    /// </summary>
    public const double VerticalParallaxMm = 0.02;

    // ---------- 绝对定向 ----------

    /// <summary>
    /// 控制点不符值上限，以 σ₀ 为单位。
    ///
    /// <para>不能拿 σ₀ 本身当判据：$rms/\sigma_0 = \sqrt{(3n-7)/n} \le \sqrt{3} &lt; 2$
    /// 对任何点数都成立，那样的"验收"是块装饰。所以看单点的 $v/\sigma_0$。</para>
    /// </summary>
    public const double AbsoluteResidualSigmas = 2.0;

    /// <summary>
    /// σ₀ 折算成像元后的上限，px。与后方交会的 VL-10（σ₀ ≤ 10δ）同一条规矩：
    /// 绝对定向的残差也应该和像点量测精度同量级。
    /// </summary>
    public const double AbsoluteSigma0Px = 10.0;

    /// <summary>
    /// 点位配置的扁平度 $\sigma_3/\sigma_1$ 下限。小于 1e-3 就是控制点接近共面，
    /// 高程全靠倾角外推——数照样出得来，但 Z 不可信。
    /// </summary>
    public const double FlatModelRatio = 1e-3;

    // ---------- 判据 ----------

    public static bool InteriorSigma0(double sigma0Mm) => sigma0Mm > InteriorSigma0Mm;

    /// <summary>一个框标的残差（两向取大的那个）是否超过 <see cref="FiducialResidualSigmas"/> 倍 σ₀。</summary>
    public static bool FiducialResidual(double vxMm, double vyMm, double sigma0Mm) =>
        sigma0Mm > 0 && Math.Max(Math.Abs(vxMm), Math.Abs(vyMm)) > FiducialResidualSigmas * sigma0Mm;

    /// <summary>控制点残差 $|v|/\sigma_0$ 是否超限。</summary>
    public static bool ResectionResidual(double ratio) => ratio > ResectionResidualSigmas;

    public static bool SmallConvergenceAngle(double degrees) => degrees < SmallConvergenceAngleDeg;

    public static bool VerticalParallax(double mm) => Math.Abs(mm) > VerticalParallaxMm;

    public static bool AbsoluteResidual(double ratio) => ratio > AbsoluteResidualSigmas;

    public static bool AbsoluteSigma0InPixels(double pixels) => pixels > AbsoluteSigma0Px;

    public static bool FlatModel(double sigma3OverSigma1) => sigma3OverSigma1 < FlatModelRatio;
}
