using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Photogrammetry.Core;

/// <summary>
/// 放大影像（点之记）的定位：先找出放放大片的那一级目录，再按点号取图。
///
/// <para>从 <see cref="ControlPointFile"/> 里搬出来的。它跟「读控制点文件」只是<b>同目录</b>这层
/// 巧合关系：控制点文件没读成功，照样可以直接问「这个点有没有放大片」。混在一起会让
/// 「解析文本」和「找图片」互为前提。</para>
///
/// <para>找不到不算错误——没有放大片也照样能刺点，只是少了参考影像。</para>
/// </summary>
public static class ZoomImageIndex
{
    private static readonly string[] Extensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    /// <summary>
    /// 找放大影像目录。约定：控制点文件同目录下的 <c>pointpos</c> 之类子目录，
    /// 图片名就是点号。找不到返回 null——没有放大片也照样能刺点。
    /// </summary>
    public static string? FindDirectory(string controlPointFile, out int imageCount)
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
                    if (Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) count++;
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
    public static string? FindImage(string? zoomDirectory, string id)
    {
        if (string.IsNullOrWhiteSpace(zoomDirectory) || !Directory.Exists(zoomDirectory)) return null;
        if (string.IsNullOrWhiteSpace(id)) return null;

        foreach (var ext in Extensions)
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
                if (Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
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
