using AIUsageMeter.Core;

namespace AIUsageMeter.Tests;

public class ClaudeParserTests
{
    [Fact]
    public void ParseUsage_LimitsFormat_ReadsSessionAndWeekly()
    {
        const string json = """
        {
          "limits": [
            { "kind": "session", "percent": 42, "resets_at": "2026-10-25T05:30:00.123456+00:00" },
            { "kind": "weekly_all", "percent": 18.5, "resets_at": "2026-10-28T00:00:00Z" },
            { "kind": "weekly_opus", "percent": 90 }
          ]
        }
        """;

        var data = ClaudeParser.ParseUsage(json);

        Assert.Equal(42, data.FiveHour!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 5, 30, 0, 123, TimeSpan.Zero), data.FiveHour.ResetsAt!.Value.AddTicks(-4560));
        Assert.Equal(TimeSpan.FromHours(5), data.FiveHour.WindowLength);
        Assert.Equal(18.5, data.Weekly!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), data.Weekly.ResetsAt);
    }

    [Fact]
    public void ParseUsage_LegacyFormat_FallsBack()
    {
        const string json = """
        {
          "five_hour": { "utilization": 63.0, "resets_at": "2026-10-25T05:30:00+00:00" },
          "seven_day": { "utilization": 12, "resets_at": null }
        }
        """;

        var data = ClaudeParser.ParseUsage(json);

        Assert.Equal(63, data.FiveHour!.Percent);
        Assert.NotNull(data.FiveHour.ResetsAt);
        Assert.Equal(12, data.Weekly!.Percent);
        Assert.Null(data.Weekly.ResetsAt);
    }

    [Fact]
    public void ParseUsage_BrokenSessionItem_FallsBackPerItem()
    {
        // session の percent が壊れている → five_hour にフォールバック。weekly は limits から取れる。
        const string json = """
        {
          "limits": [
            { "kind": "session", "percent": "abc" },
            { "kind": "weekly_all", "percent": 30 }
          ],
          "five_hour": { "utilization": 55 }
        }
        """;

        var data = ClaudeParser.ParseUsage(json);

        Assert.Equal(55, data.FiveHour!.Percent);
        Assert.Equal(30, data.Weekly!.Percent);
    }

    [Fact]
    public void ParseUsage_OneItemMissing_KeepsTheOther()
    {
        const string json = """{ "limits": [ { "kind": "weekly_all", "percent": 30 } ], "five_hour": "broken" }""";

        var data = ClaudeParser.ParseUsage(json);

        Assert.Null(data.FiveHour);
        Assert.Equal(30, data.Weekly!.Percent);
    }

    [Fact]
    public void ParseUsage_OutOfRangePercent_IsClamped()
    {
        var data = ClaudeParser.ParseUsage("""{ "limits": [ { "kind": "session", "percent": 140 } ] }""");

        Assert.Equal(100, data.FiveHour!.Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("<html>Just a moment...</html>")]
    [InlineData("[1,2,3]")]
    public void ParseUsage_BrokenJson_Throws(string json)
    {
        Assert.Throws<UsageParseException>(() => ClaudeParser.ParseUsage(json));
    }

    [Fact]
    public void ParseOrganizations_ReadsUuidNameAndPlan()
    {
        const string json = """
        [
          { "uuid": "org-1", "name": "API Org", "capabilities": ["api"] },
          { "uuid": "org-2", "name": "Personal", "capabilities": ["chat", "claude_max"], "rate_limit_tier": "default_claude_max_20x" },
          { "name": "no uuid" },
          "garbage"
        ]
        """;

        var orgs = ClaudeParser.ParseOrganizations(json);

        Assert.Equal(2, orgs.Count);
        Assert.Equal("org-2", orgs[1].Uuid);
        Assert.Equal("Personal", orgs[1].Name);
        Assert.Equal("Max 20x", orgs[1].PlanName);
        Assert.True(orgs[1].SupportsChat);
        Assert.False(orgs[0].SupportsChat);
    }

    [Fact]
    public void SelectOrganization_PrefersSavedThenChatThenFirst()
    {
        var orgs = new List<ClaudeOrganization>
        {
            new("a", "A", null, false),
            new("b", "B", "Pro", true),
        };

        Assert.Equal("a", ClaudeParser.SelectOrganization(orgs, "a")!.Uuid);
        Assert.Equal("b", ClaudeParser.SelectOrganization(orgs, "missing")!.Uuid);
        Assert.Equal("b", ClaudeParser.SelectOrganization(orgs, null)!.Uuid);
        Assert.Null(ClaudeParser.SelectOrganization([], null));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void InterpretOrganizations_AuthError_IsLoginRequired(int status)
    {
        var outcome = ClaudeParser.InterpretOrganizations(new PageFetchResult(status, "{}"));

        Assert.Equal(FetchStatus.LoginRequired, outcome.Failure!.Status);
    }

    [Fact]
    public void InterpretOrganizations_RedirectedToLogin_IsLoginRequired()
    {
        var outcome = ClaudeParser.InterpretOrganizations(new PageFetchResult(200, "<html></html>", "https://claude.ai/login?returnTo=%2F"));

        Assert.Equal(FetchStatus.LoginRequired, outcome.Failure!.Status);
    }

    [Fact]
    public void InterpretOrganizations_ServerErrorOrBrokenJson_IsError()
    {
        Assert.Equal(FetchStatus.Error, ClaudeParser.InterpretOrganizations(new PageFetchResult(500, "oops")).Failure!.Status);
        Assert.Equal(FetchStatus.Error, ClaudeParser.InterpretOrganizations(new PageFetchResult(200, "{ broken")).Failure!.Status);
        Assert.Equal(FetchStatus.Error, ClaudeParser.InterpretOrganizations(new PageFetchResult(200, "[]")).Failure!.Status);
    }

    [Fact]
    public void InterpretUsage_Success_AttachesPlanName()
    {
        var outcome = ClaudeParser.InterpretUsage(
            new PageFetchResult(200, """{ "limits": [ { "kind": "session", "percent": 10 } ] }"""), "Max");

        Assert.Equal(FetchStatus.Ok, outcome.Status);
        Assert.Equal("Max", outcome.Data!.PlanName);
        Assert.Null(outcome.Data.Weekly);
    }

    [Fact]
    public void InterpretUsage_NoWindowsOrBrokenJson_IsError()
    {
        Assert.Equal(FetchStatus.Error, ClaudeParser.InterpretUsage(new PageFetchResult(200, "{}"), null).Status);
        Assert.Equal(FetchStatus.Error, ClaudeParser.InterpretUsage(new PageFetchResult(200, "nope"), null).Status);
        Assert.Equal(FetchStatus.LoginRequired, ClaudeParser.InterpretUsage(new PageFetchResult(401, ""), null).Status);
    }
}
