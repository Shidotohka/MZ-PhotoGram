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
///
/// <para>字段逐个的说明见 <c>Docs/工程文件格式.md</c>；改字段必须同步改那份文档。</para>
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".pht";

    /// <summary>写出的版本号，与 <see cref="Project.CurrentVersion"/> 保持同一个值。</summary>
    public const int CurrentVersion = Project.CurrentVersion;

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
                BoundTiePointId = p.BoundTiePointId,
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
        TiePoints = project.TiePoints.Select(t => new TiePointDto
        {
            Id = t.Id,
            Q = t.Q,
            Rejected = t.Rejected
        }).ToList(),
        Pairs = project.Pairs.Select(p => new PairDto
        {
            Id = p.Id,
            Left = p.LeftImageId,
            Right = p.RightImageId,
            Relative = p.Relative is { } r ? new RelativeDto
            {
                Reference = r.ReferenceImageId,
                Bx = r.Bx,
                By = r.By,
                Bz = r.Bz,
                Phi = r.Phi2,
                Omega = r.Omega2,
                Kappa = r.Kappa2,
                Gauge = r.Gauge,
                Iterations = r.Iterations,
                UsedCount = r.UsedCount,
                RejectedCount = r.RejectedCount,
                FocalLength = r.FocalLength,
                Sigma0 = r.Sigma0Mm,
                RmsQ = r.RmsQ,
                MaxQ = r.MaxQ,
                Condition = r.Condition,
                TimeUtc = r.TimeUtc
            } : null
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

        // `models` 在 2026-11-07 之后不再存盘（模型点是解算的中间产物，见 Docs/小项与取舍.md X-02）。
        // 空数组不吭声——那是我们自己以前写出来的常态；但**非空**的要说一句：
        // 那里面是手写进去的东西，下一次保存就真没有了，不能让人以为它还在。
        if (dto.Extra?.TryGetValue("models", out var legacyModels) == true &&
            legacyModels.ValueKind == System.Text.Json.JsonValueKind.Array &&
            legacyModels.GetArrayLength() > 0)
        {
            project.LoadWarnings.Add(
                $"文件里有 {legacyModels.GetArrayLength()} 条 models 记录：模型点已不再存盘，" +
                "这段被忽略（下次保存就没了）。");
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
                // 一个像点只能绑一处：同时绑着控制点和同名点，说明文件被手改过或两个
                // 任务的写入撞了车。保留控制点（它有地面坐标，丢了更可惜），说出来。
                var tieId = p.BoundTiePointId;
                if (p.BoundGcpId is not null && tieId is not null)
                {
                    project.LoadWarnings.Add(
                        $"影像 {item.Id} 的像点 {p.Id} 同时绑了控制点 {p.BoundGcpId} 与同名点 {tieId}，" +
                        "已按控制点处理。");
                    tieId = null;
                }

                item.Points.Add(new ImagePoint
                {
                    Id = p.Id ?? "",
                    ImageId = item.Id,
                    PixelX = p.PixelX,
                    PixelY = p.PixelY,
                    PositionMm = new ImagePointMm(p.X, p.Y),
                    BoundGcpId = p.BoundGcpId,
                    BoundTiePointId = tieId,
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
            var pair = new StereoPair
            {
                Id = p.Id ?? "",
                LeftImageId = p.Left ?? "",
                RightImageId = p.Right ?? ""
            };

            if (p.Relative is { } r)
            {
                pair.Relative = new RelativeOrientation
                {
                    ReferenceImageId = r.Reference,
                    Bx = r.Bx,
                    By = r.By,
                    Bz = r.Bz,
                    Phi2 = r.Phi,
                    Omega2 = r.Omega,
                    Kappa2 = r.Kappa,
                    Gauge = r.Gauge,
                    Iterations = r.Iterations,
                    UsedCount = r.UsedCount,
                    RejectedCount = r.RejectedCount,
                    FocalLength = r.FocalLength,
                    Sigma0Mm = r.Sigma0,
                    RmsQ = r.RmsQ,
                    MaxQ = r.MaxQ,
                    Condition = r.Condition,
                    TimeUtc = r.TimeUtc
                };
            }

            project.Pairs.Add(pair);
        }

        RestoreTiePoints(dto, project);
        ValidatePairs(project);

        return project;
    }

    /// <summary>
    /// 还原同名点。
    ///
    /// <para>归属关系只记在像点一侧，所以这里做两件事：先按 tiePoints 表建立点号（带着
    /// 上一次解算的 q），再把「像点引用了、表里却没有」的点号补建出来。补建而不是丢弃，
    /// 是因为这类文件多半是手写的——手写的意图很清楚，用不着纠正它。</para>
    /// </summary>
    private static void RestoreTiePoints(ProjectDto dto, Project project)
    {
        foreach (var t in dto.TiePoints ?? new List<TiePointDto>())
        {
            if (string.IsNullOrWhiteSpace(t.Id))
            {
                project.LoadWarnings.Add("tiePoints 里有一条没有 id 的记录，已忽略。");
                continue;
            }

            if (project.FindTiePoint(t.Id) is not null)
            {
                project.LoadWarnings.Add($"tiePoints 里点号 {t.Id} 出现了多次，只保留第一条。");
                continue;
            }

            project.TiePoints.Add(new TiePoint { Id = t.Id, Q = t.Q, Rejected = t.Rejected });
        }

        var adopted = 0;
        foreach (var image in project.Images)
        {
            foreach (var point in image.Points)
            {
                if (string.IsNullOrEmpty(point.BoundTiePointId)) continue;
                if (project.FindTiePoint(point.BoundTiePointId) is not null) continue;

                project.TiePoints.Add(new TiePoint { Id = point.BoundTiePointId });
                adopted++;
            }
        }

        if (adopted > 0)
        {
            project.LoadWarnings.Add(
                $"有 {adopted} 个点号只出现在像点上、没进 tiePoints 表，已按像点的引用补建。");
        }

        var empty = project.TiePoints.Where(t => project.TiePointImageCount(t.Id) == 0).ToList();
        foreach (var tie in empty)
        {
            project.LoadWarnings.Add($"同名点 {tie.Id} 没有任何像点引用，已丢掉。");
            project.TiePoints.Remove(tie);
        }
    }

    /// <summary>像对指向不存在的影像时只说清楚，不删——文件可能只是换了个工程目录拼错路径。</summary>
    private static void ValidatePairs(Project project)
    {
        foreach (var pair in project.Pairs)
        {
            if (project.Images.All(i => i.Id != pair.LeftImageId))
                project.LoadWarnings.Add($"像对 {pair.Id} 的左片 {pair.LeftImageId} 不在工程里。");
            if (project.Images.All(i => i.Id != pair.RightImageId))
                project.LoadWarnings.Add($"像对 {pair.Id} 的右片 {pair.RightImageId} 不在工程里。");
            if (pair.LeftImageId == pair.RightImageId)
                project.LoadWarnings.Add($"像对 {pair.Id} 的左右片是同一张（{pair.LeftImageId}）。");
        }
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
        public List<TiePointDto>? TiePoints { get; set; }
        public List<PairDto>? Pairs { get; set; }

        /// <summary>
        /// 认不出来的键。只为一件事存在：把<b>自己曾经写过、后来删掉</b>的键认出来并说一声
        /// （现在是 `models`）。别的未知键一律静默忽略——手写文件里加个实验字段是合法的（§10）。
        /// </summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
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
        public string? BoundTiePointId { get; set; }
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

        /// <summary>相对定向结果，没解过为 null（写出时整段省略）。</summary>
        public RelativeDto? Relative { get; set; }
    }

    /// <summary>
    /// 相对定向结果的一段。角度写的是<b>度</b>（与界面一致），基线写的是单位方向。
    /// 字段名与 <see cref="RelativeOrientation"/> 的对应关系见 Docs/工程文件格式.md。
    /// </summary>
    private sealed class RelativeDto
    {
        /// <summary>基准影像 Id：辅助系的原点与轴向属于它。</summary>
        public string? Reference { get; set; }

        public double Bx { get; set; }
        public double By { get; set; }
        public double Bz { get; set; }
        public double Phi { get; set; }
        public double Omega { get; set; }
        public double Kappa { get; set; }
        public int Gauge { get; set; }
        public int Iterations { get; set; }
        public int UsedCount { get; set; }
        public int RejectedCount { get; set; }
        public double FocalLength { get; set; }
        public double Sigma0 { get; set; }
        public double RmsQ { get; set; }
        public double MaxQ { get; set; }
        public double Condition { get; set; }
        public DateTimeOffset TimeUtc { get; set; }
    }

    private sealed class TiePointDto
    {
        public string? Id { get; set; }
        public double? Q { get; set; }
        public bool Rejected { get; set; }
    }
}
