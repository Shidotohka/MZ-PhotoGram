using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Photogrammetry.Core;

/// <summary>控制点文件的来源格式。</summary>
public enum ControlPointFileFormat
{
    /// <summary>逗号/制表符分隔的纯文本。</summary>
    Csv,
    /// <summary>Word 的富文本。E-FOTO 的 gxyz.rtf 就是这种。</summary>
    Rtf
}

/// <summary>
/// 控制点导入的结果。失败时 <see cref="Points"/> 为空，原因在 <see cref="Warnings"/> 里。
/// </summary>
public sealed class ControlPointImportResult
{
    public bool Success { get; init; }
    public string SourcePath { get; init; } = "";
    public ControlPointFileFormat SourceFormat { get; init; }
    public IReadOnlyList<Gcp> Points { get; init; } = Array.Empty<Gcp>();

    /// <summary>同名点号、跳过的行、单位判定等。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>放大影像所在的目录，找不到时为 null。</summary>
    public string? ZoomDirectory { get; init; }

    /// <summary>一句话摘要，直接进状态栏与诊断面板。</summary>
    public string Summary { get; init; } = "";
}

/// <summary>
/// 控制点文件的读取。两种来源共用一套行解析：先得到纯文本行，再逐行取「点号 + 三个数」。
///
/// 地面坐标一律按<b>米</b>读入（与 <see cref="GroundPointM"/> 一致）。导入后会把坐标范围
/// 报出来，量级不对时（例如误按毫米写的文件）能在界面上第一眼看见。
/// </summary>
public static class ControlPointFile
{
    /// <summary>最少要几个点才能做后方交会。</summary>
    public const int MinPointsForResection = 4;

    // 行首是点号，后面跟三个数。故意不锚定行尾：RTF 里的超链接会把 "gxyz.rtf"
    // 粘在最后一个数字后面，锚尾会让整行匹配失败。
    private static readonly Regex DataRow = new(
        @"^\s*(?<id>[^\s,;]+)[\s,;]+" +
        @"(?<x>[-+]?\d+(?:\.\d+)?)[\s,;]+" +
        @"(?<y>[-+]?\d+(?:\.\d+)?)[\s,;]+" +
        @"(?<z>[-+]?\d+(?:\.\d+)?)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ZoomExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    // ---------------------------------------------------------------- 入口

    public static ControlPointImportResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail(path ?? "", ControlPointFileFormat.Csv, "没有给出文件路径。");
        if (!File.Exists(path))
            return Fail(path, ControlPointFileFormat.Csv, "找不到文件：" + path);

        var format = DetectFormat(path);
        string text;
        try
        {
            text = format == ControlPointFileFormat.Rtf ? RtfText.Extract(path) : File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return Fail(path, format, "读取失败：" + ex.Message);
        }

        return Parse(text, path, format);
    }

    /// <summary>按扩展名判断格式；认不出时先看内容里有没有 RTF 头。</summary>
    public static ControlPointFileFormat DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".rtf") return ControlPointFileFormat.Rtf;
        if (ext is ".csv" or ".txt" or ".dat") return ControlPointFileFormat.Csv;

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            var n = stream.Read(head);
            if (n >= 5 && Encoding.ASCII.GetString(head[..5]) == "{\\rtf")
                return ControlPointFileFormat.Rtf;
        }
        catch (IOException)
        {
            // 读不了就按 CSV 试，Parse 会给出具体的行级诊断
        }
        return ControlPointFileFormat.Csv;
    }

    /// <summary>解析已经读进内存的文本。抽出来是为了能对字符串做单元测试。</summary>
    public static ControlPointImportResult Parse(string text, string sourcePath,
        ControlPointFileFormat format)
    {
        var points = new List<Gcp>();
        var warnings = new List<string>();
        var byId = new Dictionary<string, Gcp>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var lineNo = 0;
        var skipped = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                skipped++;
                continue;
            }

            var m = DataRow.Match(line);
            if (!m.Success)
            {
                skipped++;
                continue;
            }

            var id = m.Groups["id"].Value.Trim();
            if (!TryNumber(m.Groups["x"].Value, out var x) ||
                !TryNumber(m.Groups["y"].Value, out var y) ||
                !TryNumber(m.Groups["z"].Value, out var z))
            {
                warnings.Add($"第 {lineNo} 行数字解析失败，已跳过：{line.Trim()}");
                skipped++;
                continue;
            }

            if (!seen.Add(id))
            {
                warnings.Add($"点号 {id} 重复出现（第 {lineNo} 行），保留第一次的值。");
                continue;
            }

            var gcp = new Gcp { Id = id, X = x, Y = y, Z = z, Type = ParseType(m.Groups["rest"].Value) };
            points.Add(gcp);
            byId[id] = gcp;
        }

        if (points.Count == 0)
        {
            return Fail(sourcePath, format,
                $"没读到任何控制点。共 {lineNo} 行，其中 {skipped} 行不是「点号 + 三个数」的格式。");
        }

        var zoomDir = FindZoomDirectory(sourcePath, out var zoomCount);
        if (zoomDir is not null && zoomCount > 0)
        {
            var missing = points.Count(p => FindZoomImage(zoomDir, p.Id) is null);
            if (missing > 0)
                warnings.Add($"放大影像目录里有 {points.Count - missing} 个点对得上，{missing} 个没有对应的图片。");
        }

        var xs = points.Select(p => p.X).ToList();
        var ys = points.Select(p => p.Y).ToList();
        var zs = points.Select(p => p.Z).ToList();

        if (points.Count < MinPointsForResection)
            warnings.Add($"只有 {points.Count} 个控制点，后方交会至少要 {MinPointsForResection} 个。");

        var span = Math.Max(xs.Max() - xs.Min(), ys.Max() - ys.Min());
        if (span < 10.0)
            warnings.Add($"控制点范围只有 {span:F1}，可能不是米制坐标，或者点位挤在一起。");

        var summary = $"{points.Count} 个控制点　" +
                      $"X {xs.Min():F3}–{xs.Max():F3}　Y {ys.Min():F3}–{ys.Max():F3}　" +
                      $"Z {zs.Min():F3}–{zs.Max():F3}（按米读入）";

        return new ControlPointImportResult
        {
            Success = true,
            SourcePath = sourcePath,
            SourceFormat = format,
            Points = points,
            Warnings = warnings,
            ZoomDirectory = zoomDir,
            Summary = summary
        };
    }

    private static ControlPointImportResult Fail(string path, ControlPointFileFormat format, string why) =>
        new()
        {
            Success = false,
            SourcePath = path,
            SourceFormat = format,
            Warnings = new[] { why },
            Summary = "控制点导入失败"
        };

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    /// <summary>可选列：控制 / 检查 / 平高 / 高程。认不出就当平高控制点。</summary>
    private static GcpType ParseType(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return GcpType.Planimetric;
        var head = rest.Trim().Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (head.Length == 0) return GcpType.Planimetric;

        return head[0] switch
        {
            "检查" or "检查点" or "check" or "Check" => GcpType.CheckPoint,
            "高程" or "高程点" or "elev" or "Elevation" => GcpType.Elevation,
            _ => GcpType.Planimetric
        };
    }

    // ---------------------------------------------------------------- 放大影像

    /// <summary>
    /// 找放大影像目录。约定：控制点文件同目录下的 <c>pointpos</c> 之类子目录，
    /// 图片名就是点号。找不到返回 null——没有放大片也照样能刺点。
    /// </summary>
    public static string? FindZoomDirectory(string controlPointFile, out int imageCount)
    {
        imageCount = 0;
        var dir = Path.GetDirectoryName(Path.GetFullPath(controlPointFile));
        if (dir is null || !Directory.Exists(dir)) return null;

        var candidates = new List<string>();
        foreach (var sub in Directory.EnumerateDirectories(dir))
            candidates.Add(sub);
        candidates.Insert(0, dir);

        string? best = null;
        foreach (var candidate in candidates)
        {
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(candidate))
                {
                    if (ZoomExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) count++;
                }
            }
            catch (IOException)
            {
                continue;
            }

            if (count > imageCount)
            {
                imageCount = count;
                best = candidate;
            }
        }

        // 根目录里的图片可能是别的用途，只有子目录没有时才退化到根目录
        if (best is not null && string.Equals(best, dir, StringComparison.OrdinalIgnoreCase) && imageCount < 2)
            return null;

        return imageCount > 0 ? best : null;
    }

    /// <summary>按点号找放大影像。扩展名不区分大小写。</summary>
    public static string? FindZoomImage(string? zoomDirectory, string id)
    {
        if (string.IsNullOrWhiteSpace(zoomDirectory) || !Directory.Exists(zoomDirectory)) return null;
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var ext in ZoomExtensions)
        {
            // 先试大写扩展名，这批数据用的是 .JPG，拿回来的路径大小写才对得上
            var upper = Path.Combine(zoomDirectory, id + ext.ToUpperInvariant());
            if (File.Exists(upper)) return upper;

            var lower = Path.Combine(zoomDirectory, id + ext);
            if (File.Exists(lower)) return lower;
        }

        // 有些工程会把点号补零，或用 <影像名>_<点号> 命名
        try
        {
            foreach (var file in Directory.EnumerateFiles(zoomDirectory))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (ZoomExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    return file;
            }
        }
        catch (IOException)
        {
            // 目录读不了就当没有
        }
        return null;
    }
}

/// <summary>
/// RTF 取纯文本。只做「够用」的那一档：剥控制字、处理 \\'hh 转义、\par 转换行、
/// 丢掉 {\*...} 这类未知目的地。不追求完整实现 RTF 规范。
/// </summary>
internal static class RtfText
{
    private static Encoding? _ansi;

    public static string Extract(string path)
    {
        var raw = File.ReadAllBytes(path);
        var ansi = ansiEncoding(raw);
        var sb = new StringBuilder(raw.Length / 4);
        var bytes = new List<byte>();

        void flush()
        {
            if (bytes.Count == 0) return;
            sb.Append(ansi.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        var i = 0;
        var skipGroupDepth = 0;   // >0 表示正处在一个要整体丢弃的组里
        var depth = 0;

        while (i < raw.Length)
        {
            var c = (char)raw[i];

            if (c == '{')
            {
                depth++;
                // {\* 开头的是未知目的地，整组丢掉
                if (skipGroupDepth == 0 && i + 2 < raw.Length && raw[i + 1] == (byte)'\\' &&
                    raw[i + 2] == (byte)'*')
                {
                    skipGroupDepth = depth;
                }
                i++;
                continue;
            }

            if (c == '}')
            {
                if (skipGroupDepth > 0 && depth == skipGroupDepth) skipGroupDepth = 0;
                depth--;
                i++;
                continue;
            }

            if (c != '\\')
            {
                if (skipGroupDepth == 0 && c != '\r' && c != '\n') bytes.Add(raw[i]);
                i++;
                continue;
            }

            // 控制序列
            i++;
            if (i >= raw.Length) break;
            var next = (char)raw[i];

            if (next is '\\' or '{' or '}')
            {
                if (skipGroupDepth == 0) bytes.Add(raw[i]);
                i++;
                continue;
            }

            if (next == '\'')
            {
                if (i + 2 < raw.Length)
                {
                    var hex = Encoding.ASCII.GetString(raw, i + 1, 2);
                    if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        if (skipGroupDepth == 0) bytes.Add(b);
                        i += 3;
                        continue;
                    }
                }
                i++;
                continue;
            }

            if (!char.IsLetter(next))
            {
                // \~ \- \_ 之类：只关心 \par 之外的空控制符，一律丢弃
                i++;
                continue;
            }

            // 关键字：字母 + 可选数字参数 + 可选一个空格
            var start = i;
            while (i < raw.Length && char.IsLetter((char)raw[i])) i++;
            var word = Encoding.ASCII.GetString(raw, start, i - start);

            var paramStart = i;
            if (i < raw.Length && (char)raw[i] == '-') i++;
            while (i < raw.Length && char.IsDigit((char)raw[i])) i++;
            var hasParam = i > paramStart;
            var param = 0;
            if (hasParam)
                int.TryParse(Encoding.ASCII.GetString(raw, paramStart, i - paramStart), out param);

            if (i < raw.Length && raw[i] == (byte)' ') i++;   // 关键字后面的分隔空格

            if (skipGroupDepth != 0) continue;

            switch (word)
            {
                case "par":
                case "line":
                case "row":
                case "sect":
                case "page":
                    flush();
                    sb.Append('\n');
                    break;
                case "tab":
                    bytes.Add((byte)' ');
                    bytes.Add((byte)' ');
                    break;
                case "cell":
                    bytes.Add((byte)' ');
                    break;
                case "u" when hasParam:
                    flush();
                    // \uN 后面跟 \ucN 个代替字符，常见是 1 个 '?' 或 \'hh
                    sb.Append((char)(param < 0 ? param + 65536 : param));
                    SkipFallback(raw, ref i);
                    break;
                default:
                    break;   // 其余控制字只影响排版，丢掉
            }
        }

        flush();
        return sb.ToString();
    }

    private static void SkipFallback(byte[] raw, ref int i)
    {
        // 跳过最多 2 个代替字符（\'hh 算一个，普通 ASCII 也算一个）
        var skipped = 0;
        while (i < raw.Length && skipped < 2)
        {
            if (raw[i] == (byte)'\\' && i + 1 < raw.Length && raw[i + 1] == (byte)'\'')
            {
                i += 4;
                skipped++;
                continue;
            }
            if (char.IsLetterOrDigit((char)raw[i]))
            {
                i++;
                skipped++;
                continue;
            }
            break;
        }
    }

    private static Encoding ansiEncoding(byte[] raw)
    {
        if (_ansi is not null) return _ansi;

        // 从 \ansicpg936 之类的头部取代码页；取不到就用 UTF-8
        var head = Encoding.ASCII.GetString(raw, 0, Math.Min(raw.Length, 512));
        var m = Regex.Match(head, @"\\ansicpg(\d+)");
        var codePage = m.Success && int.TryParse(m.Groups[1].Value, out var cp) ? cp : 0;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _ansi = codePage > 0 ? Encoding.GetEncoding(codePage) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            _ansi = Encoding.UTF8;
        }
        catch (NotSupportedException)
        {
            _ansi = Encoding.UTF8;
        }
        return _ansi;
    }
}
