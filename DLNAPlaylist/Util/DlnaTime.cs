using System.Globalization;

namespace DLNAPlaylist.Util;

/// <summary>
///     DLNA 的时间格式：hh:mm:ss[.fff] 或 h:mm:ss。
///     InvariantGlobalization=true 下我们手动解析/拼装。
/// </summary>
public static class DlnaTime
{
    public static string Format(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        var h = (int)ts.TotalHours;
        var m = ts.Minutes;
        var s = ts.Seconds;
        return string.Create(CultureInfo.InvariantCulture, $"{h}:{m:D2}:{s:D2}");
    }

    public static TimeSpan? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        if (input.Equals("NOT_IMPLEMENTED", StringComparison.OrdinalIgnoreCase)) return null;

        // 去掉小数部分
        var dot = input.IndexOf('.');
        if (dot >= 0) input = input[..dot];

        var parts = input.Split(':');
        if (parts.Length != 3) return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return null;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return null;
        if (h < 0 || m < 0 || s < 0) return null;
        try
        {
            return new TimeSpan(h, m, s);
        }
        catch
        {
            return null;
        }
    }
}