using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Photogrammetry.App;
using Photogrammetry.Core;

namespace Snap;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Get(args, "--selftest") is { } seed)
        {
            SelfTest(seed);
            return;
        }

        if (args.Any(a => string.Equals(a, "--fiducial", StringComparison.OrdinalIgnoreCase)))
        {
            FiducialCheck(Get(args, "--dir") ?? Path.Combine("Data", "sim"));
            return;
        }

        if (args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
        {
            ProbeProject(Get(args, "--project")
                         ?? Path.Combine("Data", "real", "Real_test.pht"));
            return;
        }

        if (args.Any(a => string.Equals(a, "--control", StringComparison.OrdinalIgnoreCase)))
        {
            ControlCheck(Get(args, "--gcp"),
                Get(args, "--dir") ?? Path.Combine("Data", "real", "2_Hammer", "HammerIndex"));
            return;
        }

        var outPath = Get(args, "--out") ?? "shot.png";
        var module = Get(args, "--module");
        var imagePath = Get(args, "--image");
        var zoomText = Get(args, "--zoom");
        var pointsText = Get(args, "--points");
        var projectPath = Get(args, "--project");
        var gcpPath = Get(args, "--gcp");

        var app = new Application();

        // 让 async/await 的续体回到 UI 线程（WPF 正常运行时由 Application 自动设置）
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Photogrammetry.App;component/Themes/Theme.xaml", UriKind.Absolute)
        });

        var win = new MainWindow
        {
            WindowState = WindowState.Normal,
            Width = 1600,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false
        };

        win.Show();
        Pump(win);

        // 先逛一趟别的模块再回到目标模块：用来复现「离开内定向后框标方式没关掉」
        if (Get(args, "--via") is { } viaModule)
        {
            if (win.SelectModuleForTooling(viaModule))
            {
                Pump(win);
                Console.WriteLine($"先切到 {viaModule}");
            }
            else
            {
                Console.WriteLine($"! 没有模块「{viaModule}」");
            }
        }

        if (module is not null)
        {
            if (!win.SelectModuleForTooling(module)) Console.WriteLine($"! 没有模块「{module}」");
            Pump(win);
        }

        if (projectPath is not null)
        {
            var task = win.OpenProjectAsync(Path.GetFullPath(projectPath));
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                Pump(win);
                System.Threading.Thread.Sleep(30);
            }
            Console.WriteLine($"project task: {task.Status}");
            if (task.Exception is not null)
            {
                Console.WriteLine(task.Exception.GetBaseException());
            }
            Pump(win);
        }

        if (Get(args, "--loadsecond") is { } secondPath)
        {
            var ws = FindChild<ImageWorkspace>(win);
            if (ws is not null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(Path.GetFullPath(secondPath));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                ws.LoadImage(bmp, new ImageItem
                {
                    Id = "IMG_0002",
                    FullPath = Path.GetFullPath(secondPath),
                    Width = bmp.PixelWidth,
                    Height = bmp.PixelHeight,
                });

                Pump(win);
                foreach (var v in ws.VisibleViews())
                    Console.WriteLine($"view has={v.HasImage} scale={v.Scale:0.0000}");
            }
        }

        if (imagePath is not null)        {
            var workspace = FindChild<ImageWorkspace>(win);
            if (workspace is null)
            {
                Console.WriteLine("workspace not found");
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(Path.GetFullPath(imagePath));
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            workspace.LoadImage(bitmap, new ImageItem
            {
                Id = Path.GetFileNameWithoutExtension(imagePath),
                RelativePath = imagePath,
                Width = bitmap.PixelWidth,
                Height = bitmap.PixelHeight,
                // M3 端到端要换算像平面坐标，顺手给一组内方位元素
                Interior = gcpPath is null
                    ? null
                    : new InteriorOrientation
                    {
                        FocalLength = 152.72,
                        X0 = 0.0,
                        Y0 = 0.0,
                        PixelSizeMm = 0.04503
                    }
            });

            Pump(win);
            foreach (var v in workspace.VisibleViews())
            {
                Console.WriteLine($"view {v.ActualWidth:0}x{v.ActualHeight:0} " +
                                  $"scale={v.Transform.Scale:0.0000} tx={v.Transform.Tx:0.0} ty={v.Transform.Ty:0.0} has={v.HasImage}");
            }

            if (pointsText is not null)
            {
                var view = workspace.VisibleViews().FirstOrDefault(v => v.HasImage);
                if (view is not null)
                {
                    foreach (var pair in pointsText.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var xy = pair.Split(',');
                        if (xy.Length == 2 &&
                            double.TryParse(xy[0], out var px) &&
                            double.TryParse(xy[1], out var py))
                        {
                            var point = view.AddPointAt(new Point(px, py));
                            Console.WriteLine($"added {point.Id} at {point.PixelX},{point.PixelY}");
                        }
                    }
                }
                Pump(win);
            }

            if (zoomText is not null && double.TryParse(zoomText, out var zoom))
            {
                workspace.ZoomBy(zoom);
                Pump(win);
            }
        }

        // M3：载入真实控制点 → 按已知外方位反算像素位置刺点 → 解算后方交会 → 截图
        if (Get(args, "--gcp") is { } gcpFile)
        {
            var ws = FindChild<ImageWorkspace>(win);
            var shown = ws is null ? null : ws.VisibleViews().FirstOrDefault(v => v.HasImage);
            if (shown?.Image is not { } shownImage)
            {
                Console.WriteLine("M3: 先给 --image");
            }
            else
            {
                RunM3(win, shownImage, gcpFile);
                Pump(win);
            }
        }

        // 相机检校文件与底部页签，方便出「内定向」的截图
        if (Get(args, "--camera") is { } cameraFile)
        {
            Console.WriteLine($"载入相机 {cameraFile}: {win.LoadCameraFrom(Path.GetFullPath(cameraFile))}");
            Pump(win);
        }

        if (Get(args, "--tab") is { } tabName && FindRadio(win, tabName) is { } tab)
        {
            tab.IsChecked = true;
            Pump(win);
        }

        if (win.Content is not FrameworkElement root)
        {
            Console.WriteLine("no content");
            return;
        }

        var w = (int)Math.Round(root.ActualWidth);
        var h = (int)Math.Round(root.ActualHeight);
        Console.WriteLine($"root {w}x{h}");

        // 状态栏右端的光标读数据跟着物理鼠标走：离屏窗口摆在本机 (0,0)，鼠标飘到哪
        // 就显示哪一像素，于是同一份代码两次截图会在那一小段上不同（实测 6 次跑出 3 种哈希）。
        // 截图前复位成「—」，让截图成为可比的基线。FindName 是 FrameworkElement 的公开方法，
        // 不需要给窗口加任何入口。
        if (win.FindName("StatusCursor") is System.Windows.Controls.TextBlock cursorReadout)
        {
            cursorReadout.Text = "—";
            Pump(win);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath))
        {
            encoder.Save(fs);
        }

        Console.WriteLine("saved " + outPath);

        if (Get(args, "--openmenu") is { } menuName && FindButton(win, menuName) is { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            Pump(win);
            Console.WriteLine($"menu open={menu.IsOpen} items={menu.Items.Count}");
            menu.IsOpen = false;
        }

        win.Close();
        app.Shutdown();
    }

    private static void SelfTest(string seedPath)
    {
        var full = Path.GetFullPath(seedPath);
        var output = Path.Combine(Path.GetDirectoryName(full)!, "selftest-out.pht");

        var project = ProjectFile.Load(full);
        Console.WriteLine($"载入：影像 {project.Images.Count}，像点 {project.Images.Sum(i => i.Points.Count)}");

        // 塞进 v2 新增的字段，验证都能往返
        project.Gcps.Clear();
        project.Gcps.Add(new Gcp { Id = "1156", X = 12482.769, Y = 14936.858, Z = 762.349 });
        project.Gcps.Add(new Gcp { Id = "6266", X = 7741.696, Y = 16232.309, Z = 703.121, Type = GcpType.CheckPoint });

        var firstImage = project.Images[0];
        firstImage.Points[0].BoundGcpId = "1156";
        firstImage.Exterior = new ExteriorOrientation
        {
            Xs = 10239.948, Ys = 14899.556, Zs = 6820.777,
            Phi = 0.407075, Omega = -0.288995, Kappa = 7.003480
        };
        firstImage.CameraId = "RC30";
        firstImage.FiducialMarks.Add(new FiducialMark { FiducialId = 3, I = 177.9, J = 178.1 });

        project.Cameras.Add(new Camera
        {
            Id = "RC30", FocalLengthMm = 152.72, X0Mm = -0.004, Y0Mm = -0.008
        });
        project.Cameras[0].Fiducials.Add(new Fiducial { Id = 3, Xmm = -106.001, Ymm = 106.003 });
        project.ControlPointSourcePath = "gxyz.rtf";

        ProjectFile.Save(project, output);
        var reloaded = ProjectFile.Load(output);

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        Check("影像数量", reloaded.Images.Count == project.Images.Count,
            $"{reloaded.Images.Count} == {project.Images.Count}");
        Check("像点数量", reloaded.Images.Sum(i => i.Points.Count) == project.Images.Sum(i => i.Points.Count),
            $"{reloaded.Images.Sum(i => i.Points.Count)}");
        Check("相对路径", reloaded.Images[0].RelativePath == "sample-target.png",
            reloaded.Images[0].RelativePath);
        Check("绝对路径解析", reloaded.Images[0].FullPath is not null && File.Exists(reloaded.Images[0].FullPath),
            reloaded.Images[0].FullPath ?? "null");
        Check("缺失影像标记", reloaded.Images[1].FileState == ImageFileState.Missing,
            reloaded.Images[1].FileState.ToString());
        Check("文件状态正常", reloaded.Images[0].FileState == ImageFileState.Ok,
            reloaded.Images[0].FileState.ToString());
        Check("内方位元素", Math.Abs((reloaded.Images[0].Interior?.FocalLength ?? 0) - 35.0) < 1e-9,
            $"{reloaded.Images[0].Interior?.FocalLength}");
        Check("像点坐标", Math.Abs(reloaded.Images[0].Points[0].PixelX - 400) < 1e-9,
            $"{reloaded.Images[0].Points[0].PixelX}");

        // v2 新增：控制点、刺点绑定、相机与框标、外方位
        Check("控制点数", reloaded.Gcps.Count == 2, reloaded.Gcps.Count.ToString());
        Check("控制点坐标", reloaded.Gcps[0].Id == "1156" &&
                            Math.Abs(reloaded.Gcps[0].Z - 762.349) < 1e-9,
            $"{reloaded.Gcps[0].Id} Z={reloaded.Gcps[0].Z}");
        Check("检查点类型", reloaded.FindGcp("6266")?.Type == GcpType.CheckPoint,
            reloaded.FindGcp("6266")?.Type.ToString() ?? "null");
        Check("刺点绑定", reloaded.Images[0].Points[0].BoundGcpId == "1156",
            reloaded.Images[0].Points[0].BoundGcpId ?? "null");
        Check("外方位", Math.Abs((reloaded.Images[0].Exterior?.Kappa ?? 0) - 7.003480) < 1e-9,
            $"{reloaded.Images[0].Exterior?.Kappa}");
        Check("相机与框标", reloaded.Cameras.Count == 1 &&
                            Math.Abs(reloaded.Cameras[0].FocalLengthMm - 152.72) < 1e-9 &&
                            reloaded.Cameras[0].Fiducials.Count == 1 &&
                            Math.Abs(reloaded.Cameras[0].Fiducials[0].Xmm + 106.001) < 1e-9,
            $"f={reloaded.Cameras[0].FocalLengthMm} 框标={reloaded.Cameras[0].Fiducials.Count}");
        Check("框标量测", reloaded.Images[0].FiducialMarks.Count == 1 &&
                          Math.Abs(reloaded.Images[0].FiducialMarks[0].J - 178.1) < 1e-9,
            reloaded.Images[0].FiducialMarks.Count.ToString());

        foreach (var f in new[] { output, output + ".bak", output + ".bak2", output + ".bak3" })
        {
            if (File.Exists(f)) File.Delete(f);
        }

        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button b && b.Content?.ToString() == content) return b;
            if (child is DependencyObject d && FindButton(d, content) is { } found) return found;
        }
        return null;
    }

    private static void Pump(Window win)    {
        // 把队列里所有优先级的任务（含 async 续体）都跑完，再强制布局
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            win.UpdateLayout();
        }
    }

    /// <summary>
    /// M3 自检：读真实控制点文件（E-FOTO 的 gxyz.rtf），检查解析、放大影像定位，
    /// 再用一个合成网做「投影 → 反解」，验证 P/Invoke 的结构体布局没错。
    /// </summary>
    /// <summary>
    /// 把工程里已刺好的点重算一遍，两种地面轴向各试一次，看哪种说得通。
    /// 用来诊断「数据没问题但解算拟合不上」——最常见的原因是 X/Y 轴向反了。
    /// </summary>
    private static void ProbeProject(string projectPath)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("工程刺点复核");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"工程 {projectPath}");

        Project project;
        try
        {
            project = ProjectFile.Load(Path.GetFullPath(projectPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.WriteLine("读不了工程：" + ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        var failures = 0;
        foreach (var image in project.Images)
        {
            if (image.Interior is not { } io || !io.IsValid)
            {
                Console.WriteLine($"\n{image.Id}：没有内定向结果，跳过");
                continue;
            }

            var observations = new List<ResectionObservation>();
            foreach (var point in image.Points)
            {
                if (point.BoundGcpId is not { } id) continue;
                if (project.FindGcp(id) is not { } gcp) continue;

                var mm = CoordinateConverter.PixelToImagePlane(
                    new ImagePixel(point.PixelX, point.PixelY), io, image.Width, image.Height);
                observations.Add(new ResectionObservation(id, mm.X, mm.Y, gcp.X, gcp.Y, gcp.Z));
            }

            Console.WriteLine($"\n影像 {image.Id}　{image.Width}×{image.Height}　" +
                              $"δ={io.PixelSizeMm:F6}　f={io.FocalLength:F3}　刺点 {observations.Count} 个");
            if (observations.Count < NativeAlgorithms.MinControlPoints)
            {
                Console.WriteLine("  点数不足，跳过");
                continue;
            }

            // 先做二维相似性检查：不解姿态、不迭代，只看地面 XY 与像平面 xy 能不能对上
            Console.WriteLine("  二维相似性检查（地面 XY 线性拟合到像平面 xy，不解姿态）");
            foreach (var order in new[] { GroundAxisOrder.EastNorth, GroundAxisOrder.NorthEast })
            {
                var flat = observations.Select(o =>
                {
                    var g = GroundAxes.ToMathFrame(
                        new GroundPointM(o.GroundX, o.GroundY, o.GroundZ), order);
                    return (X: g.X, Y: g.Y, x: o.Xmm, y: o.Ymm);
                }).ToList();

                var (det, scale, simRatio) = SimilarityCheck(flat);
                Console.WriteLine($"    {GroundAxes.Describe(order),-28} det={det,+9:F5}  " +
                                  $"相似残差 {simRatio * 100,6:F1}%  " +
                                  (det > 0 && simRatio < 0.5 ? "成立" : "镜像，旋转矩阵表达不出"));
                if (det > 0 && scale > 0)
                {
                    Console.WriteLine($"      比例尺 m = {scale:F5} mm/m → 估算航高 H = f/m = " +
                                      $"{io.FocalLength / scale:F0} m");
                }
            }

            // 判手性：三点叉乘，不解算。这是解算真正走的那一步。
            var evidence = NativeAlgorithms.DetectGroundOrder(observations);
            if (!evidence.Decided)
            {
                Console.WriteLine($"  [FAIL] {evidence.Describe()}");
                failures++;
                continue;
            }

            Console.WriteLine($"  手性判定 {GroundAxes.Describe(evidence.Order)}");
            Console.WriteLine($"    三点 {evidence.Triple}：像平面 sin = {evidence.SinImage:+0.000;-0.000}，" +
                              $"地面 sin = {evidence.SinGround:+0.000;-0.000}，" +
                              $"{evidence.Agreeing}/{evidence.Total} 组三角形一致");
            Console.WriteLine($"    {evidence.Message}");

            // 反证：不换轴直接解，必须过不了 VL-10 / VL-11。这条只做断言，不参与判定。
            var wrongOrder = evidence.Order == GroundAxisOrder.EastNorth
                ? GroundAxisOrder.NorthEast
                : GroundAxisOrder.EastNorth;
            var wrong = NativeAlgorithms.SolveResectionRobust(
                NativeAlgorithms.ToMathFrame(observations, wrongOrder), io.FocalLength, io.PixelSizeMm);
            if (wrong.Success)
            {
                Console.WriteLine($"    [FAIL] 反证失败：按 {GroundAxes.Describe(wrongOrder)} 也解出了被接受的结果" +
                                  $"（σ₀ = {wrong.Sigma0Mm:F4} mm），说明闸门不够严");
                failures++;
            }
            else
            {
                Console.WriteLine($"    [PASS] 反证：按 {GroundAxes.Describe(wrongOrder)} 解 → 拒收" +
                                  $"（Zs={wrong.Zs:F1} m，σ₀={wrong.Sigma0Mm:F4} mm）");
            }

            var result = NativeAlgorithms.SolveResectionRobust(
                NativeAlgorithms.ToMathFrame(observations, evidence.Order),
                io.FocalLength, io.PixelSizeMm);

            if (!result.Success)
            {
                Console.WriteLine($"    [FAIL] {result.Message}");
                failures++;
                continue;
            }

            Console.WriteLine($"    [PASS] 迭代 {result.Iterations} 次　σ₀ = {result.Sigma0Mm:F4} mm" +
                              $"（{result.Sigma0Mm / io.PixelSizeMm:F2} 像元）　多余观测 {result.Redundancy}");
            Console.WriteLine($"      Xs={result.Xs:F2}  Ys={result.Ys:F2}  Zs={result.Zs:F2} m　" +
                              $"φ={result.PhiDeg:+0.0000;-0.0000}°  ω={result.OmegaDeg:+0.0000;-0.0000}°  " +
                              $"κ={result.KappaDeg:+0.0000;-0.0000}°");
            Console.WriteLine($"      平均摄影距离 {result.MeanRangeM:F1} m　条件数 {result.Condition:F1}");
            Console.WriteLine("      点号      Vx(mm)    Vy(mm)   |V|/σ₀");
            foreach (var res in result.Residuals)
            {
                var norm = Math.Sqrt(res.VxMm * res.VxMm + res.VyMm * res.VyMm);
                var ratio = result.Sigma0Mm > 0 ? norm / result.Sigma0Mm : 0;
                Console.WriteLine($"      {res.GcpId,-8} {res.VxMm,9:+0.0000;-0.0000} {res.VyMm,9:+0.0000;-0.0000} " +
                                  $"{ratio,9:F2}{(ratio > 3 ? "   ← 偏大" : "")}");
            }

            // 剔除一个点看 σ₀ 掉多少：掉得最多的那个就是粗差，再算出它本该点在哪
            Console.WriteLine("      逐个剔除后的 σ₀（掉得最多的就是粗差）：");
            var baseline = result.Sigma0Mm;
            string? culprit = null;
            var culpritSigma = baseline;
            ResectionResult? culpritSolve = null;

            foreach (var drop in observations)
            {
                var subset = observations.Where(o => o.GcpId != drop.GcpId).ToList();
                if (subset.Count < NativeAlgorithms.MinControlPoints) continue;

                var without = NativeAlgorithms.SolveResectionAuto(
                    subset, io.FocalLength, io.PixelSizeMm);
                if (!without.Success) continue;

                var s = without.Sigma0Mm;
                Console.WriteLine($"        去掉 {drop.GcpId,-8} → σ₀ = {s:F4} mm" +
                                  $"（{(s / baseline):P0}）{(s < baseline * 0.5 ? "   ← 粗差嫌疑" : "")}");

                if (s < culpritSigma)
                {
                    culpritSigma = s;
                    culprit = drop.GcpId;
                    culpritSolve = without;
                }
            }

            // 用「去掉粗差后」的解把那个点投影回来，告诉作业员它本该在哪个像元
            if (culprit is not null && culpritSolve is not null && project.FindGcp(culprit) is { } bad)
            {
                var observed = observations.FirstOrDefault(o => o.GcpId == culprit);
                var ground = GroundAxes.ToMathFrame(
                    new GroundPointM(bad.X, bad.Y, bad.Z), evidence.Order);

                if (observed is not null &&
                    NativeAlgorithms.ProjectPoint(io.FocalLength, culpritSolve.ToExterior(), ground)
                        is { } mm)
                {
                    var p = CoordinateConverter.ImagePlaneToPixel(mm, io, image.Width, image.Height);
                    var dx = observed.Xmm - mm.X;
                    var dy = observed.Ymm - mm.Y;
                    var dist = Math.Sqrt(dx * dx + dy * dy) / io.PixelSizeMm;

                    Console.WriteLine($"    [粗差] {culprit}：按其余 {observations.Count - 1} 个点定出的姿态，" +
                                      $"它应落在 px ({p.I}, {p.J})；你点在 " +
                                      $"({PxOf(observed.Xmm, io, image.Width)}, " +
                                      $"{PyOf(observed.Ymm, io, image.Height)})，" +
                                      $"差 {dist:F0} 个像元");
                }
            }
        }

        Console.WriteLine(failures == 0 ? "\nPROBE PASS" : $"\nPROBE FAIL ({failures})");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 把地面 (X,Y) 线性拟合到像平面 (x,y)，用两个数判断地面轴序对不对。
    /// <para>
    /// 8 参数仿射给行列式：正常竖直摄影 det 必须为正（像与地面同向）；
    /// 4 参数相似（x = aX + bY，y = −bX + aY）给残差：拟合不上就说明存在镜像或扭曲。
    /// 旋转矩阵行列式恒为 +1，表达不出镜像，所以 det &lt; 0 时姿态怎么调都不可能拟合成功。
    /// </para>
    /// </summary>
    /// <returns>det：仿射行列式；scale：相似变换比例尺（mm/m）；simRatio：相似残差占像点幅度的比例。</returns>
    private static (double Det, double Scale, double SimRatio) SimilarityCheck(
        IReadOnlyList<(double X, double Y, double x, double y)> points)
    {
        int n = points.Count;
        double mX = points.Average(p => p.X);
        double mY = points.Average(p => p.Y);
        double mx = points.Average(p => p.x);
        double my = points.Average(p => p.y);

        double sxx = 0, syy = 0, sxy = 0;      // 地面 XY 的散度
        double sxxX = 0, syX = 0;              // Σ X̃·x̃、Σ Ỹ·x̃
        double sxxY = 0, syY = 0;              // Σ X̃·ỹ、Σ Ỹ·ỹ
        double span = 0;                       // Σ (x̃² + ỹ²)
        foreach (var p in points)
        {
            double X = p.X - mX, Y = p.Y - mY, x = p.x - mx, y = p.y - my;
            sxx += X * X; syy += Y * Y; sxy += X * Y;
            sxxX += X * x; syX += Y * x;
            sxxY += X * y; syY += Y * y;
            span += x * x + y * y;
        }

        // 仿射 x = aX + bY、y = cX + dY：两个系统共用同一个系数阵
        var (a, b) = SolveSymmetric2(sxx, sxy, syy, sxxX, syX);
        var (c, d) = SolveSymmetric2(sxx, sxy, syy, sxxY, syY);
        double det = a * d - b * c;

        // 相似 x = aX + bY、y = −bX + aY：行向量正交，系数阵退化成对角
        double n11 = sxx + syy;
        double den = n11 * n11;
        if (Math.Abs(den) < 1e-18) return (det, 0, double.NaN);

        double sa = (sxxX + syY) / n11;        // Σ(X̃ x̃ + Ỹ ỹ) / n11
        double sb = (syX - sxxY) / n11;        // Σ(Ỹ x̃ − X̃ ỹ) / n11

        double vv = 0;
        foreach (var p in points)
        {
            double X = p.X - mX, Y = p.Y - mY, x = p.x - mx, y = p.y - my;
            double vx = sa * X + sb * Y - x;
            double vy = sa * Y - sb * X - y;
            vv += vx * vx + vy * vy;
        }

        double ratio = span > 0 ? Math.Sqrt(vv / span) : double.NaN;
        return (det, Math.Sqrt(sa * sa + sb * sb), ratio);
    }

    /// <summary>解 [[A,B],[B,C]]·[u;v] = [r;s]。</summary>
    private static (double U, double V) SolveSymmetric2(
        double a, double b, double c, double r, double s)
    {
        double det = a * c - b * b;
        if (Math.Abs(det) < 1e-18) return (0, 0);
        return ((r * c - s * b) / det, (a * s - b * r) / det);
    }

    /// <summary>像平面 x(mm) → 像元 I。</summary>
    private static int PxOf(double xmm, InteriorOrientation io, int width) =>
        (int)Math.Round((xmm + io.X0) / io.PixelSizeMm + width / 2.0);

    /// <summary>像平面 y(mm) → 像元 J。</summary>
    private static int PyOf(double ymm, InteriorOrientation io, int height) =>
        (int)Math.Round(-(ymm + io.Y0) / io.PixelSizeMm + height / 2.0);

    private static void ControlCheck(string? file, string dir)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var ci = CultureInfo.InvariantCulture;
        var failures = 0;

        Console.WriteLine("M3 控制点与后方交会自检");
        Console.WriteLine(new string('=', 78));

        // 给了具体文件就只验它；否则把目录里的 .rtf/.csv 都试一遍
        string[] candidates;
        if (file is not null)
        {
            candidates = File.Exists(file) ? new[] { file } : Array.Empty<string>();
        }
        else
        {
            candidates = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.rtf").Concat(Directory.GetFiles(dir, "*.csv")).ToArray()
                : Array.Empty<string>();
        }

        if (candidates.Length == 0)
        {
            Console.WriteLine($"没有控制点文件：{file ?? dir}");
            Environment.ExitCode = 1;
            return;
        }

        foreach (var candidate in candidates)
        {
            var result = ControlPointFile.Load(candidate);
            Console.WriteLine($"\n文件 {Path.GetFileName(candidate)}　格式 {result.SourceFormat}");
            if (!result.Success)
            {
                foreach (var w in result.Warnings) Console.WriteLine("  [FAIL] " + w);
                failures++;
                continue;
            }

            Console.WriteLine("  " + result.Summary);
            Console.WriteLine($"  放大影像目录 {result.ZoomDirectory ?? "（无）"}");

            foreach (var g in result.Points)
            {
                var zoom = ZoomImageIndex.FindImage(result.ZoomDirectory, g.Id);
                Console.WriteLine($"    {g.Id,-6} {g.X,12:F3} {g.Y,12:F3} {g.Z,9:F3}  " +
                                  (zoom is null ? "无放大片" : Path.GetFileName(zoom)));
            }

            var missing = result.Points
                .Count(g => ZoomImageIndex.FindImage(result.ZoomDirectory, g.Id) is null);
            Console.WriteLine($"  点数 {result.Points.Count}　缺放大片 {missing}");
            if (missing > 0) failures++;
            foreach (var w in result.Warnings) Console.WriteLine("  [WARN] " + w);

            CheckResectionRoundTrip(result.Points, ref failures);
        }

        Console.WriteLine(failures == 0 ? "\nSELFTEST PASS" : "\nSELFTEST FAIL");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    /// <summary>合成往返：9 个控制点、已知外方位，投影成观测再反解。</summary>
    private static void CheckResectionRoundTrip(IReadOnlyList<Gcp> gcps, ref int failures)
    {
        const double f = 152.72;

        // 借用文件里点的真实坐标范围，铺成 3×3 网，这样量纲和真实数据一致。
        // 网只铺到 ±800 m：真实点位铺开近 5 km，按那个跨度反算摄影高度会到 3000 m 以上，
        // 与「近垂直摄影」的初值假设不符。
        var xs = gcps.Select(g => g.X).ToList();
        var ys = gcps.Select(g => g.Y).ToList();
        var cx = (xs.Min() + xs.Max()) / 2;
        var cy = (ys.Min() + ys.Max()) / 2;
        const double span = 800.0;

        // 摄影中心放在控制点网的重心正上方，航高 1600 m：
        // 半幅面 115.28 mm 对应的地面半幅是 1600×115.28/152.72 ≈ 1208 m，能把 ±800 m 的网包进画幅。
        var truth = new ExteriorOrientation
        {
            Xs = cx,
            Ys = cy,
            Zs = 2400.0,
            Phi = 0.35,
            Omega = -0.25,
            Kappa = 6.0
        };

        var observations = new List<ResectionObservation>();
        foreach (var (dx, dy) in new[] { (-1, -1), (0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1) })
        {
            var g = new GroundPointM(cx + dx * span, cy + dy * span, 800.0 + dx * 12.0 - dy * 8.0);
            var mm = NativeAlgorithms.ProjectPoint(f, truth, g);
            if (mm is null)
            {
                Console.WriteLine("  [FAIL] 正投影失败，合成网有问题");
                failures++;
                return;
            }
            observations.Add(new ResectionObservation($"{dx},{dy}", mm.Value.X, mm.Value.Y, g.X, g.Y, g.Z));
        }

        var solved = NativeAlgorithms.SolveResection(observations, f);
        if (!solved.Success)
        {
            Console.WriteLine($"  [FAIL] 后方交会失败 rc={solved.ErrorCode} {solved.Message}");
            Console.WriteLine($"    解出 (Xs,Ys,Zs)=({solved.Xs:F3}, {solved.Ys:F3}, {solved.Zs:F3}) " +
                              $"角=({solved.PhiDeg:F4}, {solved.OmegaDeg:F4}, {solved.KappaDeg:F4})° " +
                              $"迭代 {solved.Iterations}");
            failures++;
            return;
        }

        var dxs = solved.Xs - truth.Xs;
        var dys = solved.Ys - truth.Ys;
        var dzs = solved.Zs - truth.Zs;
        var dphi = solved.PhiDeg - truth.Phi;
        var domega = solved.OmegaDeg - truth.Omega;
        var dkappa = solved.KappaDeg - truth.Kappa;

        Console.WriteLine($"  往返检验　迭代 {solved.Iterations} 次　σ0={solved.Sigma0Mm:E2} mm　" +
                          $"摄影距离 {solved.MeanRangeM:F1} m");
        Console.WriteLine($"    dXs={dxs:E1} dYs={dys:E1} dZs={dzs:E1} m　" +
                          $"dφ={dphi:E1} dω={domega:E1} dκ={dkappa:E1}°");

        var ok = solved.Iterations <= 10 && solved.Redundancy == 12 &&
                 Math.Abs(dxs) < 1e-6 && Math.Abs(dys) < 1e-6 && Math.Abs(dzs) < 1e-6 &&
                 Math.Abs(dphi) < 1e-6 && Math.Abs(domega) < 1e-6 && Math.Abs(dkappa) < 1e-6;
        Console.WriteLine(ok ? "  [PASS] 6 个外方位元素恢复到 1e-6" : "  [FAIL] 外方位元素未恢复");
        if (!ok) failures++;

        // 结构体布局若错了，rotation 不会是正交阵
        var r = solved.Rotation;
        if (r.Count == 9)
        {
            var r00 = r[0] * r[0] + r[3] * r[3] + r[6] * r[6];
            var r01 = r[0] * r[1] + r[3] * r[4] + r[6] * r[7];
            var orthonormal = Math.Abs(r00 - 1) < 1e-9 && Math.Abs(r01) < 1e-9;
            Console.WriteLine(orthonormal
                ? "  [PASS] 旋转矩阵正交（封送布局正确）"
                : "  [FAIL] 旋转矩阵不正交，P/Invoke 结构体布局可能不对");
            if (!orthonormal) failures++;
        }
        else
        {
            Console.WriteLine("  [FAIL] 旋转矩阵长度不对");
            failures++;
        }

        // ---- 稳健入口：倒置解当初值也要能爬回真值
        // 复现现场：某次解算落进「相机在控制点下方、φ≈180°」的倒置解，结果又被写回影像，
        // 下次解算拿它当初值，就永远困在坑里。SolveResectionRobust 应该两次都试。
        var inverted = new ExteriorOrientation
        {
            Xs = truth.Xs,
            Ys = truth.Ys,
            Zs = 800.0 - 2400.0,
            Phi = 177.162,
            Omega = 4.627,
            Kappa = 90.031
        };
        var robust = NativeAlgorithms.SolveResectionRobust(observations, f, 0.04503, inverted);
        var robustOk = robust.Success &&
                       Math.Abs(robust.Xs - truth.Xs) < 1e-6 &&
                       Math.Abs(robust.Zs - truth.Zs) < 1e-6;
        Console.WriteLine(robustOk
            ? "  [PASS] 倒置解当初值仍能解回真值（不被旧结果锁住）"
            : $"  [FAIL] 稳健入口被倒置初值带跑了：{robust.Message}");
        if (!robustOk) failures++;

        // ---- 单个控制点有 30 m 粗差：σ₀ 会远超像元尺寸，必须拒收
        var polluted = new List<ResectionObservation>();
        for (var k = 0; k < observations.Count; k++)
        {
            var o = observations[k];
            polluted.Add(k == 4
                ? new ResectionObservation(o.GcpId, o.Xmm, o.Ymm, o.GroundX + 30.0, o.GroundY, o.GroundZ)
                : o);
        }
        var rejected = NativeAlgorithms.SolveResectionRobust(polluted, f, 0.04503);
        Console.WriteLine(rejected.Success
            ? $"  [FAIL] 粗差没被拒：σ₀ = {rejected.Sigma0Mm:0.0000} mm"
            : $"  [PASS] 粗差被拒：{rejected.Message}");
        if (rejected.Success) failures++;

        // ---- 手性判定：同一批点写成「X 指北、Y 指东」（交换 X/Y），必须判成左手系并换回来
        var swapped = observations
            .Select(o => new ResectionObservation(o.GcpId, o.Xmm, o.Ymm, o.GroundY, o.GroundX, o.GroundZ))
            .ToList();

        var direct = NativeAlgorithms.DetectGroundOrder(observations);
        var directOk = direct.Decided && direct.Order == GroundAxisOrder.EastNorth;
        Console.WriteLine(directOk
            ? $"  [PASS] 右手系数据判为 东-北-天（{direct.Triple}，{direct.Agreeing}/{direct.Total} 组一致）"
            : $"  [FAIL] 右手系数据判错：{direct.Describe()}");
        if (!directOk) failures++;

        var evidence = NativeAlgorithms.DetectGroundOrder(swapped);
        var evidenceOk = evidence.Decided && evidence.Order == GroundAxisOrder.NorthEast;
        Console.WriteLine(evidenceOk
            ? $"  [PASS] 交换 X/Y 后判为左手系（{evidence.Triple}，" +
              $"sin {evidence.SinImage:+0.000;-0.000} / {evidence.SinGround:+0.000;-0.000}，" +
              $"{evidence.Agreeing}/{evidence.Total} 组一致）"
            : $"  [FAIL] 手性判定错：{evidence.Describe()}");
        if (!evidenceOk) failures++;

        var fromSwapped = NativeAlgorithms.SolveResectionAuto(swapped, f, 0.04503);
        var swappedOk = fromSwapped.Success &&
                        Math.Abs(fromSwapped.Xs - truth.Xs) < 1e-6 &&
                        Math.Abs(fromSwapped.Ys - truth.Ys) < 1e-6 &&
                        Math.Abs(fromSwapped.Zs - truth.Zs) < 1e-6;
        Console.WriteLine(swappedOk
            ? "  [PASS] 换轴后解回同一真值"
            : $"  [FAIL] 换轴后解不回：{fromSwapped.Message}");
        if (!swappedOk) failures++;
    }

    /// <summary>
    /// 托管 → 原生 的端到端自检：读 Data/sim 的合成数据，经 P/Invoke 调用 photogrammetry.dll，
    /// 与生成器给出的答案对表。
    /// </summary>
    /// <summary>
    /// M3 端到端：载入控制点文件，按一个已知外方位把每个控制点正投影成像素位置再刺点，
    /// 然后走界面上的解算流程，检查能否把外方位反解回来、σ₀ 是否为 0。
    /// </summary>
    private static void RunM3(MainWindow win, ImageItem image, string gcpFile)
    {
        Console.WriteLine("\nM3 端到端───────────────────────────────────────────────");

        if (!win.LoadControlPointsFrom(Path.GetFullPath(gcpFile)))
        {
            Console.WriteLine("[FAIL] 控制点导入失败");
            return;
        }
        Console.WriteLine($"控制点 {win.CurrentProject.Gcps.Count} 个");

        var io = image.Interior;
        if (io is null || !io.IsValid)
        {
            Console.WriteLine("[FAIL] 影像没有内定向，无法换算像平面坐标");
            return;
        }

        var gcps = win.CurrentProject.Gcps.ToList();
        var cx = gcps.Average(g => g.X);
        var cy = gcps.Average(g => g.Y);
        var cz = gcps.Average(g => g.Z);

        // 航高要能把整片控制点包进画幅：半幅面 mm 对应的地面半幅 = H × 半幅面 / f
        var spread = gcps.Max(g => Math.Max(
            Math.Abs(g.X - cx) + Math.Abs(g.Y - cy), 1.0));
        var halfFrameMm = 0.5 * Math.Min(image.Width, image.Height) * io.PixelSizeMm;
        var height = Math.Max(spread * io.FocalLength / halfFrameMm * 1.15, 500.0);

        var truth = new ExteriorOrientation
        {
            Xs = cx,
            Ys = cy,
            Zs = cz + height,
            Phi = 0.4,
            Omega = -0.3,
            Kappa = 7.0
        };

        var mismatched = 0;
        foreach (var gcp in gcps)
        {
            var mm = NativeAlgorithms.ProjectPoint(io.FocalLength, truth,
                new GroundPointM(gcp.X, gcp.Y, gcp.Z));
            if (mm is null)
            {
                mismatched++;
                continue;
            }

            var pixel = CoordinateConverter.ImagePlaneToPixel(mm.Value, io, image.Width, image.Height);
            if (!win.BindGcpAt(gcp.Id, pixel.I, pixel.J, image)) mismatched++;
        }
        Console.WriteLine($"已刺点 {gcps.Count - mismatched} 个　航高约 {height:0} m　外方位真值 " +
                          $"φ={truth.Phi}° ω={truth.Omega}° κ={truth.Kappa}°");

        var solved = win.SolveResectionNow();
        if (solved is null || !solved.Success)
        {
            Console.WriteLine("[FAIL] 后方交会没解出来");
            return;
        }

        Console.WriteLine($"解算：迭代 {solved.Iterations} 次　σ₀ = {solved.Sigma0Mm:E2} mm　" +
                          $"多余观测 {solved.Redundancy}　条件数 {solved.Condition:0.#}");
        Console.WriteLine($"      Xs={solved.Xs:F3} Ys={solved.Ys:F3} Zs={solved.Zs:F3}");
        Console.WriteLine($"      φ={solved.PhiDeg:F6}° ω={solved.OmegaDeg:F6}° κ={solved.KappaDeg:F6}°");

        // 刺点按整像素落库，量化误差 0.5 px ≈ 0.0225 mm，折算到地面约半米。
        // 所以这里只检"迭代收敛且不跑偏"，亚毫米级的精确性由 --control 那段合成检验负责。
        var ok = Math.Abs(solved.Xs - truth.Xs) < 1.5 &&
                 Math.Abs(solved.Ys - truth.Ys) < 1.5 &&
                 Math.Abs(solved.Zs - truth.Zs) < 1.5 &&
                 Math.Abs(solved.PhiDeg - truth.Phi) < 0.05 &&
                 Math.Abs(solved.OmegaDeg - truth.Omega) < 0.05 &&
                 Math.Abs(solved.KappaDeg - truth.Kappa) < 0.05;
        Console.WriteLine(ok
            ? "[PASS] 刺点 → 像平面坐标 → 后方交会，外方位回到整像素量化误差以内"
            : "[FAIL] 外方位恢复误差超出整像素量化能解释的范围");

        // 截图上要看得见点表与放大影像：选中一个控制点、切回点表页
        if (gcps.Count > 0) Console.WriteLine($"选中点表行 {gcps[0].Id}: {win.SelectGcp(gcps[0].Id)}");

        // 回归：从内定向切过来以后，框标方式必须已经关掉，否则左键会被框标分支吞掉
        var ws = FindChild<ImageWorkspace>(win);
        if (ws is not null)
        {
            var views = ws.VisibleViews().ToList();
            var fiducialLeftOn = views.Any(v => v.FiducialMode);
            var canPick = views.All(v => v.ActiveGcpId is not null);
            Console.WriteLine(fiducialLeftOn
                ? "[FAIL] 还停在框标方式：后方交会里左键会被吞掉"
                : "[PASS] 框标方式已关闭");
            Console.WriteLine(canPick
                ? "[PASS] 已进入刺点模式，左键点影像可绑定控制点"
                : "[FAIL] 没有进入刺点模式");
        }
    }

    private static void FiducialCheck(string dir)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var ci = CultureInfo.InvariantCulture;

        var mmPath = Path.Combine(dir, "m2", "fiducials_image_mm.csv");
        var pxPath = Path.Combine(dir, "m2", "answer_fiducials_px.csv");
        var ansPath = Path.Combine(dir, "m2", "answer_intrinsics.csv");
        if (!File.Exists(mmPath) || !File.Exists(pxPath) || !File.Exists(ansPath))
        {
            Console.WriteLine($"缺少数据文件（{dir}）。先运行： python Data/sim/make_sim.py");
            return;
        }

        var defs = File.ReadAllLines(mmPath).Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(','))
            .Select(c => new FiducialPoint(
                int.Parse(c[0], ci), double.Parse(c[1], ci), double.Parse(c[2], ci)))
            .ToList();

        var obs = File.ReadAllLines(pxPath).Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(','))
            .Select(c => new FiducialObservation(
                int.Parse(c[0], ci), double.Parse(c[1], ci), double.Parse(c[2], ci)))
            .ToList();

        var expected = File.ReadAllLines(ansPath).Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(','))
            .ToDictionary(c => c[0], c => double.Parse(c[1], ci));

        const int W = 5120, H = 5120;

        Console.WriteLine("托管 → 原生 内定向自检");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"原生库版本 {NativeAlgorithms.Version}");
        Console.WriteLine($"真值  δ={expected["pixel_size_delta"]:F9}  x0={expected["x0"]:F9}  " +
                          $"y0={expected["y0"]:F9}  主点=({expected["principal_I"]:F3}, {expected["principal_J"]:F3})");
        Console.WriteLine();

        var r = NativeAlgorithms.SolveFiducialOrientation(defs, obs, W, H);
        if (!r.Success)
        {
            Console.WriteLine($"[FAIL] 解算失败 rc={r.ErrorCode}  {r.Message}");
            Console.WriteLine("\nDLL SELFTEST FAIL");
            return;
        }

        var dDelta = Math.Abs(r.PixelSizeMm - expected["pixel_size_delta"]);
        var dX0 = Math.Abs(r.X0Mm - expected["x0"]);
        var dY0 = Math.Abs(r.Y0Mm - expected["y0"]);
        var pass = dDelta < 1e-9 && dX0 < 1e-9 && dY0 < 1e-9;

        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] 理想取点 σ=0");
        Console.WriteLine($"       δ={r.PixelSizeMm:F9}  偏差 {dDelta:E2}");
        Console.WriteLine($"       x0={r.X0Mm:F9}  偏差 {dX0:E2}");
        Console.WriteLine($"       y0={r.Y0Mm:F9}  偏差 {dY0:E2}");
        Console.WriteLine($"       s_x={r.SxMm:F9}  s_y={r.SyMm:F9}  b2={r.B2:F9}");
        Console.WriteLine($"       θI={r.ThetaIDeg:F4}°  θJ={r.ThetaJDeg:F4}°  " +
                          $"σ0={r.Sigma0Mm:F6} mm  冗余={r.Redundancy}");

        Console.WriteLine();
        Console.WriteLine("逐点残差（mm）");
        foreach (var res in r.Residuals)
            Console.WriteLine($"       框标 {res.Id}   vx={res.VxMm,12:E3}   vy={res.VyMm,12:E3}");

        // 参数式回填
        var io = r.ToInteriorOrientation(152.72);
        var ioOk = Math.Abs(io.X0 - r.X0Mm) < 1e-12 && Math.Abs(io.PixelSizeMm - r.PixelSizeMm) < 1e-12;
        Console.WriteLine();
        Console.WriteLine($"[{(ioOk ? "PASS" : "FAIL")}] 回填 InteriorOrientation：f={io.FocalLength}  " +
                          $"x0={io.X0:F6}  y0={io.Y0:F6}  δ={io.PixelSizeMm:F6}  有效={io.IsValid}");

        // 退化场景：只给 3 个框标
        var r3 = NativeAlgorithms.SolveFiducialOrientation(defs, obs.Take(3).ToList(), W, H);
        var pass3 = !r3.Success && r3.ErrorCode == 2;
        Console.WriteLine($"[{(pass3 ? "PASS" : "FAIL")}] 仅 3 个框标应被拒绝：rc={r3.ErrorCode}  {r3.Message}");

        // 相机检校文件读取
        var camOk = false;
        try
        {
            var camera = CameraFile.Load(mmPath);
            camOk = camera.Fiducials.Count == 8
                    && Math.Abs(camera.Fiducials[0].Xmm - 106.003) < 1e-9
                    && camera.HasEnoughFiducials;
            Console.WriteLine($"[{(camOk ? "PASS" : "FAIL")}] 相机文件读取：{camera.Id}  " +
                              $"框标 {camera.Fiducials.Count} 个  首个 X={camera.Fiducials[0].Xmm:F3}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 相机文件读取：{ex.Message}");
        }

        // 真实相机文件：E-FOTO Hammer 的 .cmr（Wild RC30）
        var cmrPath = Path.Combine("Data", "real", "camera", "hammer.cmr");
        var cmrOk = true;
        if (File.Exists(cmrPath))
        {
            try
            {
                var hammer = CameraFile.Load(cmrPath);
                cmrOk = hammer.Fiducials.Count == 8
                        && Math.Abs(hammer.FocalLengthMm - 152.72) < 1e-9
                        && Math.Abs(hammer.X0Mm + 0.004) < 1e-12
                        && Math.Abs(hammer.Y0Mm + 0.008) < 1e-12
                        && Math.Abs(hammer.Fiducials[0].Xmm - 106.003) < 1e-9
                        && Math.Abs(hammer.Fiducials[7].Ymm) < 1e-9;

                Console.WriteLine($"[{(cmrOk ? "PASS" : "FAIL")}] 真实 .cmr：{hammer.Id}  " +
                                  $"f={hammer.FocalLengthMm}  x0={hammer.X0Mm}  y0={hammer.Y0Mm}  " +
                                  $"框标 {hammer.Fiducials.Count}");
            }
            catch (Exception ex)
            {
                cmrOk = false;
                Console.WriteLine($"[FAIL] 真实 .cmr：{ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[SKIP] 真实 .cmr：未找到 Data/real/camera/hammer.cmr");
        }

        var all = pass && ioOk && pass3 && camOk && cmrOk;
        Console.WriteLine();
        Console.WriteLine(all ? "DLL SELFTEST PASS" : "DLL SELFTEST FAIL");
    }

    private static string? Get(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    private static RadioButton? FindRadio(DependencyObject root, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is RadioButton rb && rb.Content?.ToString() == content) return rb;
            if (child is DependencyObject d && FindRadio(d, content) is { } found) return found;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
  