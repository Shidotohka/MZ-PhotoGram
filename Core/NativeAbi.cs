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

}
