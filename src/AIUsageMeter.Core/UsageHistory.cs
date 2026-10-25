namespace AIUsageMeter.Core;

public sealed record HistoryPoint(DateTimeOffset Time, ServiceKind Service, double? FiveHour, double? Weekly);

/// <summary>取得ごとの使用率の履歴。24 時間より古いものは削除する。</summary>
public sealed class UsageHistory
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public List<HistoryPoint> Points { get; set; } = new();

    public void Add(HistoryPoint point)
    {
        Points.Add(point);
        Prune(point.Time);
    }

    public void Prune(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        Points.RemoveAll(p => p.Time < cutoff);
    }

    public IReadOnlyList<HistoryPoint> For(ServiceKind service, DateTimeOffset now)
    {
        var cutoff = now - Retention;
        return Points
            .Where(p => p.Service == service && p.Time >= cutoff)
            .OrderBy(p => p.Time)
            .ToList();
    }
}
