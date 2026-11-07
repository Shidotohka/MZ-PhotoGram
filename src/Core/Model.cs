namespace Photogrammetry.Core;

public enum GcpType
{
    /// <summary>平高控制点。</summary>
    Planimetric,
    /// <summary>高程控制点。</summary>
    Elevation,
    /// <summary>检查点，不参与解算。</summary>
    CheckPoint
}

public enum ImageStatus
{
    NotProcessed,
    InteriorDone,
    Oriented
}

/// <summary>框标：相机级的已知量，像平面坐标（mm，原点像主点，y 向上）。</summary>
public sealed class Fiducial
{
    public required int Id { get; init; }
    public double Xmm { get; set; }
    public double Ymm { get; set; }
}

/// <summary>
/// 相机：内方位元素与框标坐标。框标属于相机而非影像——同一台相机的框标坐标固定，
/// 每张影像只是各自量了一组像元坐标（ADR-13）。
/// </summary>
public sealed class Camera
{
    public required string Id { get; init; }
    public double FocalLengthMm { get; set; }
    public double X0Mm { get; set; }
    public double Y0Mm { get; set; }
    public List<Fiducial> Fiducials { get; } = new();

    /// <summary>少于 4 个框标时不能做仿射内定向。</summary>
    public bool HasEnoughFiducials => Fiducials.Count >= 4;
}

/// <summary>框标量测：某张影像上量到的框标像元坐标，允许小数。</summary>
public sealed class FiducialMark
{
    public required int FiducialId { get; init; }
    public double I { get; set; }
    public double J { get; set; }
}

/// <summary>外方位元素。位置单位 m，姿态单位为度（传入算法前转弧度）。</summary>
public sealed class ExteriorOrientation
{
    public double Xs { get; set; }
    public double Ys { get; set; }
    public double Zs { get; set; }
    public double Phi { get; set; }
    public double Omega { get; set; }
    public double Kappa { get; set; }
}

/// <summary>影像。持有一条记录的索引信息与内/外方位元素，不含像素数据。</summary>
public sealed class ImageItem
{
    public required string Id { get; init; }

    /// <summary>相对工程文件的路径，分隔符为 '/'。保存时由 FullPath 计算得出。</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>运行时解析出的绝对路径，不写盘。</summary>
    public string? FullPath { get; set; }

    /// <summary>true 表示影像位于工程目录之外，工程不可整体迁移。</summary>
    public bool External { get; set; }

    /// <summary>打开工程时检测到的文件状态。</summary>
    public ImageFileState FileState { get; set; } = ImageFileState.Ok;

    public int Width { get; set; }
    public int Height { get; set; }

    public InteriorOrientation? Interior { get; set; }
    public ExteriorOrientation? Exterior { get; set; }
    public ImageStatus Status { get; set; } = ImageStatus.NotProcessed;

    /// <summary>
    /// 后方交会判定出的地面轴序。只用于显示时的换轴换算，<b>不写盘</b>——
    /// 它是从观测数据反推出来的结论，不是输入。没做过后方交会时为 null。
    /// </summary>
    public GroundAxisOrder? GroundOrder { get; set; }

    /// <summary>所属相机。未指定时无法做框标内定向。</summary>
    public string? CameraId { get; set; }

    /// <summary>本影像上量测的像点。像点属于影像，不挂在工程上。</summary>
    public List<ImagePoint> Points { get; } = new();

    /// <summary>本影像上量测的框标。每个框标在影像上最多一个观测。</summary>
    public List<FiducialMark> FiducialMarks { get; } = new();

    /// <summary>本影像上绑定到某控制点的像点，没有则返回 null。同一影像一个控制点最多一个像点。</summary>
    public ImagePoint? FindGcpPoint(string gcpId) =>
        Points.FirstOrDefault(p => p.BoundGcpId == gcpId);

    /// <summary>本影像上已绑定的控制点个数。</summary>
    public int BoundGcpCount => Points.Count(p => p.BoundGcpId is not null);
}

/// <summary>地面控制点。</summary>
public sealed class Gcp
{
    public required string Id { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public GcpType Type { get; set; } = GcpType.Planimetric;
}

/// <summary>像点：某张影像上的一个量测点，可归属控制点或同名点。</summary>
public sealed class ImagePoint
{
    public required string Id { get; init; }
    public required string ImageId { get; init; }

    /// <summary>像素坐标，保留以便回溯与重绘。</summary>
    public int PixelX { get; set; }
    public int PixelY { get; set; }

    /// <summary>像平面坐标，mm。内方位元素可用时由像素坐标换算得到。</summary>
    public ImagePointMm PositionMm { get; set; }

    /// <summary>绑定的控制点号。null 表示尚未绑定，是“临时点”。</summary>
    public string? BoundGcpId { get; set; }

    /// <summary>
    /// 绑定的同名点号。一个像点要么绑控制点、要么绑同名点，不会两者都绑——
    /// 同名点用于相对定向，控制点用于后方交会，混在一起谁也说不清它属于哪个任务。
    /// </summary>
    public string? BoundTiePointId { get; set; }

    /// <summary>是否已绑定到控制点或同名点。</summary>
    public bool IsBound => BoundGcpId is not null || BoundTiePointId is not null;

    /// <summary>解算后回填的像点残差，mm。</summary>
    public double? ResidualMm { get; set; }
}

/// <summary>
/// 同名点：一个几何点的<b>点号</b>，它本身不存坐标。
///
/// <para>成员是那些 <see cref="ImagePoint.BoundTiePointId"/> 等于本点号的像点——归属关系
/// 只记在像点一侧，这里不再抄一份。抄两份的下场是删了一个像点、另一个列表还留着它。</para>
///
/// <para>点号是工程的编号，跨影像通用；像点自己的 <see cref="ImagePoint.Id"/>（P01 之类）
/// 只在它所在的那张影像里唯一，两张片都可能有 P01。</para>
/// </summary>
public sealed class TiePoint
{
    public required string Id { get; init; }

    /// <summary>上下视差，mm。相对定向解算后回填；没解过为 null。</summary>
    public double? Q { get; set; }

    /// <summary>被相对定向判为粗差、没参与解算。没解过为 false。</summary>
    public bool Rejected { get; set; }
}

/// <summary>像对：相对定向元素的宿主。左右不能是同一张片。</summary>
public sealed class StereoPair
{
    public required string Id { get; init; }
    public required string LeftImageId { get; init; }
    public required string RightImageId { get; init; }

    /// <summary>相对定向结果，未解算时为 null。</summary>
    public RelativeOrientation? Relative { get; set; }
}

/// <summary>
/// 相对定向结果（连续法）。角度单位为度，与界面一致；送算法前才转弧度。
///
/// <para>基线存<b>单位方向</b>（|b| = 1）而不是教材的 (1, b_y, b_z)：旁向像对的基线主要
/// 在 y 方向，硬按 b_x 归一会被零除。哪个分量被当成 1 由 <see cref="Gauge"/> 说明，
/// 教材式的 b_y、b_z 需要时用 <see cref="TextbookBy"/> / <see cref="TextbookBz"/> 现算。</para>
///
/// <para>模型比例尺也是自由的：b 的长度被规准掉了，模型的米制尺寸要靠绝对定向的 λ 拉回来。
/// 所以这份元素只能用于「相对」的场合（模型交会），别拿模型坐标当米制坐标。</para>
/// </summary>
public sealed class RelativeOrientation
{
    /// <summary>
    /// 解算时作为基准的那张片：辅助系的原点在它的摄影中心、三轴与它的像空间系平行。
    /// 它可能不是像对的左片（用户把两张片对调过），所以必须写下来。
    /// </summary>
    public string? ReferenceImageId { get; set; }

    /// <summary>单位基线方向，像空间辅助系，原点在左片摄影中心。</summary>
    public double Bx { get; set; }
    public double By { get; set; }
    public double Bz { get; set; }

    /// <summary>右片相对左片的三个角，按 R = Rκ·Rω·Rφ 分解。</summary>
    public double Phi2 { get; set; }
    public double Omega2 { get; set; }
    public double Kappa2 { get; set; }

    /// <summary>实际冻结的基线分量：0 = b_x、1 = b_y、2 = b_z。</summary>
    public int Gauge { get; set; }

    public int Iterations { get; set; }

    /// <summary>实际参与解算的点对数（剔除粗差之后）。</summary>
    public int UsedCount { get; set; }

    /// <summary>被判为粗差剔掉的点对数。</summary>
    public int RejectedCount { get; set; }

    /// <summary>解算时用的焦距，mm。重开工程时据此核对内定向有没有被改过。</summary>
    public double FocalLength { get; set; }

    /// <summary>单位权中误差，mm。</summary>
    public double Sigma0Mm { get; set; }

    /// <summary>上下视差中误差，mm。M6 的验收指标。</summary>
    public double RmsQ { get; set; }

    /// <summary>最大上下视差绝对值，mm。</summary>
    public double MaxQ { get; set; }

    /// <summary>法方程条件数 λmax/λmin。</summary>
    public double Condition { get; set; }

    /// <summary>解算时刻（UTC）。</summary>
    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>按 b_x 归一后的 b_y，教学口径。b_x 太小时返回 double.NaN。</summary>
    public double TextbookBy => Math.Abs(Bx) < 1e-9 ? double.NaN : By / Bx;

    /// <summary>按 b_x 归一后的 b_z，教学口径。b_x 太小时返回 double.NaN。</summary>
    public double TextbookBz => Math.Abs(Bx) < 1e-9 ? double.NaN : Bz / Bx;
}

/// <summary>工程：索引的唯一真源。影像像素数据不在此结构中。</summary>
public sealed class Project
{
    /// <summary>
    /// 工程文件版本。3 = 同名点（tiePoints）与像对相对定向结果。
    /// 读入时接受 ≤ 本值的文件，写出时一律写本值。
    /// </summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";

    public List<Camera> Cameras { get; } = new();
    public List<ImageItem> Images { get; } = new();
    public List<Gcp> Gcps { get; } = new();

    /// <summary>同名点。成员归属记在像点一侧（<see cref="ImagePoint.BoundTiePointId"/>）。</summary>
    public List<TiePoint> TiePoints { get; } = new();

    /// <summary>
    /// 打开工程时没能还原的东西（悬空的同名点引用等）。运行时的诊断信息，<b>不写盘</b>。
    /// 读进来时丢掉是唯一选择——但绝不能悄悄丢，界面上得说出来。
    /// </summary>
    public List<string> LoadWarnings { get; } = new();

    /// <summary>控制点文件的相对路径。重开工程时靠它重新定位放大影像。</summary>
    public string? ControlPointSourcePath { get; set; }

    /// <summary>放大影像目录的相对路径。</summary>
    public string? ControlPointZoomDirectory { get; set; }

    /// <summary>控制点文件的运行时绝对路径，不写盘。</summary>
    public string? ControlPointSourceFullPath { get; set; }

    /// <summary>放大影像目录的运行时绝对路径，不写盘。</summary>
    public string? ControlPointZoomDirectoryFullPath { get; set; }

    public List<StereoPair> Pairs { get; } = new();

    /// <summary>按点号找控制点。</summary>
    public Gcp? FindGcp(string? id) =>
        id is null ? null : Gcps.FirstOrDefault(g => g.Id == id);

    /// <summary>按点号找同名点。</summary>
    public TiePoint? FindTiePoint(string? id) =>
        id is null ? null : TiePoints.FirstOrDefault(t => t.Id == id);

    /// <summary>某个同名点在哪些影像上有观测。没有成员的返回空表。</summary>
    public List<(ImageItem Image, ImagePoint Point)> TieMembers(string? tiePointId)
    {
        var members = new List<(ImageItem, ImagePoint)>();
        if (string.IsNullOrEmpty(tiePointId)) return members;

        foreach (var image in Images)
            foreach (var point in image.Points)
                if (point.BoundTiePointId == tiePointId)
                    members.Add((image, point));

        return members;
    }

    /// <summary>最多被观测了几张片的同名点。相对定向只需要 2 片，多余意味着可以继续做别的像对。</summary>
    public int TiePointImageCount(string? tiePointId) => TieMembers(tiePointId).Count;

    /// <summary>找已有的像对（左右顺序无关），没有返回 null。</summary>
    public StereoPair? FindPair(string? leftImageId, string? rightImageId)
    {
        if (leftImageId is null || rightImageId is null) return null;
        return Pairs.FirstOrDefault(p =>
            (p.LeftImageId == leftImageId && p.RightImageId == rightImageId) ||
            (p.LeftImageId == rightImageId && p.RightImageId == leftImageId));
    }

    /// <summary>
    /// 取像对，没有就建一个。像对是相对定向结果的宿主，必须能按左右片稳定地找回来。
    ///
    /// <para>左右对调仍算同一个像对（同一对光线，解出来的元素互为反解）。建好之后
    /// 左片以工程里记的为准——调用方要按 <see cref="StereoPair.LeftImageId"/> 去配点，
    /// 不能假定它等于自己这次传进来的那张。</para>
    /// </summary>
    public StereoPair GetOrCreatePair(string leftImageId, string rightImageId)
    {
        if (FindPair(leftImageId, rightImageId) is { } existing) return existing;

        var pair = new StereoPair
        {
            Id = NextPairId(),
            LeftImageId = leftImageId,
            RightImageId = rightImageId,
        };
        Pairs.Add(pair);
        return pair;
    }

    /// <summary>下一个可用的像对号：PR01、PR02…</summary>
    public string NextPairId()
    {
        for (var n = 1; n < 10000; n++)
        {
            var id = $"PR{n:00}";
            if (Pairs.All(p => p.Id != id)) return id;
        }
        return $"PR{Pairs.Count + 1}";
    }

    /// <summary>下一个可用的同名点号：T01、T02…</summary>
    public string NextTiePointId()
    {
        for (var n = 1; n < 100000; n++)
        {
            var id = $"T{n:00}";
            if (TiePoints.All(t => t.Id != id)) return id;
        }
        return $"T{TiePoints.Count + 1}";
    }

    /// <summary>参与解算的控制点（排除检查点）。</summary>
    public IEnumerable<Gcp> AdjustmentGcps => Gcps.Where(g => g.Type != GcpType.CheckPoint);

    /// <summary>上次选中的实体 Id，用于恢复现场。</summary>
    public string? LastSelectedId { get; set; }
}
