using System.Text.Json;
using AIUsageMeter.Core;

namespace AIUsageMeter;

internal interface IUsageFetcher
{
    /// <summary>対象サイトのページが開かれた状態で呼ばれ、ページ内 fetch で使用量を取得する。</summary>
    Task<FetchOutcome> FetchAsync(ServiceBrowser browser, CancellationToken cancellationToken);
}

/// <summary>ページ内スクリプトが例外を投げた（ネットワークエラー・中断など）。</summary>
internal sealed class PageScriptException(string errorName, string message) : Exception(message)
{
    public bool IsAbort => errorName == "AbortError";
}

internal sealed class ClaudeFetcher(Func<string?> selectedOrganizationId) : IUsageFetcher
{
    private const string OrganizationsScript = "__post(await __get('/api/organizations'));";

    public IReadOnlyList<ClaudeOrganization> Organizations { get; private set; } = [];

    public ClaudeOrganization? ActiveOrganization { get; private set; }

    public async Task<FetchOutcome> FetchAsync(ServiceBrowser browser, CancellationToken cancellationToken)
    {
        var organizations = ClaudeParser.InterpretOrganizations(
            PageResults.ToFetchResult(await browser.RunScriptAsync(OrganizationsScript, cancellationToken)));
        if (organizations.Failure is not null)
        {
            return organizations.Failure;
        }

        Organizations = organizations.Organizations;
        var active = ClaudeParser.SelectOrganization(Organizations, selectedOrganizationId())!;
        ActiveOrganization = active;

        var usageScript =
            $"__post(await __get('/api/organizations/' + encodeURIComponent({JsonSerializer.Serialize(active.Uuid)}) + '/usage'));";
        var usage = PageResults.ToFetchResult(await browser.RunScriptAsync(usageScript, cancellationToken));
        return ClaudeParser.InterpretUsage(usage, active.PlanName);
    }
}

internal sealed class ChatGptFetcher : IUsageFetcher
{
    // アクセストークンは毎回ページ内で取得し、ページの外（C# 側）には有無しか渡さない。
    private const string UsageScript = """
        const s = await fetch('/api/auth/session', { credentials: 'include', cache: 'no-store' });
        let token = null;
        try { token = (await s.json()).accessToken || null; } catch (_) { token = null; }
        if (!s.ok || !token) { __post({ sessionStatus: s.status, hasToken: false }); return; }
        const u = await __get('https://chatgpt.com/backend-api/wham/usage', { Authorization: 'Bearer ' + token });
        __post({ sessionStatus: s.status, hasToken: true, status: u.status, body: u.body });
        """;

    public async Task<FetchOutcome> FetchAsync(ServiceBrowser browser, CancellationToken cancellationToken)
    {
        var result = await browser.RunScriptAsync(UsageScript, cancellationToken);
        PageResults.ThrowIfError(result);
        var page = new ChatGptPageResult(
            PageResults.GetInt(result, "sessionStatus") ?? 0,
            result.TryGetProperty("hasToken", out var hasToken) && hasToken.ValueKind == JsonValueKind.True,
            PageResults.GetInt(result, "status"),
            PageResults.GetString(result, "body"));
        return ChatGptParser.Interpret(page, DateTimeOffset.Now);
    }
}

internal static class PageResults
{
    public static void ThrowIfError(JsonElement result)
    {
        if (result.TryGetProperty("error", out var error))
        {
            throw new PageScriptException(GetString(result, "errorName") ?? string.Empty, error.ToString());
        }
    }

    public static PageFetchResult ToFetchResult(JsonElement result)
    {
        ThrowIfError(result);
        return new PageFetchResult(GetInt(result, "status") ?? 0, GetString(result, "body"), GetString(result, "url"));
    }

    public static int? GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    public static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
