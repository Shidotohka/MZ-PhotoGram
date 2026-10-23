using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Photogrammetry.Core;

namespace Photogrammetry.App;

/// <summary>
/// 框标坐标的手工录入与改正。
/// <para>
/// 相机检校文件来自不同厂商，格式与内容都不保证；扫描影像也可能根本没有检校文件。
/// 所以必须留一条手工路径，而不是逼着用户去改文件。
/// </para>
/// </summary>
public partial class FiducialEditorWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private readonly string _cameraId;

    public ObservableCollection<Row> Rows { get; } = new();

    /// <summary>点「确定」后可用。取消时为 null。</summary>
    public Camera? Result { get; private set; }

    public sealed class Row : INotifyPropertyChanged
    {
        private int _id;
        private double _x;
        private double _y;

        public int Id
        {
            get => _id;
            set { _id = value; Raise(); }
        }

        public double X
        {
            get => _x;
            set { _x = value; Raise(); Raise(nameof(Where)); }
        }

        public double Y
        {
            get => _y;
            set { _y = value; Raise(); Raise(nameof(Where)); }
        }

        public string Where => FiducialHelper.Describe(X, Y);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public FiducialEditorWindow(Camera? camera)
    {
        InitializeComponent();
        _cameraId = camera?.Id ?? "手动录入";

        if (camera is not null)
        {
            BoxF.Text = camera.FocalLengthMm.ToString("0.###", Ci);
            BoxX0.Text = camera.X0Mm.ToString("0.####", Ci);
            BoxY0.Text = camera.Y0Mm.ToString("0.####", Ci);

            foreach (var fiducial in camera.Fiducials)
                Rows.Add(new Row { Id = fiducial.Id, X = fiducial.Xmm, Y = fiducial.Ymm });
        }

        // 没有现成数据时，先给 8 行空位，省得一行一行加
        while (Rows.Count < 4)
            Rows.Add(new Row { Id = Rows.Count + 1 });

        Grid.ItemsSource = Rows;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var nextId = Rows.Count == 0 ? 1 : Rows.Max(r => r.Id) + 1;
        Rows.Add(new Row { Id = nextId });
        Grid.ScrollIntoView(Rows[^1]);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not Row row) return;
        Rows.Remove(row);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 正在编辑的单元格必须先提交，否则读到的是旧值
        Grid.CommitEdit(DataGridEditingUnit.Row, true);

        ErrorText.Text = string.Empty;

        if (!TryRead(BoxF, out var f) || !TryRead(BoxX0, out var x0) || !TryRead(BoxY0, out var y0))
        {
            ErrorText.Text = "焦距与主点必须是数字（小数点用点）。";
            return;
        }

        if (f <= 0)
        {
            ErrorText.Text = "焦距必须大于 0。";
            return;
        }

        var filled = Rows.Where(r => r.X != 0 || r.Y != 0).ToList();
        if (filled.Count < 4)
        {
            ErrorText.Text = $"至少要 4 个框标，当前有 {filled.Count} 个填了坐标。";
            return;
        }

        var duplicated = filled.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            ErrorText.Text = $"编号 {duplicated.Key} 重复了。";
            return;
        }

        var camera = new Camera
        {
            Id = _cameraId,
            FocalLengthMm = f,
            X0Mm = x0,
            Y0Mm = y0,
        };

        foreach (var row in filled.OrderBy(r => r.Id))
            camera.Fiducials.Add(new Fiducial { Id = row.Id, Xmm = row.X, Ymm = row.Y });

        Result = camera;
        DialogResult = true;
    }

    private static bool TryRead(TextBox box, out double value) =>
        double.TryParse(box.Text, NumberStyles.Float, Ci, out value);
}
