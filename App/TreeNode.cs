using System.Collections.ObjectModel;
using System.Windows.Media;

namespace Photogrammetry.App;

/// <summary>数据树的节点视图模型。Tag 指向真实的工程实体。</summary>
public sealed class TreeNode
{
    public string Icon { get; init; } = "";

    public string Title { get; init; } = "";

    public string Badge { get; init; } = "";

    public Brush BadgeBrush { get; init; } = Brushes.Gray;

    public bool IsExpanded { get; init; } = true;

    /// <summary>空分组时的提示行，不参与选中。</summary>
    public bool IsPlaceholder { get; init; }

    public object? Tag { get; init; }

    public ObservableCollection<TreeNode> Children { get; } = new();
}
