using System.Globalization;

namespace Photogrammetry.Core;

/// <summary>
/// 相机检校文件读取。支持两种格式：
/// <list type="bullet">
///   <item>E-FOTO 的 <c>.cmr</c>：第 1 行 <c>x0 y0</c>，第 2 行 <c>f</c>，之后每行 <c>编号 X Y</c>。</item>
///   <item>简单 CSV：首行可有表头，之后每行 <c>编号,X_mm,Y_mm</c>。</item>
/// </list>
/// 分隔符接受逗号、制表符与空格。
/// </summary>
public static class CameraFile
{
    public static Camera Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到相机检校文件。", path);

        var id = Path.GetFileNameWithoutExtension(path);
        var lines = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
            throw new InvalidDataException("相机检校文件是空的。");

        var camera = Path.GetExtension(path).Equals(".cmr", StringComparison.OrdinalIgnoreCase)
            ? ParseCmr(id, lines, path)
            : ParseCsv(id, lines, path);

        if (camera.Fiducials.Count == 0)
            throw new InvalidDataException($"「{Path.GetFileName(path)}」里读不到任何框标坐标。");

        return camera;
    }

    private static Camera ParseCmr(string id, List<string> lines, string path)
    {
        if (lines.Count < 3)
            throw new InvalidDataException(
                $"「{Path.GetFileName(path)}」内容不足：至少要有 x0 y0、f 与一个框标。");

        var camera = new Camera { Id = id };

        var head = Split(lines[0]);
        if (head.Length >= 2)
        {
            camera.X0Mm = Num(head[0]);
            camera.Y0Mm = Num(head[1]);
        }

        camera.FocalLengthMm = Num(lines[1]);

        foreach (var line in lines.Skip(2))
        {
            var cells = Split(line);
            if (cells.Length < 3) continue;
            camera.Fiducials.Add(new Fiducial
            {
                Id = (int)Math.Round(Num(cells[0])),
                Xmm = Num(cells[1]),
                Ymm = Num(cells[2]),
            });
        }

        return camera;
    }

    private static Camera ParseCsv(string id, List<string> lines, string path)
    {
        var camera = new Camera { Id = id };

        foreach (var line in lines)
        {
            var cells = Split(line);
            if (cells.Length < 3) continue;

            // 首行可能是表头，跳过
            if (!double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                continue;

            var fiducial = new Fiducial
            {
                Id = (int)Math.Round(number),
                Xmm = Num(cells[1]),
                Ymm = Num(cells[2]),
            };

            // 表头里的 "X_mm" 之类会解析失败，Num 会抛出；这里先校验
            camera.Fiducials.Add(fiducial);
        }

        return camera;
    }

    private static string[] Split(string line) =>
        line.Split(new[] { ',', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

    private static double Num(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new InvalidDataException($"无法解析数值「{text}」。");
    }
}
