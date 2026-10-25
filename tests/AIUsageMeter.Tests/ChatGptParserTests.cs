using AIUsageMeter.Core;

namespace AIUsageMeter.Tests;

public class ChatGptParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseUsage_Normal_ReadsBothWindowsAndPlan()
    {
        const string json = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "allowed": true,
            "primary_window": { "used_percent": 37, "limit_window_seconds": 18000, "reset_after_seconds": 3600, "reset_at": 1761364800 },
            "secondary_window": { "used_percent": 12.5, "limit_window_seconds": 604800, "reset_at": 1761800000 }
          }
        }
        """;

        var data = ChatGptParser.ParseUsage(json, Now);

        Assert.Equal("Plus", data.PlanName);
        Assert.Equal(37, data.FiveHour!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761364800), data.FiveHour.ResetsAt);
        Assert.Equal(TimeSpan.FromHours(5), data.FiveHour.WindowLength);
        Assert.Equal(12.5, data.Weekly!.Percent);
        Assert.Equal(TimeSpan.FromDays(7), data.Weekly.WindowLength);
    }

    [Fact]
    public void ParseUsage_SecondaryMissing_KeepsPrimary()
    {
        const string json = """{ "plan_type": "pro", "rate_limit": { "primary_window": { "used_percent": 5 }, "secondary_window": null } }""";

        var data = ChatGptParser.ParseUsage(json, Now);

        Assert.Equal(5, data.FiveHour!.Percent);
        Assert.Null(data.FiveHour.ResetsAt);
        Assert.Null(data.Weekly);
        Assert.Equal("Pro", data.PlanName);
    }

    [Fact]
    public void ParseUsage_ResetAtMissing_UsesResetAfterSeconds()
    {
        const string json = """{ "rate_limit": { "primary_window": { "used_percent": 5, "reset_after_seconds": 600 } } }""";

        var data = ChatGptParser.ParseUsage(json, Now);

        Assert.Equal(Now.AddMinutes(10), data.FiveHour!.ResetsAt);
    }

    [Fact]
    public void ParseUsage_BrokenPrimaryItem_KeepsSecondary()
    {
        const string json = """{ "rate_limit": { "primary_window": { "used_percent": null }, "secondary_window": { "used_percent": "20" } } }""";

        var data = ChatGptParser.ParseUsage(json, Now);

        Assert.Null(data.FiveHour);
        Assert.Equal(20, data.Weekly!.Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"rate_limit\": ")]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("\"text\"")]
    public void ParseUsage_BrokenJson_Throws(string json)
    {
        Assert.Throws<UsageParseException>(() => ChatGptParser.ParseUsage(json, Now));
    }

    [Theory]
    [InlineData("plus", "Plus")]
    [InlineData("PRO", "Pro")]
    [InlineData("self_serve_business", "Self Serve Business")]
    [InlineData(null, null)]
    public void FormatPlan_MapsKnownAndUnknown(string? input, string? expected)
    {
        Assert.Equal(expected, ChatGptParser.FormatPlan(input));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    public void Interpret_NoToken_IsLoginRequired(int sessionStatus)
    {
        var outcome = ChatGptParser.Interpret(new ChatGptPageResult(sessionStatus, false, null, null), Now);

        Assert.Equal(FetchStatus.LoginRequired, outcome.Status);
    }

    [Fact]
    public void Interpret_SessionServerError_IsError()
    {
        Assert.Equal(FetchStatus.Error, ChatGptParser.Interpret(new ChatGptPageResult(502, false, null, null), Now).Status);
    }

    [Fact]
    public void Interpret_UsageStatuses()
    {
        Assert.Equal(FetchStatus.LoginRequired, ChatGptParser.Interpret(new ChatGptPageResult(200, true, 401, ""), Now).Status);
        Assert.Equal(FetchStatus.Error, ChatGptParser.Interpret(new ChatGptPageResult(200, true, 500, ""), Now).Status);
        Assert.Equal(FetchStatus.Error, ChatGptParser.Interpret(new ChatGptPageResult(200, true, 200, "{ broken"), Now).Status);
        Assert.Equal(FetchStatus.Error, ChatGptParser.Interpret(new ChatGptPageResult(200, true, 200, """{ "plan_type": "free" }"""), Now).Status);

        var ok = ChatGptParser.Interpret(
            new ChatGptPageResult(200, true, 200, """{ "rate_limit": { "primary_window": { "used_percent": 1 } } }"""), Now);
        Assert.Equal(FetchStatus.Ok, ok.Status);
        Assert.Equal(1, ok.Data!.FiveHour!.Percent);
    }
}
