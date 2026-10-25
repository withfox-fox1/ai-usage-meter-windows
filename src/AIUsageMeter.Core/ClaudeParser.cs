using System.Text.Json;

namespace AIUsageMeter.Core;

public sealed record ClaudeOrganization(string Uuid, string Name, string? PlanName, bool SupportsChat);

public sealed record ClaudeOrganizationsOutcome(FetchOutcome? Failure, IReadOnlyList<ClaudeOrganization> Organizations);

/// <summary>claude.ai の <c>/api/organizations</c> と <c>/usage</c> の応答を読み取る。</summary>
public static class ClaudeParser
{
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    public static IReadOnlyList<ClaudeOrganization> ParseOrganizations(string? json)
    {
        using var doc = JsonUtil.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new UsageParseException("組織一覧の形式が不正です");
        }

        var organizations = new List<ClaudeOrganization>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var uuid = JsonUtil.GetString(item, "uuid");
            if (string.IsNullOrWhiteSpace(uuid))
            {
                continue;
            }

            var name = JsonUtil.GetString(item, "name");
            var capabilities = ReadCapabilities(item);
            organizations.Add(new ClaudeOrganization(
                uuid,
                string.IsNullOrWhiteSpace(name) ? uuid : name,
                DetectPlan(capabilities, JsonUtil.GetString(item, "rate_limit_tier")),
                capabilities.Contains("chat")));
        }

        return organizations;
    }

    /// <summary>保存済みの選択 → チャット可能な組織 → 先頭、の順に選ぶ。</summary>
    public static ClaudeOrganization? SelectOrganization(IReadOnlyList<ClaudeOrganization> organizations, string? selectedUuid) =>
        organizations.FirstOrDefault(o => o.Uuid == selectedUuid)
        ?? organizations.FirstOrDefault(o => o.SupportsChat)
        ?? organizations.FirstOrDefault();

    /// <summary>
    /// 新形式 <c>limits[]</c> を優先し、無い・壊れている項目は旧形式
    /// <c>five_hour</c> / <c>seven_day</c> にフォールバックする。項目ごとに独立して判定する。
    /// </summary>
    public static UsageData ParseUsage(string? json)
    {
        using var doc = JsonUtil.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UsageParseException("使用量の形式が不正です");
        }

        return new UsageData(
            FromLimits(root, "session", FiveHours) ?? FromLegacy(root, "five_hour", FiveHours),
            FromLimits(root, "weekly_all", SevenDays) ?? FromLegacy(root, "seven_day", SevenDays));
    }

    public static ClaudeOrganizationsOutcome InterpretOrganizations(PageFetchResult result)
    {
        if (result.IsAuthError || IsLoginRedirect(result.FinalUrl))
        {
            return new(FetchOutcome.LoginRequired(), []);
        }

        if (!result.IsSuccess)
        {
            return new(FetchOutcome.Error($"組織一覧の取得に失敗しました (HTTP {result.Status})"), []);
        }

        try
        {
            var organizations = ParseOrganizations(result.Body);
            return organizations.Count == 0
                ? new(FetchOutcome.Error("組織が見つかりません"), [])
                : new(null, organizations);
        }
        catch (UsageParseException ex)
        {
            return new(FetchOutcome.Error(ex.Message), []);
        }
    }

    public static FetchOutcome InterpretUsage(PageFetchResult result, string? planName)
    {
        if (result.IsAuthError || IsLoginRedirect(result.FinalUrl))
        {
            return FetchOutcome.LoginRequired();
        }

        if (!result.IsSuccess)
        {
            return FetchOutcome.Error($"使用量の取得に失敗しました (HTTP {result.Status})");
        }

        try
        {
            var data = ParseUsage(result.Body) with { PlanName = planName };
            return data.HasAnyWindow ? FetchOutcome.Ok(data) : FetchOutcome.Error("使用量の項目が見つかりません");
        }
        catch (UsageParseException ex)
        {
            return FetchOutcome.Error(ex.Message);
        }
    }

    public static bool IsLoginRedirect(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase);

    private static UsageWindow? FromLimits(JsonElement root, string kind, TimeSpan length)
    {
        if (!JsonUtil.TryGetArray(root, "limits", out var limits))
        {
            return null;
        }

        foreach (var item in limits.EnumerateArray())
        {
            if (JsonUtil.GetString(item, "kind") != kind || JsonUtil.GetPercent(item, "percent") is not double percent)
            {
                continue;
            }

            return new UsageWindow(percent, JsonUtil.GetTimestamp(item, "resets_at"), length);
        }

        return null;
    }

    private static UsageWindow? FromLegacy(JsonElement root, string name, TimeSpan length)
    {
        if (!JsonUtil.TryGetObject(root, name, out var window) || JsonUtil.GetPercent(window, "utilization") is not double percent)
        {
            return null;
        }

        return new UsageWindow(percent, JsonUtil.GetTimestamp(window, "resets_at"), length);
    }

    private static HashSet<string> ReadCapabilities(JsonElement organization)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (JsonUtil.TryGetArray(organization, "capabilities", out var capabilities))
        {
            foreach (var item in capabilities.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    private static string? DetectPlan(HashSet<string> capabilities, string? rateLimitTier)
    {
        var tier = rateLimitTier?.ToLowerInvariant() ?? string.Empty;
        if (tier.Contains("max_20x"))
        {
            return "Max 20x";
        }

        if (tier.Contains("max_5x"))
        {
            return "Max 5x";
        }

        if (capabilities.Contains("claude_max") || tier.Contains("max"))
        {
            return "Max";
        }

        if (capabilities.Contains("claude_pro") || tier.Contains("pro"))
        {
            return "Pro";
        }

        if (tier.Contains("enterprise"))
        {
            return "Enterprise";
        }

        if (capabilities.Contains("raven") || tier.Contains("team") || tier.Contains("raven"))
        {
            return "Team";
        }

        return capabilities.Contains("chat") ? "Free" : null;
    }
}
