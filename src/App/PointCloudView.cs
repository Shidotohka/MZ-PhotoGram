using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 稀疏连接点云的三维散点视图：正交投影 + 拖动旋转 + 滚轮缩放。
///
/// <para>为什么不用 HelixToolkit / WPF 3D：这里只是画几千个点，引一个三维库不划算，也不该为了
/// 一个诊断窗口给工程加依赖。点用 <see cref="WriteableBitmap"/> 直接写像素（一次
/// <c>WritePixels</c>），比在 Canvas 里堆几千个 <see cref="Ellipse"/> 快两个量级——拖动旋转时
/// 才跟得上手。包围盒的十二条棱用 <see cref="Line"/> 单独画在覆盖层上，给三维一个参照。</para>
///
/// <para>显示的是<b>地面坐标</b>（X 东、Y 北、Z 高，单位随外方位而定）。这不是稠密点云：
/// 一个点就是一对匹配上的同名点交会出来的结果，几百到几千个，用来看有没有整片错位或飞点。</para>
/// </summary>
internal sealed class PointCloudView
{
    /// <summary>强调蓝，与主题 <c>AccentBrush</c> 同值；离视点越远越淡（混进纸底色）。</summary>
    private static readonly Color Accent = Color.FromRgb(0x0F, 0x6F, 0xA8);

    private static readonly Color Paper = Color.FromRgb(0xFF, 0xFF, 0xFF);

    private static readonly int PaperArgb = Pack(Paper.R, Paper.G, Paper.B);

    private static readonly Brush EdgeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xD9)));

    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0x9F)));

    /// <summary>包围盒的十二条棱：角点编号按 x/y/z 各取 0/1，第 i 位对应哪条轴。</summary>
    private static readonly (int A, int B)[] EdgePairs =
    {
        (0, 1), (2, 3), (4, 5), (6, 7),   // 沿 x
        (0, 2), (1, 3), (4, 6), (5, 7),   // 沿 y
        (0, 4), (1, 5), (2, 6), (3, 7),   // 沿 z
    };

    private readonly Image _image;
    private readonly Canvas _overlay;
    private readonly Line[] _edges = new Line[12];
    private readonly TextBlock[] _axisLabels = new TextBlock[3];

    private float[] _x = Array.Empty<float>();
    private float[] _y = Array.Empty<float>();
    private float[] _z = Array.Empty<float>();
    private int[] _order = Array.Empty<int>();
    private double[] _screenX = Array.Empty<double>();
    private double[] _screenY = Array.Empty<double>();
    private double[] _depth = Array.Empty<double>();

    private double _halfX = 1, _halfY = 1, _halfZ = 1;

    /// <summary>真实跨度（米）。<see cref="_halfX"/> 那三个是归一化后的，只给投影用。</summary>
    private double _extentX, _extentY, _extentZ;

    private WriteableBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private int _width;
    private int _height;

    private double _azimuth = 35;
    private double _elevation = 24;
    private double _zoom = 1;

    private Point _dragFrom;
    private bool _dragging;

    public PointCloudView(Image image, Canvas overlay)
    {
        _image = image;
        _overlay = overlay;

        for (var k = 0; k < _edges.Length; k++)
        {
            _edges[k] = new Line
            {
                Stroke = EdgeBrush,
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            };
            _overlay.Children.Add(_edges[k]);
        }

        var names = new[] { "X", "Y", "Z" };
        for (var k = 0; k < names.Length; k++)
        {
            _axisLabels[k] = new TextBlock
            {
                Text = names[k],
                FontSize = 10,
                Foreground = LabelBrush,
                Visibility = Visibility.Collapsed,
            };
            _overlay.Children.Add(_axisLabels[k]);
        }

        _overlay.MouseLeftButtonDown += OnMouseDown;
        _overlay.MouseLeftButtonUp += OnMouseUp;
        _overlay.MouseMove += OnMouseMove;
        _overlay.MouseWheel += OnMouseWheel;
        _overlay.MouseLeave += (_, _) => EndDrag();
        _overlay.SizeChanged += (_, _) => Render();
    }

    public int Count => _x.Length;

    /// <summary>点集跨度（米），给窗口下方那行小字用。</summary>
    public double ExtentX => _extentX;

    public double ExtentY => _extentY;

    public double ExtentZ => _extentZ;

    /// <summary>装点：算出中心与半径，归一化到单位球，投影时只看朝向。</summary>
    public void SetPoints(IReadOnlyList<GroundPointM> points)
    {
        var n = points.Count;
        if (n == 0)
        {
            _x = _y = _z = Array.Empty<float>();
            _order = Array.Empty<int>();
            _screenX = _screenY = _depth = Array.Empty<double>();
            _halfX = _halfY = _halfZ = 1;
            _extentX = _extentY = _extentZ = 0;
            return;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var minZ = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var maxZ = double.MinValue;

        for (var k = 0; k < n; k++)
        {
            var p = points[k];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }

        var centerX = 0.5 * (minX + maxX);
        var centerY = 0.5 * (minY + maxY);
        var centerZ = 0.5 * (minZ + maxZ);

        var halfX = 0.5 * (maxX - minX);
        var halfY = 0.5 * (maxY - minY);
        var halfZ = 0.5 * (maxZ - minZ);

        // 归一化用「半对角线」当半径，这样任何朝向都不会超出单位球，缩放系数可以固定。
        var radius = Math.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
        if (!(radius > 0)) radius = 1.0;

        _extentX = halfX * 2;
        _extentY = halfY * 2;
        _extentZ = halfZ * 2;

        _halfX = Math.Max(halfX / radius, 0.01);
        _halfY = Math.Max(halfY / radius, 0.01);
        _halfZ = Math.Max(halfZ / radius, 0.01);

        _x = new float[n];
        _y = new float[n];
        _z = new float[n];
        for (var k = 0; k < n; k++)
        {
            var p = points[k];
            _x[k] = (float)((p.X - centerX) / radius);
            _y[k] = (float)((p.Y - centerY) / radius);
            _z[k] = (float)((p.Z - centerZ) / radius);
        }

        _order = new int[n];
        _screenX = new double[n];
        _screenY = new double[n];
        _depth = new double[n];
    }

    /// <summary>回到初始视角。</summary>
    public void Reset()
    {
        _azimuth = 35;
        _elevation = 24;
        _zoom = 1;
        Render();
    }

    /// <summary>重画一帧：投点、按深度从远到近铺像素、再摆棱和轴标。</summary>
    public void Render()
    {
        if (!EnsureBitmap()) return;

        var width = _width;
        var height = _height;
        var n = _x.Length;

        Array.Fill(_pixels, PaperArgb);

        var scale = Math.Min(width, height) * 0.44 * _zoom;
        var originX = width * 0.5;
        var originY = height * 0.5;

        var azimuth = _azimuth * Math.PI / 180.0;
        var elevation = _elevation * Math.PI / 180.0;
        var ca = Math.Cos(azimuth);
        var sa = Math.Sin(azimuth);
        var ce = Math.Cos(elevation);
        var se = Math.Sin(elevation);

        if (n > 0)
        {
            var minDepth = double.MaxValue;
            var maxDepth = double.MinValue;

            for (var k = 0; k < n; k++)
            {
                var x1 = _x[k] * ca - _y[k] * sa;
                var y1 = _x[k] * sa + _y[k] * ca;
                var y2 = y1 * ce - _z[k] * se;
                var depth = y1 * se + _z[k] * ce;

                _screenX[k] = originX + x1 * scale;
                _screenY[k] = originY - y2 * scale;
                _depth[k] = depth;
                _order[k] = k;

                if (depth < minDepth) minDepth = depth;
                if (depth > maxDepth) maxDepth = depth;
            }

            // 远的先画，近的后画——重叠处的遮挡关系才对。
            var order = _order;
            var depths = _depth;
            Array.Sort(order, (a, b) => depths[a].CompareTo(depths[b]));

            var span = maxDepth - minDepth;
            if (!(span > 0)) span = 1.0;

            var size = n > 20000 ? 1 : 2;
            var lastColumn = width - size;
            var lastRow = height - size;

            for (var s = 0; s < n; s++)
            {
                var k = order[s];
                var ix = (int)_screenX[k];
                var iy = (int)_screenY[k];
                if (ix < 0 || iy < 0 || ix > lastColumn || iy > lastRow) continue;

                // t: 0 = 最远，1 = 最近。远的混向纸底色，立体感全靠这个。
                var t = (_depth[k] - minDepth) / span;
                var f = 0.28 + 0.72 * t;
                var color = Pack(
                    (int)(Paper.R + (Accent.R - Paper.R) * f),
                    (int)(Paper.G + (Accent.G - Paper.G) * f),
                    (int)(Paper.B + (Accent.B - Paper.B) * f));

                for (var dy = 0; dy < size; dy++)
                {
                    var row = (iy + dy) * width;
                    for (var dx = 0; dx < size; dx++)
                    {
                        _pixels[row + ix + dx] = color;
                    }
                }
            }
        }

        _bitmap!.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);
        UpdateFrame(originX, originY, scale, ca, sa, ce, se);
    }

    /// <summary>包围盒十二条棱 + X/Y/Z 三个轴标。</summary>
    /// <remarks>
    /// 轴标挂在包围盒的角上，不挂在轴端——点云常常是"扁"的（Z 跨度远小于 X/Y），
    /// 轴端投影出来就落在点云中间，被几千个点埋掉。挂在角上再给一点屏幕偏移，
    /// 无论哪个朝向都露在外面（角点编号见 <see cref="EdgePairs"/> 的位编码）。
    /// </remarks>
    private void UpdateFrame(double originX, double originY, double scale,
                             double ca, double sa, double ce, double se)
    {
        var visible = _x.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 每帧一个小数组（8 个 double）：不能用 stackalloc，局部函数捕获不了 Span。
        var cornerX = new double[8];
        var cornerY = new double[8];
        for (var c = 0; c < 8; c++)
        {
            // 位 0 = x、位 1 = y、位 2 = z，取 0 为负端、1 为正端。
            var x = (c & 1) != 0 ? _halfX : -_halfX;
            var y = (c & 2) != 0 ? _halfY : -_halfY;
            var z = (c & 4) != 0 ? _halfZ : -_halfZ;
            ProjectRaw(x, y, z, originX, originY, scale, ca, sa, ce, se, out cornerX[c], out cornerY[c]);
        }

        for (var e = 0; e < EdgePairs.Length; e++)
        {
            var (a, b) = EdgePairs[e];
            var line = _edges[e];
            line.X1 = cornerX[a];
            line.Y1 = cornerY[a];
            line.X2 = cornerX[b];
            line.Y2 = cornerY[b];
            line.Visibility = visible;
        }

        // 1 = (+,−,−)、2 = (−,+,−)：X、Y 轴正端所在的角。
        Place(0, 1, 8, 4);
        Place(1, 2, -18, 4);

        // Z 单独处理：点云常常是扁的（Z 跨度远小于 X/Y），z± 两个角在屏幕上几乎重合，
        // 挂在角上会糊成一团。改挂到"当前视角最靠上的那个角"上方——永远看得见。
        var top = 0;
        for (var c = 1; c < 8; c++)
        {
            if (cornerY[c] < cornerY[top]) top = c;
        }

        Place(2, top, -4, -16);

        void Place(int index, int corner, double dx, double dy)
        {
            var label = _axisLabels[index];
            Canvas.SetLeft(label, cornerX[corner] + dx);
            Canvas.SetTop(label, cornerY[corner] + dy);
            label.Visibility = visible;
        }
    }

    private static void ProjectRaw(double x, double y, double z,
                                   double originX, double originY, double scale,
                                   double ca, double sa, double ce, double se,
                                   out double screenX, out double screenY)
    {
        var x1 = x * ca - y * sa;
        var y1 = x * sa + y * ca;
        var y2 = y1 * ce - z * se;
        screenX = originX + x1 * scale;
        screenY = originY - y2 * scale;
    }

    private bool EnsureBitmap()
    {
        var width = (int)Math.Round(_overlay.ActualWidth);
        var height = (int)Math.Round(_overlay.ActualHeight);
        if (width < 8 || height < 8) return false;

        if (_bitmap != null && width == _width && height == _height) return true;

        _width = width;
        _height = height;
        _pixels = new int[width * height];
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        return true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Reset();
            return;
        }

        _dragFrom = e.GetPosition(_overlay);
        _dragging = true;
        _overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _overlay.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var now = e.GetPosition(_overlay);
        _azimuth -= (now.X - _dragFrom.X) * 0.45;
        _elevation += (now.Y - _dragFrom.Y) * 0.45;
        _elevation = Math.Clamp(_elevation, -88, 88);
        _dragFrom = now;
        Render();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom *= e.Delta > 0 ? 1.12 : 1.0 / 1.12;
        _zoom = Math.Clamp(_zoom, 0.25, 8);
        Render();
        e.Handled = true;
    }

    private static int Pack(int r, int g, int b) =>
        (0xFF << 24) | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
