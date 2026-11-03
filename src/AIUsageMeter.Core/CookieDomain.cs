namespace AIUsageMeter.Core;

public static class CookieDomain
{
    /// <summary>Cookie のドメインが指定ホストまたはそのサブドメインかを判定する。先頭のドットは無視する。</summary>
    public static bool BelongsTo(string cookieDomain, string host)
    {
        var domain = cookieDomain.TrimStart('.');
        return domain.Equals(host, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }
}
