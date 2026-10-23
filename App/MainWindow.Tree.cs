using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 数据树：节点构建、拖拽载入、双击选中、右键菜单。
/// </summary>
public partial class MainWindow
{
    // ---------- 数据树 ----------

    private void RefreshTree()
    {
        var roots = new List<TreeNode>
        {
            BuildImageGroup(),
            BuildPlaceholderGroup("控制点", "◆", _project.Gcps.Count, "尚未导入控制点"),
            BuildPlaceholderGroup("像对", "⊟", _project.Pairs.Count, "尚未建立像对"),
            BuildPlaceholderGroup("模型", "◈", _project.Models.Count, "尚未建立模型"),
            BuildPlaceholderGroup("计算结果", "≡", 0, "尚无计算结果")
        };

        DataTree.ItemsSource = roots;
    }

    private TreeNode BuildImageGroup()
    {
        var group = new TreeNode
        {
            Icon = "▣",
            Title = $"影像库 · {_project.Images.Count}"
        };

        if (_project.Images.Count == 0)
        {
            group.Children.Add(Placeholder("尚未添加影像"));
            return group;
        }

        foreach (var image in _project.Images)
        {
            var slot = Workspace.SlotOf(image);
            var missing = image.FileState == ImageFileState.Missing;

            var badge = slot ?? (missing ? "缺失" : image.Points.Count > 0 ? $"{image.Points.Count} 点" : "");
            var badgeBrush = slot is not null ? AccentBadge : missing ? ErrBadge : DimBadge;

            var node = new TreeNode
            {
                Icon = missing ? "⊘" : slot is not null ? "▶" : "▣",
                Title = missing ? $"{image.Id}（文件缺失）" : image.Id,
                Badge = badge,
                BadgeBrush = badgeBrush,
                Tag = image
            };

            foreach (var point in image.Points)
            {
                node.Children.Add(new TreeNode
                {
                    Icon = "·",
                    Title = point.Id,
                    Badge = $"{point.PixelX}, {point.PixelY}",
                    BadgeBrush = DimBadge,
                    Tag = point,
                    IsExpanded = false
                });
            }

            group.Children.Add(node);
        }

        return group;
    }

    private TreeNode BuildPlaceholderGroup(string title, string icon, int count, string emptyHint)
    {
        var group = new TreeNode { Icon = icon, Title = $"{title} · {count}" };
        if (count == 0) group.Children.Add(Placeholder(emptyHint));
        return group;
    }

    private static TreeNode Placeholder(string text) => new()
    {
        Icon = "",
        Title = text,
        IsPlaceholder = true,
        BadgeBrush = DimBadge
    };

    private void DataTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeNode { Tag: ImagePoint point }) return;

        var owner = _project.Images.FirstOrDefault(i => i.Points.Contains(point));
        if (owner is not null) _ = SelectPointAsync(owner, point);
    }

    private async Task SelectPointAsync(ImageItem owner, ImagePoint point)
    {
        await LoadItemAsync(owner);
        Workspace.SelectPoint(point);
    }

    // ---------- 树上的拖拽 / 双击 / 右键 ----------

    private void DataTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void DataTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var delta = _dragStart - e.GetPosition(null);
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (SelectedNodeImage() is not { } image) return;

        DragDrop.DoDragDrop(DataTree, new DataObject(typeof(ImageItem), image), DragDropEffects.Copy);
    }

    /// <summary>
    /// 右键不会改变 TreeView 的选中项，导致右键菜单不知道作用于哪个节点。
    /// 这里先把光标下的节点选中，菜单才能拿到正确的目标。
    /// </summary>
    private void DataTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        item.IsSelected = true;
        item.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void DataTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedNodeImage() is { } image) _ = LoadItemAsync(image);
    }

    private ImageItem? SelectedNodeImage() =>
        (DataTree.SelectedItem as TreeNode)?.Tag as ImageItem;

    private void CtxLoad_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNodeImage() is { } image) _ = LoadItemAsync(image);
    }

    private void CtxUnload_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNodeImage() is not { } image) return;
        Workspace.Unload(image);
        RefreshTree();
    }

    /// <summary>从工程里移除影像。只去掉索引记录，磁盘文件不动。</summary>
    private void CtxRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNodeImage() is not { } image) return;

        var answer = MessageBox.Show(this,
            $"从工程中移除影像「{image.Id}」？\n\n只从工程里去掉，磁盘上的文件不受影响。",
            "移除影像", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Workspace.Unload(image);
        _project.Images.Remove(image);
        _bitmapCache.Remove(image.Id);

        RefreshTree();
        UpdateStatusCounts();
        UpdateFiducialUi();
        ImageInfoText.Text = "未载入";

        DiagSummary.Text = $"✓ 已从工程移除 {image.Id}（文件未删除）";
        DiagSummary.Foreground = OkBadge;
    }

}
