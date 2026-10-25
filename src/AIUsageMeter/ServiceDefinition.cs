using AIUsageMeter.Core;

namespace AIUsageMeter;

internal enum IconShape
{
    Circle,
    Hexagon,
}

/// <param name="BackgroundPage">
/// 取得時に非表示 WebView2 で開くページ。同一オリジンの軽いページ（robots.txt）にして、
/// SPA を常駐させないことでメモリ・CPU 消費を抑える。
/// </param>
internal sealed record ServiceDefinition(
    ServiceKind Kind,
    string DisplayName,
    Color Color,
    IconShape Shape,
    Uri BackgroundPage,
    Uri LoginPage)
{
    public static readonly ServiceDefinition Claude = new(
        ServiceKind.Claude,
        "Claude",
        Color.FromArgb(0xD9, 0x77, 0x57),
        IconShape.Circle,
        new Uri("https://claude.ai/robots.txt"),
        new Uri("https://claude.ai/login"));

    public static readonly ServiceDefinition ChatGpt = new(
        ServiceKind.ChatGpt,
        "ChatGPT",
        Color.FromArgb(0x10, 0xA3, 0x7F),
        IconShape.Hexagon,
        new Uri("https://chatgpt.com/robots.txt"),
        new Uri("https://chatgpt.com/auth/login"));

    public string Host => BackgroundPage.Host;
}
