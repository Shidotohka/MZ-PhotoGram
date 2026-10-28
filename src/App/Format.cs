using System;
using System.Globalization;

namespace Photogrammetry.App;

/// <summary>
/// 界面上的数字写法。只此一处：符号规则、单位、NaN 的写法各面板各写一套，
/// 迟早会出现「同一个量在两个地方长得不一样」。
/// </summary>
internal static class Format
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>定点数。NaN（没有这个值）写成「—」，不要写成「NaN」。</summary>
    public static string Fixed(double value, string format) =>
        double.IsNaN(value) ? "—" : CollapseZero(value, format).ToString(format, Ci);

    /// <summary>带符号的定点数：正数补「+」，负数保留「−」。</summary>
    public static string Signed(double value, string format) =>
        double.IsNaN(value)
            ? "—"
            : CollapseZero(value, format).ToString("+" + format + ";-" + format, Ci);

    /// <summary>
    /// 把「四舍五入后就是 0 的负数」扳成 +0。
    ///
    /// <para>踩过的坑：<c>-1e-17</c> 用 <c>"+0.000000;-0.000000"</c> 格式化，.NET 给出
    /// <c>-+0.000000</c>——负号补在正号段前面。既难看，又连 <c>double.Parse</c> 都解析不了
    /// （离屏工具读界面上的数时会直接抛异常）。值本来就小到显示成 0，就不该再带符号。</para>
    /// </summary>
    private static double CollapseZero(double value, string format)
    {
        if (!(value < 0) || double.IsNegativeInfinity(value)) return value;

        var decimals = Decimals(format);
        if (decimals < 0) return value;

        // 小于最后一位的一半，四舍五入就是 0
        return value > -0.5 * Math.Pow(10, -decimals) ? 0 : value;
    }

    /// <summary>格式串第一段的小数位数（<c>"0.0000"</c> → 4，<c>"0.0E+0"</c> → 1）。</summary>
    private static int Decimals(string format)
    {
        var section = format.Split(';')[0];
        var dot = section.IndexOf('.');
        if (dot < 0) return 0;

        var count = 0;
        for (var i = dot + 1; i < section.Length; i++)
        {
            if (section[i] is '0' or '#') count++;
            else break;
        }

        return count;
    }
}
