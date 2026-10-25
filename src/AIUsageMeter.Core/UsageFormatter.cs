using System.Globalization;

namespace AIUsageMeter.Core;

public static class UsageFormatter
{
    /// <summary>NotifyIcon.Text の上限。</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>表示用の整数パーセント。100 未満が四捨五入で 100 になる場合は 99 に留める。</summary>
    public static int DisplayPercent(double percent)
    {
        var rounded = (int)Math.Round(percent, MidpointRounding.AwayFromZero);
        if (rounded >= 100 && percent < 100)
        {
            rounded = 99;
        }

        return Math.Clamp(rounded, 0, 100);
    }

    public static string Percent(UsageWindow? window) =>
        window is null ? "--" : $"{DisplayPercent(window.Percent)}%";

    /// <summary>リセット時刻。当日なら「14:30」、それ以外は「10/28 14:30」。</summary>
    public static string ResetClock(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var local = resetsAt.ToLocalTime();
        var format = local.Date == now.ToLocalTime().Date ? "HH:mm" : "M/d HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>リセットまでの残り時間。「あと2時間13分」など。</summary>
    public static string Remaining(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var left = resetsAt - now;
        if (left <= TimeSpan.FromMinutes(1))
        {
            return "まもなく";
        }

        if (left.TotalDays >= 1)
        {
            return $"あと{(int)left.TotalDays}日{left.Hours}時間";
        }

        if (left.TotalHours >= 1)
        {
            return $"あと{(int)left.TotalHours}時間{left.Minutes}分";
        }

        return $"あと{(int)Math.Ceiling(left.TotalMinutes)}分";
    }

    /// <summary>例：「Claude 5h 42% / 週 18%（リセット 14:30）」。</summary>
    public static string Tooltip(string serviceName, ServiceDisplayStatus status, UsageData? data, DateTimeOffset now)
    {
        var text = status switch
        {
            ServiceDisplayStatus.Unknown => $"{serviceName}：取得中…",
            ServiceDisplayStatus.LoginRequired => $"{serviceName}：要ログイン（右クリック →「ログイン」）",
            _ when data is null => $"{serviceName}：取得に失敗しました",
            _ => Summary(serviceName, data, now) + (status == ServiceDisplayStatus.Stale ? " ※前回値" : string.Empty),
        };

        return text.Length > MaxTooltipLength ? text[..MaxTooltipLength] : text;
    }

    private static string Summary(string serviceName, UsageData data, DateTimeOffset now)
    {
        var text = $"{serviceName} 5h {Percent(data.FiveHour)} / 週 {Percent(data.Weekly)}";
        if (data.FiveHour?.ResetsAt is DateTimeOffset reset)
        {
            text += $"（リセット {ResetClock(reset, now)}）";
        }

        return text;
    }
}
