using AIUsageMeter.Core;

namespace AIUsageMeter.Tests;

public class FormattingAndHistoryTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(41.5, 42)]
    [InlineData(99.6, 99)]
    [InlineData(100, 100)]
    [InlineData(-3, 0)]
    public void DisplayPercent_RoundsButNeverShows100Early(double input, int expected)
    {
        Assert.Equal(expected, UsageFormatter.DisplayPercent(input));
    }

    [Fact]
    public void Tooltip_ShowsBothWindowsAndFiveHourReset()
    {
        var now = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 25, 10, 0, 0)));
        var data = new UsageData(new UsageWindow(42, now.AddHours(4).AddMinutes(30)), new UsageWindow(18, now.AddDays(3)));

        var text = UsageFormatter.Tooltip("Claude", ServiceDisplayStatus.Ok, data, now);

        Assert.Equal("Claude 5h 42% / 週 18%（リセット 14:30）", text);
    }

    [Fact]
    public void Tooltip_StatusVariants()
    {
        var now = DateTimeOffset.Now;
        var data = new UsageData(null, new UsageWindow(18, null));

        Assert.Equal("ChatGPT 5h -- / 週 18% ※前回値", UsageFormatter.Tooltip("ChatGPT", ServiceDisplayStatus.Stale, data, now));
        Assert.Contains("要ログイン", UsageFormatter.Tooltip("Claude", ServiceDisplayStatus.LoginRequired, null, now));
        Assert.Contains("失敗", UsageFormatter.Tooltip("Claude", ServiceDisplayStatus.Error, null, now));
        Assert.True(UsageFormatter.Tooltip(new string('x', 200), ServiceDisplayStatus.Ok, data, now).Length <= UsageFormatter.MaxTooltipLength);
    }

    [Fact]
    public void Remaining_FormatsByMagnitude()
    {
        var now = DateTimeOffset.Now;

        Assert.Equal("あと2時間13分", UsageFormatter.Remaining(now.AddHours(2).AddMinutes(13).AddSeconds(5), now));
        Assert.Equal("あと3日4時間", UsageFormatter.Remaining(now.AddDays(3).AddHours(4).AddMinutes(1), now));
        Assert.Equal("あと5分", UsageFormatter.Remaining(now.AddMinutes(4).AddSeconds(30), now));
        Assert.Equal("まもなく", UsageFormatter.Remaining(now.AddSeconds(-10), now));
    }

    [Theory]
    [InlineData(10, UsageLevel.Normal)]
    [InlineData(50, UsageLevel.Warning)]
    [InlineData(79.9, UsageLevel.Warning)]
    [InlineData(80, UsageLevel.Critical)]
    public void UsageLevels_Thresholds(double percent, UsageLevel expected)
    {
        Assert.Equal(expected, UsageLevels.FromPercent(percent));
    }

    [Fact]
    public void History_PrunesOlderThan24Hours()
    {
        var now = DateTimeOffset.Now;
        var history = new UsageHistory();
        history.Points.Add(new HistoryPoint(now.AddHours(-25), ServiceKind.Claude, 1, 1));
        history.Points.Add(new HistoryPoint(now.AddHours(-23), ServiceKind.Claude, 2, 2));

        history.Add(new HistoryPoint(now, ServiceKind.ChatGpt, 3, 3));

        Assert.Equal(2, history.Points.Count);
        Assert.Single(history.For(ServiceKind.Claude, now));
        Assert.Equal(2, history.For(ServiceKind.Claude, now)[0].FiveHour);
    }
}
