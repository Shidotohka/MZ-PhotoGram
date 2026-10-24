using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>工作区形态，与设计规范 4.2 一一对应。</summary>
public enum WorkspaceMode
{
    Single,
    /// <summary>刺点：左为控制点放大影像，右为整幅航摄影像。后方交会用这个。</summary>
    Pick,
    Double,
    Model,
    Report
}

public sealed class ImageDropEventArgs : EventArgs
{
    public ImageDropEventArgs(ImageItem item, ImageView target)
    {
        Item = item;
        Target = target;
    }

    public ImageItem Item { get; }

    public ImageView Target { get; }
}

public partial class ImageWorkspace : UserControl
{
    /// <summary>当前正在交互的视窗（鼠标最后进入的那个）。</summary>
    private ImageView? _active;

    public event EventHandler<CursorEventArgs>? CursorMoved;
    public event EventHandler? ViewChanged;
    public event EventHandler<ImageItem>? ImageLoaded;
    public event EventHandler? SelectionChanged;
    public event EventHandler? ImageClosed;
    public event EventHandler<ImageDropEventArgs>? ImageDropped;

    /// <summary>框标取点。sender 是发生取点的那个 <see cref="ImageView"/>。</summary>
    public event EventHandler<FiducialPickEventArgs>? FiducialPicked;

    /// <summary>框标取点模式下右键，请求撤销上一个框标。</summary>
    public event EventHandler? FiducialUndoRequested;

    /// <summary>控制点刺点。sender 是发生刺点的那个 <see cref="ImageView"/>。</summary>
    public event EventHandler<GcpPickEventArgs>? GcpPicked;

    public ImageWorkspace()
    {
        InitializeComponent();

        // 每个视窗拥有独立的变换：双图之间互不影响（ADR-07 修订）
        foreach (var view in new[] { SingleView, PickView, LeftView, RightView })
        {
            view.CursorMoved += (_, e) =>
            {
                if (e.Inside) _active = view;
                CursorMoved?.Invoke(this, e);
            };
            view.ViewChanged += (_, e) => ViewChanged?.Invoke(this, e);
            view.SelectionChanged += (_, e) =>
            {
                _active = view;
                SelectionChanged?.Invoke(this, e);
            };
            view.ImageClosed += (_, e) => ImageClosed?.Invoke(this, e);
            view.FiducialPicked += (_, e) => FiducialPicked?.Invoke(view, e);
            view.FiducialUndoRequested += (_, e) => FiducialUndoRequested?.Invoke(view, e);
            view.GcpPicked += (_, e) => GcpPicked?.Invoke(view, e);
        }

        AllowDrop = true;
        DragOver += OnDragOver;
        DragLeave += (_, _) => HideDropHints();
        Drop += OnDrop;

        SingleView.Caption = "IMG_0001 · 单像量测";
        PickView.Caption = "航摄影像 · 未选择";
        LeftView.Caption = "左影像 · 未选择";
        RightView.Caption = "右影像 · 未选择";
    }

    public WorkspaceMode Mode { get; private set; } = WorkspaceMode.Double;

    /// <summary>当前视窗：优先鼠标所在视窗，否则取第一个有影像的可见视窗。</summary>
    public ImageView? ActiveView =>
        _active is not null && VisibleViews().Contains(_active)
            ? _active
            : VisibleViews().FirstOrDefault(v => v.HasImage);

    public double Scale => ActiveView?.Scale ?? 1.0;

    public void SetMode(WorkspaceMode mode)
    {
        Mode = mode;
        SinglePanel.Visibility = mode == WorkspaceMode.Single ? Visibility.Visible : Visibility.Collapsed;
        PickPanel.Visibility = mode == WorkspaceMode.Pick ? Visibility.Visible : Visibility.Collapsed;
        DoublePanel.Visibility = mode == WorkspaceMode.Double ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = mode == WorkspaceMode.Model ? Visibility.Visible : Visibility.Collapsed;
        ReportPanel.Visibility = mode == WorkspaceMode.Report ? Visibility.Visible : Visibility.Collapsed;

        EnsureVisibleImage();
    }

    /// <summary>切换形态后若当前可见视窗全空，把已载入的影像带过去。</summary>
    private void EnsureVisibleImage()
    {
        var visible = VisibleViews().ToList();
        if (visible.Count == 0) return;
        if (visible.Any(v => v.HasImage)) return;

        var donor = new[] { SingleView, PickView, LeftView, RightView }
            .FirstOrDefault(v => v.HasImage && v.Image is not null && v.Source is not null);

        if (donor is null) return;
        visible[0].SetImage(donor.Source!, donor.Image!);
        ImageLoaded?.Invoke(this, donor.Image!);
    }

    /// <summary>载入影像；若该影像已在某个可见视窗中显示，则不再重复载入。</summary>
    public ImageView LoadImage(BitmapSource source, ImageItem item)
    {
        foreach (var existing in VisibleViews())
        {
            if (ReferenceEquals(existing.Image, item)) return existing;
        }

        var target = NextTarget();
        target.SetImage(source, item);
        ImageLoaded?.Invoke(this, item);
        return target;
    }

    /// <summary>清空全部视窗。</summary>
    public void Reset()
    {
        SingleView.Clear();
        PickView.Clear();
        LeftView.Clear();
        RightView.Clear();
    }

    // ---------- 载入 / 移除 / 拖放 ----------

    /// <summary>把影像载入到指定视窗。</summary>
    public void LoadInto(ImageView view, BitmapSource source, ImageItem item)
    {
        view.SetImage(source, item);
        ImageLoaded?.Invoke(this, item);
    }

    /// <summary>该影像当前显示在哪个视窗（"单"/"左"/"右"），未显示返回 null。</summary>
    public string? SlotOf(ImageItem item)
    {
        if (SingleView.Image is { } s && ReferenceEquals(s, item)) return "单";
        if (LeftView.Image is { } l && ReferenceEquals(l, item)) return "左";
        if (RightView.Image is { } r && ReferenceEquals(r, item)) return "右";
        return null;
    }

    public void Unload(ImageItem item)
    {
        foreach (var view in new[] { SingleView, PickView, LeftView, RightView })
        {
            if (view.Image is { } current && ReferenceEquals(current, item)) view.Clear();
        }
    }

    private ImageView? ViewAt(Point position)
    {
        if (SinglePanel.Visibility == Visibility.Visible) return SingleView;
        if (PickPanel.Visibility == Visibility.Visible) return PickView;
        if (DoublePanel.Visibility != Visibility.Visible) return null;
        return position.X < ActualWidth / 2 ? LeftView : RightView;
    }

    private void HideDropHints()
    {
        SingleView.ShowDropHint(false);
        PickView.ShowDropHint(false);
        LeftView.ShowDropHint(false);
        RightView.ShowDropHint(false);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var acceptable = e.Data.GetDataPresent(typeof(ImageItem));
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;

        HideDropHints();
        if (acceptable && ViewAt(e.GetPosition(this)) is { } target) target.ShowDropHint(true);

        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        HideDropHints();

        if (e.Data.GetData(typeof(ImageItem)) is not ImageItem item) return;
        if (ViewAt(e.GetPosition(this)) is not { } target) return;

        ImageDropped?.Invoke(this, new ImageDropEventArgs(item, target));
        e.Handled = true;
    }

    /// <summary>四个视窗，不论当前是否可见。</summary>
    public IEnumerable<ImageView> AllViews() => new[] { SingleView, PickView, LeftView, RightView };

    /// <summary>切换框标方式。开启时左键只走框标，不再新建像点。</summary>
    public void SetFiducialMode(bool enabled)
    {
        foreach (var view in AllViews()) view.FiducialMode = enabled;
    }

    /// <summary>设置当前待测框标编号；null 表示暂时没有可点的框标。</summary>
    public void SetActiveFiducial(int? fiducialId)
    {
        foreach (var view in AllViews()) view.ActiveFiducialId = fiducialId;
    }

    /// <summary>框标数据变动后，重绘所有视窗的框标标记。</summary>
    public void RefreshFiducials()
    {
        foreach (var view in AllViews()) view.RefreshFiducials();
    }

    /// <summary>设置当前待刺的控制点号；null 表示退出刺点模式。</summary>
    public void SetActiveGcp(string? gcpId)
    {
        foreach (var view in AllViews()) view.ActiveGcpId = gcpId;
    }

    /// <summary>设定各视窗画哪一层标记。每个流程只显示自己那层的点。</summary>
    public void SetLayer(ImageLayer layer)
    {
        foreach (var view in AllViews()) view.Layer = layer;
    }

    public IEnumerable<ImageView> VisibleViews()
    {
        if (SinglePanel.Visibility == Visibility.Visible) yield return SingleView;
        if (PickPanel.Visibility == Visibility.Visible) yield return PickView;
        if (DoublePanel.Visibility == Visibility.Visible)
        {
            yield return LeftView;
            yield return RightView;
        }
    }

    private ImageView NextTarget()
    {
        var views = VisibleViews().ToList();
        if (views.Count == 0) return SingleView;
        return views.FirstOrDefault(v => !v.HasImage) ?? views[0];
    }

    public void ZoomBy(double factor) => ActiveView?.ZoomBy(factor);

    public void FitToWindow()
    {
        if (ActiveView is { } active)
        {
            active.FitToWindow();
            return;
        }

        foreach (var view in VisibleViews())
        {
            if (view.HasImage) view.FitToWindow();
        }
    }

    // ---------- 取点 ----------

    public ImagePoint? SelectedPoint =>
        VisibleViews().Select(v => v.SelectedPoint).FirstOrDefault(p => p is not null);

    /// <summary>在可见视窗中选中指定像点；该点所属影像必须已经显示。</summary>
    public bool SelectPoint(ImagePoint point)
    {
        foreach (var view in VisibleViews())
        {
            if (view.Image?.Points.Contains(point) != true) continue;
            view.Select(point);
            return true;
        }
        return false;
    }

    public int PointCount => VisibleViews().Sum(v => v.Image?.Points.Count ?? 0);

    public void CancelSelection()
    {
        foreach (var view in VisibleViews()) view.CancelSelection();
    }

    public void DeleteSelected()
    {
        foreach (var view in VisibleViews()) view.DeleteSelected();
    }

    // ---------- 参考放大影像 ----------

    /// <summary>
    /// 在刺点形态左侧显示某个控制点的放大影像。
    /// 文件不存在时只显示提示，<b>不影响刺点</b>——这块只是给作业员做对照用的。
    /// </summary>
    public void ShowReference(string? path, string caption)
    {
        ReferenceCaption.Text = caption;

        BitmapSource? bitmap = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                bitmap = image;
            }
            catch (Exception)
            {
                bitmap = null;
            }
        }

        ReferenceImage.Source = bitmap;
        ReferenceImage.Visibility = bitmap is null ? Visibility.Collapsed : Visibility.Visible;
        ReferenceHint.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>清掉参考影像。</summary>
    public void ClearReference() => ShowReference(null, "控制点放大影像");
}
