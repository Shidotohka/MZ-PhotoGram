using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 稀疏连接点云的三维视图（HelixToolkit）。
///
/// <para>相机、旋转、缩放、坐标轴、网格全交给 Helix，这里只负责三件事：把点灌进去、
/// 按高程分档染色、给一个能一次装下整片点云的初始视角。</para>
///
/// <para>显示的是<b>地面坐标</b>（X 东、Y 北、Z 高）。Z 朝上——这是摄影测量的习惯，
/// Helix 默认按 Y 朝上摆相机，所以相机要显式给 <c>UpDirection</c>，
/// 否则整片点云会"立"起来。</para>
///
/// <para>这不是稠密点云：一个点就是一对同名点交会出来的结果，几百到几千个，
/// 用来看有没有整片错位或飞点。几百万点这一档 WPF 三维扛不住，要换 SharpDX（ADR-23）。</para>
/// </summary>
internal sealed class PointCloudView
{
    /// <summary>按高程分几档染色的颜色数。少了看不出起伏，多了就成了噪声。</summary>
    private const int Buckets = 6;

    /// <summary>低处（浅）到高处（深）的蓝色梯度，与主题的强调蓝同族。</summary>
    private static readonly Color Low = Color.FromRgb(0xA8, 0xCB, 0xE2);

    private static readonly Color High = Color.FromRgb(0x0B, 0x4A, 0x72);

    private readonly HelixViewport3D _viewport;
    private readonly List<PointsVisual3D> _layers = new(Buckets);

    private bool _fitted;
    private Point3D _center;
    private double _radius = 1;

    public PointCloudView(Grid host)
    {
        _viewport = new HelixViewport3D
        {
            Background = Brushes.White,
            ShowCoordinateSystem = true,
            ShowViewCube = false,     // 立方体对"看一片点云"没帮助，占地方
            ShowFrameRate = false,
            IsHeadLightEnabled = true,
        };

        for (var k = 0; k < Buckets; k++)
        {
            var layer = new PointsVisual3D
            {
                Color = Ramp(k / (double)(Buckets - 1)),
                Size = 2.2,
            };
            _layers.Add(layer);
            _viewport.Children.Add(layer);
        }

        host.Children.Add(_viewport);

        // 视口自己的尺寸变化才是"可以摆机位了"的信号：页签没切过来之前宽高是 0，
        // 窗口那层调一次 Render() 不解决问题。
        _viewport.Loaded += (_, _) => Render();
        _viewport.SizeChanged += (_, _) => Render();

        // Helix 自带左键旋转、滚轮缩放、中键平移，但没有"回到最初视角"。
        // 双击复位——信息行里写了这一条，就得真有。
        _viewport.MouseDoubleClick += (_, _) =>
        {
            _fitted = false;
            Render();
        };
    }

    public int Count { get; private set; }

    /// <summary>点集真实跨度（米），给窗口下方那行小字用。</summary>
    public double ExtentX { get; private set; }

    public double ExtentY { get; private set; }

    public double ExtentZ { get; private set; }

    /// <summary>装点：算包围盒，按高程分档塞进各层的 <c>Point3DCollection</c>。</summary>
    public void SetPoints(IReadOnlyList<GroundPointM> points)
    {
        Count = points.Count;

        if (points.Count == 0)
        {
            ExtentX = ExtentY = ExtentZ = 0;
            foreach (var layer in _layers)
            {
                layer.Points = new Point3DCollection();
            }

            return;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var minZ = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        var maxZ = double.MinValue;

        for (var k = 0; k < points.Count; k++)
        {
            var p = points[k];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
            if (p.Z < minZ) minZ = p.Z;
            if (p.Z > maxZ) maxZ = p.Z;
        }

        ExtentX = maxX - minX;
        ExtentY = maxY - minY;
        ExtentZ = maxZ - minZ;

        var spanZ = ExtentZ;
        if (!(spanZ > 0)) spanZ = 1.0;

        var buckets = new List<Point3D>[Buckets];
        for (var k = 0; k < Buckets; k++) buckets[k] = new List<Point3D>(points.Count / Buckets + 16);

        for (var k = 0; k < points.Count; k++)
        {
            var p = points[k];
            var t = (p.Z - minZ) / spanZ;
            var b = Math.Clamp((int)(t * Buckets), 0, Buckets - 1);
            buckets[b].Add(new Point3D(p.X, p.Y, p.Z));
        }

        for (var k = 0; k < Buckets; k++)
        {
            _layers[k].Points = new Point3DCollection(buckets[k]);
        }

        Fit(minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>回到初始视角（重装一次点云时也会走这里）。</summary>
    public void Reset()
    {
        _fitted = false;
        Render();
    }

    /// <summary>
    /// 首次拿到真实尺寸后再摆机位：页签没切过来之前视口宽高是 0，那时算出的距离是错的。
    /// </summary>
    /// <remarks>
    /// 不用 <c>HelixViewport3D.ZoomExtents()</c>：它带动画，把包围盒贴到画面边缘，
    /// 点云会顶到边框上，而且事后改机位会被动画覆盖（实测改了等于没改）。自己按
    /// 视场角算距离，边界与留白都在手里。
    /// </remarks>
    public void Render()
    {
        if (_fitted || Count == 0) return;

        var width = _viewport.ActualWidth;
        var height = _viewport.ActualHeight;
        if (width < 8 || height < 8) return;
        if (_viewport.Camera is not ProjectionCamera camera) return;

        // 竖直、水平两个视场角取小的那个，保证长边方向也装得下。
        // FieldOfView 只在透视相机上有；正交相机退化成固定 45°，够用。
        var fovY = (camera as PerspectiveCamera)?.FieldOfView * Math.PI / 180.0 ?? Math.PI / 4.0;
        var fovX = 2.0 * Math.Atan(Math.Tan(fovY / 2.0) * (width / height));
        var half = Math.Min(fovY, fovX) / 2.0;
        var distance = _radius / Math.Sin(half) * 1.06;   // 6% 留白

        // 从西南方向斜上方看，仰角约 30°。Z 朝上，靠 UpDirection 定住。
        var direction = new Vector3D(0.55, -1, 0.62);
        direction.Normalize();

        var position = _center + direction * distance;
        camera.Position = position;
        camera.LookDirection = _center - position;
        camera.UpDirection = new Vector3D(0, 0, 1);
        camera.NearPlaneDistance = distance / 1000.0;
        camera.FarPlaneDistance = distance * 100.0;

        _fitted = true;
    }

    /// <summary>只记包围球，机位等视口拿到尺寸再摆。</summary>
    private void Fit(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        _center = new Point3D(0.5 * (minX + maxX), 0.5 * (minY + maxY), 0.5 * (minZ + maxZ));

        var dx = maxX - minX;
        var dy = maxY - minY;
        var dz = maxZ - minZ;
        var radius = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
        _radius = radius > 0 ? radius : 1.0;

        _fitted = false;
    }

    /// <summary>0 = 最低（浅）→ 1 = 最高（深）。</summary>
    private static Color Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(Low.R + (High.R - Low.R) * t),
            (byte)(Low.G + (High.G - Low.G) * t),
            (byte)(Low.B + (High.B - Low.B) * t));
    }
}
