namespace AIUsageMeter.Core;

/// <summary>1 つの制限枠についての通知状態。設定ファイルに保存して再起動後も引き継ぐ。</summary>
public sealed class ThresholdState
{
    public bool Initialized { get; set; }

    public DateTimeOffset? ResetsAt { get; set; }

    /// <summary>現在「超えている」と記録済みのしきい値。</summary>
    public List<int> Crossed { get; set; } = new();
}

/// <summary>
/// しきい値を下から超えた瞬間だけ通知対象を返す。
/// <list type="bullet">
/// <item>上にいる間は再通知しない。</item>
/// <item>リセット時刻が変わったら（＝枠がリセットされたら）状態を戻す。</item>
/// <item>初回観測時は状態の記録のみ行い、通知しない（過去にいつ超えたか分からないため）。</item>
/// </list>
/// </summary>
public static class NotificationEvaluator
{
    public static readonly IReadOnlyList<int> DefaultThresholds = [80, 95];

    /// <summary>API 側の丸め等によるリセット時刻の揺れを「リセット」と誤判定しないための許容幅。</summary>
    public static readonly TimeSpan ResetTolerance = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<int> Evaluate(ThresholdState state, UsageWindow window) =>
        Evaluate(state, window, DefaultThresholds);

    public static IReadOnlyList<int> Evaluate(ThresholdState state, UsageWindow window, IReadOnlyList<int> thresholds)
    {
        if (!state.Initialized)
        {
            state.Initialized = true;
            state.ResetsAt = window.ResetsAt;
            state.Crossed = thresholds.Where(t => window.Percent >= t).ToList();
            return [];
        }

        if (HasReset(state.ResetsAt, window.ResetsAt))
        {
            state.Crossed.Clear();
        }

        state.ResetsAt = window.ResetsAt;

        // 下回ったしきい値は、次に超えたとき再び通知できるよう解除する。
        state.Crossed.RemoveAll(t => window.Percent < t);

        var fired = thresholds
            .Where(t => window.Percent >= t && !state.Crossed.Contains(t))
            .OrderBy(t => t)
            .ToList();
        state.Crossed.AddRange(fired);
        return fired;
    }

    private static bool HasReset(DateTimeOffset? previous, DateTimeOffset? current)
    {
        if (previous is null || current is null)
        {
            return previous is not null || current is not null;
        }

        return (current.Value - previous.Value).Duration() > ResetTolerance;
    }
}
