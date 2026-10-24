namespace Photogrammetry.Core;

/// <summary>屏幕坐标（设备像素）。仅用于鼠标事件与命中判定，不参与任何计算。</summary>
public readonly record struct ScreenPointPx(double X, double Y);

/// <summary>影像像素坐标。原点在影像左上角，y 轴向下。</summary>
public readonly record struct ImagePixel(int I, int J);

/// <summary>像平面坐标。原点在像主点，x 向右、y 向上，单位 mm。</summary>
public readonly record struct ImagePointMm(double X, double Y);

/// <summary>地面坐标，单位 m。</summary>
public readonly record struct GroundPointM(double X, double Y, double Z);

/// <summary>
/// 地面坐标的轴序。Z 恒向上，X/Y 怎么摆决定了三轴是右手系还是左手系。
///
/// 旋转矩阵行列式恒为 +1，只能表达右手系。左手系的数据直接喂进去会多一次镜像，
/// 表现是「怎么解都拟合不上、Zs 出负数」，而残差又不至于大到一眼看出错。
/// </summary>
public enum GroundAxisOrder
{
    /// <summary>X 指东、Y 指北、Z 向上：右手系，摄影测量教材与 <see cref="GroundPointM"/> 的默认口径。</summary>
    EastNorth,

    /// <summary>X 指北、Y 指东、Z 向上：左手系，国内测量学与大地测量的习惯写法。</summary>
    NorthEast
}

/// <summary>地面坐标轴向换算。换算只在一处做，别散到各模块。</summary>
public static class GroundAxes
{
    /// <summary>把按 <paramref name="order"/> 书写的坐标转成数学用的右手系（X 东、Y 北、Z 上）。</summary>
    public static GroundPointM ToMathFrame(GroundPointM p, GroundAxisOrder order) =>
        order == GroundAxisOrder.NorthEast ? new GroundPointM(p.Y, p.X, p.Z) : p;

    /// <summary>轴向的中文说明，直接进界面与诊断。</summary>
    public static string Describe(GroundAxisOrder order) => order switch
    {
        GroundAxisOrder.NorthEast => "北-东-天（X 指北、Y 指东）",
        _ => "东-北-天（X 指东、Y 指北）"
    };
}

/// <summary>
/// 内方位元素。仅描述相机内部几何，不含畸变（畸变在需要时另行挂接）。
/// </summary>
public sealed class InteriorOrientation
{
    /// <summary>主距，mm。</summary>
    public double FocalLength { get; init; }

    /// <summary>像主点在影像中心的偏移量 x，mm。</summary>
    public double X0 { get; init; }

    /// <summary>像主点在影像中心的偏移量 y，mm。</summary>
    public double Y0 { get; init; }

    /// <summary>像元尺寸，mm/px。</summary>
    public double PixelSizeMm { get; init; }

    public bool IsValid =>
        FocalLength > 0 && PixelSizeMm > 0 &&
        double.IsFinite(X0) && double.IsFinite(Y0);
}

/// <summary>
/// 像素坐标与像平面坐标之间的唯一换算实现。
/// 界面上的光标读数与算法输入必须走这里，不允许各模块自行推导。
/// </summary>
public static class CoordinateConverter
{
    /// <summary>像素坐标 → 像平面坐标。</summary>
    public static ImagePointMm PixelToImagePlane(
        ImagePixel pixel, InteriorOrientation io, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(io);
        var x = (pixel.I - width / 2.0) * io.PixelSizeMm - io.X0;
        var y = -(pixel.J - height / 2.0) * io.PixelSizeMm - io.Y0;
        return new ImagePointMm(x, y);
    }

    /// <summary>像平面坐标 → 像素坐标（用于在影像上回绘点位）。</summary>
    public static ImagePixel ImagePlaneToPixel(
        ImagePointMm point, InteriorOrientation io, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(io);
        var i = (point.X + io.X0) / io.PixelSizeMm + width / 2.0;
        var j = -(point.Y + io.Y0) / io.PixelSizeMm + height / 2.0;
        return new ImagePixel((int)Math.Round(i), (int)Math.Round(j));
    }
}
