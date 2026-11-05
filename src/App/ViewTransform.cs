namespace Photogrammetry.App;

/// <summary>
/// 影像视图变换：screen = Scale × image + (Tx, Ty)。
///
/// <para><b>每个视窗自己一份，不共享。</b>两图的尺寸、比例尺、定向状态都可能不同，
/// 共享同一实例会让一张适配、另一张错位——那是 M1-e 修过的缺陷（设计规范 ADR-06）。
/// 曾经有过一个 <c>CopyFrom</c> 就是为共享写的，随那次修正一起删掉了；
/// 真正的双图联动（同步运动 + 固定视差）属 M6 立体观测，届时以开关形式另做。</para>
/// </summary>
public sealed class ViewTransform
{
    public double Scale { get; set; } = 1.0;
    public double Tx { get; set; }
    public double Ty { get; set; }

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();
}
