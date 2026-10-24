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

    /// <summary>是否已绑定到控制点或同名点。</summary>
    public bool IsBound => BoundGcpId is not null;

    /// <summary>解算后回填的像点残差，mm。</summary>
    public double? ResidualMm { get; set; }
}

/// <summary>同名点：连接多张影像上同名像点。</summary>
public sealed class TiePoint
{
    public required string Id { get; init; }
    public List<string> ImagePointIds { get; } = new();

    /// <summary>上下视差残差，mm。</summary>
    public double? Q { get; set; }
}

/// <summary>像对：相对定向元素的宿主。</summary>
public sealed class StereoPair
{
    public required string Id { get; init; }
    public required string LeftImageId { get; init; }
    public required string RightImageId { get; init; }

    /// <summary>相对定向五元素，未解算时为 null。</summary>
    public RelativeOrientation? Relative { get; set; }

    public List<string> TiePointIds { get; } = new();
}

/// <summary>相对定向元素（连续法）。</summary>
public sealed class RelativeOrientation
{
    public double By { get; set; }
    public double Bz { get; set; }
    public double Phi2 { get; set; }
    public double Omega2 { get; set; }
    public double Kappa2 { get; set; }
    public double RmsQ { get; set; }
}

/// <summary>模型：绝对定向的输入与输出。</summary>
public sealed class ModelItem
{
    public required string Id { get; init; }

    /// <summary>模型坐标系下的点，键为点号。</summary>
    public Dictionary<string, GroundPointM> ModelPoints { get; } = new();

    public AbsoluteOrientation? Absolute { get; set; }
    public List<string> GcpIds { get; } = new();
}

/// <summary>绝对定向七参数。</summary>
public sealed class AbsoluteOrientation
{
    public double Scale { get; set; } = 1.0;
    public double Phi { get; set; }
    public double Omega { get; set; }
    public double Kappa { get; set; }
    public double DeltaX { get; set; }
    public double DeltaY { get; set; }
    public double DeltaZ { get; set; }
    public double RmsGcp { get; set; }
}

/// <summary>结果索引。完整残差表存放在 results/ 下的独立文件。</summary>
public sealed class ResultRef
{
    public required string Id { get; init; }
    public required string Task { get; init; }
    public string? FilePath { get; set; }
    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Summary { get; set; }
}

/// <summary>工程：索引的唯一真源。影像像素数据不在此结构中。</summary>
public sealed class Project
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";

    public List<Camera> Cameras { get; } = new();
    public List<ImageItem> Images { get; } = new();
    public List<Gcp> Gcps { get; } = new();

    /// <summary>控制点文件的相对路径。重开工程时靠它重新定位放大影像。</summary>
    public string? ControlPointSourcePath { get; set; }

    /// <summary>放大影像目录的相对路径。</summary>
    public string? ControlPointZoomDirectory { get; set; }

    /// <summary>控制点文件的运行时绝对路径，不写盘。</summary>
    public string? ControlPointSourceFullPath { get; set; }

    /// <summary>放大影像目录的运行时绝对路径，不写盘。</summary>
    public string? ControlPointZoomDirectoryFullPath { get; set; }

    /// <summary>按 Id 找相机，找不到返回 null。</summary>
    public Camera? FindCamera(string? id) =>
        id is null ? null : Cameras.FirstOrDefault(c => c.Id == id);
    public List<StereoPair> Pairs { get; } = new();
    public List<ModelItem> Models { get; } = new();
    public List<ResultRef> Results { get; } = new();

    /// <summary>按点号找控制点。</summary>
    public Gcp? FindGcp(string? id) =>
        id is null ? null : Gcps.FirstOrDefault(g => g.Id == id);

    /// <summary>参与解算的控制点（排除检查点）。</summary>
    public IEnumerable<Gcp> AdjustmentGcps => Gcps.Where(g => g.Type != GcpType.CheckPoint);

    /// <summary>上次选中的实体 Id，用于恢复现场。</summary>
    public string? LastSelectedId { get; set; }
}
