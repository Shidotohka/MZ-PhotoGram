using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Photogrammetry.Core;

/// <summary>影像文件的运行时状态，不写盘。</summary>
public enum ImageFileState
{
    Ok,
    Missing
}

/// <summary>
/// 工程的读写。工程文件是索引：只存路径与参数，不嵌入影像数据。
/// 写入采用「临时文件 → 备份轮转 → 替换」的方式，避免中途崩溃留下半个工程。
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".pht";
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ---------- 保存 ----------

    public static void Save(Project project, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)
                        ?? throw new ArgumentException("工程路径无效", nameof(path));

        Directory.CreateDirectory(directory);
        UpdateRelativePaths(project, directory);

        var json = JsonSerializer.Serialize(ToDto(project), Options);

        using var fileLock = AcquireLock(full);

        var tmp = full + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));

        RotateBackups(full);

        if (File.Exists(full)) File.Replace(tmp, full, full + ".bak", true);
        else File.Move(tmp, full);
    }

    private static void UpdateRelativePaths(Project project, string directory)
    {
        foreach (var image in project.Images)
        {
            if (string.IsNullOrEmpty(image.FullPath)) continue;

            var relative = Path.GetRelativePath(directory, image.FullPath);
            image.RelativePath = relative.Replace('\\', '/');
            image.External = relative.StartsWith("..", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(project.ControlPointSourceFullPath))
            project.ControlPointSourcePath =
                ToRelative(directory, project.ControlPointSourceFullPath);
        if (!string.IsNullOrEmpty(project.ControlPointZoomDirectoryFullPath))
            project.ControlPointZoomDirectory =
                ToRelative(directory, project.ControlPointZoomDirectoryFullPath);
    }

    private static string ToRelative(string directory, string fullPath) =>
        Path.GetRelativePath(directory, fullPath).Replace('\\', '/');

    private static string ToFull(string directory, string relative) =>
        Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static void RotateBackups(string full)
    {
        var bak = full + ".bak";
        if (!File.Exists(bak)) return;

        File.Copy(bak, full + ".bak2", true);
        File.Copy(bak, full + ".bak3", true);
    }

    private static FileStream AcquireLock(string full)
    {
        var lockPath = full + ".lock";
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("该工程正在被另一个进程编辑，请先在那边保存并关闭。");
        }
    }

    // ---------- 读取 ----------

    public static Project Load(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;

        var json = File.ReadAllText(full, Encoding.UTF8);
        var dto = JsonSerializer.Deserialize<ProjectDto>(json, Options)
                  ?? throw new InvalidDataException("工程文件为空或格式不正确。");

        if (dto.Version > CurrentVersion)
        {
            throw new InvalidDataException(
                $"工程文件版本为 {dto.Version}，高于当前程序支持的 {CurrentVersion}，请升级程序。");
        }

        return FromDto(dto, directory);
    }

    // ---------- DTO 映射 ----------

    private static ProjectDto ToDto(Project project) => new()
    {
        Version = CurrentVersion,
        Name = project.Name,
        LastSelectedId = project.LastSelectedId,
        ControlPointSource = project.ControlPointSourcePath,
        ControlPointZoomDirectory = project.ControlPointZoomDirectory,
        Cameras = project.Cameras.Select(c => new CameraDto
        {
            Id = c.Id,
            FocalLength = c.FocalLengthMm,
            X0 = c.X0Mm,
            Y0 = c.Y0Mm,
            Fiducials = c.Fiducials.Select(f => new FiducialDto
            {
                Id = f.Id,
                X = f.Xmm,
                Y = f.Ymm
            }).ToList()
        }).ToList(),
        Images = project.Images.Select(i => new ImageDto
        {
            Id = i.Id,
            Path = i.RelativePath,
            External = i.External,
            Width = i.Width,
            Height = i.Height,
            CameraId = i.CameraId,
            FocalLength = i.Interior?.FocalLength ?? 0,
            X0 = i.Interior?.X0 ?? 0,
            Y0 = i.Interior?.Y0 ?? 0,
            PixelSize = i.Interior?.PixelSizeMm ?? 0,
            HasInterior = i.Interior is not null,
            Status = i.Status.ToString(),
            HasExterior = i.Exterior is not null,
            Xs = i.Exterior?.Xs ?? 0,
            Ys = i.Exterior?.Ys ?? 0,
            Zs = i.Exterior?.Zs ?? 0,
            Phi = i.Exterior?.Phi ?? 0,
            Omega = i.Exterior?.Omega ?? 0,
            Kappa = i.Exterior?.Kappa ?? 0,
            FiducialMarks = i.FiducialMarks.Select(m => new FiducialMarkDto
            {
                Id = m.FiducialId,
                I = m.I,
                J = m.J
            }).ToList(),
            Points = i.Points.Select(p => new PointDto
            {
                Id = p.Id,
                PixelX = p.PixelX,
                PixelY = p.PixelY,
                X = p.PositionMm.X,
                Y = p.PositionMm.Y,
                BoundGcpId = p.BoundGcpId,
                ResidualMm = p.ResidualMm
            }).ToList()
        }).ToList(),
        Gcps = project.Gcps.Select(g => new GcpDto
        {
            Id = g.Id,
            X = g.X,
            Y = g.Y,
            Z = g.Z,
            Type = g.Type.ToString()
        }).ToList(),
        Pairs = project.Pairs.Select(p => new PairDto
        {
            Id = p.Id,
            Left = p.LeftImageId,
            Right = p.RightImageId
        }).ToList(),
        Models = project.Models.Select(m => new ModelDto
        {
            Id = m.Id,
            GcpIds = m.GcpIds.ToList()
        }).ToList()
    };

    private static Project FromDto(ProjectDto dto, string directory)
    {
        var project = new Project
        {
            Version = dto.Version,
            Name = dto.Name ?? "",
            LastSelectedId = dto.LastSelectedId,
            ControlPointSourcePath = dto.ControlPointSource,
            ControlPointZoomDirectory = dto.ControlPointZoomDirectory
        };

        if (!string.IsNullOrEmpty(dto.ControlPointSource))
        {
            var full = ToFull(directory, dto.ControlPointSource);
            project.ControlPointSourceFullPath = File.Exists(full) ? full : null;
        }
        if (!string.IsNullOrEmpty(dto.ControlPointZoomDirectory))
        {
            var full = ToFull(directory, dto.ControlPointZoomDirectory);
            project.ControlPointZoomDirectoryFullPath = Directory.Exists(full) ? full : null;
        }

        foreach (var c in dto.Cameras ?? new List<CameraDto>())
        {
            var camera = new Camera
            {
                Id = c.Id ?? Guid.NewGuid().ToString("N"),
                FocalLengthMm = c.FocalLength,
                X0Mm = c.X0,
                Y0Mm = c.Y0
            };
            foreach (var f in c.Fiducials ?? new List<FiducialDto>())
                camera.Fiducials.Add(new Fiducial { Id = f.Id, Xmm = f.X, Ymm = f.Y });
            project.Cameras.Add(camera);
        }

        foreach (var i in dto.Images ?? new List<ImageDto>())
        {
            var relative = (i.Path ?? "").Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(directory, relative));

            var item = new ImageItem
            {
                Id = i.Id ?? Guid.NewGuid().ToString("N"),
                RelativePath = i.Path ?? "",
                FullPath = full,
                External = i.External,
                Width = i.Width,
                Height = i.Height,
                CameraId = i.CameraId,
                FileState = File.Exists(full) ? ImageFileState.Ok : ImageFileState.Missing,
                Status = Enum.TryParse<ImageStatus>(i.Status, out var status)
                    ? status
                    : ImageStatus.NotProcessed
            };

            if (i.HasInterior)
            {
                item.Interior = new InteriorOrientation
                {
                    FocalLength = i.FocalLength,
                    X0 = i.X0,
                    Y0 = i.Y0,
                    PixelSizeMm = i.PixelSize
                };
            }

            if (i.HasExterior)
            {
                item.Exterior = new ExteriorOrientation
                {
                    Xs = i.Xs,
                    Ys = i.Ys,
                    Zs = i.Zs,
                    Phi = i.Phi,
                    Omega = i.Omega,
                    Kappa = i.Kappa
                };
            }

            foreach (var m in i.FiducialMarks ?? new List<FiducialMarkDto>())
                item.FiducialMarks.Add(new FiducialMark { FiducialId = m.Id, I = m.I, J = m.J });

            foreach (var p in i.Points ?? new List<PointDto>())
            {
                item.Points.Add(new ImagePoint
                {
                    Id = p.Id ?? "",
                    ImageId = item.Id,
                    PixelX = p.PixelX,
                    PixelY = p.PixelY,
                    PositionMm = new ImagePointMm(p.X, p.Y),
                    BoundGcpId = p.BoundGcpId,
                    ResidualMm = p.ResidualMm
                });
            }

            project.Images.Add(item);
        }

        foreach (var g in dto.Gcps ?? new List<GcpDto>())
        {
            project.Gcps.Add(new Gcp
            {
                Id = g.Id ?? "",
                X = g.X,
                Y = g.Y,
                Z = g.Z,
                Type = Enum.TryParse<GcpType>(g.Type, out var type) ? type : GcpType.Planimetric
            });
        }

        foreach (var p in dto.Pairs ?? new List<PairDto>())
        {
            project.Pairs.Add(new StereoPair
            {
                Id = p.Id ?? "",
                LeftImageId = p.Left ?? "",
                RightImageId = p.Right ?? ""
            });
        }

        foreach (var m in dto.Models ?? new List<ModelDto>())
        {
            var model = new ModelItem { Id = m.Id ?? "" };
            foreach (var id in m.GcpIds ?? new List<string>()) model.GcpIds.Add(id);
            project.Models.Add(model);
        }

        return project;
    }

    // ---------- DTO 定义 ----------

    private sealed class ProjectDto
    {
        public int Version { get; set; }
        public string? Name { get; set; }
        public string? LastSelectedId { get; set; }
        public string? ControlPointSource { get; set; }
        public string? ControlPointZoomDirectory { get; set; }
        public List<CameraDto>? Cameras { get; set; }
        public List<ImageDto>? Images { get; set; }
        public List<GcpDto>? Gcps { get; set; }
        public List<PairDto>? Pairs { get; set; }
        public List<ModelDto>? Models { get; set; }
    }

    private sealed class CameraDto
    {
        public string? Id { get; set; }
        public double FocalLength { get; set; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public List<FiducialDto>? Fiducials { get; set; }
    }

    private sealed class FiducialDto
    {
        public int Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class FiducialMarkDto
    {
        public int Id { get; set; }
        public double I { get; set; }
        public double J { get; set; }
    }

    private sealed class ImageDto
    {
        public string? Id { get; set; }
        public string? Path { get; set; }
        public bool External { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? CameraId { get; set; }
        public bool HasInterior { get; set; }
        public double FocalLength { get; set; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double PixelSize { get; set; }
        public string? Status { get; set; }
        public bool HasExterior { get; set; }
        public double Xs { get; set; }
        public double Ys { get; set; }
        public double Zs { get; set; }
        public double Phi { get; set; }
        public double Omega { get; set; }
        public double Kappa { get; set; }
        public List<FiducialMarkDto>? FiducialMarks { get; set; }
        public List<PointDto>? Points { get; set; }
    }

    private sealed class PointDto
    {
        public string? Id { get; set; }
        public int PixelX { get; set; }
        public int PixelY { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public string? BoundGcpId { get; set; }
        public double? ResidualMm { get; set; }
    }

    private sealed class GcpDto
    {
        public string? Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? Type { get; set; }
    }

    private sealed class PairDto
    {
        public string? Id { get; set; }
        public string? Left { get; set; }
        public string? Right { get; set; }
    }

    private sealed class ModelDto
    {
        public string? Id { get; set; }
        public List<string>? GcpIds { get; set; }
    }
}
