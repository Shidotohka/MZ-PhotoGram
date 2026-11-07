using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Photogrammetry.App;
using Photogrammetry.App.Services;
using Photogrammetry.Core;
using Photogrammetry.Core.Tasks;

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

        if (args.Any(a => string.Equals(a, "--intersect", StringComparison.OrdinalIgnoreCase)))
        {
            IntersectCheck();
            return;
        }

        if (args.Any(a => string.Equals(a, "--match-relative", StringComparison.OrdinalIgnoreCase)))
        {
            MatchRelativeCheck(Get(args, "--dir") ?? Path.Combine("Data", "real", "2_Hammer", "HammerIndex"));
            return;
        }

        if (args.Any(a => string.Equals(a, "--absolute", StringComparison.OrdinalIgnoreCase)))
        {
            AbsoluteCheck();
            return;
        }

        if (args.Any(a => string.Equals(a, "--relative", StringComparison.OrdinalIgnoreCase)))
        {
            RelativeCheck();
            return;
        }

        if (args.Any(a => string.Equals(a, "--abi", StringComparison.OrdinalIgnoreCase)))
        {
            AbiCheck();
            return;
        }

        // 影像缓存的容量行为：不开窗口、不解工程，直接拿几张临时 PNG 压预算。
        if (args.Any(a => string.Equals(a, "--cache", StringComparison.OrdinalIgnoreCase)))
        {
            CacheCheck();
            return;
        }

        // 结果集合的覆盖语义：不开窗口，只验「不同对象并存、同一对象覆盖、换工程清空」。
        // 这是这次改动的根——旧结果页只有一个槽，第二步会把第一步顶掉。
        if (args.Any(a => string.Equals(a, "--results", StringComparison.OrdinalIgnoreCase)))
        {
            ResultStoreCheck();
            return;
        }

        if (args.Any(a => string.Equals(a, "--control", StringComparison.OrdinalIgnoreCase)))
        {
            ControlCheck(Get(args, "--gcp"),
                Get(args, "--dir") ?? Path.Combine("Data", "real", "2_Hammer", "HammerIndex"));
            return;
        }

        // 离线分析一个 .pht：不解图、不开窗口，直接从工程里存的像点算一遍，
        // 把「平面 / 高程」拆开报出来——界面上只有三维合量，看不出问题在哪一头。
        if (Get(args, "--analyze") is { } analyzePath)
        {
            AnalyzeProject(analyzePath, args.Any(a => string.Equals(a, "--loo", StringComparison.OrdinalIgnoreCase)));
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

        // 点云页签单独出图：不靠主窗，直接拿合成地形灌进报告窗口。
        // 用来验投影、深度雾、包围盒与轴标——这几样错了截图一眼能看出来。
        if (args.Any(a => string.Equals(a, "--cloud-demo", StringComparison.OrdinalIgnoreCase)))
        {
            CloudDemo(Get(args, "--out") ?? "cloud.png",
                      args.Any(a => string.Equals(a, "--hist", StringComparison.OrdinalIgnoreCase)),
                      args.Any(a => string.Equals(a, "--empty", StringComparison.OrdinalIgnoreCase)));
            return;
        }

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

        // 前方交会的界面端到端：两张片装上内/外方位 → 各点一个同名点 → 按解算
        if (args.Any(a => string.Equals(a, "--intersect-demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunIntersectDemo(win);
            Pump(win);
        }

        // 相对定向的界面端到端：两张片各点 12 个同名点 → 点对进点表 → 按解算 → 看面板
        if (args.Any(a => string.Equals(a, "--relative-demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunRelativeDemo(win);
            Pump(win);
        }

        // 模型交会的界面端到端：铺相对定向那套布景 → 8 个控制点按真实刺点路径落点
        // → 切到「模型交会」→ 求模型点 → 接着走绝对定向
        if (args.Any(a => string.Equals(a, "--model-demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunRelativeDemo(win);
            if (Environment.ExitCode == 0)
            {
                RunModelDemo(win);
                Pump(win);
            }
        }

        // 绝对定向的界面端到端：铺相对定向那套布景 → 造 8 个控制点（2 个检查点）
        // → 切到「绝对定向」→ 按解算 → 看结果窗口
        if (args.Any(a => string.Equals(a, "--absolute-demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunRelativeDemo(win);
            if (Environment.ExitCode == 0)
            {
                RunAbsoluteDemo(win);
                Pump(win);
            }
        }

        // 参数式内定向的微米输入：界面填 µm，落库必须是 mm/px
        if (args.Any(a => string.Equals(a, "--interior-um", StringComparison.OrdinalIgnoreCase)))
        {
            CheckInteriorMicrons(win);
            Pump(win);
        }

        // 双图状态探针：两张片都在双图里时，来回切模块后它们还在不在、右栏状态跟不跟
        if (args.Any(a => string.Equals(a, "--view-test", StringComparison.OrdinalIgnoreCase)))
        {
            ViewTest(win);
            Pump(win);
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

        // 内定向的界面端到端：填框标量测 → 按「解算内定向」→ 看结果窗口上有没有那几行
        if (args.Any(a => string.Equals(a, "--interior-demo", StringComparison.OrdinalIgnoreCase)))
        {
            RunInteriorDemo(win, Get(args, "--camera"));
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

        // 结果窗口是独立的顶层窗口，不在主窗的渲染树里；要单独 Render 一次才能看它长什么样。
        // 只用于文档截图与人工核对，正常截图流程不受影响。
        if (Get(args, "--result-shot") is { } resultShotPath)
        {
            if (win.ResultWindowForTooling is { Content: FrameworkElement resultRoot } resultWindow)
            {
                resultWindow.UpdateLayout();
                Pump(win);

                var rw = (int)Math.Round(resultRoot.ActualWidth);
                var rh = (int)Math.Round(resultRoot.ActualHeight);
                var resultRtb = new RenderTargetBitmap(rw, rh, 96, 96, PixelFormats.Pbgra32);
                resultRtb.Render(resultRoot);

                var resultEncoder = new PngBitmapEncoder();
                resultEncoder.Frames.Add(BitmapFrame.Create(resultRtb));
                using var resultStream = File.Create(resultShotPath);
                resultEncoder.Save(resultStream);

                Console.WriteLine($"saved {resultShotPath}（结果窗口 {rw}x{rh}）");
            }
            else
            {
                Console.WriteLine("[FAIL] 没有打开的结果窗口，--result-shot 拍不到东西");
                Environment.ExitCode = 1;
            }
        }

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

        if (!File.Exists(full))
        {
            Console.WriteLine($"种子工程不存在：{full}");
            Console.WriteLine("这个自检要的是 Design/sample-project.pht（两片，image[0] = sample-target.png，f=35）。");
            Console.WriteLine("它没有入库（Design/ 在 .gitignore 里），所以这条路现在是跳过的，不是通过。");
            return;
        }

        var project = ProjectFile.Load(full);

        // 下面每一条断言都写死给那套两片工程（sample-target.png + 一张故意缺失的片，f=35）。
        // 换任何别的工程来跑，必然是"六条红"，而那种红没有信息量。
        // 先认种子：不认就明说跳过，别让人以为工程读写坏了。
        if (project.Images.Count < 2 ||
            !string.Equals(project.Images[0].RelativePath, "sample-target.png", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"种子不对：{Path.GetFileName(full)} 有 {project.Images.Count} 张片，" +
                              $"第一张是「{project.Images.FirstOrDefault()?.RelativePath}」。");
            Console.WriteLine("这个自检只对 Design/sample-project.pht 有意义（它的期望值写死在断言里），跳过。");
            return;
        }

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

    /// <summary>
    /// 报告窗口「点云」页签的离屏出图。合成一片起伏地形灌进
    /// <see cref="MatchReportWindow"/>，走真实构造路径——验投影、深度雾、包围盒、轴标。
    /// <paramref name="histogram"/> 为真时切回直方图页签，用来确认两页没互相踩。
    /// </summary>
    private static void CloudDemo(string outPath, bool histogram, bool empty)
    {
        var random = new Random(12345);
        var count = empty ? 0 : 4000;
        var points = new List<AutoIntersectPoint>(count);

        for (var k = 0; k < count; k++)
        {
            var x = (random.NextDouble() - 0.5) * 800.0;
            var y = (random.NextDouble() - 0.5) * 640.0;
            var z = 26.0 * Math.Sin(x / 130.0) * Math.Cos(y / 95.0)
                    + 14.0 * Math.Sin(y / 42.0)
                    + (random.NextDouble() - 0.5) * 1.2;

            var ground = new IntersectionResult
            {
                Success = true,
                Point = new GroundPointM(x, y, z),
                ConvergenceAngleDeg = 30.0 + 20.0 * random.NextDouble(),
            };

            points.Add(new AutoIntersectPoint(
                k, x, y, x, y,
                new ImagePointMm(x, y), new ImagePointMm(x, y),
                ground, random.NextDouble(), random.NextDouble() * 0.05));
        }

        var run = new AutoIntersectBatchRun { Points = points, Matched = count };

        var report = new MatchReportWindow(run)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false,
        };

        report.Show();
        Pump(report);

        if (report.FindName("ReportTabs") is TabControl tabs)
        {
            tabs.SelectedIndex = histogram ? 0 : 1;
            Pump(report);
        }

        var root = (FrameworkElement)report.Content;
        var width = (int)Math.Round(root.ActualWidth);
        var height = (int)Math.Round(root.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var stream = File.Create(outPath))
        {
            encoder.Save(stream);
        }

        Console.WriteLine($"saved {outPath}（{width}x{height}，{count} 点）");
        if (report.FindName("CloudInfo") is TextBlock info)
        {
            var at = info.TranslatePoint(new Point(0, 0), root);
            Console.WriteLine($"  info: h={info.ActualHeight:0.#} y={at.Y:0.#} text=\"{info.Text}\"");
        }

        report.Close();
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
                // 目录里常与控制点文件混放相机检校文件（cmr*.rtf / *.cmr）。
                // 它按控制点格式当然读不出来，但那不是"控制点文件坏了"——
                // 能按相机格式读出来就跳过，不记失败，否则这条自检永远红。
                if (LooksLikeCameraFile(candidate))
                {
                    Console.WriteLine($"  这是相机检校文件，不是控制点文件，跳过" +
                                      $"（{result.Warnings.FirstOrDefault() ?? "无说明"}）");
                    continue;
                }

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

    /// <summary>
    /// 这个文件是不是相机检校文件。只在控制点解析已经失败时才调用，
    /// 所以不会误伤真正的控制点文件。
    ///
    /// <para>为什么要靠名字：本数据集里 <c>cmr.rc30_ham2.rtf</c> 与 <c>gxyz.rtf</c>
    /// 混放在同一个目录，两者<b>都是 RTF</b>，按内容嗅探分不开——
    /// <c>CameraFile.Load</c> 读它也会抛（它只吃纯文本 .cmr / CSV）。
    /// E-FOTO 的相机文件习惯以 <c>cmr</c> 开头，就按这个约定认。</para>
    /// </summary>
    private static bool LooksLikeCameraFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("cmr", StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetExtension(path).Equals(".cmr", StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            CameraFile.Load(path);
            return true;
        }
        catch
        {
            return false;
        }
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
    /// <summary>
    /// 内定向的界面端到端：按一份<b>已知</b>内参把相机框标反算成像素、填进框标量测，
    /// 再按界面上那个「解算内定向」按钮，最后看「结果窗口」里列出来的东西对不对。
    ///
    /// <para>已知内参是临时定的（δ 按"框标铺满画幅 90%"算），因为这一条测的是
    /// <b>按钮 → 解算 → 结果窗口</b>这条线接不接得上，不是内定向数学本身——
    /// 那个由 <c>--fiducial</c> 对着 <c>Data/sim/m2</c> 的真值验。填进去的三个数
    /// 解完应当原样回来，这顺带说明界面显示的确实是解算值。</para>
    /// </summary>
    private static void RunInteriorDemo(MainWindow win, string? cameraFile)
    {
        Console.WriteLine("\n内定向界面端到端─────────────────────────────────────");

        if (cameraFile is null)
        {
            Console.WriteLine("[FAIL] 需要 --camera 相机检校文件");
            Environment.ExitCode = 1;
            return;
        }

        var ws = FindChild<ImageWorkspace>(win);
        var view = ws?.VisibleViews().FirstOrDefault(v => v.HasImage);
        if (view?.Image is not { } image)
        {
            Console.WriteLine("[FAIL] 需要 --image，并把影像载入工作区");
            Environment.ExitCode = 1;
            return;
        }

        var camera = CameraFile.Load(Path.GetFullPath(cameraFile));
        if (!camera.HasEnoughFiducials)
        {
            Console.WriteLine($"[FAIL] 相机 {camera.Id} 只有 {camera.Fiducials.Count} 个框标");
            Environment.ExitCode = 1;
            return;
        }

        // 缩略图与原始片的像元尺寸不同：按"最外的框标落在画幅 90% 内"定 δ，
        // 再给一点主点偏移，解出来要原样回到这三个数
        var halfSpan = camera.Fiducials.Max(f => Math.Max(Math.Abs(f.Xmm), Math.Abs(f.Ymm)));
        var delta = 2 * halfSpan / (0.9 * Math.Min(image.Width, image.Height));
        const double x0 = 0.012;
        const double y0 = -0.009;

        image.FiducialMarks.Clear();
        foreach (var f in camera.Fiducials)
        {
            image.FiducialMarks.Add(new FiducialMark
            {
                FiducialId = f.Id,
                I = (f.Xmm + x0) / delta + image.Width / 2.0,
                J = -(f.Ymm + y0) / delta + image.Height / 2.0,
            });
        }

        Console.WriteLine($"  相机 {camera.Id}　框标 {camera.Fiducials.Count} 个　" +
                          $"影像 {image.Width}×{image.Height}　填进去 δ={delta:0.000000} mm/px、" +
                          $"x0={x0:0.000}、y0={y0:0.000}");

        // 走界面上的那个按钮，不另开入口
        if (FindButton(win, "解算内定向") is not { } solveButton)
        {
            Console.WriteLine("[FAIL] 找不到「解算内定向」按钮");
            Environment.ExitCode = 1;
            return;
        }

        solveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(win);

        var panel = win.SnapshotResult();
        var names = panel.Items.Select(r => r.Name).ToList();
        var shown = panel.Title.Contains("内定向") && names.Contains("像元尺寸") &&
                    names.Contains("主点 x") && panel.Rows.Count == camera.Fiducials.Count;
        Console.WriteLine(shown
            ? $"[PASS] 结果窗口：{panel.Items.Count} 行元素、{panel.Rows.Count} 行残差"
            : $"[FAIL] 结果窗口不对：title=[{panel.Title}] 元素=[{string.Join("/", names)}] " +
              $"残差={panel.Rows.Count}（应有 {camera.Fiducials.Count}）");
        if (!shown) Environment.ExitCode = 1;

        // 显示出来的三个数必须就是解出来的，不能是"按进去的数重新写了一遍"
        static double ValueOf(IReadOnlyList<ResultRow> items, string symbol) =>
            double.TryParse(items.FirstOrDefault(r => r.Symbol == symbol)?.Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : double.NaN;

        var dDelta = Math.Abs(ValueOf(panel.Items, "δ") - delta);
        var dX0 = Math.Abs(ValueOf(panel.Items, "x₀") - x0);
        var dY0 = Math.Abs(ValueOf(panel.Items, "y₀") - y0);
        var backOk = dDelta < 1e-6 && dX0 < 1e-6 && dY0 < 1e-6;
        Console.WriteLine(backOk
            ? $"[PASS] δ/x0/y0 原样回来（偏差 {dDelta:E1} / {dX0:E1} / {dY0:E1}）"
            : $"[FAIL] 回代偏差过大：δ {dDelta:E2}　x0 {dX0:E2}　y0 {dY0:E2}");
        if (!backOk) Environment.ExitCode = 1;

        var worst = panel.Rows
            .Select(row => row.Cells.Length > 2
                ? Math.Max(Math.Abs(double.Parse(row.Cells[1], CultureInfo.InvariantCulture)),
                           Math.Abs(double.Parse(row.Cells[2], CultureInfo.InvariantCulture)))
                : double.NaN)
            .DefaultIfEmpty(0)
            .Max();
        Console.WriteLine(worst < 1e-6
            ? $"[PASS] 逐点残差全为 0（最大 {worst:E1} mm）"
            : $"[FAIL] 逐点残差最大 {worst:E3} mm，框标是精确反算的，不该有残差");
        if (!(worst < 1e-6)) Environment.ExitCode = 1;

        CheckProcessInfo(win, "内定向");
    }

    /// <summary>
    /// 过程信息（成功与否、迭代几次）不该占结果：结果窗口里只有「任务 · 对象」，
    /// 迭代/σ₀/条件数进日志页。这条检查两件事都盯住。
    /// </summary>
    private static void CheckProcessInfo(MainWindow win, string module)
    {
        var title = win.SnapshotResult().Title;
        var clean = !title.Contains("成功") && !title.Contains("迭代") && !title.Contains("轮");
        Console.WriteLine(clean
            ? $"[PASS] 结果标题只有对象：{title}"
            : $"[FAIL] 过程信息还留在结果标题上：[{title}]");
        if (!clean) Environment.ExitCode = 1;

        var line = win.LogLines.LastOrDefault(l => l.Contains(module));
        Console.WriteLine(line is not null
            ? $"[PASS] 日志记了过程：{line}"
            : $"[FAIL] 日志里没有{module}的记录");
        if (line is null) Environment.ExitCode = 1;
    }

    /// <summary>
    /// 前方交会的界面端到端：给左窗、右窗的影像装上内方位与一组已知外方位，
    /// 按几何把同一个地面点投影成两张片上的像素，走<b>视窗自己的取点路径</b>点上去，
    /// 再按「解算交会点」，检查结果与真值、以及面板上真的显示了坐标。
    ///
    /// <para>与 <c>--intersect</c> 分工不同：那条验数学与闸门，不碰界面；这条验界面接线。</para>
    /// </summary>
    private static void RunIntersectDemo(MainWindow win)
    {
        Console.WriteLine("\n前方交会界面端到端─────────────────────────────────────");

        var ws = FindChild<ImageWorkspace>(win);
        var views = ws?.VisibleViews().Where(v => v.HasImage).ToList();
        if (ws is null || views is null || views.Count != 2)
        {
            Console.WriteLine("[FAIL] 需要两张片：--image 左片 --loadsecond 右片");
            Environment.ExitCode = 1;
            return;
        }

        const double f = 152.72;

        var items = views.Select(v => v.Image!).ToList();

        // 同一台相机、同一个 230 mm 幅面：缩略图与原始片只差采样密度。
        // 写死 0.04503 会把缩略图当成 23 mm 幅面的超远摄镜头（视场 4°），
        // 交会角被压到 7°，整像素量化放大成几十米——那是布景错，不是算法错。
        var frameMm = 230.0;
        var pixelMm = frameMm / Math.Min(items[0].Width, items[0].Height);
        var io = new InteriorOrientation { FocalLength = f, PixelSizeMm = pixelMm };

        const double h = 3000.0;

        // 旁向重叠 30%：基线 = 短边幅面的 70%，与 --intersect 自检同一套几何
        var swathM = frameMm / f * h;
        var baseM = 0.70 * swathM;

        for (var k = 0; k < 2; k++)
        {
            items[k].Interior = io;
            items[k].Exterior = new ExteriorOrientation
            {
                Xs = 1000,
                Ys = 2000 + k * baseM,
                Zs = h,
                Phi = k == 0 ? -0.5 : -0.45,
                Omega = k == 0 ? 0.3 : 0.25,
                Kappa = k == 0 ? 2.0 : 2.4,
            };
        }

        // 交会点压在航高的 83%，与原始片的布景同一个比例
        var truth = new GroundPointM(1000, 2000 + baseM / 2, h * 0.17);
        Console.WriteLine($"真值 {truth.X:0.000}, {truth.Y:0.000}, {truth.Z:0.000} m　" +
                          $"基线 {baseM:0} m　航高 {h:0} m");

        for (var k = 0; k < 2; k++)
        {
            var mm = NativeAlgorithms.ProjectPoint(f, items[k].Exterior!, truth);
            if (mm is not { } m)
            {
                Console.WriteLine($"[FAIL] 第 {k + 1} 张片投影失败");
                Environment.ExitCode = 1;
                return;
            }

            var px = CoordinateConverter.ImagePlaneToPixel(m, io, items[k].Width, items[k].Height);
            // 走视窗自己的取点路径：会在影像上留下看得见的临时像点，并触发选中事件
            views[k].AddPointAt(new Point(px.I, px.J));
            Console.WriteLine($"  第 {k + 1} 张片 {items[k].Id}：像素 ({px.I}, {px.J})");
        }

        Pump(win);

        var run = win.SolveIntersectionNow();
        if (!run.Success)
        {
            Console.WriteLine($"[FAIL] 交会没解出来：{run.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var result = run.Result!;
        var err = Distance(result.Point, truth);

        // 门槛按"整像素量化"的量级给，不写死一个米数：每根视线差半像素，
        // 交会角越小放大越厉害。缩略图的像元粗十倍，精度自然差十倍——那不是缺陷。
        var perRay = 0.5 * pixelMm / f * (h - truth.Z);
        var expected = 2 * perRay / Math.Sin(result.ConvergenceAngleDeg * Math.PI / 180);

        Console.WriteLine($"交会 {result.Point.X:0.000}, {result.Point.Y:0.000}, {result.Point.Z:0.000} m　" +
                          $"误差 {err:0.000} m　交会角 {result.ConvergenceAngleDeg:0.00}°　" +
                          $"量化理论量级 {expected:0.000} m　像元 {pixelMm:0.000000} mm/px");
        Console.WriteLine(err < 3 * Math.Max(expected, 0.1)
            ? "[PASS] 两张片各点一下 → 交会点回到整像素量化能解释的范围内"
            : $"[FAIL] 交会点偏差超过整像素量化量级的 3 倍（{err:0.000} m）");

        var panel = win.SnapshotResult();
        var names = panel.Items.Select(r => r.Name).ToList();
        Console.WriteLine($"结果窗口：{panel.Title}　{panel.Items.Count} 行元素、{panel.Rows.Count} 行残差");

        var shown = panel.Title.Contains("前方交会") && names.Contains("交会角") &&
                    panel.Rows.Count == 2;
        Console.WriteLine(shown
            ? $"[PASS] 结果窗口显示了交会元素与两行残差"
            : $"[FAIL] 结果窗口不对：title=[{panel.Title}] 元素=[{string.Join("/", names)}] 残差={panel.Rows.Count}");
        if (!shown) Environment.ExitCode = 1;

        // 界面上的数必须是解算出来的那个数，不是"看起来像"的另一个数
        var xRow = panel.Items.FirstOrDefault(r => r.Name == "交会点 X");
        var expectX = result.Point.X.ToString("0.000", CultureInfo.InvariantCulture);
        var valueOk = xRow?.Value == expectX;
        Console.WriteLine(valueOk
            ? $"[PASS] 交会点 X 显示 {xRow!.Value} m（与解算结果一致）"
            : $"[FAIL] 交会点 X 显示 [{xRow?.Value}]，解算结果是 {expectX}");
        if (!valueOk) Environment.ExitCode = 1;

        // 结果不再住底部页签：解完要自己弹出来，否则用户不知道算完了
        Console.WriteLine(win.ResultWindowIsOpen
            ? "[PASS] 解完自动打开结果窗口"
            : "[FAIL] 没打开结果窗口，用户看不到结果");
        if (!win.ResultWindowIsOpen) Environment.ExitCode = 1;

        CheckProcessInfo(win, "前方交会");
    }

    /// <summary>
    /// 相对定向的界面端到端：两张片各点 12 个同名像点，看它们是否落成点表里的 12 对，
    /// 再按「解算」按钮那条路径跑一次，最后看面板上有没有结果。
    ///
    /// <para>这里刻意走 <see cref="ImageView.AddPointAt"/>，和用户单击走同一条路径
    /// （新建像点 → 选中 → SelectionChanged → 攒点对 → 落成同名点），
    /// 不是另开一个「塞数据」的后门——那种测法只能证明数据结构和算法是对的，
    /// 证明不了界面接得上。</para>
    /// </summary>
    private static void RunRelativeDemo(MainWindow win)
    {
        Console.WriteLine("\n相对定向界面端到端─────────────────────────────────────");

        var ws = FindChild<ImageWorkspace>(win);
        var views = ws?.VisibleViews().Where(v => v.HasImage).ToList();
        if (ws is null || views is null || views.Count != 2)
        {
            Console.WriteLine("[FAIL] 需要两张片：--image 左片 --loadsecond 右片");
            Environment.ExitCode = 1;
            return;
        }

        const double f = 152.72;
        const double pixelMm = 0.04503;
        const double h = 3000.0;
        const double depth = 2200.0;

        var io = new InteriorOrientation { FocalLength = f, PixelSizeMm = pixelMm };
        var items = views.Select(v => v.Image!).ToList();
        for (var k = 0; k < 2; k++) items[k].Interior = io;

        // 内方位是直接写进模型的，绕过了「载入」与「解算」那两条会刷右栏的路径，
        // 不补这一下，截图里「当前影像」会一直写着「内方位元素：未设置」。
        win.RefreshInspector();

        // 工作区里的影像未必在工程里（--image / --loadsecond 是临时塞进去的）。
        // 同名点的成员是在工程里按影像找的，所以先把这两张登记进工程——
        // 用户是从影像库拖进来的，本来就在工程里，这里只是补上工具跳过的这一步。
        foreach (var item in items)
        {
            if (win.CurrentProject.Images.All(i => i.Id != item.Id)) win.CurrentProject.Images.Add(item);
        }

        Console.WriteLine($"  左 {items[0].Id} {items[0].Width}×{items[0].Height}　" +
                          $"右 {items[1].Id} {items[1].Width}×{items[1].Height}");

        // 布景按**影像自己的幅面**缩：这一对样片是缩略图（512 见方），
        // 按 5616×3744 的航片尺寸摆点会整片落在框外
        var halfY = items[0].Height / 2.0 * pixelMm / f * depth;
        var halfX = items[0].Width / 2.0 * pixelMm / f * depth;
        var baseM = 0.70 * 2.0 * halfY;

        for (var k = 0; k < 2; k++)
        {
            items[k].Exterior = new ExteriorOrientation
            {
                Xs = 1000,
                Ys = 2000 + k * baseM,
                Zs = h,
                Phi = k == 0 ? -0.5 : -0.45,
                Omega = k == 0 ? 0.3 : 0.25,
                Kappa = k == 0 ? 2.0 : 2.4,
            };
        }

        var overlapLowY = 2000 + baseM - halfY;
        var overlapHighY = 2000 + halfY;
        var stepX = 0.55 * halfX / 1.5;

        const int count = 12;
        var added = 0;
        for (var k = 0; k < count; k++)
        {
            var ix = k % 4;
            var iy = k / 4;
            var p = new GroundPointM(
                1000.0 + (ix - 1.5) * stepX,
                overlapLowY + (0.18 + 0.32 * iy) * (overlapHighY - overlapLowY),
                h - depth + 0.05 * depth * Math.Sin(k * 1.7));

            var pixels = new ImagePixel[2];
            var ok = true;
            for (var side = 0; side < 2; side++)
            {
                var mm = NativeAlgorithms.ProjectPoint(f, items[side].Exterior!, p);
                if (mm is not { } m)
                {
                    ok = false;
                    break;
                }

                var px = CoordinateConverter.ImagePlaneToPixel(m, io, items[side].Width, items[side].Height);
                if (px.I < 0 || px.J < 0 || px.I >= items[side].Width || px.J >= items[side].Height)
                {
                    Console.WriteLine($"[FAIL] 第 {k + 1} 个点落在第 {side + 1} 张片框幅外：({px.I}, {px.J})");
                    ok = false;
                    break;
                }

                pixels[side] = px;
            }

            if (!ok)
            {
                Environment.ExitCode = 1;
                return;
            }

            // 左右两片各点一次。第一次只是选中，第二次落成一对同名点
            views[0].AddPointAt(new Point(pixels[0].I, pixels[0].J));
            views[1].AddPointAt(new Point(pixels[1].I, pixels[1].J));
            added++;
        }

        Pump(win);
        Console.WriteLine($"  两片各点了 {added} 次，左 ({items[0].Id}) 右 ({items[1].Id})");

        var ties = win.CurrentProject.TiePoints.Count;
        Console.WriteLine(ties == count
            ? $"[PASS] 点表里攒出 {ties} 对同名点"
            : $"[FAIL] 同名点 {ties} 对（应为 {count}）");
        if (ties != count) Environment.ExitCode = 1;

        var grid = win.FindName("TieGrid") as DataGrid;
        var rows = (grid?.ItemsSource as System.Collections.ICollection)?.Count ?? -1;
        Console.WriteLine(rows == count
            ? $"[PASS] 点表显示了 {rows} 行"
            : $"[FAIL] 点表显示 {rows} 行（应为 {count}）");
        if (rows != count) Environment.ExitCode = 1;

        var run = win.SolveRelativeNow();
        if (!run.Success)
        {
            Console.WriteLine($"[FAIL] 相对定向没解出来：{run.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var r = run.Result!;
        Console.WriteLine($"  {r.Iterations} 轮　基线 ({r.Bx:+0.000000;-0.000000}, {r.By:+0.000000;-0.000000}, " +
                          $"{r.Bz:+0.000000;-0.000000})　φ {r.PhiDeg:+0.0000;-0.0000}°　" +
                          $"q_rms {r.QRmsMm:0.0000} mm");
        Console.WriteLine(r.QRmsMm < 0.03
            ? "[PASS] 上下视差落在整像素量化能解释的量级"
            : $"[FAIL] q_rms {r.QRmsMm:0.0000} mm 偏大");
        if (r.QRmsMm >= 0.03) Environment.ExitCode = 1;

        var panel = win.SnapshotResult();
        var names = panel.Items.Select(r => r.Name).ToList();
        Console.WriteLine($"结果窗口：{panel.Title}　{panel.Items.Count} 行元素、{panel.Rows.Count} 行残差");

        var shown = panel.Title.Contains("相对定向") && names.Contains("基线") &&
                    panel.Rows.Count == count && panel.Headers.FirstOrDefault() == "点号";
        Console.WriteLine(shown
            ? $"[PASS] 结果窗口显示了相对方位元素与 {panel.Rows.Count} 行上下视差"
            : $"[FAIL] 结果窗口不对：title=[{panel.Title}] 元素=[{string.Join("/", names)}] " +
              $"残差={panel.Rows.Count} 列=[{string.Join("/", panel.Headers)}]");
        if (!shown) Environment.ExitCode = 1;

        // 逐点 q 要与解算器给出的逐点残差逐位一致（同一次解，没道理不同）
        var expectQ = r.ResidualQ[0].ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture);
        var shownQ = panel.Rows[0].Cells.Length > 1 ? panel.Rows[0].Cells[1] : "";
        Console.WriteLine(shownQ == expectQ
            ? $"[PASS] 第 1 个点的 q 显示 {shownQ} mm（与解算器一致）"
            : $"[FAIL] 第 1 个点的 q 显示 [{shownQ}]，解算器给的是 {expectQ}");
        if (shownQ != expectQ) Environment.ExitCode = 1;

        Console.WriteLine($"[PASS] 结果写进了像对：{win.CurrentProject.Pairs.Count} 个像对");

        CheckProcessInfo(win, "相对定向");
    }

    /// <summary>
    /// 绝对定向的界面端到端。前置：<see cref="RunRelativeDemo"/> 已经铺好两片与像对。
    ///
    /// <para>造 8 个控制点（后 2 个是检查点），按已知外方位投影成两片上的刺点，
    /// 然后切到「绝对定向」按一次解算——走的是界面真正的按钮路径，不是另开一个口子。</para>
    /// </summary>
    /// <summary>
    /// 双图状态探针：两张片放进左右窗之后，来回切一次模块，看影像还在不在、右栏跟不跟。
    /// 用来复现「拖进第二张后右栏不更新」「切走再回来第二张不见了」这类操作问题。
    /// </summary>
    private static void ViewTest(MainWindow win)
    {
        Console.WriteLine("\n双图状态探针───────────────────────────────────────────");

        var ws = FindChild<ImageWorkspace>(win);
        if (ws is null)
        {
            Console.WriteLine("[FAIL] 找不到工作区");
            Environment.ExitCode = 1;
            return;
        }

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        var afterLoad = Snapshot();
        Check("拖进来的两张片都在可见视窗里",
            afterLoad.Count == 2 && afterLoad.All(v => v.HasImage),
            string.Join("　", afterLoad.Select(v => $"{v.Name}={v.Id}")));

        var pairText = (win.FindName("RelativePairText") as TextBlock)?.Text ?? "";
        var loadedIds = afterLoad.Select(v => v.Id).ToList();
        Check("右栏已经显示了两片（不是停在拖之前的状态）",
            loadedIds.All(id => id != "—" && pairText.Contains(id)),
            pairText.Replace("\n", " | "));

        // 来回切一次模块
        win.SelectModuleForTooling("内定向");
        Pump(win);
        win.SelectModuleForTooling("相对定向");
        Pump(win);

        var afterRound = Snapshot();
        Check("切走再切回来，两张片还在",
            afterRound.Count == 2 && afterRound.All(v => v.HasImage),
            string.Join("　", afterRound.Select(v => $"{v.Name}={v.Id}")));

        Check("切回来之后右栏仍然显示两片",
            ((win.FindName("RelativePairText") as TextBlock)?.Text ?? "").Replace("\n", " | ") ==
            pairText.Replace("\n", " | "),
            (win.FindName("RelativePairText") as TextBlock)?.Text?.Replace("\n", " | ") ?? "");

        // ---- 并发载入：两张片几乎同时进来（拖放是 fire-and-forget，最容易在这里翻车）----
        var items = ws.VisibleViews().Select(v => v.Image).OfType<ImageItem>().ToList();
        // --image 那条路只写 RelativePath；并发载入要解码，必须补 FullPath
        foreach (var it in items)
        {
            if (it.FullPath is null && it.RelativePath is { } rel) it.FullPath = Path.GetFullPath(rel);
        }
        if (items.Count == 2)
        {
            Console.WriteLine($"  并发载入：{items[0].Id}(path={items[0].FullPath ?? "null"})　" +
                              $"{items[1].Id}(path={items[1].FullPath ?? "null"})");
            ws.Reset();
            Pump(win);

            var load = win.LoadTwoImagesTogetherForTooling(items[0], items[1]);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!load.IsCompleted && DateTime.UtcNow < deadline)
            {
                Pump(win);
                System.Threading.Thread.Sleep(20);
            }

            Console.WriteLine($"  任务 {load.Status}　异常 {load.Exception?.GetBaseException().Message ?? "无"}");
            var afterRace = Snapshot();
            Check("两张片同时载入也各占一个视窗（载入串行闸）",
                afterRace.Count == 2 && afterRace.All(v => v.HasImage) &&
                afterRace.Select(v => v.Id).Distinct().Count() == 2,
                string.Join("　", afterRace.Select(v => $"{v.Name}={v.Id}")));
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "VIEW PASS" : $"VIEW FAIL（{failures} 项）");
        if (failures != 0) Environment.ExitCode = 1;

        List<(string Name, bool HasImage, string Id)> Snapshot() =>
            ws.VisibleViews().Select(v => (v.GetType().Name, v.HasImage, v.Image?.Id ?? "—")).ToList();
    }

    /// <summary>
    /// 离线分析一个 <c>.pht</c>：不开窗口、不解码影像，直接拿工程里存着的
    /// 「像点 + 相对定向 + 控制点」重算模型交会与绝对定向。
    ///
    /// <para>为什么要有它：界面上的结果窗口只给三维合量的 rms，而交会角小的时候高程误差被
    /// $H/B$ 放大，平面与高程能差一个量级——数字拆开才看得出瓶颈在哪。</para>
    /// </summary>
    private static void AnalyzeProject(string path, bool leaveOneOut = false)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (!File.Exists(path))
        {
            Console.WriteLine($"找不到工程文件：{path}");
            Environment.ExitCode = 1;
            return;
        }

        var project = ProjectFile.Load(path);

        Console.WriteLine($"工程分析　{Path.GetFileName(path)}");
        Console.WriteLine(new string('=', 78));

        // ---- 影像与内方位 ----
        foreach (var image in project.Images)
        {
            var io = image.Interior;
            Console.WriteLine($"  {image.Id}　{image.Width}×{image.Height} px　" +
                (io is { IsValid: true }
                    ? $"f {io.FocalLength:0.###} mm　δ {io.PixelSizeMm * 1000:0.###} µm（{io.PixelSizeMm:0.000000} mm/px）　x0 {io.X0:+0.0000;-0.0000} y0 {io.Y0:+0.0000;-0.0000} mm"
                    : "无内方位"));
        }

        Console.WriteLine($"  控制点 {project.Gcps.Count} 个（检查点 {project.Gcps.Count(g => g.Type == GcpType.CheckPoint)} 个）" +
                          $"　同名点 {project.TiePoints.Count} 对");

        var pair = project.Pairs.FirstOrDefault(p => p.Relative is not null);
        if (pair is null)
        {
            Console.WriteLine("\n没有解过相对定向的像对，后面的算不了。");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"\n  像对 {pair.LeftImageId} × {pair.RightImageId}");
        var rel = pair.Relative!;
        Console.WriteLine($"  相对定向　b=({rel.Bx:+0.0000000;-0.0000000}, {rel.By:+0.0000000;-0.0000000}, {rel.Bz:+0.0000000;-0.0000000})" +
                          $"　φ {rel.Phi2:+0.0000;-0.0000}° ω {rel.Omega2:+0.0000;-0.0000}° κ {rel.Kappa2:+0.0000;-0.0000}°");
        Console.WriteLine($"  　　　　　q_rms {rel.RmsQ:0.0000} mm　σ₀ {rel.Sigma0Mm:0.0000} mm　{rel.UsedCount} 对参与");

        // ---- 模型交会 ----
        var model = ModelIntersectionTask.Run(project, pair);
        if (!model.Success)
        {
            Console.WriteLine($"\n模型交会失败：{model.Failure}　{model.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var worst = model.Observations.Min(o => o.ConvergenceAngleDeg);
        var depth = model.Observations.Average(o => Math.Abs(o.Model.Z));
        Console.WriteLine($"\n  模型交会　{model.Observations.Count} 个点（检查点 {model.Observations.Count(o => o.Type == GcpType.CheckPoint)}）" +
                          $"　最差交会角 {worst:0.00}°");
        foreach (var w in model.Skipped.Take(8)) Console.WriteLine($"    跳过 {w}");
        if (model.Skipped.Count > 8) Console.WriteLine($"    跳过…共 {model.Skipped.Count} 个");

        foreach (var o in model.Observations)
        {
            Console.WriteLine($"    {o.GcpId,-6} 模型 ({o.Model.X:+0.0000;-0.0000}, {o.Model.Y:+0.0000;-0.0000}, {o.Model.Z:+0.0000;-0.0000})" +
                              $"　交会角 {o.ConvergenceAngleDeg:0.00}°");
        }

        // ---- 绝对定向 ----
        var run = AbsoluteOrientationTask.Run(project, pair);
        if (!run.Success)
        {
            Console.WriteLine($"\n绝对定向失败：{run.Failure}　{run.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var r = run.Result!;
        Console.WriteLine($"\n  绝对定向　λ {r.Scale:0.000000} m（模型单位 → 米）");
        Console.WriteLine($"  　　　　　φ {r.PhiDeg:+0.0000;-0.0000}° ω {r.OmegaDeg:+0.0000;-0.0000}° κ {r.KappaDeg:+0.0000;-0.0000}°" +
                          $"　平移 ({r.X0:0.000}, {r.Y0:0.000}, {r.Z0:0.000}) m");
        Console.WriteLine($"  　　　　　不符值 rms {r.Rms:0.0000} m　平面 Δxy {run.RmsXy:0.0000} m　高程 Δz {run.RmsZ:0.0000} m" +
                          $"　σ₀ {r.Sigma0:0.0000} m");
        Console.WriteLine($"  　　　　　深度放大 H/B {run.DepthAmplification:0.000}　地面采样 {run.GroundSampleDistance:0.0000} m/px" +
                          $"　σ₀ 折 {r.Sigma0 / run.GroundSampleDistance:0.00} px");
        Console.WriteLine($"  　　　　　参与 {run.Used.Count}　检查点 {run.Checks.Count}　多余观测 {r.Redundancy}" +
                          (r.Singular.Count >= 3 && r.Singular[0] > 0
                              ? $"　σ₃/σ₁ {r.Singular[2] / r.Singular[0]:0.0E+0}"
                              : ""));

        Console.WriteLine();
        Console.WriteLine($"  {"点号",-8}{"Δxy (m)",12}{"Δz (m)",12}{"|v| (m)",12}{"v/σ₀",9}");
        foreach (var o in run.Used)
        {
            var t = r.Transform(o.Model);
            var g = GroundAxes.ToMathFrame(o.Ground, run.GroundOrder);
            var dxy = Math.Sqrt((t.X - g.X) * (t.X - g.X) + (t.Y - g.Y) * (t.Y - g.Y));
            var dz = t.Z - g.Z;
            var ratio = r.Sigma0 > 0 ? o.ResidualLength / r.Sigma0 : double.NaN;
            Console.WriteLine($"  {o.GcpId,-8}{dxy,12:0.0000}{dz,12:+0.0000;-0.0000}{o.ResidualLength,12:0.0000}{ratio,9:0.00}");
        }
        foreach (var c in run.Checks)
        {
            var t = c.Transformed ?? r.Transform(c.Model);
            var g = GroundAxes.ToMathFrame(c.Ground, run.GroundOrder);
            var dxy = Math.Sqrt((t.X - g.X) * (t.X - g.X) + (t.Y - g.Y) * (t.Y - g.Y));
            Console.WriteLine($"  {c.GcpId,-8}{dxy,12:0.0000}{t.Z - g.Z,12:+0.0000;-0.0000}{"—",12}{"检查点",9}");
        }

        foreach (var w in run.Warnings) Console.WriteLine($"  ! {w}");

        // ---- 留一法：轮流把每个点当检查点 ----
        // 一个点被拿掉后解会变多少、它自己误差多大，这两件事一起看才能分清
        // 「某个点刺歪了」和「整片几何本来就弱」。
        if (leaveOneOut && model.Observations.Count >= 4)
        {
            var originals = project.Gcps.ToDictionary(g => g.Id, g => g.Type);
            Console.WriteLine();
            Console.WriteLine("  留一法：轮流留一个点不参与解算，看它自己的偏差");
            Console.WriteLine($"  {"留给它",-10}{"Δxy (m)",12}{"Δz (m)",12}{"λ 变化 (m)",14}{"φ/ω 变化 (°)",16}");

            foreach (var observation in model.Observations)
            {
                foreach (var g in project.Gcps) g.Type = GcpType.Planimetric;
                project.Gcps.First(g => g.Id == observation.GcpId).Type = GcpType.CheckPoint;

                var loo = AbsoluteOrientationTask.Run(project, pair);
                if (!loo.Success || loo.Result is not { } lr)
                {
                    Console.WriteLine($"  {observation.GcpId,-10}{"—",12}{"—",12}同一批点解不出来");
                    continue;
                }

                var check = loo.Checks.FirstOrDefault();
                var t = check?.Transformed ?? lr.Transform(observation.Model);
                var gk = GroundAxes.ToMathFrame(observation.Ground, loo.GroundOrder);
                var dxy = Math.Sqrt((t.X - gk.X) * (t.X - gk.X) + (t.Y - gk.Y) * (t.Y - gk.Y));
                var dz = t.Z - gk.Z;
                var dPhi = lr.PhiDeg - r.PhiDeg;
                var dOmega = lr.OmegaDeg - r.OmegaDeg;
                var scaleText = $"{lr.Scale - r.Scale:+0.0000;-0.0000;0.0000}";
                var angleText = $"{dPhi:+0.000;-0.000;0.000} / {dOmega:+0.000;-0.000;0.000}";

                Console.WriteLine($"  {observation.GcpId,-10}{dxy,12:0.0000}{dz,12:+0.0000;-0.0000}" +
                                  $"{scaleText,14}{angleText,20}");
            }

            foreach (var g in project.Gcps) g.Type = originals[g.Id];
        }

        // 一句结论：把瓶颈指名道姓地说出来
        var gsd = run.GroundSampleDistance;
        if (!double.IsNaN(gsd) && gsd > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  结论：平面 {run.RmsXy / gsd:0.00} px、高程 {run.RmsZ / gsd:0.00} px" +
                              $"（σ₀ {r.Sigma0 / gsd:0.00} px，判据 ≤ 10 px）；" +
                              $"深度被 H/B = {run.DepthAmplification:0.0} 放大。");
        }
    }

    private static void RunAbsoluteDemo(MainWindow win)
    {
        Console.WriteLine("\n绝对定向界面端到端─────────────────────────────────────");

        var project = win.CurrentProject;
        var pair = project.Pairs.FirstOrDefault(p => p.Relative is not null);
        if (pair is null)
        {
            Console.WriteLine("[FAIL] 没有解过相对定向的像对——先跑 --relative-demo 那套布景");
            Environment.ExitCode = 1;
            return;
        }

        var left = project.Images.First(i => i.Id == pair.LeftImageId);
        var right = project.Images.First(i => i.Id == pair.RightImageId);

        // 与相对定向 demo 同一套布景；控制点走界面真正的那条刺点路径
        var (ground, bound, baseM) = SeedGcpScene(win, left, right);
        Console.WriteLine($"  造了 {ground.Count} 个控制点（后 2 个检查点），两片共绑定 {bound} 个像点");

        if (!win.SelectModuleForTooling("绝对定向"))
        {
            Console.WriteLine("[FAIL] 切不到「绝对定向」模块");
            Environment.ExitCode = 1;
            return;
        }

        Pump(win);
        win.UpdateAbsolutePanel();
        Pump(win);

        var pairText = (win.FindName("AbsolutePairText") as System.Windows.Controls.TextBlock)?.Text;
        var hint = (win.FindName("AbsoluteHint") as System.Windows.Controls.TextBlock)?.Text;
        Console.WriteLine($"  面板：像对「{pairText}」");
        Console.WriteLine($"        提示「{hint}」");

        var run = win.SolveAbsoluteNow();
        Pump(win);

        if (!run.Success)
        {
            Console.WriteLine($"[FAIL] 绝对定向没解出来：{run.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var r = run.Result!;
        Console.WriteLine($"  λ {r.Scale:0.000000}　φ {r.PhiDeg:+0.0000;-0.0000}°　" +
                          $"ω {r.OmegaDeg:+0.0000;-0.0000}°　κ {r.KappaDeg:+0.0000;-0.0000}°");
        Console.WriteLine($"  rms {r.Rms:0.0000} m　σ₀ {r.Sigma0:0.0000} m　" +
                          $"控点 {run.Used.Count}　检查点 {run.Checks.Count}　多余观测 {r.Redundancy}");

        // 模型坐标以基线长为单位，所以 λ 必须等于这条基线的长度
        var deviation = Math.Abs(r.Scale - baseM) / baseM;
        Console.WriteLine(deviation < 0.05
            ? $"[PASS] λ = 基线长度　基线 {baseM:0.0} m，λ {r.Scale:0.000} m（差 {100 * (r.Scale - baseM) / baseM:+0.0;-0.0}%）"
            : $"[FAIL] λ {r.Scale:0.000} m 与基线 {baseM:0.0} m 差 {100 * deviation:0.0}%");
        if (deviation >= 0.05) Environment.ExitCode = 1;

        var panel = win.SnapshotResult();
        var names = panel.Items.Select(i => i.Name).ToList();
        var shown = panel.Title.Contains("绝对定向") && names.Contains("尺度") &&
                    panel.Rows.Count == ground.Count;
        Console.WriteLine(shown
            ? $"[PASS] 结果窗口显示了七参数与 {panel.Rows.Count} 行不符值"
            : $"[FAIL] 结果窗口不对：title=[{panel.Title}] 元素=[{string.Join("/", names)}] 行数={panel.Rows.Count}");
        if (!shown) Environment.ExitCode = 1;

        var header = panel.Headers.FirstOrDefault();
        Console.WriteLine(header == "点号"
            ? "[PASS] 不符值表的列头是点号"
            : $"[FAIL] 不符值表列头是「{header}」");

        // 平面/高程拆开 + 判据换成像素：这两行是这次加的，得在真的结果窗口上看到
        var hasSplit = names.Contains("不符值（平面）") && names.Contains("不符值（高程）");
        Console.WriteLine(hasSplit
            ? "[PASS] 结果窗口把平面与高程分开列了"
            : $"[FAIL] 没找到平面的那两行：元素=[{string.Join("/", names)}]");
        if (!hasSplit) Environment.ExitCode = 1;

        var sigmaRow = panel.Items.FirstOrDefault(i => i.Symbol == "σ₀");
        var gateOk = sigmaRow is not null && sigmaRow.Note.Contains("px") && sigmaRow.Note.Contains("判据");
        Console.WriteLine(gateOk
            ? $"[PASS] σ₀ 的判据折成像元　{sigmaRow!.Note}"
            : $"[FAIL] σ₀ 那行没有像素判据：[{sigmaRow?.Note}]");
        if (!gateOk) Environment.ExitCode = 1;

        CheckProcessInfo(win, "绝对定向");
    }
    /// <summary>
    /// 给模型交会 / 绝对定向的演示造控制点场景：与相对定向 demo 同一套布景，
    /// 8 个控制点（后 2 个检查点），按已知外方位投影成两片上的刺点。
    ///
    /// <para>走 <see cref="MainWindow.BindGcpAt"/>——也就是界面点下去的那条路，
    /// 不直接往模型里塞 ImagePoint，否则测的就不是界面了。</para>
    /// </summary>
    private static (List<GroundPointM> Ground, int Bound, double BaseM) SeedGcpScene(
        MainWindow win, ImageItem left, ImageItem right, int count = 8, int checkFrom = 6)
    {
        const double f = 152.72;
        const double h = 3000.0;
        const double depth = 2200.0;

        var io = left.Interior!;
        var pixelMm = io.PixelSizeMm;
        var halfY = left.Height / 2.0 * pixelMm / f * depth;
        var halfX = left.Width / 2.0 * pixelMm / f * depth;
        var baseM = 0.70 * 2.0 * halfY;
        var overlapLowY = 2000 + baseM - halfY;
        var overlapHighY = 2000 + halfY;

        var ground = new List<GroundPointM>();
        for (var k = 0; k < count; k++)
        {
            var ix = k % 4;
            var iy = k / 4;
            ground.Add(new GroundPointM(
                1000.0 + (ix - 1.5) * 0.45 * halfX,
                overlapLowY + (0.30 + 0.40 * iy) * (overlapHighY - overlapLowY),
                h - depth + 25.0 * Math.Sin(k * 1.3)));
        }

        var bound = 0;
        for (var k = 0; k < ground.Count; k++)
        {
            var id = $"K{k + 1:00}";
            var g = ground[k];
            win.CurrentProject.Gcps.Add(new Gcp
            {
                Id = id,
                X = g.X,
                Y = g.Y,
                Z = g.Z,
                Type = k >= checkFrom ? GcpType.CheckPoint : GcpType.Planimetric,
            });

            foreach (var image in new[] { left, right })
            {
                var mm = NativeAlgorithms.ProjectPoint(f, image.Exterior!, g);
                if (mm is not { } m) continue;

                var px = CoordinateConverter.ImagePlaneToPixel(m, io, image.Width, image.Height);
                if (px.I < 0 || px.J < 0 || px.I >= image.Width || px.J >= image.Height) continue;

                if (win.BindGcpAt(id, px.I, px.J, image)) bound++;
            }
        }


        return (ground, bound, baseM);
    }

    /// <summary>
    /// 模型交会的界面端到端。前置：<see cref="RunRelativeDemo"/> 已铺好两片与像对。
    /// </summary>
    private static void RunModelDemo(MainWindow win)
    {
        Console.WriteLine("\n模型交会界面端到端─────────────────────────────────────");

        var project = win.CurrentProject;
        var pair = project.Pairs.FirstOrDefault(p => p.Relative is not null);
        if (pair is null)
        {
            Console.WriteLine("[FAIL] 没有解过相对定向的像对——先跑 --relative-demo 那套布景");
            Environment.ExitCode = 1;
            return;
        }

        var left = project.Images.First(i => i.Id == pair.LeftImageId);
        var right = project.Images.First(i => i.Id == pair.RightImageId);

        if (!win.SelectModuleForTooling("模型交会"))
        {
            Console.WriteLine("[FAIL] 切不到「模型交会」模块");
            Environment.ExitCode = 1;
            return;
        }

        Pump(win);

        // 先切进模块、再刺点：这就是用户手上的顺序（空模块 → 一个个刺 → 按「求模型点」）。
        // 按旧代码，按钮状态只在切模块和解算时算一次，刺完点仍是灰的，看着像"点了没反应"。
        Console.WriteLine(win.ModelSolveEnabled
            ? "[FAIL] 一个点都没刺，「求模型点」却是可点的"
            : "[PASS] 空模块时「求模型点」灰着（闸门起作用）");
        if (win.ModelSolveEnabled) Environment.ExitCode = 1;

        var (ground, bound, baseM) = SeedGcpScene(win, left, right);
        Console.WriteLine($"  造了 {ground.Count} 个控制点（后 2 个检查点），两片共绑定 {bound} 个像点");

        Pump(win);
        Console.WriteLine(win.ModelSolveEnabled
            ? "[PASS] 刺完点后「求模型点」自动变可用（状态随刺点刷新）"
            : "[FAIL] 刺完点按钮仍是灰的——状态没刷新，用户会以为点了没反应");
        if (!win.ModelSolveEnabled) Environment.ExitCode = 1;

        win.UpdateModelPanel();
        Pump(win);

        var hint = (win.FindName("ModelHint") as System.Windows.Controls.TextBlock)?.Text;
        var pairText = (win.FindName("ModelPairText") as System.Windows.Controls.TextBlock)?.Text;
        Console.WriteLine($"  面板：像对「{pairText}」");
        Console.WriteLine($"        提示「{hint}」");

        var outcome = win.SolveModelNow();
        Pump(win);

        if (!outcome.Success)
        {
            Console.WriteLine($"[FAIL] 模型交会没解出来：{outcome.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var worst = outcome.Observations.Min(o => o.ConvergenceAngleDeg);
        Console.WriteLine($"  {outcome.Observations.Count} 个模型点，最差交会角 {worst:0.00}°");
        Console.WriteLine(worst >= 15.0
            ? "[PASS] 交会角都在 15° 以上"
            : $"[FAIL] 有交会角小于 15° 的点（最差 {worst:0.00}°）");
        if (worst < 15.0) Environment.ExitCode = 1;

        var spanX = outcome.Observations.Max(o => o.Model.X) - outcome.Observations.Min(o => o.Model.X);
        var spanY = outcome.Observations.Max(o => o.Model.Y) - outcome.Observations.Min(o => o.Model.Y);
        var spanZ = outcome.Observations.Max(o => o.Model.Z) - outcome.Observations.Min(o => o.Model.Z);
        Console.WriteLine($"  跨度 X {spanX:0.000}　Y {spanY:0.000}　Z {spanZ:0.000}（基线长 {baseM:0} m，比例尺未知）");
        Console.WriteLine(spanY > 0.05 && spanY < 0.60
            ? "[PASS] 模型跨度是基线长的量级（比例尺 |b| = 1）"
            : $"[FAIL] 跨度 {spanY:0.000} 不像基线长量级");
        if (!(spanY > 0.05 && spanY < 0.60)) Environment.ExitCode = 1;

        var grid = win.FindName("ModelGrid") as System.Windows.Controls.DataGrid;
        var rows = (grid?.ItemsSource as System.Collections.ICollection)?.Count ?? -1;
        Console.WriteLine(rows == ground.Count
            ? $"[PASS] 点表列出了 {rows} 个控制点"
            : $"[FAIL] 点表 {rows} 行（应为 {ground.Count}）");
        if (rows != ground.Count) Environment.ExitCode = 1;

        var row0 = grid?.Items.Count > 0 ? grid.Items[0] : null;
        var modelX = row0?.GetType().GetProperty("ModelX")?.GetValue(row0) as string;
        Console.WriteLine(modelX is not null && modelX != "—"
            ? $"[PASS] 点表里已有模型坐标（第一行 X = {modelX}）"
            : "[FAIL] 点表里模型坐标还是空档");
        if (modelX is null || modelX == "—") Environment.ExitCode = 1;

        var panel = win.SnapshotResult();
        var names = panel.Items.Select(i => i.Name).ToList();
        var shown = panel.Title.Contains("模型交会") && names.Contains("最差交会角") &&
                    panel.Rows.Count == ground.Count;
        Console.WriteLine(shown
            ? $"[PASS] 结果窗口显示了模型点与 {panel.Rows.Count} 行坐标"
            : $"[FAIL] 结果窗口不对：title=[{panel.Title}] 元素=[{string.Join("/", names)}] 行数={panel.Rows.Count}");
        if (!shown) Environment.ExitCode = 1;

        // 模型交会表上的右键「设为检查点」——这条路上曾经读错了表（只看控制点表），
        // 于是在这张表上右键是死的。下面走的正是菜单那两个处理器。
        var k01 = win.CurrentProject.Gcps.FirstOrDefault(g => g.Id == "K01");
        var rowFound = win.SetGcpTypeFromModelTableForTooling("K01", checkPoint: true);
        Console.WriteLine(rowFound && k01 is { Type: GcpType.CheckPoint }
            ? "[PASS] 模型交会表右键能把控制点改成检查点"
            : $"[FAIL] 改类型没生效（找到行={rowFound}，类型={k01?.Type}）");
        if (!rowFound || k01 is not { Type: GcpType.CheckPoint }) Environment.ExitCode = 1;

        // 检查点必须真的退出解算，否则"独立检核"就是空话
        var asCheck = AbsoluteOrientationTask.Prepare(win.CurrentProject, pair);
        var checkCount = asCheck.Observations.Count(o => o.Type == GcpType.CheckPoint);
        Console.WriteLine(checkCount == 3
            ? "[PASS] 改成检查点后，绝对定向的输入里它就退出了解算（3 个检查点）"
            : $"[FAIL] 检查点只有 {checkCount} 个（应为 3）");
        if (checkCount != 3) Environment.ExitCode = 1;

        var stillThere = win.SetGcpTypeFromModelTableForTooling("K01", checkPoint: false);
        Console.WriteLine(stillThere && k01 is { Type: GcpType.Planimetric }
            ? "[PASS] 同一行再改回控制点也生效"
            : $"[FAIL] 改回控制点没生效（类型={k01?.Type}）");
        if (!stillThere || k01 is not { Type: GcpType.Planimetric }) Environment.ExitCode = 1;

        // 同一条链接着走绝对定向：模型交会的产物就是它的输入
        if (win.SelectModuleForTooling("绝对定向"))
        {
            Pump(win);
            var run = win.SolveAbsoluteNow();
            Pump(win);
            Console.WriteLine(run.Success
                ? $"[PASS] 接着做绝对定向：λ {run.Result!.Scale:0.000}（基线 {baseM:0} m）"
                : $"[FAIL] 绝对定向没解出来：{run.Message}");
            if (!run.Success) Environment.ExitCode = 1;
        }

        // 这次改动要的正是这一条：相对定向算完再算模型交会，前一份还在。
        // 旧的结果窗口只有一个槽，第二步一算第一步就没了，想回看只能重算。
        var results = win.SnapshotResults();
        var hasRelative = results.Any(r => r.Kind == ResultKind.Relative);
        var hasModel = results.Any(r => r.Kind == ResultKind.Model);
        Console.WriteLine(hasRelative && hasModel
            ? $"[PASS] 两步的结果并存（{results.Count} 份：{string.Join("、", results.Select(r => r.Title))}）"
            : $"[FAIL] 结果集合丢了前一份：{results.Count} 份，" +
              $"[{string.Join("、", results.Select(r => r.Kind?.ToString() ?? "—"))}]");
        if (!hasRelative || !hasModel) Environment.ExitCode = 1;

        Console.WriteLine(win.ResultWindowIsOpen
            ? "[PASS] 解完自动打开结果窗口"
            : "[FAIL] 结果窗口没打开，用户看不到结果");
        if (!win.ResultWindowIsOpen) Environment.ExitCode = 1;

        // 窗口按模块划作用域：最后解的是绝对定向，窗口就该停在绝对定向
        Console.WriteLine(win.ResultWindowScope == ResultKind.Absolute
            ? "[PASS] 结果窗口跟着切到最后解的那一步（绝对定向）"
            : $"[FAIL] 结果窗口停在 {win.ResultWindowScope?.ToString() ?? "（未打开）"}");
        if (win.ResultWindowScope != ResultKind.Absolute) Environment.ExitCode = 1;

        // 断言完了切回模型交会，并把底部页签停到「点表」：截图要展示模型坐标
        if (win.SelectModuleForTooling("模型交会"))
        {
            win.UpdateModelPanel();
            if (FindRadio(win, "点表") is { } tab) tab.IsChecked = true;
            Pump(win);
        }

        CheckProcessInfo(win, "模型交会");
    }


    private static void RunM3(MainWindow win, ImageItem image, string gcpFile)    {
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

        // 结果窗口：外方位六个元素 + 每个控制点一行残差
        var panel = win.SnapshotResult();
        var names = panel.Items.Select(r => r.Name).ToList();
        var shownResult = panel.Title.Contains("后方交会") && names.Contains("摄影中心 X") &&
                          names.Contains("姿态角 κ") && panel.Rows.Count == gcps.Count;
        Console.WriteLine(shownResult
            ? $"[PASS] 结果窗口：{panel.Items.Count} 行元素、{panel.Rows.Count} 行残差"
            : $"[FAIL] 结果窗口不对：title=[{panel.Title}] 元素=[{string.Join("/", names)}] " +
              $"残差={panel.Rows.Count}（应有 {gcps.Count}）");

        var xsRow = panel.Items.FirstOrDefault(r => r.Name == "摄影中心 X");
        var expectXs = solved.Xs.ToString("0.000", CultureInfo.InvariantCulture);
        Console.WriteLine(xsRow?.Value == expectXs
            ? $"[PASS] 摄影中心 X 显示 {xsRow!.Value} m（与解算结果一致）"
            : $"[FAIL] 摄影中心 X 显示 [{xsRow?.Value}]，解算结果是 {expectXs}");

        CheckProcessInfo(win, "后方交会");

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

    /// <summary>
    /// 打印托管 → 原生这一侧的 ABI 清单：每个带 <c>StructLayout</c> 的结构体的字段顺序、类型、
    /// 字节偏移，以及全部 <c>DllImport</c> 签名。
    ///
    /// <para>用途是重构时的对表。封送布局错一个字段（类型、顺序、SizeConst）<b>不会编译报错</b>，
    /// 但解算会静默算错。改 ABI 前后各跑一次，把输出 diff 比看代码可靠。</para>
    /// </summary>
    /// <summary>
    /// 前方交会的端到端自检：造一对旁向重叠约 30% 的像对，把一个已知地面点投影到两片，
    /// 再用交会把它解回来。数值与闸门各有覆盖——闸门那几条（交会角太小、缺外方位、
    /// 点不在框幅里）比数值更容易写错，而且错了界面会给出误导人的提示。
    /// </summary>
    private static void IntersectCheck()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        Console.WriteLine("前方交会自检");
        Console.WriteLine(new string('=', 78));

        const double f = 152.72;
        const double pixelMm = 0.04503;
        const int width = 5616;
        const int height = 3744;
        const double h = 3000.0;

        var io = new InteriorOrientation { FocalLength = f, PixelSizeMm = pixelMm };

        // 旁向重叠 30%：重叠在<b>短边</b>（3744 像素）方向，基线 = 短边幅面的 70%
        var swathM = height * pixelMm / f * h;
        var baseM = 0.70 * swathM;

        ImageItem Cam(string id, double ys, double phi, double omega, double kappa) => new()
        {
            Id = id,
            Width = width,
            Height = height,
            Interior = io,
            Exterior = new ExteriorOrientation
            {
                Xs = 1000, Ys = ys, Zs = h, Phi = phi, Omega = omega, Kappa = kappa
            }
        };

        var left = Cam("L", 2000, -0.5, 0.3, 2.0);
        var right = Cam("R", 2000 + baseM, -0.45, 0.25, 2.4);

        // 交会点取在重叠区正中、比摄影中心低 2500 m
        var truth = new GroundPointM(1000, 2000 + baseM / 2, h - 2500);

        var lmm = NativeAlgorithms.ProjectPoint(f, left.Exterior!, truth);
        var rmm = NativeAlgorithms.ProjectPoint(f, right.Exterior!, truth);
        if (lmm is not { } l || rmm is not { } r)
        {
            Console.WriteLine("FAIL  投影失败，后面全部跳过");
            Console.WriteLine("\nSELFTEST FAIL");
            Environment.ExitCode = 1;
            return;
        }

        var halfW = 0.5 * width * pixelMm;
        var halfH = 0.5 * height * pixelMm;
        Check("像点落在框幅内",
            Math.Abs(l.X) < halfW && Math.Abs(l.Y) < halfH &&
            Math.Abs(r.X) < halfW && Math.Abs(r.Y) < halfH,
            $"左 ({l.X:+0.00;-0.00}, {l.Y:+0.00;-0.00}) mm　右 ({r.X:+0.00;-0.00}, {r.Y:+0.00;-0.00}) mm");

        // ---- 精确像点：验交会本身，不掺整像素量化
        var exact = NativeAlgorithms.IntersectRays(left.Exterior!, right.Exterior!, l, r, f, 0.02);
        var exactErr = Distance(exact.Point, truth);
        Check("精确像点 → 交会点回到真值", exact.Success && exactErr < 1e-6,
            $"误差 {exactErr:E3} m");
        Check("交会角在合理区间", exact.ConvergenceAngleDeg is > 45 and < 55,
            $"{exact.ConvergenceAngleDeg:0.00}°（旁向基线 {baseM:0} m、航高 {h:0} m）");
        Check("基线长度", Math.Abs(exact.BaseLength - baseM) < 0.5,
            $"{exact.BaseLength:0.000} m");
        Check("残余上下视差与交会差", Math.Abs(exact.VerticalParallaxMm) < 1e-9 &&
                                   Math.Abs(exact.MissDistance) < 1e-6,
            $"视差 {exact.VerticalParallaxMm:E2} mm　交会差 {exact.MissDistance:E2} m");
        // 投影系数配的是未归一化的方向向量 (x, y, -f)：N·|d| 就是摄影中心到交会点的距离。
        // 顺势验一下方向向量的定义没被改过——两片的 |d| 本来就不相等（x/y 不同），
        // 所以 N₁/N₂ 并不等于 Range₁/Range₂。
        var lenL = Math.Sqrt(l.X * l.X + l.Y * l.Y + f * f);
        var lenR = Math.Sqrt(r.X * r.X + r.Y * r.Y + f * f);
        Check("投影系数与距离自洽",
            Math.Abs(exact.NLeft * lenL - exact.RangeLeft) < 1e-6 &&
            Math.Abs(exact.NRight * lenR - exact.RangeRight) < 1e-6,
            $"N·|d| 左 {exact.NLeft * lenL:0.0000}、右 {exact.NRight * lenR:0.0000} m　" +
            $"（N 左 {exact.NLeft:0.0000}　N 右 {exact.NRight:0.0000}）");

        // ---- 走流程层：像素坐标进，地面坐标出（含整像素量化）
        var lp = CoordinateConverter.ImagePlaneToPixel(l, io, width, height);
        var rp = CoordinateConverter.ImagePlaneToPixel(r, io, width, height);
        var run = IntersectionTask.Solve(left, right, lp, rp);
        var pixErr = Distance(run.Point, truth);
        Check("像素点 → 交会点（含整像素量化）", run.Success && pixErr < 1.5,
            $"左 ({lp.I},{lp.J}) 右 ({rp.I},{rp.J})　误差 {pixErr:0.000} m");
        Check("精度指标非零", run.Success && run.Result!.MX > 0 && run.Result.MZ >= run.Result.MX,
            $"mX {run.Result?.MX:0.000}　mY {run.Result?.MY:0.000}　mZ {run.Result?.MZ:0.000} m");
        Check("单点无警告", run.Warnings.Count == 0,
            run.Warnings.Count == 0 ? "干净" : string.Join("；", run.Warnings));

        // ---- 小基线：交会角不足时必须提醒，不能静静给个数
        var close = Cam("R2", 2000 + 40, -0.45, 0.25, 2.4);
        var closeMm = NativeAlgorithms.ProjectPoint(f, close.Exterior!, truth)!.Value;
        var closePixel = CoordinateConverter.ImagePlaneToPixel(closeMm, io, width, height);
        var closeRun = IntersectionTask.Solve(left, close, lp, closePixel);
        Check("小基线触发交会角提醒",
            closeRun.Success && closeRun.Warnings.Any(w => w.Contains("交会角")),
            closeRun.Success
                ? $"{closeRun.Result!.ConvergenceAngleDeg:0.00}°　{closeRun.Warnings.Count} 条提示"
                : closeRun.Message);

        // ---- 闸门
        Check("同一张片被拒", IntersectionTask.Solve(left, left, lp, rp).Failure == IntersectionFailure.SameImage,
            IntersectionTask.Solve(left, left, lp, rp).Failure.ToString());

        var noExterior = Cam("N", 2000 + baseM, 0, 0, 0);
        noExterior.Exterior = null;
        Check("缺绝对外方位被拒",
            IntersectionTask.Solve(left, noExterior, lp, rp).Failure == IntersectionFailure.NoExterior,
            IntersectionTask.Solve(left, noExterior, lp, rp).Message);

        Check("没点右片被拒",
            IntersectionTask.Solve(left, right, lp, null).Failure == IntersectionFailure.RightNotMeasured,
            IntersectionTask.Solve(left, right, lp, null).Message);

        Check("像点出界被拒",
            IntersectionTask.Solve(left, right, new ImagePixel(-5, 10), rp).Failure
                == IntersectionFailure.PixelOutsideImage,
            "I = -5");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static double Distance(GroundPointM a, GroundPointM b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // ---------------------------------------------------------------- 相对定向

    /// <summary>
    /// 相对定向的端到端自检：原生封装 → 流程层 → 工程持久化，一条链走两遍。
    ///
    /// <para>第一遍给<b>精确像点</b>（mm，不经像素量化），验的是算法与 ABI：解回来要落在
    /// 合成时给的真值上，误差是浮点级的。第二遍把同一批点<b>量化成整数像素</b>再走流程层，
    /// 验的是「点表 → 解算 → 写回工程 → 存盘 → 重开」这条路，误差落在半个像元该有的量级。</para>
    /// </summary>
    /// <summary>整幅影像 → 8 位灰度缓冲（不缩放，5120² 约 26 MB/幅）。</summary>
    private static (byte[] Pixels, int Width, int Height) LoadGray(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)),
                                           BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapSource gray = frame.Format == PixelFormats.Gray8
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);

        var width = gray.PixelWidth;
        var height = gray.PixelHeight;
        var pixels = new byte[width * height];
        gray.CopyPixels(pixels, width, 0);
        return (pixels, width, height);
    }

    private static (double Cx, double Cy) Principal(InteriorOrientation io, int width, int height) =>
        (width / 2.0 + io.X0 / io.PixelSizeMm, height / 2.0 - io.Y0 / io.PixelSizeMm);

    /// <summary>
    /// M10-b + M11 自检：<b>全程不给绝对外方位</b>，只靠相对定向的 $(\mathbf b,R)$ 跑
    /// 「自动匹配 → 核线过滤 → 交会」，验四件事：
    /// ①几何能从 `StereoPair.Relative` 拼出来；②核线残差与有绝对外方位时同量级；
    /// ③`AutoIntersectTask` 不再被"缺外方位"挡掉；④交会出的是模型坐标，且深度方向有跨度。
    /// </summary>
    private static void MatchRelativeCheck(string dir)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        Console.WriteLine("匹配接相对定向（M10-b + M11）自检 — 两片都没有绝对外方位");
        Console.WriteLine(new string('=', 78));

        const double f = 152.72;
        const double pixelMm = 0.04503;
        const double x0Mm = -1.3775;

        var leftPath = Path.Combine(dir, "01-156_50mic.jpg");
        var rightPath = Path.Combine(dir, "01-155_50mic.jpg");
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            Console.WriteLine($"FAIL  找不到影像：{leftPath}");
            Environment.ExitCode = 1;
            return;
        }

        var (lp, lw, lh) = LoadGray(leftPath);
        var (rp, rw, rh) = LoadGray(rightPath);
        Console.WriteLine($"左 01-156 {lw}x{lh}　右 01-155 {rw}x{rh}");
        Console.WriteLine();

        var io = new InteriorOrientation { FocalLength = f, PixelSizeMm = pixelMm, X0 = x0Mm };
        var left = new ImageItem { Id = "01-156", Width = lw, Height = lh, Interior = io };
        var right = new ImageItem { Id = "01-155", Width = rw, Height = rh, Interior = io };
        Check("两片都没有绝对外方位", left.Exterior is null && right.Exterior is null, "Exterior = null");

        // ---- 1 不带核线配一遍，拿点对去解相对定向 ----
        var plain = NativeAlgorithms.MatchFeatures(
            lp, lw, lh, lw, rp, rw, rh, rw,
            pixelMm, Principal(io, lw, lh), Principal(io, rw, rh), null);
        Check("不带核线能配上点", plain.Points.Count >= 200,
            $"{plain.Points.Count} 对，每片检出 {plain.Detected}");

        // ---- 2 解相对定向 ----
        var lmm = new List<ImagePointMm>(plain.Points.Count);
        var rmm = new List<ImagePointMm>(plain.Points.Count);
        foreach (var p in plain.Points)
        {
            lmm.Add(CoordinateConverter.PixelToImagePlane(p.LeftI, p.LeftJ, io, lw, lh));
            rmm.Add(CoordinateConverter.PixelToImagePlane(p.RightI, p.RightJ, io, rw, rh));
        }

        var ro = NativeAlgorithms.SolveRelativeOrientation(lmm, rmm, f);
        Check("相对定向解出来了", ro.Success,
            ro.Success
                ? $"{ro.Iterations} 轮　{ro.UsedCount}/{plain.Points.Count} 参与　q_rms {ro.QRmsMm:0.0000} mm"
                : ro.Message);
        if (!ro.Success)
        {
            Environment.ExitCode = 1;
            return;
        }

        var relative = new RelativeOrientation
        {
            ReferenceImageId = left.Id,
            Bx = ro.Bx,
            By = ro.By,
            Bz = ro.Bz,
            Phi2 = ro.PhiDeg,
            Omega2 = ro.OmegaDeg,
            Kappa2 = ro.KappaDeg,
            Gauge = ro.Gauge,
            FocalLength = f,
            Sigma0Mm = ro.Sigma0Mm,
            RmsQ = ro.QRmsMm,
        };

        // ---- 3 几何只能来自相对定向 ----
        var geometry = PairGeometryBuilder.FromRelative(relative, left, right);
        Check("几何能由 StereoPair.Relative 拼出来", geometry is not null,
            geometry?.Source ?? "返回 null");
        if (geometry is null)
        {
            Environment.ExitCode = 1;
            return;
        }

        Check("成果标成模型坐标", geometry.IsModelCoordinates,
            $"IsModelCoordinates = {geometry.IsModelCoordinates}");

        // ---- 4 带核线重配：残差应与"有绝对外方位"时同量级 ----
        var constrained = NativeAlgorithms.MatchFeatures(
            lp, lw, lh, lw, rp, rw, rh, rw,
            pixelMm, Principal(io, lw, lh), Principal(io, rw, rh),
            (geometry.Epipolar.Bx, geometry.Epipolar.By, geometry.Epipolar.Bz,
             geometry.Epipolar.Rotation, f));

        var epi = constrained.Points.Select(p => p.EpipolarMm).OrderBy(v => v).ToList();
        var median = epi.Count == 0 ? 0.0 : epi[epi.Count / 2];
        Check("核线残差与绝对外方位时同量级（中位 < 0.05 mm）", epi.Count > 0 && median < 0.05,
            epi.Count == 0 ? "一个都没配上" : $"{epi.Count} 对，中位 {median:0.0000} mm");
        if (epi.Count == 0)
        {
            Environment.ExitCode = 1;
            return;
        }

        // ---- 5 端到端：不给 Exterior，只给 relative ----
        var run = AutoIntersectTask.Run(left, right, lp, lw, rp, rw, 5000, 0.8, 0.10, relative);
        Check("AutoIntersectTask 只靠相对定向就能跑", run.Success,
            run.Success ? $"{run.Points.Count} 点　{run.GeometrySource}" : run.Message);
        if (!run.Success)
        {
            Environment.ExitCode = 1;
            return;
        }

        Check("结果标成模型坐标", run.IsModelCoordinates, $"IsModelCoordinates = {run.IsModelCoordinates}");

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in run.Points)
        {
            var g = p.Ground.Point;
            minX = Math.Min(minX, g.X); maxX = Math.Max(maxX, g.X);
            minY = Math.Min(minY, g.Y); maxY = Math.Max(maxY, g.Y);
            minZ = Math.Min(minZ, g.Z); maxZ = Math.Max(maxZ, g.Z);
        }

        Console.WriteLine();
        Console.WriteLine($"模型点跨度　X {maxX - minX:0.000}　Y {maxY - minY:0.000}　Z {maxZ - minZ:0.000}" +
                          "（|b| = 1，比例尺未知，要米制须绝对定向）");

        // 深度方向有跨度才说明是立体，不是把点全压在一个平面上。
        Check("模型点有深度跨度（不是一张纸）", maxZ - minZ > 1e-6,
            $"ΔZ = {maxZ - minZ:0.0000}");

        // ---- 6 基准片换成右片：走 FromRelative 里没被测过的那一支 ----
        //
        // 数学上两种摆法必须给出同一个 (b,R)：核线只认「哪张是左片、哪张是右片」，不认谁当基准。
        // 左片为基准：R_l = I、b 就是 (Bx,By,Bz)，于是 R_pair = R_r、b_pair = R_l·b = b。
        // 右片为基准：左片摆到 (b',R')，FromPoses 得 R_pair = I·R'ᵀ、b_pair = R'·(-b')。
        // 代入 b' = R_右(X_左-X_右)、R' = R_左R_右ᵀ，两条各自化简都等于同一组 (R_r, b)——
        // 所以这一支写错（漏转置、漏负号）不会「差一点」，会差整个几何。
        var roSwapped = NativeAlgorithms.SolveRelativeOrientation(rmm, lmm, f);
        Check("对调左右后仍能解出相对定向", roSwapped.Success,
            roSwapped.Success
                ? $"{roSwapped.Iterations} 轮　{roSwapped.UsedCount}/{plain.Points.Count} 参与　q_rms {roSwapped.QRmsMm:0.0000} mm"
                : roSwapped.Message);

        PairGeometry? geometryRightBase = null;
        if (roSwapped.Success)
        {
            var relativeRightBase = new RelativeOrientation
            {
                ReferenceImageId = right.Id,      // ← 明确指定基准片是右片，逼 FromRelative 走另一支
                Bx = roSwapped.Bx,
                By = roSwapped.By,
                Bz = roSwapped.Bz,
                Phi2 = roSwapped.PhiDeg,
                Omega2 = roSwapped.OmegaDeg,
                Kappa2 = roSwapped.KappaDeg,
                Gauge = roSwapped.Gauge,
                FocalLength = f,
                Sigma0Mm = roSwapped.Sigma0Mm,
                RmsQ = roSwapped.QRmsMm,
            };

            geometryRightBase = PairGeometryBuilder.FromRelative(relativeRightBase, left, right);
        }

        // 下面五项无论前面成没成都照样记一次：中途失败不能让项数悄悄变少，
        // 否则"全 PASS"可能只是剩下的项目少了几条。
        Check("基准片是右片时也能拼出几何", geometryRightBase is not null,
            geometryRightBase?.Source ?? "返回 null（相对定向没解出来）");

        Check("来源里写明了「右片为基准」",
            geometryRightBase is not null && geometryRightBase.Source.Contains("右片为基准"),
            geometryRightBase?.Source ?? "没有几何");

        // (b,R) 与左片为基准时一致：这是这一支对不对的判据，不是"跑通了"就算。
        var db = double.NaN;
        var dMax = double.NaN;
        if (geometryRightBase is not null)
        {
            db = Math.Max(
                Math.Abs(geometryRightBase.Epipolar.Bx - geometry.Epipolar.Bx),
                Math.Max(Math.Abs(geometryRightBase.Epipolar.By - geometry.Epipolar.By),
                         Math.Abs(geometryRightBase.Epipolar.Bz - geometry.Epipolar.Bz)));

            dMax = 0.0;
            for (var i = 0; i < 9; i++)
            {
                dMax = Math.Max(dMax, Math.Abs(geometryRightBase.Epipolar.Rotation[i] - geometry.Epipolar.Rotation[i]));
            }
        }

        Check("两种基准摆出的 (b,R) 是同一组（Δb、ΔR < 1e-4）",
            geometryRightBase is not null && db < 1e-4 && dMax < 1e-4,
            geometryRightBase is null
                ? "没有几何"
                : $"Δb = {db:0.0e+0}　ΔR = {dMax:0.0e+0}（两次独立解算，差的是拟合噪声，不是几何）");

        // 拿右片为基准的几何再去带核线重配：残差应与前者同量级——
        // 上面比的是两次解算的差，这里比的是它跟真实像点的一致性，两件事都要过。
        var epiRbCount = 0;
        var medianRb = 0.0;
        if (geometryRightBase is not null)
        {
            var constrainedRightBase = NativeAlgorithms.MatchFeatures(
                lp, lw, lh, lw, rp, rw, rh, rw,
                pixelMm, Principal(io, lw, lh), Principal(io, rw, rh),
                (geometryRightBase.Epipolar.Bx, geometryRightBase.Epipolar.By, geometryRightBase.Epipolar.Bz,
                 geometryRightBase.Epipolar.Rotation, f));

            var epiRb = constrainedRightBase.Points.Select(p => p.EpipolarMm).OrderBy(v => v).ToList();
            epiRbCount = epiRb.Count;
            medianRb = epiRbCount == 0 ? 0.0 : epiRb[epiRbCount / 2];
        }

        Check("右片为基准时的核线残差与左片为基准时同量级（中位 < 0.05 mm）",
            epiRbCount > 0 && medianRb < 0.05,
            epiRbCount == 0
                ? "一个都没配上"
                : $"{epiRbCount} 对，中位 {medianRb:0.0000} mm（左片为基准时 {median:0.0000} mm）");

        Console.WriteLine();

        Console.WriteLine(failures == 0 ? "MATCH-RELATIVE PASS" : $"MATCH-RELATIVE FAIL（{failures} 项）");
        if (failures != 0) Environment.ExitCode = 1;
    }

    /// <summary>
    /// 绝对定向端到端自检（M7）：合成像对 → 相对定向 → 控制点交会 → 七参数 → 地面坐标。
    ///
    /// <para>为什么全用合成数据：真实工程里控制点在**同一个像对的两片上**凑不到 3 个
    /// （`01-155` 一个都没刺），而绝对定向恰恰要求每点两片都有像点。合成数据能把
    /// 「已知真值 → 反解 → 比真值」这条路走完整，真数据等补刺点后再验。</para>
    ///
    /// <para>容差按刺点精度定：像元 0.04503 mm、航高 2000 m，地面采样间隔约 0.59 m/像元，
    /// 所以「回到地面」的偏差在 1 m 量级是正常的，不是算法误差。</para>
    /// </summary>
    private static void AbsoluteCheck()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        const double f = 152.72;
        const double pixelMm = 0.04503;
        const int width = 5120;
        const int height = 5120;

        var io = new InteriorOrientation { FocalLength = f, PixelSizeMm = pixelMm };

        // 两片：航高 2000 m，基线约 1000 m。模型坐标以基线长为单位，所以 λ 应等于基线长度。
        var leftPose = new ExteriorOrientation { Xs = 0, Ys = 0, Zs = 2000, Phi = 0.6, Omega = -0.4, Kappa = 1.2 };
        var rightPose = new ExteriorOrientation { Xs = 1000, Ys = 30, Zs = 1980, Phi = 0.5, Omega = -0.35, Kappa = 1.6 };
        var baseline = Math.Sqrt(1000.0 * 1000.0 + 30.0 * 30.0 + 20.0 * 20.0);

        var left = new ImageItem { Id = "SIM-L", Width = width, Height = height, Interior = io };
        var right = new ImageItem { Id = "SIM-R", Width = width, Height = height, Interior = io };

        var project = new Project();
        project.Images.Add(left);
        project.Images.Add(right);

        Console.WriteLine("绝对定向自检（M7 端到端：相对定向 → 控制点交会 → 七参数）");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"合成像对　航高 2000 m　基线 {baseline:0.0} m　地面采样间隔约 {2000.0 / f * pixelMm:0.000} m/像元");
        Console.WriteLine();

        // ---- 1 同名点（只给相对定向用，不参与绝对定向）----
        var tieLeft = new List<ImagePointMm>();
        var tieRight = new List<ImagePointMm>();
        for (var k = 0; k < 12; k++)
        {
            var tie = new GroundPointM(120.0 + 55.0 * (k % 4), 120.0 + 60.0 * (k / 4), 18.0 * (k % 3));
            var pl = NativeAlgorithms.ProjectPoint(f, leftPose, tie);
            var pr = NativeAlgorithms.ProjectPoint(f, rightPose, tie);
            if (pl is null || pr is null)
            {
                Check("同名点全部落在两片像幅内", false, $"第 {k} 个点投影失败");
                Environment.ExitCode = 1;
                return;
            }
            tieLeft.Add(pl.Value);
            tieRight.Add(pr.Value);
        }

        var ro = NativeAlgorithms.SolveRelativeOrientation(tieLeft, tieRight, f);
        Check("合成像对能解出相对定向", ro.Success,
            ro.Success
                ? $"{ro.Iterations} 轮　{ro.UsedCount}/{tieLeft.Count} 参与　q_rms {ro.QRmsMm:0.0000} mm"
                : ro.Message);

        if (!ro.Success)
        {
            Environment.ExitCode = 1;
            return;
        }

        var relative = new RelativeOrientation
        {
            ReferenceImageId = left.Id,
            Bx = ro.Bx,
            By = ro.By,
            Bz = ro.Bz,
            Phi2 = ro.PhiDeg,
            Omega2 = ro.OmegaDeg,
            Kappa2 = ro.KappaDeg,
            Gauge = ro.Gauge,
            FocalLength = f,
            Sigma0Mm = ro.Sigma0Mm,
            RmsQ = ro.QRmsMm,
        };

        var pair = new StereoPair
        {
            Id = "SIM-P1",
            LeftImageId = left.Id,
            RightImageId = right.Id,
            Relative = relative,
        };
        project.Pairs.Add(pair);

        // ---- 2 控制点：8 个，其中 2 个是检查点 ----
        var controlGround = new[]
        {
            new GroundPointM(100, 100, 5),
            new GroundPointM(500, 120, 12),
            new GroundPointM(120, 520, 30),
            new GroundPointM(480, 500, 8),
            new GroundPointM(300, 300, 45),
            new GroundPointM(200, 420, 70),
            new GroundPointM(420, 200, 20),
            new GroundPointM(350, 350, 95),
        };
        var checkIds = new[] { "C7", "C8" };

        for (var k = 0; k < controlGround.Length; k++)
        {
            var id = $"C{k + 1}";
            var g = controlGround[k];
            project.Gcps.Add(new Gcp
            {
                Id = id,
                X = g.X,
                Y = g.Y,
                Z = g.Z,
                Type = checkIds.Contains(id) ? GcpType.CheckPoint : GcpType.Planimetric,
            });

            Bind(left, leftPose, id, g);
            Bind(right, rightPose, id, g);
        }

        // ---- 3 真值已知的情况下解七参数 ----
        var prep = AbsoluteOrientationTask.Prepare(project, pair);
        Check("准备阶段凑齐观测", prep.Ready,
            prep.Ready
                ? $"{prep.Count} 个控制点（含 2 个检查点），像对 {prep.PairLabel}"
                : $"{prep.Failure}：{prep.Message}");
        Check("模型坐标被标成自由比例尺", prep.IsModelCoordinates, $"IsModelCoordinates = {prep.IsModelCoordinates}");

        var run = AbsoluteOrientationTask.Run(project, pair);
        Check("只靠相对定向就能解出七参数", run.Success,
            run.Success ? $"λ = {run.Result!.Scale:0.000000}" : $"{run.Failure}：{run.Message}");

        if (!run.Success)
        {
            Environment.ExitCode = 1;
            return;
        }

        Check("尺度 = 基线长度（模型以基线长为单位）",
            Math.Abs(run.Result!.Scale - baseline) < 2.0,
            $"λ = {run.Result.Scale:0.0000}　真值 {baseline:0.0000}（差 {run.Result.Scale - baseline:+0.0000;-0.0000}）");

        Check("控制点与检查点分开", run.Used.Count == 6 && run.Checks.Count == 2,
            $"参与 {run.Used.Count}，检查 {run.Checks.Count}");

        var worst = 0.0;
        foreach (var o in run.Used)
        {
            var t = run.Result.Transform(o.Model);
            var d = Math.Sqrt(Math.Pow(t.X - o.Ground.X, 2) + Math.Pow(t.Y - o.Ground.Y, 2) +
                              Math.Pow(t.Z - o.Ground.Z, 2));
            if (d > worst) worst = d;
        }

        Check("控制点算回地面坐标（像元量化约 0.3 m）", worst < 1.5,
            $"最大偏差 {worst:0.000} m　rms {run.Result.Rms:0.000} m　σ0 {run.Result.Sigma0:0.000} m");

        var worstCheck = run.CheckHeightMax;
        var worstPlan = run.CheckPlanimetricMax;
        Check("检查点不参与解算，但预测得对",
            !double.IsNaN(worstCheck) && worstPlan < 1.5 && worstCheck < 1.5,
            $"平面 {worstPlan:0.000} m　高程 {worstCheck:0.000} m");

        Check("多余观测数 = 3n − 7", run.Result.Redundancy == 3 * 6 - 7,
            $"r = {run.Result.Redundancy}");

        // ---- 3a 平面 / 高程拆开 ----
        // 三维合量的平方必须等于两个分量平方之和（逐点 |v|² = Δx²+Δy²+Δz²），
        // 这条恒等式一验，就知道拆分不是另算了一套残差。
        var split = run.RmsXy * run.RmsXy + run.RmsZ * run.RmsZ;
        var whole = run.Result.Rms * run.Result.Rms;
        Check("平面 + 高程的平方和等于三维合量",
            Math.Abs(split - whole) < 1e-9 * Math.Max(1.0, whole),
            $"Δxy {run.RmsXy:0.0000} m　Δz {run.RmsZ:0.0000} m　合量² 差 {split - whole:+0.0E+0;-0.0E+0}");

        // 本布景：航高 2000 m、基线 1000 m ⇒ H/B ≈ 2，所以深度放大倍数该是 2 左右。
        // 注意这里 Δz/Δxy 并不等于 H/B：合成像对的射线无噪声，误差只来自像元量化，
        // 平面与高程被量化摊平了。真实刺点数据的分量关系才由交会角决定。
        Check("深度放大倍数 = H/B ≈ 2，且高程不比平面好",
            run.RmsZ > run.RmsXy && Math.Abs(run.DepthAmplification - 2.0) < 0.35,
            $"H/B = {run.DepthAmplification:0.00}　Δz {run.RmsZ:0.0000} / Δxy {run.RmsXy:0.0000} m");

        // GSD = H·δ/f；这里的 H 是 λ·|Z_model|（模型以基线长为单位）
        Check("地面采样间隔算得对（0.59 m/px）",
            Math.Abs(run.GroundSampleDistance - 0.590) < 0.02,
            $"{run.GroundSampleDistance:0.0000} m/px");

        var sigmaPx = run.Result.Sigma0 / run.GroundSampleDistance;
        Check("σ₀ 折算成像元后远小于 10 px 的判据", sigmaPx < 10.0 && sigmaPx > 0.05,
            $"{sigmaPx:0.00} px（{run.Result.Sigma0:0.000} m）");

        // ---- 3b 地面文件写成左手系（X/Y 互换）----
        // 判据不是"再解一次比残差"，而是模型交会里直接调的后方交会那个三点叉乘。
        // 所以这项要验两件事：判得出来（NorthEast），并且换回轴后解出的是同一个七参数。
        var cleanScale = run.Result.Scale;
        var cleanRotation = run.Result.Rotation.ToArray();
        foreach (var gcp in project.Gcps) (gcp.X, gcp.Y) = (gcp.Y, gcp.X);

        var swappedPrep = AbsoluteOrientationTask.Prepare(project, pair);
        var swappedEvidence = swappedPrep.Evidence;
        Check("地面 X/Y 写反时判出左手系",
            swappedEvidence is { Decided: true } se && se.Order == GroundAxisOrder.NorthEast,
            swappedEvidence is { } e2
                ? $"{e2.Order}　三点 {e2.Triple}　sin像 {e2.SinImage:+0.000;-0.000} / " +
                  $"sin地 {e2.SinGround:+0.000;-0.000}　一致 {e2.Agreeing}/{e2.Total}"
                : "判据没给出证据");

        var swappedRun = AbsoluteOrientationTask.Run(project, pair);
        var rotationDelta = 0.0;
        if (swappedRun.Success)
        {
            var r2 = swappedRun.Result!.Rotation;
            for (var k = 0; k < cleanRotation.Length && k < r2.Count; k++)
            {
                rotationDelta = Math.Max(rotationDelta, Math.Abs(cleanRotation[k] - r2[k]));
            }
        }
        Check("换回东-北后解出同一个七参数（不是重解碰运气）",
            swappedRun.Success && swappedRun.GroundOrder == GroundAxisOrder.NorthEast &&
            Math.Abs(swappedRun.Result!.Scale - cleanScale) < 1e-9 && rotationDelta < 1e-9,
            swappedRun.Success
                ? $"λ {cleanScale:0.000000} → {swappedRun.Result!.Scale:0.000000}　" +
                  $"R 最大差 {rotationDelta:0.0E+0}"
                : $"{swappedRun.Failure}：{swappedRun.Message}");

        foreach (var gcp in project.Gcps) (gcp.X, gcp.Y) = (gcp.Y, gcp.X);

        // ---- 4 刺点带 1 像元误差 ----
        // 注意：整批加同一个偏移（比如都 +1）会被 λ 与平移吸收掉，σ0 几乎不动，
        // 那种"噪声测试"是假的。正负交替才留得下残差。
        var noiseIndex = 0;
        foreach (var gcp in project.Gcps.ToList())
        {
            var p = right.FindGcpPoint(gcp.Id);
            if (p is not null) p.PixelX += (noiseIndex++ % 2 == 0) ? 1 : -1;
        }

        var noisy = AbsoluteOrientationTask.Run(project, pair);
        Check("刺点带 1 像元误差仍能解", noisy.Success,
            noisy.Success ? $"σ0 = {noisy.Result!.Sigma0:0.000} m" : noisy.Message);
        if (noisy.Success)
        {
            // 1 像元 ≈ 0.59 m 地面。交替误差吸收不掉，σ0 必须明显大于干净时的量化残差。
            var clean = run.Result!.Sigma0;
            Check("σ0 随刺点误差上升（没有被整体平移吸收）",
                noisy.Result!.Sigma0 > 1.5 * clean && noisy.Result.Sigma0 < 2.0,
                $"干净 {clean:0.000} m → 加减 1 像元 {noisy.Result.Sigma0:0.000} m");
        }

        // ---- 5 单边刺点：应被跳过，而不是硬算 ----
        var dropped = right.FindGcpPoint("C1");
        if (dropped is not null) right.Points.Remove(dropped);
        var partial = AbsoluteOrientationTask.Prepare(project, pair);
        Check("单边刺点的控制点被明确跳过",
            partial.Skipped.Any(s => s.Contains("只在")),
            partial.Skipped.FirstOrDefault(s => s.Contains("C1")) ?? "没给出说明");

        var partialRun = AbsoluteOrientationTask.Run(project, pair);
        Check("少一个控点仍能解（5 个参与）", partialRun.Success && partialRun.Used.Count == 5,
            partialRun.Success ? $"参与 {partialRun.Used.Count}" : $"{partialRun.Failure}：{partialRun.Message}");

        // ---- 6 没有相对定向就别解 ----
        var noRelative = AbsoluteOrientationTask.Run(project, new StereoPair
        {
            Id = "SIM-P2",
            LeftImageId = left.Id,
            RightImageId = right.Id,
        });
        Check("没有相对定向时明确拒绝", noRelative.Failure == AbsoluteOrientationFailure.NoRelative,
            $"{noRelative.Failure}：{noRelative.Message}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ABSOLUTE PASS" : $"ABSOLUTE FAIL（{failures} 项）");
        if (failures != 0) Environment.ExitCode = 1;

        // 本地函数：把一个地面点投影到某片上，做成"刺点"
        void Bind(ImageItem image, ExteriorOrientation pose, string gcpId, GroundPointM ground)
        {
            var mm = NativeAlgorithms.ProjectPoint(f, pose, ground);
            if (mm is null) return;
            var pixel = CoordinateConverter.ImagePlaneToPixel(mm.Value, io, image.Width, image.Height);
            image.Points.Add(new ImagePoint
            {
                Id = $"{gcpId}@{image.Id}",
                ImageId = image.Id,
                PixelX = pixel.I,
                PixelY = pixel.J,
                BoundGcpId = gcpId,
            });
        }
    }

    /// <summary>
    /// 参数式内定向的<b>微米输入</b>：面板按厂家手册填 6.000 µm，落库必须是 0.006 mm/px，
    /// 并且「输入检查」卡上要同时看到两种单位、不再要求相机检校文件。
    /// </summary>
    private static void CheckInteriorMicrons(MainWindow win)
    {
        Console.WriteLine("\n参数式内定向：微米输入───────────────────────────────────");

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        var view = FindChild<ImageWorkspace>(win)?.VisibleViews().FirstOrDefault(v => v.HasImage);
        if (view?.Image is not { } image)
        {
            Console.WriteLine("[FAIL] 先给 --image 一张影像");
            Environment.ExitCode = 1;
            return;
        }

        win.SelectModuleForTooling("内定向");
        Pump(win);

        // 这正是 UltraCam Xp 手册上那组数：f 100.500 mm、δ 6.000 µm、主点 (0.000, 0.120) mm
        win.ApplyParametricForTooling("100.500", "0.000", "0.120", "6.000");

        var io = image.Interior;
        Check("填 6.000 µm 落库为 0.006 mm/px",
            io is not null && Math.Abs(io.PixelSizeMm - 0.006) < 1e-12,
            io is null ? "没有写入内方位" : $"PixelSizeMm = {io.PixelSizeMm:R}");

        if (io is not null)
        {
            var step = CoordinateConverter.PixelToImagePlane(
                new ImagePixel(image.Width / 2 + 1, image.Height / 2), io, image.Width, image.Height);
            Check("一个像元的像平面步长 = 0.006 mm", Math.Abs(step.X - 0.006) < 1e-12,
                $"x(中心右侧 1 像元) = {step.X:R} mm");
        }

        var text = win.CheckSummaryText;
        Check("「输入检查」不再要求相机检校文件", !text.Contains("未载入相机检校文件"),
            text.Replace("\n", " | "));
        Check("「输入检查」里两种单位都给了",
            text.Contains("µm") && text.Contains("mm/px"), text.Replace("\n", " | "));

        // 参数式也要留一份结果：以前它只是把四个数抄进模型，结果窗口里什么都没有
        var parametric = win.SnapshotResult();
        var recorded = parametric.Kind == ResultKind.Interior &&
                       parametric.Items.Any(item => item.Symbol == "f") &&
                       parametric.Rows.Count == 0;
        Check("参数式内定向也留了一份结果", recorded,
            $"{parametric.Title}：{parametric.Items.Count} 行元素、{parametric.Rows.Count} 行残差");
        Check("结果窗口跟着切到内定向", win.ResultWindowScope == ResultKind.Interior,
            win.ResultWindowScope?.ToString() ?? "（未打开）");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "INTERIOR-UM PASS" : $"INTERIOR-UM FAIL（{failures} 项）");
        if (failures != 0) Environment.ExitCode = 1;
    }

    private static void RelativeCheck()
    {        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        Console.WriteLine("相对定向自检");
        Console.WriteLine(new string('=', 78));

        const double f = 152.72;
        const double pixelMm = 0.04503;
        const int width = 5616;
        const int height = 3744;
        const double h = 3000.0;

        var io = new InteriorOrientation { FocalLength = f, PixelSizeMm = pixelMm };

        // 旁向像对：重叠 30% 在短边方向，所以基线是短边幅面的 70%。
        // 幅面要按**模型点的深度**算，不是航高——点比基准面近，幅面会同比缩小。
        const double depth = 2200.0;
        var halfSwath = height / 2.0 * pixelMm / f * depth;
        var baseM = 0.70 * 2.0 * halfSwath;

        ExteriorOrientation Pose(double ys, double phi, double omega, double kappa) => new()
        {
            Xs = 1000, Ys = ys, Zs = h, Phi = phi, Omega = omega, Kappa = kappa
        };

        var leftEo = Pose(2000, -0.5, 0.3, 2.0);
        var rightEo = Pose(2000 + baseM, -0.45, 0.25, 2.4);

        // 真值：基线在辅助系里的方向 + 右片相对左片的旋转。
        // 输出的 rotation 是「辅助系方向 → 右片像空间」，所以它等于 R_右 · R_左ᵀ，
        // 不是反过来——写反了只会差一个转置，看起来仍然「像个旋转阵」。
        var baselineGround = new[]
        {
            rightEo.Xs - leftEo.Xs, rightEo.Ys - leftEo.Ys, rightEo.Zs - leftEo.Zs
        };
        var rLeft = RotFromAngles(Rad(leftEo.Phi), Rad(leftEo.Omega), Rad(leftEo.Kappa));
        var rRight = RotFromAngles(Rad(rightEo.Phi), Rad(rightEo.Omega), Rad(rightEo.Kappa));

        var bTrue = MatVec(rLeft, baselineGround);
        var bLength = Math.Sqrt(bTrue[0] * bTrue[0] + bTrue[1] * bTrue[1] + bTrue[2] * bTrue[2]);
        for (var k = 0; k < 3; k++) bTrue[k] /= bLength;

        var rTrue = MatMul(rRight, MatT(rLeft));

        const int nPoints = 12;      // 4 列 × 3 行

        var overlapLowY = rightEo.Ys - halfSwath;
        var overlapHighY = leftEo.Ys + halfSwath;
        var lmm = new List<ImagePointMm>();
        var rmm = new List<ImagePointMm>();
        var ground = new List<GroundPointM>();

        // 12 个模型点，摆成 4×3 的格子而不是一条线。
        // 沿一条斜线排的时候，5 个元素几乎都能被这一列点「解释」，条件数 5.8e5，
        // 单个点的杠杆接近 0.94——2 mm 的粗差最后只留下 0.12 mm 的残差，谁也看不出来。
        // 那不是判据不灵，是点位本身给不出约束。
        for (var k = 0; k < nPoints; k++)
        {
            var ix = k % 4;
            var iy = k / 4;
            var x = 1000.0 + (ix - 1.5) * 800.0;
            var y = overlapLowY + (0.18 + 0.32 * iy) * (overlapHighY - overlapLowY);
            var z = h - depth + 120.0 * Math.Sin(k * 1.7);

            var p = new GroundPointM(x, y, z);
            var pl = NativeAlgorithms.ProjectPoint(f, leftEo, p);
            var pr = NativeAlgorithms.ProjectPoint(f, rightEo, p);
            if (pl is not { } a || pr is not { } b)
            {
                Console.WriteLine($"FAIL  第 {k + 1} 个模型点投影失败，后面全部跳过");
                Console.WriteLine("\nSELFTEST FAIL");
                Environment.ExitCode = 1;
                return;
            }

            ground.Add(p);
            lmm.Add(a);
            rmm.Add(b);
        }

        var halfW = 0.5 * width * pixelMm;
        var halfH = 0.5 * height * pixelMm;

        // 出界要先说清是哪一个、差多少；否则只报一句 FAIL，看不出是布置错了还是算法错了
        var outside = new List<string>();
        for (var k = 0; k < lmm.Count; k++)
        {
            if (Math.Abs(lmm[k].X) >= halfW || Math.Abs(lmm[k].Y) >= halfH)
                outside.Add($"左{k + 1}({lmm[k].X:0.0},{lmm[k].Y:0.0})");
            if (Math.Abs(rmm[k].X) >= halfW || Math.Abs(rmm[k].Y) >= halfH)
                outside.Add($"右{k + 1}({rmm[k].X:0.0},{rmm[k].Y:0.0})");
        }

        Check($"{nPoints} 个模型点都落在两片框幅内", outside.Count == 0,
            outside.Count == 0
                ? $"基线 {baseM:0} m　重叠带 Y ∈ [{overlapLowY:0}, {overlapHighY:0}]"
                : $"{string.Join("、", outside)}　框幅 ±{halfW:0.0} × ±{halfH:0.0} mm");

        // ---- 1 精确像点：验算法与 ABI
        var exact = NativeAlgorithms.SolveRelativeOrientation(lmm, rmm, f, 0.02);

        var bErr = Math.Max(Math.Abs(exact.Bx - bTrue[0]),
            Math.Max(Math.Abs(exact.By - bTrue[1]), Math.Abs(exact.Bz - bTrue[2])));
        Check("精确像点 → 基线方向回到真值", exact.Success && bErr < 1e-9,
            $"b = ({exact.Bx:+0.0000000;-0.0000000}, {exact.By:+0.0000000;-0.0000000}, " +
            $"{exact.Bz:+0.0000000;-0.0000000})　误差 {bErr:E2}");

        var rSolved = RotFromAngles(exact.PhiRad, exact.OmegaRad, exact.KappaRad);
        var rErr = MatMaxDiff(rSolved, rTrue);
        Check("精确像点 → 三个角复原相对旋转", exact.Success && rErr < 1e-9,
            $"最大元素差 {rErr:E2}");

        Check("上下视差归零", exact.Success && exact.QRmsMm < 1e-9,
            $"q_rms {exact.QRmsMm:E2} mm　σ₀ {exact.Sigma0Mm:E2} mm");
        Check("迭代 2~10 轮", exact.Iterations is >= 2 and <= 10, $"{exact.Iterations} 轮");
        Check($"{nPoints} 对点全部参与", exact.UsedCount == nPoints && exact.RejectedCount == 0,
            $"参与 {exact.UsedCount}　剔除 {exact.RejectedCount}　余 {exact.Redundancy}");
        Check("旁向像对冻结 b_y", exact.Gauge == 1, $"gauge = {exact.Gauge}");
        Check("条件数合理", exact.Condition is > 0 and < 1e6, $"{exact.Condition:0.###E+0}");

        // 模型交会：把相对定向结果当外方位喂给前方交会，模型点应当落在一条光线上
        var model = NativeAlgorithms.IntersectRays(
            new ExteriorOrientation(),
            new ExteriorOrientation
            {
                Xs = exact.Bx, Ys = exact.By, Zs = exact.Bz,
                Phi = exact.PhiDeg, Omega = exact.OmegaDeg, Kappa = exact.KappaDeg
            },
            lmm[0], rmm[0], f);
        Check("结果可当外方位直接做模型交会", model.Success && model.MissDistance < 1e-6,
            $"交会差 {model.MissDistance:E2}（模型单位）　比例系数 N 左 {model.NLeft:0.0000}");

        // ---- 2 整数像素：验「点表 → 解算 → 写回工程」
        var lpix = lmm.Select(p => CoordinateConverter.ImagePlaneToPixel(p, io, width, height)).ToArray();
        var rpix = rmm.Select(p => CoordinateConverter.ImagePlaneToPixel(p, io, width, height)).ToArray();

        Project MakeProject(int count, bool extraHalfPair = false)
        {
            var project = new Project { Name = "相对定向自检" };
            var li = new ImageItem { Id = "IMG_L", Width = width, Height = height, Interior = io, Exterior = leftEo };
            var ri = new ImageItem { Id = "IMG_R", Width = width, Height = height, Interior = io, Exterior = rightEo };
            project.Images.Add(li);
            project.Images.Add(ri);

            for (var k = 0; k < count; k++)
            {
                var tieId = $"T{k + 1:00}";
                project.TiePoints.Add(new TiePoint { Id = tieId });

                li.Points.Add(new ImagePoint
                {
                    Id = $"P{k + 1:00}",
                    ImageId = li.Id,
                    PixelX = lpix[k].I,
                    PixelY = lpix[k].J,
                    BoundTiePointId = tieId,
                    PositionMm = CoordinateConverter.PixelToImagePlane(lpix[k], io, width, height),
                });
                ri.Points.Add(new ImagePoint
                {
                    Id = $"P{k + 1:00}",
                    ImageId = ri.Id,
                    PixelX = rpix[k].I,
                    PixelY = rpix[k].J,
                    BoundTiePointId = tieId,
                    PositionMm = CoordinateConverter.PixelToImagePlane(rpix[k], io, width, height),
                });
            }

            if (extraHalfPair)
            {
                // 只点了左片的一个点：不该参与解算，但要被说出来
                project.TiePoints.Add(new TiePoint { Id = "T99" });
                li.Points.Add(new ImagePoint
                {
                    Id = "P99", ImageId = li.Id, PixelX = 2000, PixelY = 1500, BoundTiePointId = "T99"
                });
            }

            return project;
        }

        var project1 = MakeProject(nPoints, extraHalfPair: true);
        var run = RelativeOrientationTask.Run(project1, project1.Images[0], project1.Images[1]);

        Check("流程层解算成功", run.Success, run.Success ? RelativeOrientationTask.Describe(run) : run.Message);
        Check("半对的点被跳过且说清楚",
            run.Skipped.Count == 1 && run.Skipped[0].Contains("T99") && run.Pairs.Count == nPoints,
            run.Skipped.Count == 1 ? run.Skipped[0] : $"跳过 {run.Skipped.Count} 条");

        var quantErr = run.Success ? run.Result!.QRmsMm : double.NaN;
        // 整像素量化：每个坐标最多 0.5 px = 0.0226 mm，视差只取其中一部分
        Check("整数像素 → 视差落在量化量级", run.Success && quantErr < 0.05,
            $"q_rms {quantErr:0.0000} mm（半个像元 {0.5 * pixelMm:0.0000} mm）");

        Check("结果写进像对", run.Success && project1.Pairs.Count == 1 &&
                             project1.Pairs[0].Relative is not null,
            project1.Pairs.Count == 1
                ? $"{project1.Pairs[0].Id} 基准 {project1.Pairs[0].Relative?.ReferenceImageId}　" +
                  $"φ {project1.Pairs[0].Relative?.Phi2:0.0000}°"
                : $"像对 {project1.Pairs.Count} 个");

        var pair1 = project1.Pairs.FirstOrDefault();
        Check("像对里的元素与解算结果一致",
            pair1?.Relative is { } rel &&
            Math.Abs(rel.RmsQ - run.Result!.QRmsMm) < 1e-15 &&
            Math.Abs(rel.Sigma0Mm - run.Result.Sigma0Mm) < 1e-15 &&
            rel.UsedCount == run.Result.UsedCount,
            pair1?.Relative is { } rr ? $"q_rms {rr.RmsQ:0.000000}　σ₀ {rr.Sigma0Mm:0.000000}" : "没有像对");

        Check("逐点 q 写进同名点",
            project1.TiePoints.Count(t => t.Q is not null) == nPoints &&
            project1.TiePoints.All(t => !t.Rejected),
            $"有 q 的点 {project1.TiePoints.Count(t => t.Q is not null)} 个，" +
            $"被剔除 {project1.TiePoints.Count(t => t.Rejected)} 个");

        // ---- 3 存盘再读回：字段名与数值都得原样回来
        var tempPath = Path.Combine(Path.GetTempPath(), "pgm-relative-selftest.pht");
        string json;
        Project reloaded;
        try
        {
            ProjectFile.Save(project1, tempPath);
            json = File.ReadAllText(tempPath);
            reloaded = ProjectFile.Load(tempPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  存盘/读回抛异常：{ex.Message}");
            failures++;
            Console.WriteLine();
            Console.WriteLine($"SELFTEST FAIL ({failures})");
            Environment.ExitCode = 1;
            return;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        Check("存盘带上同名点与相对定向结果",
            json.Contains("\"tiePoints\"", StringComparison.Ordinal) &&
            json.Contains("\"boundTiePointId\"", StringComparison.Ordinal) &&
            json.Contains("\"relative\"", StringComparison.Ordinal) &&
            json.Contains("\"version\": 3", StringComparison.Ordinal),
            $"文件 {json.Length} 字节");

        // 模型点不进工程（Docs/小项与取舍.md X-02）：这个键曾经写出来过，但从来是空数组，
        // 留着会让人以为"模型没存上"。现在连键都不写。
        Check("存盘不写 models 键",
            !json.Contains("\"models\"", StringComparison.Ordinal),
            "模型点是一次解算的中间产物，不落盘");

        // 旧文件里真带着一条模型记录（只可能来自手写）：不能崩，也不能悄悄丢。
        var legacyPath = Path.Combine(Path.GetTempPath(), "pgm-legacy-models.pht");
        var legacyJson = json.TrimEnd();
        legacyJson = legacyJson[..legacyJson.LastIndexOf('}')] +
                     ",\n  \"models\": [ { \"id\": \"M01\", \"gcpIds\": [\"6157\"] } ]\n}";
        File.WriteAllText(legacyPath, legacyJson);
        var legacy = ProjectFile.Load(legacyPath);
        File.Delete(legacyPath);
        Check("旧文件里的非空 models 被忽略，但会说出来",
            legacy.LoadWarnings.Any(w => w.Contains("models", StringComparison.Ordinal)),
            legacy.LoadWarnings.FirstOrDefault(w => w.Contains("models", StringComparison.Ordinal))
            ?? "（没有警告——静默丢数据）");

        Check("读回：同名点与绑定关系",
            reloaded.TiePoints.Count == nPoints + 1 &&
            reloaded.Images.Sum(i => i.Points.Count(p => p.BoundTiePointId is not null)) == 2 * nPoints + 1 &&
            reloaded.LoadWarnings.Count == 0,
            $"同名点 {reloaded.TiePoints.Count} 个　绑定 {reloaded.Images.Sum(i =>
                i.Points.Count(p => p.BoundTiePointId is not null))} 处　" +
            $"警告 {reloaded.LoadWarnings.Count} 条");

        var reloadedRel = reloaded.Pairs.FirstOrDefault()?.Relative;
        Check("读回：相对定向元素逐个相等",
            reloadedRel is not null && pair1?.Relative is { } src &&
            reloadedRel.Bx == src.Bx && reloadedRel.By == src.By && reloadedRel.Bz == src.Bz &&
            reloadedRel.Phi2 == src.Phi2 && reloadedRel.Omega2 == src.Omega2 &&
            reloadedRel.Kappa2 == src.Kappa2 && reloadedRel.RmsQ == src.RmsQ &&
            reloadedRel.Sigma0Mm == src.Sigma0Mm && reloadedRel.Gauge == src.Gauge &&
            reloadedRel.ReferenceImageId == src.ReferenceImageId,
            reloadedRel is null ? "没有相对定向结果" : $"q_rms {reloadedRel.RmsQ:0.000000}　基准 {reloadedRel.ReferenceImageId}");

        Check("读回：逐点 q 与剔除标记",
            reloaded.TiePoints.Where(t => t.Q is not null).Select(t => t.Q!.Value)
                .SequenceEqual(project1.TiePoints.Where(t => t.Q is not null).Select(t => t.Q!.Value)),
            $"{reloaded.TiePoints.Count(t => t.Q is not null)} 个点有 q");

        // 读回之后再解一次，元素应当一模一样（点表是唯一的输入）
        var again = RelativeOrientationTask.Run(reloaded, reloaded.Images[0], reloaded.Images[1]);
        Check("重开工程后再解一次，结果可复现",
            again.Success && again.Result!.QRmsMm == run.Result!.QRmsMm &&
            again.Result.PhiDeg == run.Result.PhiDeg,
            again.Success ? $"q_rms {again.Result!.QRmsMm:0.000000000000000}" : again.Message);

        // ---- 4 粗差
        //
        // 旁向像对（基线在 v 方向）的核面法向 ≈ (f, 0, x)，共面条件量的是右片点在 u-w
        // 面里的偏差——所以粗差要造在**像素 I** 方向。造在 J 方向会几乎不进 q（实测偏
        // 1 mm 只剩 0.014 mm 的残差），剔不出来不是判据的问题，是那个方向不敏感。
        var outlier = MakeProject(nPoints);
        outlier.Images[1].Points[nPoints - 1].PixelX += 44;      // ≈ 2 mm，约 44 个像元
        var outlierRun = RelativeOrientationTask.Run(outlier, outlier.Images[0], outlier.Images[1]);
        Check("2 mm 的粗差点被剔掉",
            outlierRun.Success && outlierRun.Result!.RejectedCount == 1 &&
            outlier.TiePoints.Count(t => t.Rejected) == 1,
            outlierRun.Success
                ? $"剔除 {outlierRun.Result!.RejectedCount}　参与 {outlierRun.Result.UsedCount}　" +
                  $"提示 {outlierRun.Warnings.Count} 条"
                : outlierRun.Message);
        Check("剔除粗差后有提醒",
            outlierRun.Warnings.Any(w => w.Contains("粗差")),
            outlierRun.Warnings.Count > 0 ? outlierRun.Warnings[0] : "没有提醒");

        // ---- 5 闸门
        var p5 = MakeProject(5);
        Check("只有 5 对被拒",
            RelativeOrientationTask.Run(p5, p5.Images[0], p5.Images[1]).Failure
                == RelativeFailure.NotEnoughTiePoints,
            RelativeOrientationTask.Run(p5, p5.Images[0], p5.Images[1]).Message);

        var p9 = MakeProject(nPoints);
        Check("同一张片被拒",
            RelativeOrientationTask.Run(p9, p9.Images[0], p9.Images[0]).Failure
                == RelativeFailure.SameImage,
            RelativeOrientationTask.Run(p9, p9.Images[0], p9.Images[0]).Message);

        var noInterior = MakeProject(nPoints);
        noInterior.Images[0].Interior = null;
        Check("缺内定向被拒",
            RelativeOrientationTask.Run(noInterior, noInterior.Images[0], noInterior.Images[1]).Failure
                == RelativeFailure.NoInterior,
            RelativeOrientationTask.Run(noInterior, noInterior.Images[0], noInterior.Images[1]).Message);

        var otherCamera = MakeProject(nPoints);
        otherCamera.Images[1].Interior = new InteriorOrientation { FocalLength = 153.0, PixelSizeMm = pixelMm };
        Check("焦距不同被拒",
            RelativeOrientationTask.Run(otherCamera, otherCamera.Images[0], otherCamera.Images[1]).Failure
                == RelativeFailure.MismatchedFocalLength,
            RelativeOrientationTask.Run(otherCamera, otherCamera.Images[0], otherCamera.Images[1]).Message);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    /// <summary>教材展开式 R = Rκ·Rω·Rφ（地面 → 像空间），与原生库同一个约定。</summary>
    private static double[] RotFromAngles(double phi, double omega, double kappa)
    {
        var sp = Math.Sin(phi);
        var cp = Math.Cos(phi);
        var sw = Math.Sin(omega);
        var cw = Math.Cos(omega);
        var sk = Math.Sin(kappa);
        var ck = Math.Cos(kappa);

        return new[]
        {
            cp * ck - sp * sw * sk,  cw * sk,  sp * ck + cp * sw * sk,
           -cp * sk - sp * sw * ck,  cw * ck, -sp * sk + cp * sw * ck,
           -sp * cw,                -sw,      cp * cw,
        };
    }

    private static double[] MatVec(double[] m, double[] v) => new[]
    {
        m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
        m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
        m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
    };

    private static double[] MatMul(double[] a, double[] b)
    {
        var o = new double[9];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                o[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            }
        }
        return o;
    }

    private static double[] MatT(double[] a) => new[]
    {
        a[0], a[3], a[6],
        a[1], a[4], a[7],
        a[2], a[5], a[8],
    };

    private static double MatMaxDiff(double[] a, double[] b)
    {
        var worst = 0.0;
        for (var i = 0; i < 9; i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }

    private static void AbiCheck()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var asm = typeof(NativeAlgorithms).Assembly;
        Console.WriteLine("ABI 布局清单");
        Console.WriteLine(new string('=', 78));

        // 注意：StructLayout 不是保留在元数据里的自定义特性，GetCustomAttribute 拿不到；
        // 它落在 TypeAttributes 的布局标志上，要通过 Type.StructLayoutAttribute 读。
        var structs = asm.GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum && !t.IsPrimitive)
            .Where(t => t.StructLayoutAttribute is { Value: LayoutKind.Sequential or LayoutKind.Explicit })
            // 编译器生成的内联数组初始化类型，跟 ABI 无关，留着只会让对比出假差异
            .Where(t => t.DeclaringType?.Name != "<PrivateImplementationDetails>")
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var t in structs)
        {
            Console.WriteLine($"struct {t.Name}  size={SizeOf(t)}  declaring={t.DeclaringType?.Name ?? "-"}");

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var extra = f.GetCustomAttribute<MarshalAsAttribute>() is { } ma
                    ? $"  MarshalAs({ma.Value}" +
                      (ma.Value == UnmanagedType.ByValArray ? $", SizeConst={ma.SizeConst}" : "") + ")"
                    : "";
                Console.WriteLine($"    {f.Name,-16} {f.FieldType.Name,-10} off={OffsetOf(t, f)}{extra}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("DllImport 声明");
        Console.WriteLine(new string('=', 78));

        var imports = asm.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var m in imports)
        {
            var d = m.GetCustomAttribute<DllImportAttribute>()!;
            var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"{d.Value}!{d.EntryPoint ?? m.Name}  conv={d.CallingConvention}  " +
                              $"{m.ReturnType.Name} ({ps})");
        }

        Console.WriteLine();
        Console.WriteLine($"struct {structs.Count} 个　DllImport {imports.Count} 个");
        Console.WriteLine("ABI 清单打印完毕");
    }

    private static string SizeOf(Type t)
    {
        try { return Marshal.SizeOf(t).ToString(CultureInfo.InvariantCulture); }
        catch (ArgumentException) { return "?"; }
    }

    private static string OffsetOf(Type t, FieldInfo f)
    {
        try { return Marshal.OffsetOf(t, f.Name).ToInt64().ToString(CultureInfo.InvariantCulture); }
        catch (ArgumentException) { return "?"; }
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

    /// <summary>
    /// 影像缓存的容量行为（[小项与取舍](../Docs/小项与取舍.md) X-01、ADR-24）。
    ///
    /// <para><b>只断言对外承诺</b>：不超预算、命中不重解、淘汰后还能重载、清空归零。
    /// 不断言"淘汰的一定是最久没用那张"——那是 <c>MemoryCache</c> 的内部策略（分代时钟 + 按比例压缩），
    /// 不是我们的约定，钉死它等于把库的实现细节写进测试。</para>
    /// </summary>
    private static void CacheCheck()
    {
        Console.WriteLine("影像缓存─────────────────────────────────────────────");

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        // 64×64 的 PNG，按 4 字节/像元估算是 16 KB/张；文件多大不影响结论。
        const int side = 64;
        const long one = (long)side * side * 4;

        var dir = Path.Combine(Path.GetTempPath(), "pgm-cache-test");
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        try
        {
            for (var k = 0; k < 4; k++)
            {
                var path = Path.Combine(dir, $"p{k}.png");
                WritePng(path, side, side, k);
                files.Add(path);
            }

            // ---- 1 装得下就不淘汰 ----
            var lib = new ImageLibrary(maxBytes: one * 2 + one / 2);   // 40 KB，正好放两张
            var a = lib.LoadAsync("A", files[0]).GetAwaiter().GetResult();
            lib.LoadAsync("B", files[1]).GetAwaiter().GetResult();
            Check("两张都装得下", lib.Count == 2 && lib.Bytes == 2 * one,
                $"{lib.Count} 张　{lib.Bytes / 1024} KB / 预算 {lib.Limit / 1024} KB");

            // ---- 2 再要一次走命中，不重解码 ----
            var again = lib.LoadAsync("A", files[0]).GetAwaiter().GetResult();
            Check("再要一次是命中，不重复解码",
                ReferenceEquals(again, a) && lib.Count == 2 && lib.Bytes == 2 * one,
                $"同一个位图对象 {ReferenceEquals(again, a)}　{lib.Count} 张");

            // ---- 3 超预算就淘汰，占用不越线 ----
            lib.LoadAsync("C", files[2]).GetAwaiter().GetResult();
            string Alive(ImageLibrary cache) =>
                string.Join(",", new[] { "A", "B", "C", "D" }.Where(id => cache.TryGet(id, out _)));
            Check("超预算时淘汰，占用不越线",
                lib.Bytes <= lib.Limit && lib.Count == 2,
                $"{lib.Count} 张　{lib.Bytes / 1024} KB / 预算 {lib.Limit / 1024} KB　活着：{Alive(lib)}");

            // ---- 4 刚打开的那张必须活着（库自己的压缩顺序恰好相反，这条是那道防线的守门人）----
            // 上面第 2 条又读了一次 A，所以此刻的顺序是「B 最旧 → A 刚用过 → C 最新」，该出局的是 B。
            Check("刚打开的那张不会被自己挤掉",
                lib.TryGet("C", out _) && !lib.TryGet("B", out _),
                $"活着：{Alive(lib)}（最久没用的 B 应该已经出局）");

            // ---- 5 被淘汰的那张还能重新载入（淘汰只该让它变慢，不该让它消失）----
            var reloaded = lib.LoadAsync("B", files[1]).GetAwaiter().GetResult();
            Check("被淘汰的影像还能重载",
                reloaded is { PixelWidth: side, PixelHeight: side },
                $"B 已淘汰，重载 {reloaded.PixelWidth}×{reloaded.PixelHeight}");

            // ---- 6 删一张，占用跟着降 ----
            var before = lib.Bytes;
            lib.Remove("C");
            Check("删掉的影像立刻取不到，占用跟着降",
                !lib.TryGet("C", out _) && lib.Bytes < before,
                $"占用 {before / 1024} → {lib.Bytes / 1024} KB");

            // ---- 7 换工程时清空归零 ----
            lib.Clear();
            Check("清空之后既没有条目也没有占用", lib.Count == 0 && lib.Bytes == 0,
                $"{lib.Count} 张　{lib.Bytes} 字节");

            // ---- 8 预算比一张还小时也不崩 ----
            var tiny = new ImageLibrary(maxBytes: one / 2);
            var p3 = tiny.LoadAsync("D", files[3]).GetAwaiter().GetResult();
            Check("预算比一张还小时，图照样读得出来",
                p3 is { PixelWidth: side } && tiny.Count == 0,
                $"预算 {tiny.Limit / 1024} KB，单张 {one / 1024} KB，缓存里 {tiny.Count} 张（单张超预算就不收）");
        }
        finally
        {
            foreach (var f in files) if (File.Exists(f)) File.Delete(f);
        }

        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 结果集合的覆盖语义（ADR-25）。旧的结果窗口只有一个槽：第一步算完再看第二步，
    /// 第一步的结果就被顶掉了。这里钉住新约定——<b>不同对象并存、同一对象覆盖、换工程清空</b>。
    ///
    /// <para>不开窗口：窗口只是这个集合的视图，视图的事由 <c>--model-demo</c> /
    /// <c>--absolute-demo</c> 那条端到端去验（那里会连着解两步）。</para>
    /// </summary>
    private static void ResultStoreCheck()
    {
        Console.WriteLine("结果集合────────────────────────────────────────────────");

        var failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
            if (!ok) failures++;
        }

        var store = new ResultStore();

        Check("空集合：没有结果、也没有最近一份",
            store.Count == 0 && store.Latest is null && store.All.Count == 0,
            $"{store.Count} 份");

        var resection = new SolveResult
        {
            Kind = ResultKind.Resection,
            TargetId = "IMG-1",
            Title = "后方交会 · IMG-1",
        };
        resection.Add("摄影中心 X", "X_S", "1.000");
        store.Put(resection);

        var intersection = new SolveResult
        {
            Kind = ResultKind.Intersection,
            TargetId = "IMG-1 / IMG-2",
            Title = "前方交会 · IMG-1 / IMG-2",
        };
        store.Put(intersection);

        Check("两份不同结果并存（第二步没有顶掉第一步）",
            store.Count == 2 && ReferenceEquals(store.Latest, intersection) &&
            ReferenceEquals(store.All[0], resection),
            $"{store.Count} 份：{string.Join("、", store.All.Select(r => r.Title))}");

        Check("放入时补上解算时刻", resection.SolvedAt != default,
            resection.SolvedAt.ToString("HH:mm:ss"));

        var redo = new SolveResult
        {
            Kind = ResultKind.Resection,
            TargetId = "IMG-1",
            Title = "后方交会 · IMG-1（重解）",
        };
        store.Put(redo);

        Check("同一对象重解只覆盖、不堆重复",
            store.Count == 2 && ReferenceEquals(store.Get(ResultKind.Resection, "IMG-1"), redo),
            $"{store.Count} 份，重解后取回的是新那份");

        Check("覆盖不改变它在列表里的位置",
            ReferenceEquals(store.All[0], redo),
            $"顺序：{string.Join("、", store.All.Select(r => r.Title))}");

        Check("按模块看：份数、对象列表、最近一份",
            store.CountOf(ResultKind.Intersection) == 1 &&
            store.Of(ResultKind.Resection).Count == 1 &&
            ReferenceEquals(store.LatestOf(ResultKind.Resection), redo),
            $"前方交会 {store.CountOf(ResultKind.Intersection)} 份、后方交会 " +
            $"{store.CountOf(ResultKind.Resection)} 份，最近一份 {store.LatestOf(ResultKind.Resection)?.Title}");

        // 同一模块对多张片解算：两份并存，窗口的「对象」下拉就是列这两个
        var second = new SolveResult
        {
            Kind = ResultKind.Resection,
            TargetId = "IMG-2",
            Title = "后方交会 · IMG-2",
        };
        store.Put(second);
        Check("同一模块的多张片并存（对象下拉有 2 项）",
            store.CountOf(ResultKind.Resection) == 2 &&
            store.Of(ResultKind.Resection).Count == 2 &&
            ReferenceEquals(store.LatestOf(ResultKind.Resection), second),
            $"后方交会 {store.CountOf(ResultKind.Resection)} 份：" +
            $"{string.Join("、", store.Of(ResultKind.Resection).Select(r => r.TargetId))}");

        store.Clear();
        Check("清空归零（换工程时用）",
            store.Count == 0 && store.Latest is null && store.All.Count == 0 && store.Of(ResultKind.Resection).Count == 0,
            $"{store.Count} 份");

        store.Clear();
        Check("空集合上再清空是幂等的",
            store.Count == 0 && store.Latest is null, $"{store.Count} 份");

        Console.WriteLine(failures == 0 ? "SELFTEST PASS" : $"SELFTEST FAIL ({failures})");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static void WritePng(string path, int width, int height, int seed)
    {
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i * 7 + seed * 31) % 251);

        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
  
