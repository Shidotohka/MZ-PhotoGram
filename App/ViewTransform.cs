namespace Photogrammetry.App;

/// <summary>
/// 影像视图变换：screen = Scale × image + (Tx, Ty)。
/// 双图模式下两个视窗共享同一实例，实现联动（设计规范 ADR-07）。
/// </summary>
public sealed class ViewTransform
{
    public double Scale { get; set; } = 1.0;
    public double Tx { get; set; }
    public double Ty { get; set; }

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    public void CopyFrom(ViewTransform other)
    {
        Scale = other.Scale;
        Tx = other.Tx;
        Ty = other.Ty;
        Notify();
    }
}
