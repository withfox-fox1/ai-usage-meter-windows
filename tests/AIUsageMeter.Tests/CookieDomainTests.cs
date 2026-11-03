using AIUsageMeter.Core;

namespace AIUsageMeter.Tests;

public class CookieDomainTests
{
    [Theory]
    [InlineData("claude.ai", true)]
    [InlineData(".claude.ai", true)]
    [InlineData("CLAUDE.AI", true)]
    [InlineData("a.claude.ai", true)]
    [InlineData("notclaude.ai", false)]
    [InlineData("claude.ai.example.com", false)]
    [InlineData(".chatgpt.com", false)]
    [InlineData("accounts.google.com", false)]
    public void BelongsTo_MatchesOnlyHostAndSubdomains(string cookieDomain, bool expected)
    {
        Assert.Equal(expected, CookieDomain.BelongsTo(cookieDomain, "claude.ai"));
    }
}
