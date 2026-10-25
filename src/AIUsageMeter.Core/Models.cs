namespace AIUsageMeter.Core;

public enum ServiceKind
{
    Claude,
    ChatGpt,
}

/// <summary>1 つの制限枠（5 時間・週間）の使用状況。</summary>
public sealed record UsageWindow(double Percent, DateTimeOffset? ResetsAt, TimeSpan? WindowLength = null);

public sealed record UsageData(UsageWindow? FiveHour, UsageWindow? Weekly, string? PlanName = null)
{
    public bool HasAnyWindow => FiveHour is not null || Weekly is not null;
}

public enum FetchStatus
{
    Ok,
    LoginRequired,
    Error,

    /// <summary>通信が途中で中断された。エラー扱いせず、表示も変更しない。</summary>
    Cancelled,
}

public sealed record FetchOutcome(FetchStatus Status, UsageData? Data = null, string? Message = null)
{
    public static FetchOutcome Ok(UsageData data) => new(FetchStatus.Ok, data);

    public static FetchOutcome LoginRequired(string? message = null) =>
        new(FetchStatus.LoginRequired, null, message ?? "ログインが必要です");

    public static FetchOutcome Error(string message) => new(FetchStatus.Error, null, message);

    public static FetchOutcome Cancelled(string? message = null) => new(FetchStatus.Cancelled, null, message);
}

/// <summary>トレイアイコン・パネルに表示する状態。</summary>
public enum ServiceDisplayStatus
{
    Unknown,
    Ok,

    /// <summary>直近の取得に失敗し、前回値を表示している。</summary>
    Stale,
    LoginRequired,

    /// <summary>取得に失敗し、表示できる前回値もない。</summary>
    Error,
}

public enum UsageLevel
{
    Normal,
    Warning,
    Critical,
}

public static class UsageLevels
{
    public const double WarningThreshold = 50;
    public const double CriticalThreshold = 80;

    public static UsageLevel FromPercent(double percent) =>
        percent >= CriticalThreshold ? UsageLevel.Critical
        : percent >= WarningThreshold ? UsageLevel.Warning
        : UsageLevel.Normal;
}

/// <summary>ページ内 fetch の結果（HTTP ステータス・本文・最終 URL）。</summary>
public sealed record PageFetchResult(int Status, string? Body, string? FinalUrl = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public bool IsAuthError => Status is 401 or 403;
}

public sealed class UsageParseException : Exception
{
    public UsageParseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
