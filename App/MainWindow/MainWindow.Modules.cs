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
/// 作业方法与模块切换。
/// 
/// 每个新模块要在这里加一条 Modules 项、一条 Workflows 步骤、一个 ApplyModule 分支——
/// 这也正是 Docs/重构计划.md 里"新增模块要改六处"的其中三处。
/// </summary>
public partial class MainWindow
{
    // ---------- 模块 ----------

    /// <summary>
    /// 切换作业方法：模块栏只留该方法的那一串步骤，按从左到右的顺序走。
    ///
    /// 当前模块若属于被换掉的方法，就落到新方法的第一步——否则会出现"选中的按钮
    /// 已被藏起来、模块栏看起来一个都没选中"的状态。
    /// </summary>
    private void Method_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MethodBox.SelectedIndex < 0 || MethodBox.SelectedIndex >= Workflows.Length) return;
        var steps = Workflows[MethodBox.SelectedIndex].Steps;

        foreach (var rb in ModuleBar.Children.OfType<RadioButton>())
        {
            var name = rb.Content?.ToString() ?? string.Empty;
            rb.Visibility = steps.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
        }

        var current = ModuleBar.Children.OfType<RadioButton>()
            .FirstOrDefault(r => r.IsChecked == true);
        if (current is null || current.Visibility != Visibility.Visible)
        {
            var first = ModuleBar.Children.OfType<RadioButton>()
                .FirstOrDefault(r => r.Content?.ToString() == steps[0]);
            if (first is not null) first.IsChecked = true;
        }
    }

    /// <summary>
    /// 按模块名选中，并自动切到包含该模块的作业方法。
    ///
    /// 只给离屏渲染工具（Snap）用——界面上没有入口，正常切换走模块栏。之所以放在
    /// 这里而不是工具里：哪个模块属于哪条方法，只有 <see cref="Workflows"/> 知道，
    /// 工具自己去猜就会踩「选中了一个被藏起来的按钮」这个坑。全表见 MainWindow.Tooling.cs。
    /// </summary>
    internal bool SelectModuleForTooling(string moduleName)
    {
        var index = Array.FindIndex(Workflows, w => w.Steps.Contains(moduleName));
        if (index < 0) return false;

        if (MethodBox.SelectedIndex != index) MethodBox.SelectedIndex = index;

        var rb = ModuleBar.Children.OfType<RadioButton>()
            .FirstOrDefault(r => r.Content?.ToString() == moduleName);
        if (rb is null) return false;

        rb.IsChecked = true;
        return true;
    }

    private void Module_Checked(object sender, RoutedEventArgs e)
    {
        if (Workspace is null || sender is not RadioButton rb) return;
        ApplyModule(rb);
    }

    /// <summary>当前模块名，点表与标记层都按它分发。</summary>
    private string _moduleName = "内定向";

    private void ApplyModule(RadioButton rb)
    {
        var name = rb.Content?.ToString() ?? string.Empty;
        _moduleName = name;
        InspectorTitle.Text = name;
        StatusTask.Text = name;

        if (Modules.TryGetValue(name, out var info))
        {
            ModuleHint.Text = info.Hint;
            Workspace.SetMode(info.Mode);
            PointTools.Visibility = info.Mode == WorkspaceMode.Report
                ? Visibility.Collapsed
                : Visibility.Visible;
            RefreshTree();
        }

        var isInterior = name == "内定向";
        var isResection = name == "后方交会";
        var isIntersection = name == "前方交会";
        InteriorPanel.Visibility = isInterior ? Visibility.Visible : Visibility.Collapsed;
        ResectionPanel.Visibility = isResection ? Visibility.Visible : Visibility.Collapsed;
        IntersectionPanel.Visibility = isIntersection ? Visibility.Visible : Visibility.Collapsed;
        GenericTaskPanel.Visibility = isInterior || isResection || isIntersection
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (isInterior)
        {
            ApplyInteriorMode(ModeFiducial.IsChecked == true);
        }
        else
        {
            // 离开内定向必须把框标方式也关掉。只清"待测框标编号"是不够的：
            // 框标方式还开着时，左键会被框标分支吞掉，后方交会里怎么点都没反应。
            Workspace.SetFiducialMode(false);
            CancelFiducialPicking();
        }

        // 每个流程只显示自己那层的点：内定向看不到刺点，后方交会看不到框标
        Workspace.SetLayer(isInterior ? ImageLayer.Fiducials
            : isResection ? ImageLayer.ControlPoints
            : ImageLayer.All);

        // 刺点只在后方交会里生效；离开这个模块就退出刺点模式，免得在内定向里误点
        if (!isResection) SetActiveGcp(null);

        RefreshPointTable();
        if (isResection) UpdateResectionPanel();

        // 离开前方交会就把配对清掉：留着两个临时像点却不在这个模块，谁也说不清它们是什么
        if (isIntersection) UpdateIntersectionPanel();
        else ClearIntersectionPair();
    }

}
