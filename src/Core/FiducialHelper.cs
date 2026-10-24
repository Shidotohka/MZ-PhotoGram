namespace Photogrammetry.Core;

/// <summary>
/// 框标的方位描述。
/// <para>
/// 影像上的框标没有刻字，看上去一模一样；编号顺序是相机制造时定下的，程序不能猜。
/// 这里只是把已知的毫米坐标翻成一句人话（"右下角""上边中点"），供界面上提示作业员。
/// 判定不出来或坐标不典型时不硬凑。
/// </para>
/// </summary>
public static class FiducialHelper
{
    /// <summary>离中心多近算"位于中线上"，mm。</summary>
    public const double CenterToleranceMm = 1.0;

    /// <summary>由像平面坐标给出方位描述。无法判断返回空字符串。</summary>
    public static string Describe(double xMm, double yMm)
    {
        var vertical = yMm > CenterToleranceMm ? "上" : yMm < -CenterToleranceMm ? "下" : "";
        var horizontal = xMm > CenterToleranceMm ? "右" : xMm < -CenterToleranceMm ? "左" : "";

        if (vertical.Length == 0 && horizontal.Length == 0) return "中心";
        if (vertical.Length == 0) return horizontal + "边中点";
        if (horizontal.Length == 0) return vertical + "边中点";
        return horizontal + vertical + "角";
    }

    /// <summary>
    /// 整组框标是否大致关于像主点对称。不对称时方位描述容易误导，界面应只显示坐标。
    /// </summary>
    public static bool IsRoughlySymmetric(IReadOnlyList<Fiducial> fiducials)
    {
        if (fiducials.Count == 0) return false;

        var hasLeft = fiducials.Any(f => f.Xmm < -CenterToleranceMm);
        var hasRight = fiducials.Any(f => f.Xmm > CenterToleranceMm);
        var hasUp = fiducials.Any(f => f.Ymm > CenterToleranceMm);
        var hasDown = fiducials.Any(f => f.Ymm < -CenterToleranceMm);

        return (hasLeft && hasRight) || (hasUp && hasDown);
    }
}
