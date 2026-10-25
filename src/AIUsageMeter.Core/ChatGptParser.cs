using System.Globalization;
using System.Text.Json;

namespace AIUsageMeter.Core;

/// <summary>
/// ChatGPT 取得スクリプトの結果。アクセストークンそのものはページ外へ出さず、有無だけを受け取る。
/// </summary>
public sealed record ChatGptPageResult(int SessionStatus, bool HasToken, int? UsageStatus, string? UsageBody);

/// <summary>chatgpt.com の <c>/backend-api/wham/usage</c>（Codex の上限）を読み取る。</summary>
public static class ChatGptParser
{
    public static UsageData ParseUsage(string? json, DateTimeOffset now)
    {
        using var doc = JsonUtil.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UsageParseException("使用量の形式が不正です");
        }

        UsageWindow? fiveHour = null;
        UsageWindow? weekly = null;
        if (JsonUtil.TryGetObject(root, "rate_limit", out var rateLimit))
        {
            fiveHour = ParseWindow(rateLimit, "primary_window", now);
            weekly = ParseWindow(rateLimit, "secondary_window", now);
        }

        return new UsageData(fiveHour, weekly, FormatPlan(JsonUtil.GetString(root, "plan_type")));
    }

    public static FetchOutcome Interpret(ChatGptPageResult result, DateTimeOffset now)
    {
        if (!result.HasToken)
        {
            return result.SessionStatus >= 500
                ? FetchOutcome.Error($"セッションの取得に失敗しました (HTTP {result.SessionStatus})")
                : FetchOutcome.LoginRequired();
        }

        if (result.UsageStatus is 401 or 403)
        {
            return FetchOutcome.LoginRequired();
        }

        if (result.UsageStatus is not (>= 200 and < 300))
        {
            return FetchOutcome.Error($"使用量の取得に失敗しました (HTTP {result.UsageStatus})");
        }

        try
        {
            var data = ParseUsage(result.UsageBody, now);
            return data.HasAnyWindow ? FetchOutcome.Ok(data) : FetchOutcome.Error("Codex の使用量の項目が見つかりません");
        }
        catch (UsageParseException ex)
        {
            return FetchOutcome.Error(ex.Message);
        }
    }

    public static string? FormatPlan(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        return planType.ToLowerInvariant() switch
        {
            "free" => "Free",
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" => "Edu",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(planType.Replace('_', ' ').ToLowerInvariant()),
        };
    }

    private static UsageWindow? ParseWindow(JsonElement rateLimit, string name, DateTimeOffset now)
    {
        if (!JsonUtil.TryGetObject(rateLimit, name, out var window) || JsonUtil.GetPercent(window, "used_percent") is not double percent)
        {
            return null;
        }

        var resetsAt = JsonUtil.GetTimestamp(window, "reset_at");
        if (resetsAt is null && JsonUtil.GetNumber(window, "reset_after_seconds") is double after && after >= 0)
        {
            resetsAt = now.AddSeconds(after);
        }

        TimeSpan? length = JsonUtil.GetNumber(window, "limit_window_seconds") is double seconds && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

        return new UsageWindow(percent, resetsAt, length);
    }
}
