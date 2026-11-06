using System;
using System.Runtime.InteropServices;

namespace Photogrammetry.Core;

/// <summary>
/// photogrammetry.dll 的托管侧 ABI：结构体布局、<c>DllImport</c> 声明。
///
/// <para>这一层只做一件事：让每个字段与 <c>native/include/photogrammetry.h</c> 里的同名
/// 结构体一一对应。字段顺序、类型、<c>SizeConst</c> 错一个都<b>不会编译报错</b>，只会静默算错，
/// 所以这里不放任何策略——判手性、换轴、过闸门都在 <see cref="NativeAlgorithms"/>。</para>
///
/// <para>改动之后必须跑 <c>Snap --abi</c>，把结构体尺寸与字段偏移跟改前对表。</para>
/// </summary>
internal static class NativeAbi
{
    /// <summary>原生库名。不写 .dll、不写平台前缀，由运行时按平台补全。</summary>
    internal const string Library = "photogrammetry";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
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
    internal struct Output
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
    internal static extern int pgm_fiducial_orientation(ref Input input, ref Output output);

    // ---------------------------------------------------------------- 后方交会

    [StructLayout(LayoutKind.Sequential)]
    internal struct ResectionInput
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
    internal struct ResectionOutput
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
    internal static extern int pgm_resection(ref ResectionInput input, ref ResectionOutput output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pgm_project_point(
        double focalLengthMm, double xs, double ys, double zs,
        double phiRad, double omegaRad, double kappaRad,
        double groundX, double groundY, double groundZ,
        out double outXmm, out double outYmm);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pgm_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pgm_last_error();

    // ---------------------------------------------------------------- 前方交会

    /// <summary>一张片的摄影中心与姿态，角度为弧度。对应 <c>pgm_camera_pose</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CameraPose
    {
        public double Xs, Ys, Zs;
        public double PhiRad, OmegaRad, KappaRad;
    }

    /// <summary>
    /// 空间前方交会的输入，对应 <c>pgm_intersection_input</c>。
    /// 像点坐标单位 mm，原点像主点、x 右 y 上，与后方交会的输入同一定义。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IntersectionInput
    {
        public double FocalLengthMm;
        public CameraPose Left;
        public CameraPose Right;
        public double LeftXmm, LeftYmm, RightXmm, RightYmm;

        /// <summary>像点坐标的先验中误差；0 表示不评精度（三个 m_ 全为 0）。</summary>
        public double ImageSigmaMm;
    }

    /// <summary>空间前方交会的输出，对应 <c>pgm_intersection_output</c>。全部是 double。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IntersectionOutput
    {
        public double GroundX, GroundY, GroundZ;

        /// <summary>教材的投影系数 N₁、N₂。</summary>
        public double NLeft, NRight;

        public double ConvergenceAngleDeg;
        public double BaseLength;
        public double RangeLeft, RangeRight;

        public double MissDistance;
        public double ResidualLeftXmm, ResidualLeftYmm;
        public double ResidualRightXmm, ResidualRightYmm;
        public double VerticalParallaxMm;

        public double MX, MY, MZ;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pgm_forward_intersection(
        ref IntersectionInput input, ref IntersectionOutput output);

    // ---------------------------------------------------------------- 相对定向

    /// <summary>
    /// 相对定向的输入，对应 <c>pgm_relative_orientation_input</c>。
    /// 四个数组必须一样长（同一批同名点对），长度给在 <c>Count</c> 里。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RelativeInput
    {
        public int Count;
        public IntPtr LeftX;
        public IntPtr LeftY;
        public IntPtr RightX;
        public IntPtr RightY;
        public double FocalLengthMm;

        /// <summary>像点坐标先验中误差 mm。0 表示不知道，粗差门槛取 3σ₀ 与 0.01 mm 的大者。</summary>
        public double ImageSigmaMm;

        /// <summary>&lt;= 0 取原生默认（50）。</summary>
        public int MaxIterations;

        /// <summary>&lt;= 0 取原生默认（1e-10，对归一化改正数）。</summary>
        public double Tolerance;
    }

    /// <summary>
    /// 相对定向的输出，对应 <c>pgm_relative_orientation_output</c>。
    /// 两个指针指向调用方预分配的 <c>Count</c> 个元素，不需要时给 <see cref="IntPtr.Zero"/>。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RelativeOutput
    {
        /// <summary>单位基线方向，|b| = 1。</summary>
        public double Bx, By, Bz;

        public double PhiRad, OmegaRad, KappaRad;
        public double PhiDeg, OmegaDeg, KappaDeg;

        /// <summary>辅助系方向 → 像空间 的 3×3，行主序。与后方交会的 Rotation 同一约定。</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Rotation;

        /// <summary>实际冻结的基线分量：0 = b_x、1 = b_y、2 = b_z。</summary>
        public int Gauge;

        public int Iterations;
        public int UsedCount;
        public int RejectedCount;
        public int Redundancy;

        public double Sigma0Mm;
        public double QRmsMm;
        public double QMaxAbsMm;
        public double Condition;

        /// <summary>逐点上下视差 mm。</summary>
        public IntPtr ResidualQ;

        /// <summary>逐点 1 = 参与解算、0 = 被当粗差剔除。</summary>
        public IntPtr UsedFlags;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pgm_relative_orientation(
        ref RelativeInput input, ref RelativeOutput output);

    // ---------------------------------------------------------------- 绝对定向

    /// <summary>
    /// 七参数相似变换的输入，对应 <c>pgm_absolute_orientation_input</c>。
    /// <c>Count</c> 个模型点与同样数量的地面点一一对应，三个坐标数组各自长度 <c>Count</c>。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AbsoluteInput
    {
        public int Count;
        public IntPtr ModelX;
        public IntPtr ModelY;
        public IntPtr ModelZ;
        public IntPtr GroundX;
        public IntPtr GroundY;
        public IntPtr GroundZ;
    }

    /// <summary>
    /// 绝对定向的输出，对应 <c>pgm_absolute_orientation_output</c>。
    /// 两个残差指针指向调用方预分配的数组（3×Count 与 Count），不需要时给 <see cref="IntPtr.Zero"/>。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AbsoluteOutput
    {
        public double Scale;

        /// <summary>模型系 → 地面系，行主序，det = +1。</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Rotation;

        public double X0, Y0, Z0;

        public double PhiRad, OmegaRad, KappaRad;
        public double PhiDeg, OmegaDeg, KappaDeg;

        public int UsedCount;
        public int Redundancy;

        public double Sigma0;
        public double Rms;
        public double MaxAbsResidual;

        /// <summary>H = Σm'g'ᵀ 的三个奇异值，降序。σ2≈0 共线，σ3≈0 共面。</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public double[] Singular;

        public double Condition;
        public int MirrorSuspected;

        /// <summary>逐点不符值，3×Count，X/Y/Z 交错，单位与地面坐标相同。</summary>
        public IntPtr ResidualXyz;

        /// <summary>逐点不符值的模长，Count 个。</summary>
        public IntPtr ResidualLength;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pgm_absolute_orientation(
        ref AbsoluteInput input, ref AbsoluteOutput output);

    // ---------------------------------------------------------------- 计算机视觉匹配

    /// <summary>
    /// 匹配模块的 DLL 名。与 <see cref="Library"/> 分开：这个 DLL 链接 OpenCV，
    /// 只在匹配时加载，缺了不影响其他算法（见 ADR-22）。
    /// </summary>
    internal const string LibraryCv = "photogrammetry_cv";

    /// <summary>成功返回码，与 C 侧的 <c>PGM_OK</c> 对应。</summary>
    internal const int Ok = 0;

    /// <summary>核线几何的输入（与 <c>pgm_epipolar_input</c> 逐字段对应）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CvEpipolar
    {
        public double Bx, By, Bz;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public double[] Rotation;

        public double FocalLengthMm;
    }

    /// <summary>一次匹配的输入（与 <c>pgm_cv_match_input</c> 逐字段对应）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CvMatchInput
    {
        public IntPtr LeftPixels;
        public int LeftWidth, LeftHeight, LeftStep;

        public IntPtr RightPixels;
        public int RightWidth, RightHeight, RightStep;

        public double PixelSizeMm;
        public double CxLeft, CyLeft;
        public double CxRight, CyRight;

        public int UseEpipolar;
        public CvEpipolar Epipolar;

        public double Ratio;
        public double EpipolarToleranceMm;
    }

    [DllImport(LibraryCv, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pgm_cv_match(
        ref CvMatchInput input, int capacity,
        double[] outLeftI, double[] outLeftJ,
        double[] outRightI, double[] outRightJ,
        double[] outScore, double[] outEpipolarMm,
        out int outCount, out int outInliers, out int outDetected);

    [DllImport(LibraryCv, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pgm_cv_last_error();

}
