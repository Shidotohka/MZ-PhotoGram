using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Photogrammetry.Core;

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
