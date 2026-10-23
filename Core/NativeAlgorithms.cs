using System.Runtime.InteropServices;

namespace Photogrammetry.Core;

/// <summary>框标定义：相机级的已知量，像平面坐标（mm，原点像主点，y 向上）。</summary>
public sealed record FiducialPoint(int Id, double Xmm, double Ymm);

/// <summary>框标量测：某张影像上的像元坐标，允许小数。</summary>
public sealed record FiducialObservation(int Id, double I, double J);

/// <summary>单个框标的残差，单位 mm，v = 计算值 − 观测值。</summary>
public sealed record FiducialResidual(int Id, double VxMm, double VyMm);

/// <summary>内定向解算结果。算法在 <c>photogrammetry.dll</c> 中，这里只承载返回值。</summary>
public sealed class FiducialOrientationResult
{
    public bool Success { get; init; }

    /// <summary>原生库返回码，0 为成功。</summary>
    public int ErrorCode { get; init; }

    /// <summary>失败原因或补充说明。</summary>
    public string Message { get; init; } = string.Empty;

    public double A0 { get; init; }
    public double A1 { get; init; }
    public double A2 { get; init; }
    public double B0 { get; init; }
    public double B1 { get; init; }
    public double B2 { get; init; }

    /// <summary>沿 I 方向的比例，mm/px。</summary>
    public double SxMm { get; init; }

    /// <summary>沿 J 方向的比例，mm/px。</summary>
    public double SyMm { get; init; }

    /// <summary>像元尺寸，(s_x + s_y) / 2，mm/px。</summary>
    public double PixelSizeMm { get; init; }

    public double X0Mm { get; init; }
    public double Y0Mm { get; init; }

    public double PrincipalI { get; init; }
    public double PrincipalJ { get; init; }

    public double ThetaIDeg { get; init; }
    public double ThetaJDeg { get; init; }

    /// <summary>单位权中误差，mm。</summary>
    public double Sigma0Mm { get; init; }

    public double MaxAbsResidualMm { get; init; }

    public int UsedCount { get; init; }

    /// <summary>多余观测数 = 2n − 6。</summary>
    public int Redundancy { get; init; }

    public IReadOnlyList<FiducialResidual> Residuals { get; init; } = Array.Empty<FiducialResidual>();

    /// <summary>把解出的内参填进参数式内方位元素。f 由相机检定给出，不由内定向求解。</summary>
    public InteriorOrientation ToInteriorOrientation(double focalLengthMm) => new()
    {
        FocalLength = focalLengthMm,
        X0 = X0Mm,
        Y0 = Y0Mm,
        PixelSizeMm = PixelSizeMm,
    };
}

/// <summary>一个控制点的观测：地面坐标（m）与它在像平面上的坐标（mm）。</summary>
public sealed record ResectionObservation(
    string GcpId, double Xmm, double Ymm, double GroundX, double GroundY, double GroundZ);

/// <summary>
/// 地面轴序的判定证据：取一组最不共线的三点，比它们在地面与像平面上的绕向。
/// 绕向相同即文件已是右手系，相反即需要换轴。
/// </summary>
public sealed record GroundOrderEvidence(
    bool Decided,
    GroundAxisOrder Order,
    string Message,
    string Triple,
    double SinImage,
    double SinGround,
    int Agreeing,
    int Total)
{
    /// <summary>用于界面与日志的一行摘要。</summary>
    public string Describe() => Decided
        ? $"手性判定 {GroundAxes.Describe(Order)}：三点 {Triple}，" +
          $"像平面 sin={SinImage:+0.000;-0.000}、地面 sin={SinGround:+0.000;-0.000}，" +
          $"{Agreeing}/{Total} 组三角形一致"
        : $"手性无法判定：{Message}";
}

/// <summary>单个控制点的像点残差，mm，v = 计算 − 观测。</summary>
public sealed record ResectionResidual(string GcpId, double VxMm, double VyMm);

/// <summary>后方交会解算结果。算法在 <c>photogrammetry.dll</c> 中，这里只承载返回值。</summary>
public sealed class ResectionResult
{
    /// <summary>是否可接受。<see cref="SolveResectionRobust"/> 会在几何/残差检查不过时把它置 false。</summary>
    public bool Success { get; set; }

    /// <summary>原生库返回码，0 为成功，7 表示迭代未收敛（此时下面的值仅供诊断）。</summary>
    public int ErrorCode { get; set; }

    /// <summary>失败原因或补充说明。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>摄影中心，m。</summary>
    public double Xs { get; init; }
    public double Ys { get; init; }
    public double Zs { get; init; }

    /// <summary>三个姿态角，度。</summary>
    public double PhiDeg { get; init; }
    public double OmegaDeg { get; init; }
    public double KappaDeg { get; init; }

    public double PhiRad { get; init; }
    public double OmegaRad { get; init; }
    public double KappaRad { get; init; }

    /// <summary>行主序 3×3 旋转矩阵，地面系 → 像空间。</summary>
    public IReadOnlyList<double> Rotation { get; init; } = Array.Empty<double>();

    public int Iterations { get; init; }
    public int UsedCount { get; init; }

    /// <summary>多余观测数 = 2n − 6。</summary>
    public int Redundancy { get; init; }

    /// <summary>单位权中误差，mm。</summary>
    public double Sigma0Mm { get; init; }

    public double MaxAbsResidualMm { get; init; }

    /// <summary>参数中误差：位置用 m，角度用弧度。</summary>
    public double MXs { get; init; }
    public double MYs { get; init; }
    public double MZs { get; init; }
    public double MPhi { get; init; }
    public double MOmega { get; init; }
    public double MKappa { get; init; }

    /// <summary>列平衡后法方程的条件数。越大越病态。</summary>
    public double Condition { get; init; }

    public IReadOnlyList<ResectionResidual> Residuals { get; init; } =
        Array.Empty<ResectionResidual>();

    /// <summary>平均摄影距离（摄影中心到各控制点），m。用来做单位合理性的第一眼检查。</summary>
    public double MeanRangeM { get; init; }

    public ExteriorOrientation ToExterior() => new()
    {
        Xs = Xs,
        Ys = Ys,
        Zs = Zs,
        Phi = PhiDeg,
        Omega = OmegaDeg,
        Kappa = KappaDeg
    };
}

/// <summary>
/// 原生算法库的托管入口，同时承担这条流水线上的<b>数据适配</b>。
/// <para>
/// 职责划分（界面设计规范 §8.1）：平差、迭代、投影在 <c>photogrammetry.dll</c> 里，
/// 它只接受干净的右手系。判地面轴序、换轴、结果过闸门这些「要知道数据长什么样」的事在本类里做。
/// </para>
/// </summary>
public static class NativeAlgorithms
{
    private const string Library = "photogrammetry";

    /// <summary>4 个框标以下不允许解算：3 个时多余观测为 0，残差恒为 0。</summary>
    public const int MinFiducials = 4;

    /// <summary>原生返回码：b₂ ≥ 0，y 轴符号异常。多半是框标编号与点位对不上。</summary>
    public const int ErrorAxisSign = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Count;
        public IntPtr FiducialX;
        public IntPtr FiducialY;
        public IntPtr ImageI;
        public IntPtr ImageJ;
        public int ImageWidth;
        public int ImageHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Output
    {
        public double A0, A1, A2, B0, B1, B2;
        public double Sx, Sy, PixelSize;
        public double X0, Y0;
        public double PrincipalI, PrincipalJ;
        public double ThetaIDeg, ThetaJDeg;
        public double Sigma0;
        public double MaxAbsResidual;
        public int UsedCount;
        public int Redundancy;
        public IntPtr ResidualX;
        public IntPtr ResidualY;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pgm_fiducial_orientation(ref Input input, ref Output output);

    // ---------------------------------------------------------------- 后方交会

    [StructLayout(LayoutKind.Sequential)]
    private struct ResectionInput
    {
        public int Count;
        public IntPtr ImageX;
        public IntPtr ImageY;
        public IntPtr GroundX;
        public IntPtr GroundY;
        public IntPtr GroundZ;
        public double FocalLengthMm;
        public int UseInitial;
        public double InitialXs, InitialYs, InitialZs;
        public double InitialPhi, InitialOmega, InitialKappa;
        public int MaxIterations;
        public double ToleranceLength;
        public double ToleranceAngle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResectionOutput
    {
        public double Xs, Ys, Zs;
        public double PhiRad, OmegaRad, KappaRad;
        public double PhiDeg, OmegaDeg, KappaDeg;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Rotation;

        public int Iterations;
        public int UsedCount;
        public int Redundancy;

        public double Sigma0Mm;
        public double MaxAbsResidualMm;
        public double MXs, MYs, MZs;
        public double MPhi, MOmega, MKappa;
        public double Condition;
        public IntPtr ResidualX;
        public IntPtr ResidualY;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pgm_resection(ref ResectionInput input, ref ResectionOutput output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pgm_project_point(
        double focalLengthMm, double xs, double ys, double zs,
        double phiRad, double omegaRad, double kappaRad,
        double groundX, double groundY, double groundZ,
        out double outXmm, out double outYmm);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pgm_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pgm_last_error();

    /// <summary>原生库版本。</summary>
    public static string Version => Marshal.PtrToStringUTF8(pgm_version()) ?? string.Empty;

    /// <summary>最近一次原生调用的错误描述。</summary>
    public static string LastError => Marshal.PtrToStringUTF8(pgm_last_error()) ?? string.Empty;

    /// <summary>
    /// 六参数仿射内定向。按 <see cref="FiducialPoint.Id"/> 与 <see cref="FiducialObservation.Id"/> 配对，
    /// 只解算两边都存在的框标。
    /// </summary>
    public static FiducialOrientationResult SolveFiducialOrientation(
        IReadOnlyList<FiducialPoint> fiducials,
        IReadOnlyList<FiducialObservation> observations,
        int imageWidth,
        int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(fiducials);
        ArgumentNullException.ThrowIfNull(observations);
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "影像宽高必须为正。");

        var byId = new Dictionary<int, FiducialPoint>(fiducials.Count);
        foreach (var f in fiducials) byId[f.Id] = f;

        var pairs = new List<(FiducialPoint Def, FiducialObservation Obs)>(observations.Count);
        foreach (var o in observations)
            if (byId.TryGetValue(o.Id, out var def))
                pairs.Add((def, o));

        var n = pairs.Count;
        if (n == 0)
        {
            return new FiducialOrientationResult
            {
                Success = false,
                ErrorCode = -1,
                Message = "没有可配对的框标：定义与量测的编号对不上。",
            };
        }

        var fx = new double[n];
        var fy = new double[n];
        var ii = new double[n];
        var jj = new double[n];
        for (var k = 0; k < n; k++)
        {
            fx[k] = pairs[k].Def.Xmm;
            fy[k] = pairs[k].Def.Ymm;
            ii[k] = pairs[k].Obs.I;
            jj[k] = pairs[k].Obs.J;
        }

        IntPtr pfx = IntPtr.Zero, pfy = IntPtr.Zero, pii = IntPtr.Zero, pjj = IntPtr.Zero;
        IntPtr prx = IntPtr.Zero, pry = IntPtr.Zero;
        try
        {
            pfx = Alloc(fx);
            pfy = Alloc(fy);
            pii = Alloc(ii);
            pjj = Alloc(jj);
            prx = Marshal.AllocHGlobal(n * sizeof(double));
            pry = Marshal.AllocHGlobal(n * sizeof(double));

            var input = new Input
            {
                Count = n,
                FiducialX = pfx,
                FiducialY = pfy,
                ImageI = pii,
                ImageJ = pjj,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
            };
            var output = new Output { ResidualX = prx, ResidualY = pry };

            int rc;
            try
            {
                rc = pgm_fiducial_orientation(ref input, ref output);
            }
            catch (DllNotFoundException)
            {
                return new FiducialOrientationResult
                {
                    Success = false,
                    ErrorCode = -1,
                    Message = "找不到 photogrammetry.dll。先运行 " +
                              "cmake --build native/build --config Release 再重新构建托管项目。",
                };
            }
            catch (BadImageFormatException)
            {
                return new FiducialOrientationResult
                {
                    Success = false,
                    ErrorCode = -1,
                    Message = "photogrammetry.dll 位数不匹配。托管进程是 x64，DLL 也必须是 x64。",
                };
            }

            var residualX = new double[n];
            var residualY = new double[n];
            Marshal.Copy(prx, residualX, 0, n);
            Marshal.Copy(pry, residualY, 0, n);

            var residuals = new List<FiducialResidual>(n);
            for (var k = 0; k < n; k++)
                residuals.Add(new FiducialResidual(pairs[k].Obs.Id, residualX[k], residualY[k]));

            return new FiducialOrientationResult
            {
                Success = rc == 0,
                ErrorCode = rc,
                Message = rc == 0 ? string.Empty : LastError,
                A0 = output.A0,
                A1 = output.A1,
                A2 = output.A2,
                B0 = output.B0,
                B1 = output.B1,
                B2 = output.B2,
                SxMm = output.Sx,
                SyMm = output.Sy,
                PixelSizeMm = output.PixelSize,
                X0Mm = output.X0,
                Y0Mm = output.Y0,
                PrincipalI = output.PrincipalI,
                PrincipalJ = output.PrincipalJ,
                ThetaIDeg = output.ThetaIDeg,
                ThetaJDeg = output.ThetaJDeg,
                Sigma0Mm = output.Sigma0,
                MaxAbsResidualMm = output.MaxAbsResidual,
                UsedCount = output.UsedCount,
                Redundancy = output.Redundancy,
                Residuals = residuals,
            };
        }
        finally
        {
            foreach (var p in new[] { pfx, pfy, pii, pjj, prx, pry })
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    // ---------------------------------------------------------------- 后方交会封装

    /// <summary>后方交会至少需要 4 个控制点：3 个时多余观测为 0，残差恒为 0。</summary>
    public const int MinControlPoints = 4;

    /// <summary>原生返回码：迭代到上限仍未收敛。</summary>
    public const int ErrorNotConverged = 7;

    /// <summary>
    /// σ₀ 超过「像元尺寸 × 这个倍数」就判为解不成立。
    /// 手工取点误差总是在 1 个像元左右，模型若连 10 个像元都解释不了，问题不在噪声。
    /// </summary>
    public const double MaxSigma0InPixels = 10.0;

    /// <summary>
    /// 后方交会的稳妥入口：自动初值与外部给的初值各解一次，取残差小的那个，
    /// 再做几何与残差检查。
    ///
    /// 为什么不直接用 <paramref name="hint"/> 当唯一初值：解算成功会把外方位写回影像，
    /// 下一次解算又拿它当初值——一旦某次落进倒置解（相机跑到控制点下方、φ≈180°），
    /// 之后每次都会从那个坑里出发，越解越歪。两次都试、留好的那次，才不会被自己的旧结果锁住。
    /// </summary>
    public static ResectionResult SolveResectionRobust(
        IReadOnlyList<ResectionObservation> observations,
        double focalLengthMm,
        double pixelSizeMm,
        ExteriorOrientation? hint = null,
        int maxIterations = 0)
    {
        ArgumentNullException.ThrowIfNull(observations);

        var fromAuto = SolveResection(observations, focalLengthMm, null, maxIterations);

        var best = fromAuto;
        if (hint is not null)
        {
            var fromHint = SolveResection(observations, focalLengthMm, hint, maxIterations);
            if (fromHint.Success && (!fromAuto.Success || fromHint.Sigma0Mm < fromAuto.Sigma0Mm))
                best = fromHint;
        }

        if (!best.Success) return best;

        // ---- 检查一：残差要与取点精度相称
        if (pixelSizeMm > 0 && best.Sigma0Mm > MaxSigma0InPixels * pixelSizeMm)
        {
            best.Success = false;
            best.Message =
                $"解算结果不可信：σ₀ = {best.Sigma0Mm:0.0000} mm，" +
                $"相当于 {best.Sigma0Mm / pixelSizeMm:0.#} 个像元（限 {MaxSigma0InPixels:0} 个）。" +
                "模型解释不了量测值，先查刺点有没有点错、控制点是否属于这张影像、单位对不对。";
            return best;
        }

        // ---- 检查二：摄影中心必须在控制点上方
        // 倒置解在数学上是合法驻点（相机在下、φ≈180°、W 仍然为负），但航空摄影不存在这种姿态。
        if (observations.Count > 0)
        {
            var meanZ = observations.Average(o => o.GroundZ);
            if (best.Zs < meanZ)
            {
                best.Success = false;
                best.Message =
                    $"解出来的是倒置解：摄影中心 Z = {best.Zs:0.0} m 低于控制点平均高程 {meanZ:0.0} m，" +
                    $"φ = {best.PhiDeg:0.0}°。倒置解在数学上成立但不可能是航摄姿态，" +
                    "多半是控制点点号与影像对不上，或者地面坐标单位不对。";
                return best;
            }
        }

        return best;
    }

    /// <summary>
    /// 三角形法判手性所需的最小 |sin|。三点越接近共线，叉乘越小、符号越容易被噪声翻掉。
    /// 0.3 对应夹角约 17°。
    /// </summary>
    public const double MinHandednessSine = 0.30;

    /// <summary>
    /// <b>判地面轴序（手性）。</b>
    ///
    /// 取三点，分别在像平面与地面上连成两条有向边，做二维叉乘
    /// $S=(x_2-x_1)(y_3-y_1)-(y_2-y_1)(x_3-x_1)$（有向面积的两倍）。
    /// 任何线性映射都满足 $S'=\det(M)\,S$，而旋转与缩放的 $\det=s^2>0$ 保号，
    /// 所以两组 $S$ 的符号相同 ⇔ 地面已与像平面同向（右手系）。
    ///
    /// 这个判据对旋转、比例尺、Z 值、像片倾角全都不敏感——它们只改变三角形的大小，
    /// 翻不了它的绕向。因此只需 3 个点、不用解算、能手算复核。
    ///
    /// 实现上取<b>最不共线</b>的那一组（所有三点组合里 min(|sin像|,|sin地|) 最大的一对），
    /// 并统计其余三角形与它的一致组数，作为判定强度的旁证。
    /// </summary>
    public static GroundOrderEvidence DetectGroundOrder(
        IReadOnlyList<ResectionObservation> fileFrame)
    {
        ArgumentNullException.ThrowIfNull(fileFrame);

        var n = fileFrame.Count;
        if (n < 3)
        {
            return new GroundOrderEvidence(false, GroundAxisOrder.EastNorth,
                $"判手性至少要 3 个控制点，现在只有 {n} 个。", string.Empty, 0, 0, 0, 0);
        }

        // 所有三点组合：算有向面积的正弦，留下足够不共线的
        var candidates = new List<(string Triple, double SinImage, double SinGround, double Score, bool SameSign)>();
        for (var i = 0; i < n - 2; i++)
        for (var j = i + 1; j < n - 1; j++)
        for (var k = j + 1; k < n; k++)
        {
            var a = fileFrame[i];
            var b = fileFrame[j];
            var c = fileFrame[k];

            var sinImage = SignedSine(a.Xmm, a.Ymm, b.Xmm, b.Ymm, c.Xmm, c.Ymm);
            var sinGround = SignedSine(a.GroundX, a.GroundY, b.GroundX, b.GroundY, c.GroundX, c.GroundY);

            var score = Math.Min(Math.Abs(sinImage), Math.Abs(sinGround));
            if (score < 1e-9) continue;   // 退化成一条线，投不了票

            candidates.Add(($"{a.GcpId}-{b.GcpId}-{c.GcpId}", sinImage, sinGround, score,
                Math.Sign(sinImage) == Math.Sign(sinGround)));
        }

        if (candidates.Count == 0)
        {
            return new GroundOrderEvidence(false, GroundAxisOrder.EastNorth,
                "所有三点组合都近于共线，无法判手性。控制点在影像上应当散开一些。",
                string.Empty, 0, 0, 0, 0);
        }

        var best = candidates.MaxBy(t => t.Score);
        if (best.Score < MinHandednessSine)
        {
            return new GroundOrderEvidence(false, GroundAxisOrder.EastNorth,
                $"最不共线的三点 {best.Triple} 也只有 |sin| = {best.Score:0.000}（需 {MinHandednessSine:0.00}），" +
                "控制点分布太窄，判手性不可靠。",
                best.Triple, best.SinImage, best.SinGround, 0, 0);
        }

        // 结论取最不共线的那组；其余三角形给它投票，用来看判定稳不稳
        var usable = candidates.Where(t => t.Score >= MinHandednessSine).ToList();
        var agreeing = usable.Count(t => t.SameSign == best.SameSign);
        var order = best.SameSign ? GroundAxisOrder.EastNorth : GroundAxisOrder.NorthEast;

        var message = best.SameSign
            ? "地面 (X,Y) 与像平面 (x,y) 绕向一致，文件已是右手系（X 东、Y 北）。"
            : "地面 (X,Y) 与像平面 (x,y) 绕向相反：文件是左手系（X 北、Y 东），已换轴。";
        if (agreeing < usable.Count)
        {
            message += $"另有 {usable.Count - agreeing}/{usable.Count} 组三角形得出相反结论，" +
                       "多半是某个控制点刺错了，建议复核。";
        }

        return new GroundOrderEvidence(true, order, message, best.Triple,
            best.SinImage, best.SinGround, agreeing, usable.Count);
    }

    /// <summary>
    /// 有向面积的正弦：$2S/(|\mathbf u||\mathbf v|)$，等于两向量夹角的正弦，带符号。
    /// 无量纲，所以像平面（mm）与地面（m）可以直接比较。
    /// </summary>
    private static double SignedSine(
        double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var ux = x2 - x1;
        var uy = y2 - y1;
        var vx = x3 - x1;
        var vy = y3 - y1;
        var cross = ux * vy - uy * vx;
        var scale = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        return scale > 0 ? cross / scale : 0;
    }

    /// <summary>把文件里的地面坐标按判定的轴序换成数学系（X 东、Y 北、Z 上）。</summary>
    public static List<ResectionObservation> ToMathFrame(
        IReadOnlyList<ResectionObservation> fileFrame, GroundAxisOrder order)
    {
        ArgumentNullException.ThrowIfNull(fileFrame);

        return fileFrame
            .Select(o =>
            {
                var p = GroundAxes.ToMathFrame(
                    new GroundPointM(o.GroundX, o.GroundY, o.GroundZ), order);
                return new ResectionObservation(o.GcpId, o.Xmm, o.Ymm, p.X, p.Y, p.Z);
            })
            .ToList();
    }

    /// <summary>判手性 → 换轴 → 解算。这是界面上「解算后方交会」走的唯一路径。</summary>
    public static ResectionResult SolveResectionAuto(
        IReadOnlyList<ResectionObservation> fileFrame,
        double focalLengthMm,
        double pixelSizeMm,
        ExteriorOrientation? hint = null,
        int maxIterations = 0)
    {
        var evidence = DetectGroundOrder(fileFrame);
        if (!evidence.Decided)
        {
            return new ResectionResult
            {
                Success = false,
                ErrorCode = -1,
                Message = evidence.Message,
            };
        }

        return SolveResectionRobust(
            ToMathFrame(fileFrame, evidence.Order), focalLengthMm, pixelSizeMm, hint, maxIterations);
    }

    /// <summary>
    /// 空间后方交会。<paramref name="observations"/> 的像平面坐标必须已经由内定向换算过，
    /// 地面坐标必须已经是右手系（X 东、Y 北、Z 上）。
    /// </summary>
    public static ResectionResult SolveResection(
        IReadOnlyList<ResectionObservation> observations,
        double focalLengthMm,
        ExteriorOrientation? initial = null,
        int maxIterations = 0)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (focalLengthMm <= 0 || !double.IsFinite(focalLengthMm))
            throw new ArgumentOutOfRangeException(nameof(focalLengthMm), "主距必须为正。");

        var n = observations.Count;
        if (n == 0)
        {
            return new ResectionResult
            {
                Success = false,
                ErrorCode = -1,
                Message = "没有可用的控制点观测。",
            };
        }

        var ox = new double[n];
        var oy = new double[n];
        var gx = new double[n];
        var gy = new double[n];
        var gz = new double[n];
        for (var k = 0; k < n; k++)
        {
            var o = observations[k];
            ox[k] = o.Xmm;
            oy[k] = o.Ymm;
            gx[k] = o.GroundX;
            gy[k] = o.GroundY;
            gz[k] = o.GroundZ;
        }

        IntPtr pox = IntPtr.Zero, poy = IntPtr.Zero, pgx = IntPtr.Zero, pgy = IntPtr.Zero, pgz = IntPtr.Zero;
        IntPtr prx = IntPtr.Zero, pry = IntPtr.Zero;
        try
        {
            pox = Alloc(ox);
            poy = Alloc(oy);
            pgx = Alloc(gx);
            pgy = Alloc(gy);
            pgz = Alloc(gz);
            prx = Marshal.AllocHGlobal(n * sizeof(double));
            pry = Marshal.AllocHGlobal(n * sizeof(double));

            var input = new ResectionInput
            {
                Count = n,
                ImageX = pox,
                ImageY = poy,
                GroundX = pgx,
                GroundY = pgy,
                GroundZ = pgz,
                FocalLengthMm = focalLengthMm,
                UseInitial = initial is null ? 0 : 1,
                InitialXs = initial?.Xs ?? 0,
                InitialYs = initial?.Ys ?? 0,
                InitialZs = initial?.Zs ?? 0,
                InitialPhi = (initial?.Phi ?? 0) * Math.PI / 180.0,
                InitialOmega = (initial?.Omega ?? 0) * Math.PI / 180.0,
                InitialKappa = (initial?.Kappa ?? 0) * Math.PI / 180.0,
                MaxIterations = maxIterations,
                ToleranceLength = 0,
                ToleranceAngle = 0,
            };

            var output = new ResectionOutput
            {
                Rotation = new double[9],
                ResidualX = prx,
                ResidualY = pry,
            };

            int rc;
            try
            {
                rc = pgm_resection(ref input, ref output);
            }
            catch (DllNotFoundException)
            {
                return new ResectionResult
                {
                    Success = false,
                    ErrorCode = -1,
                    Message = "找不到 photogrammetry.dll。先运行 " +
                              "cmake --build native/build --config Release 再重新构建托管项目。",
                };
            }
            catch (BadImageFormatException)
            {
                return new ResectionResult
                {
                    Success = false,
                    ErrorCode = -1,
                    Message = "photogrammetry.dll 位数不匹配。托管进程是 x64，DLL 也必须是 x64。",
                };
            }

            var residualX = new double[n];
            var residualY = new double[n];
            Marshal.Copy(prx, residualX, 0, n);
            Marshal.Copy(pry, residualY, 0, n);

            var residuals = new List<ResectionResidual>(n);
            for (var k = 0; k < n; k++)
                residuals.Add(new ResectionResidual(observations[k].GcpId, residualX[k], residualY[k]));

            var meanRange = 0.0;
            for (var k = 0; k < n; k++)
            {
                var dx = gx[k] - output.Xs;
                var dy = gy[k] - output.Ys;
                var dz = gz[k] - output.Zs;
                meanRange += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            meanRange /= n;

            return new ResectionResult
            {
                Success = rc == 0,
                ErrorCode = rc,
                Message = rc == 0 ? string.Empty : LastError,
                Xs = output.Xs,
                Ys = output.Ys,
                Zs = output.Zs,
                PhiRad = output.PhiRad,
                OmegaRad = output.OmegaRad,
                KappaRad = output.KappaRad,
                PhiDeg = output.PhiDeg,
                OmegaDeg = output.OmegaDeg,
                KappaDeg = output.KappaDeg,
                Rotation = output.Rotation ?? Array.Empty<double>(),
                Iterations = output.Iterations,
                UsedCount = output.UsedCount,
                Redundancy = output.Redundancy,
                Sigma0Mm = output.Sigma0Mm,
                MaxAbsResidualMm = output.MaxAbsResidualMm,
                MXs = output.MXs,
                MYs = output.MYs,
                MZs = output.MZs,
                MPhi = output.MPhi,
                MOmega = output.MOmega,
                MKappa = output.MKappa,
                Condition = output.Condition,
                Residuals = residuals,
                MeanRangeM = meanRange,
            };
        }
        finally
        {
            foreach (var p in new[] { pox, poy, pgx, pgy, pgz, prx, pry })
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>共线条件方程正算：由外方位元素把地面点投影到像平面坐标（mm）。</summary>
    public static ImagePointMm? ProjectPoint(
        double focalLengthMm, ExteriorOrientation eo, GroundPointM ground)
    {
        ArgumentNullException.ThrowIfNull(eo);
        var rc = pgm_project_point(
            focalLengthMm, eo.Xs, eo.Ys, eo.Zs,
            eo.Phi * Math.PI / 180.0, eo.Omega * Math.PI / 180.0, eo.Kappa * Math.PI / 180.0,
            ground.X, ground.Y, ground.Z,
            out var x, out var y);
        return rc == 0 ? new ImagePointMm(x, y) : null;
    }

    private static IntPtr Alloc(double[] values)
    {
        var p = Marshal.AllocHGlobal(values.Length * sizeof(double));
        Marshal.Copy(values, 0, p, values.Length);
        return p;
    }
}
