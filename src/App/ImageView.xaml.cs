using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Photogrammetry.Core;

namespace Photogrammetry.App;

public sealed class CursorEventArgs : EventArgs
{
    public ImagePixel Pixel { get; init; }
    public ImagePointMm? ImageMm { get; init; }
    public bool Inside { get; init; }
}

/// <summary>框标取点事件：用户在某张影像上点了某个框标。</summary>
public sealed class FiducialPickEventArgs : EventArgs
{
    public required int FiducialId { get; init; }
    public required double I { get; init; }
    public required double J { get; init; }
}

/// <summary>控制点刺点事件：用户在某张影像上为某个控制点点了一个位置。</summary>
public sealed class GcpPickEventArgs : EventArgs
{
    public required string GcpId { get; init; }
    public required double I { get; init; }
    public required double J { get; init; }
}

/// <summary>
/// 视窗显示哪一层标记。每个流程只画自己那层的点——内定向界面里不该出现控制点的刺点，
/// 后方交会界面里也不该出现框标十字，否则作业员分不清哪些属于自己的任务。
/// </summary>
public enum ImageLayer
{
    /// <summary>都画。临时取点、未分模块时用。</summary>
    All,
    /// <summary>只画框标。</summary>
    Fiducials,
    /// <summary>只画已绑定到控制点的像点。</summary>
    ControlPoints,
    /// <summary>什么都不画（等宽对照窗之类）。</summary>
    None
}

/// <summary>
/// 单个影像视窗：影像显示、中键平移、滚轮锚定缩放、取点与选中。
/// </summary>
public partial class ImageView : UserControl
{
    private const double MinScale = 0.05;
    private const double MaxScale = 8.0;
    private const double HitRadius = 10.0;

    private static readonly Brush UnboundBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0x73, 0x15));
    private static readonly Brush BoundBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6F, 0xA8));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush FiducialBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x5A));

    private sealed record MarkerVisual(Canvas Container, Ellipse Dot, TextBlock Label);

    private readonly Dictionary<ImagePoint, MarkerVisual> _markers = new();
    private readonly Dictionary<int, (Canvas Visual, FiducialMark Mark)> _fiducialVisuals = new();

    private ViewTransform _transform = new();
    private BitmapSource? _source;
    private ImageItem? _item;
    private ImagePoint? _selected;

    private bool _panning;
    private Point _panAnchor;
    private bool _userAdjusted;

    public event EventHandler<CursorEventArgs>? CursorMoved;
    public event EventHandler? ViewChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? ImageClosed;
    public event EventHandler<FiducialPickEventArgs>? FiducialPicked;

    /// <summary>框标取点模式下右键：请求撤掉刚取的那个框标。</summary>
    public event EventHandler? FiducialUndoRequested;

    /// <summary>
    /// 框标方式。为 true 时左键只走框标取点，绝不新建像点——哪怕此刻没有可用的框标编号，
    /// 也宁可不响应，否则用户会以为自己在点框标，实际却攒了一堆像点。
    /// </summary>
    public bool FiducialMode { get; set; }

    /// <summary>当前待测框标编号。仅在 <see cref="FiducialMode"/> 为 true 时有意义。</summary>
    public int? ActiveFiducialId { get; set; }

    /// <summary>框标数据变动后调用，重绘框标标记。</summary>
    public void RefreshFiducials() => SyncFiducialVisuals();

    /// <summary>像点集合变动后调用（例如外部直接增删了 <see cref="ImageItem.Points"/>），重绘标记。</summary>
    public void RefreshPoints() => SyncMarkers();

    private ImageLayer _layer = ImageLayer.All;

    /// <summary>这个视窗画哪一层标记。切换会立刻重绘。</summary>
    public ImageLayer Layer
    {
        get => _layer;
        set
        {
            if (_layer == value) return;
            _layer = value;
            SyncMarkers();
        }
    }

    private bool ShowsPoint(ImagePoint point) => _layer switch
    {
        ImageLayer.ControlPoints => point.BoundGcpId is not null,
        ImageLayer.Fiducials or ImageLayer.None => false,
        _ => true,
    };

    /// <summary>控制点刺点模式：左键只回报位置，由上层决定绑到哪个控制点、怎么落库。</summary>
    public event EventHandler<GcpPickEventArgs>? GcpPicked;

    /// <summary>
    /// 当前待刺的控制点号。非 null 时左键不再新建像点，而是回报一个刺点位置。
    /// 与框标方式互斥：框标方式优先（内定向时不会误刺控制点）。
    /// </summary>
    public string? ActiveGcpId { get; set; }

    public ImageView()
    {
        InitializeComponent();
        _transform.Changed += ApplyTransform;
        Loaded += (_, _) => { if (HasImage) FitToWindow(); };

        // 首次载入时布局可能尚未完成，尺寸变化时若用户还没手动缩放则重新适配
        Host.SizeChanged += (_, _) =>
        {
            if (!HasImage) return;
            if (_userAdjusted) ApplyTransform();
            else FitToWindow();
        };
    }

    public ImageItem? Image => _item;

    public bool HasImage => _source is not null;

    public BitmapSource? Source => _source;

    public ImagePoint? SelectedPoint => _selected;

    public string Caption
    {
        get => CaptionText.Text;
        set => CaptionText.Text = value;
    }

    public ViewTransform Transform
    {
        get => _transform;
        set
        {
            if (ReferenceEquals(_transform, value)) return;
            _transform.Changed -= ApplyTransform;
            _transform = value;
            _transform.Changed += ApplyTransform;
            ApplyTransform();
        }
    }

    public double Scale => _transform.Scale;

    // ---------- 影像 ----------

    public void SetImage(BitmapSource source, ImageItem item)
    {
        _source = source;
        _item = item;
        _selected = null;
        _userAdjusted = false;

        Photo.Source = source;
        Photo.Width = source.PixelWidth;
        Photo.Height = source.PixelHeight;
        Surface.Width = source.PixelWidth;
        Surface.Height = source.PixelHeight;

        EmptyHint.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        Caption = $"{item.Id} · {source.PixelWidth} × {source.PixelHeight}";
        StateText.Text = item.Interior is { } io
            ? $"f {io.FocalLength:0.0}mm · {io.PixelSizeMm * 1000:0.0}μm"
            : "";

        SyncMarkers();
        Dispatcher.BeginInvoke(new Action(FitToWindow), System.Windows.Threading.DispatcherPriority.Render);
    }

    public void Clear()
    {        _source = null;
        _item = null;
        _selected = null;
        _userAdjusted = false;

        Photo.Source = null;
        EmptyHint.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Collapsed;
        Caption = "未选择影像";
        StateText.Text = "";

        _markers.Clear();
        Overlay.Children.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Clear();
        ImageClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>拖拽经过时高亮本视窗，提示可以放入。</summary>
    public void ShowDropHint(bool show) =>
        DropHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 取点 ----------
    /// <summary>在指定影像坐标处新建一个未绑定像点并选中。</summary>
    public ImagePoint AddPointAt(Point imagePosition)
    {
        if (_item is null) throw new InvalidOperationException("尚未载入影像");

        var px = (int)Math.Round(imagePosition.X);
        var py = (int)Math.Round(imagePosition.Y);

        var point = new ImagePoint
        {
            Id = NextPointId(),
            ImageId = _item.Id,
            PixelX = px,
            PixelY = py,
            BoundGcpId = null
        };

        if (_item.Interior is { } io)
        {
            point.PositionMm = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(px, py), io, _item.Width, _item.Height);
        }

        _item.Points.Add(point);
        SyncMarkers();
        Select(point);
        return point;
    }

    public void Select(ImagePoint? point)
    {
        if (ReferenceEquals(_selected, point)) return;
        _selected = point;
        UpdateMarkerStyles();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>右键语义：未绑定的点删除，已绑定的点只取消选中。</summary>
    public void CancelSelection()
    {
        if (_selected is null) return;

        if (!_selected.IsBound && _item is not null)
        {
            _item.Points.Remove(_selected);
            RemoveMarker(_selected);
            _selected = null;
            SyncMarkers();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Select(null);
    }

    public void DeleteSelected()
    {
        if (_selected is null || _selected.IsBound) return;
        CancelSelection();
    }

    private string NextPointId()
    {
        if (_item is null) return "P01";
        for (var n = 1; n < 10000; n++)
        {
            var id = $"P{n:00}";
            if (_item.Points.All(p => p.Id != id)) return id;
        }
        return $"P{_item.Points.Count + 1}";
    }

    // ---------- 点位绘制 ----------

    private void SyncMarkers()
    {
        if (_item is null)
        {
            foreach (var v in _markers.Values) Overlay.Children.Remove(v.Container);
            _markers.Clear();
            SyncFiducialVisuals();
            return;
        }

        // 不属于本层的点要撤掉标记；属于本层的补上
        foreach (var stale in _markers.Keys.Where(p => !_item.Points.Contains(p) || !ShowsPoint(p)).ToList())
        {
            Overlay.Children.Remove(_markers[stale].Container);
            _markers.Remove(stale);
        }

        foreach (var point in _item.Points.Where(p => ShowsPoint(p) && !_markers.ContainsKey(p)))
        {
            var visual = CreateMarker(point);
            _markers[point] = visual;
            Overlay.Children.Add(visual.Container);
        }

        if (_selected is not null && !_markers.ContainsKey(_selected)) _selected = null;

        // 绑定关系可能在外部改过，标签跟着变
        foreach (var (point, visual) in _markers)
        {
            var label = point.BoundGcpId ?? point.Id;
            if (visual.Label.Text != label) visual.Label.Text = label;
        }

        UpdateMarkerStyles();
        PositionMarkers();
        SyncFiducialVisuals();
    }

    private MarkerVisual CreateMarker(ImagePoint point)
    {
        var container = new Canvas { Width = 0, Height = 0 };

        var dot = new Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            Stroke = UnboundBrush,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(dot, -7);
        Canvas.SetTop(dot, -7);

        var label = new TextBlock
        {
            // 已绑定的点显示控制点点号，比内部观测号有用得多
            Text = point.BoundGcpId ?? point.Id,
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Foreground = UnboundBrush
        };
        Canvas.SetLeft(label, 10);
        Canvas.SetTop(label, -7);

        container.Children.Add(dot);
        container.Children.Add(label);
        return new MarkerVisual(container, dot, label);
    }

    private void RemoveMarker(ImagePoint point)
    {
        if (!_markers.TryGetValue(point, out var visual)) return;
        Overlay.Children.Remove(visual.Container);
        _markers.Remove(point);
    }

    private void PositionMarkers()
    {
        foreach (var (point, visual) in _markers)
        {
            var screen = ImageToScreen(new Point(point.PixelX, point.PixelY));
            Canvas.SetLeft(visual.Container, screen.X);
            Canvas.SetTop(visual.Container, screen.Y);
        }
    }

    private void UpdateMarkerStyles()
    {
        foreach (var (point, visual) in _markers)
        {
            var isSelected = ReferenceEquals(point, _selected);
            var brush = isSelected ? SelectedBrush : point.IsBound ? BoundBrush : UnboundBrush;

            visual.Dot.Stroke = brush;
            visual.Dot.StrokeDashArray = point.IsBound ? null : new DoubleCollection { 3, 2 };
            visual.Dot.StrokeThickness = isSelected ? 2.5 : 2;
            visual.Dot.Width = isSelected ? 18 : 14;
            visual.Dot.Height = isSelected ? 18 : 14;
            Canvas.SetLeft(visual.Dot, isSelected ? -9 : -7);
            Canvas.SetTop(visual.Dot, isSelected ? -9 : -7);
            visual.Label.Foreground = brush;
        }
    }

    private void SyncFiducialVisuals()
    {
        foreach (var (visual, _) in _fiducialVisuals.Values) Overlay.Children.Remove(visual);
        _fiducialVisuals.Clear();

        // 框标只属于内定向那一层
        if (_item is null || _layer is ImageLayer.ControlPoints or ImageLayer.None) return;

        foreach (var mark in _item.FiducialMarks)
        {
            var visual = CreateFiducialVisual(mark);
            _fiducialVisuals[mark.FiducialId] = (visual, mark);
            Overlay.Children.Add(visual);
        }

        PositionFiducials();
    }

    private static Canvas CreateFiducialVisual(FiducialMark mark)
    {
        var container = new Canvas { Width = 0, Height = 0 };

        var box = new Rectangle
        {
            Width = 18,
            Height = 18,
            Stroke = FiducialBrush,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            RadiusX = 2,
            RadiusY = 2,
        };
        Canvas.SetLeft(box, -9);
        Canvas.SetTop(box, -9);

        var label = new TextBlock
        {
            Text = mark.FiducialId.ToString(),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Foreground = FiducialBrush,
        };
        Canvas.SetLeft(label, 12);
        Canvas.SetTop(label, -7);

        container.Children.Add(box);
        container.Children.Add(label);
        return container;
    }

    private void PositionFiducials()
    {
        foreach (var (visual, mark) in _fiducialVisuals.Values)
        {
            var screen = ImageToScreen(new Point(mark.I, mark.J));
            Canvas.SetLeft(visual, screen.X);
            Canvas.SetTop(visual, screen.Y);
        }
    }

    private ImagePoint? HitTest(Point screen)
    {
        if (_item is null) return null;

        ImagePoint? best = null;
        var bestDistance = HitRadius;

        foreach (var point in _item.Points)
        {
            var target = ImageToScreen(new Point(point.PixelX, point.PixelY));
            var distance = (target - screen).Length;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = point;
            }
        }

        return best;
    }

    // ---------- 变换 ----------

    public Point ScreenToImage(Point screen) =>
        new((screen.X - _transform.Tx) / _transform.Scale,
            (screen.Y - _transform.Ty) / _transform.Scale);

    public Point ImageToScreen(Point image) =>
        new(image.X * _transform.Scale + _transform.Tx,
            image.Y * _transform.Scale + _transform.Ty);

    /// <summary>以视窗内某点为中心缩放。</summary>
    public void ZoomAt(Point screen, double factor)
    {
        if (!HasImage) return;

        var image = ScreenToImage(screen);
        var next = Math.Clamp(_transform.Scale * factor, MinScale, MaxScale);
        if (Math.Abs(next - _transform.Scale) < 1e-9) return;

        var k = next / _transform.Scale;
        _userAdjusted = true;
        _transform.Scale = next;
        _transform.Tx = screen.X - k * (screen.X - _transform.Tx);
        _transform.Ty = screen.Y - k * (screen.Y - _transform.Ty);
        _transform.Notify();
    }

    public void ZoomBy(double factor) =>
        ZoomAt(new Point(Host.ActualWidth / 2, Host.ActualHeight / 2), factor);

    /// <summary>适配窗口并居中。</summary>
    public void FitToWindow()
    {
        if (!HasImage || _source is null) return;

        var w = Host.ActualWidth;
        var h = Host.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var scale = Math.Min(w / _source.PixelWidth, h / _source.PixelHeight) * 0.98;
        scale = Math.Clamp(scale, MinScale, MaxScale);

        _transform.Scale = scale;
        _transform.Tx = (w - _source.PixelWidth * scale) / 2;
        _transform.Ty = (h - _source.PixelHeight * scale) / 2;
        _userAdjusted = false;
        _transform.Notify();
    }

    private void ApplyTransform()
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(_transform.Scale, _transform.Scale));
        group.Children.Add(new TranslateTransform(_transform.Tx, _transform.Ty));
        Surface.RenderTransform = group;

        RenderOptions.SetBitmapScalingMode(
            Photo,
            _transform.Scale >= 2
                ? BitmapScalingMode.NearestNeighbor
                : BitmapScalingMode.HighQuality);

        PositionMarkers();
        PositionFiducials();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 交互 ----------

    private void Host_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !HasImage) return;

        _panning = true;
        _panAnchor = e.GetPosition(Host);
        Host.CaptureMouse();
        Host.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Host_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) EndPan();
    }

    private void Host_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning) return;
        CursorMoved?.Invoke(this, new CursorEventArgs { Inside = false });
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Host.ReleaseMouseCapture();
        Host.Cursor = null;
    }

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(Host);

        if (_panning)
        {
            var delta = position - _panAnchor;
            _panAnchor = position;
            _userAdjusted = true;
            _transform.Tx += delta.X;
            _transform.Ty += delta.Y;
            _transform.Notify();
        }

        ReportCursor(position);
    }

    private void Host_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasImage) return;
        ZoomAt(e.GetPosition(Host), e.Delta > 0 ? 1.16 : 1 / 1.16);
        e.Handled = true;
    }

    private void Host_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage || _item is null || _source is null) return;

        var screen = e.GetPosition(Host);

        // 框标方式：左键只给框标取点，不新建像点
        if (FiducialMode)
        {
            e.Handled = true;

            if (ActiveFiducialId is not { } fiducialId) return;

            var pick = ScreenToImage(screen);
            if (pick.X < 0 || pick.Y < 0 ||
                pick.X >= _source.PixelWidth || pick.Y >= _source.PixelHeight) return;

            FiducialPicked?.Invoke(this, new FiducialPickEventArgs
            {
                FiducialId = fiducialId,
                I = pick.X,
                J = pick.Y,
            });
            e.Handled = true;
            return;
        }

        // 刺点模式：左键只回报位置，不建临时点，也不做命中判定。
        // 重刺就是移动该控制点的像点，所以压在已有标记上也要照点不误。
        if (ActiveGcpId is { } gcpId)
        {
            var pick = ScreenToImage(screen);
            if (pick.X < 0 || pick.Y < 0 ||
                pick.X >= _source.PixelWidth || pick.Y >= _source.PixelHeight) return;

            GcpPicked?.Invoke(this, new GcpPickEventArgs
            {
                GcpId = gcpId,
                I = pick.X,
                J = pick.Y,
            });
            e.Handled = true;
            return;
        }

        var hit = HitTest(screen);
        if (hit is not null)
        {
            Select(hit);
            e.Handled = true;
            return;
        }

        var image = ScreenToImage(screen);
        if (image.X < 0 || image.Y < 0 ||
            image.X >= _source.PixelWidth || image.Y >= _source.PixelHeight) return;

        AddPointAt(image);
        e.Handled = true;
    }

    private void Host_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 框标方式下，右键回退刚取的那个框标
        if (FiducialMode)
        {
            FiducialUndoRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        CancelSelection();
    }

    private void ReportCursor(Point screen)
    {
        if (!HasImage || _source is null)
        {
            CursorMoved?.Invoke(this, new CursorEventArgs { Inside = false });
            return;
        }

        var image = ScreenToImage(screen);
        var inside = image.X >= 0 && image.Y >= 0
                     && image.X < _source.PixelWidth && image.Y < _source.PixelHeight;

        var pixel = new ImagePixel((int)Math.Round(image.X), (int)Math.Round(image.Y));

        ImagePointMm? mm = null;
        if (_item?.Interior is { } io && io.IsValid)
        {
            mm = CoordinateConverter.PixelToImagePlane(pixel, io, _source.PixelWidth, _source.PixelHeight);
        }

        CursorMoved?.Invoke(this, new CursorEventArgs { Pixel = pixel, ImageMm = mm, Inside = inside });
    }
}
