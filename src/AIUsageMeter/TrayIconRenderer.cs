using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using AIUsageMeter.Core;

namespace AIUsageMeter;

/// <summary>トレイアイコン（丸・六角形の上に 5 時間の使用率）を描く。</summary>
internal static class TrayIconRenderer
{
    public static readonly Color WarningColor = Color.FromArgb(0xF5, 0xC2, 0x18);
    public static readonly Color CriticalColor = Color.FromArgb(0xD9, 0x30, 0x25);
    private const int FadedAlpha = 110;

    public static Icon Render(ServiceDefinition definition, ServiceDisplayStatus status, double? fiveHourPercent, int size)
    {
        size = Math.Max(16, size);
        var (text, background, foreground, faded) = Describe(definition, status, fiveHourPercent);
        var alpha = faded ? FadedAlpha : 255;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            using var shape = CreateShape(definition.Shape, size);
            using var shapeBrush = new SolidBrush(Color.FromArgb(alpha, background));
            graphics.FillPath(shapeBrush, shape);

            using var textPath = CreateTextPath(text, size);
            using var textBrush = new SolidBrush(Color.FromArgb(alpha, foreground));
            graphics.FillPath(textBrush, textPath);
        }

        return ToIcon(bitmap);
    }

    public static Color LevelColor(ServiceDefinition definition, double percent) => UsageLevels.FromPercent(percent) switch
    {
        UsageLevel.Critical => CriticalColor,
        UsageLevel.Warning => WarningColor,
        _ => definition.Color,
    };

    public static GraphicsPath CreateShape(IconShape shape, float size, float x = 0, float y = 0)
    {
        var path = new GraphicsPath();
        if (shape == IconShape.Circle)
        {
            path.AddEllipse(x + 0.5f, y + 0.5f, size - 1, size - 1);
        }
        else
        {
            // 数字が収まるよう、横幅いっぱいのフラットトップ六角形にする。
            path.AddPolygon(new[]
            {
                new PointF(x + size * 0.25f, y + size * 0.04f),
                new PointF(x + size * 0.75f, y + size * 0.04f),
                new PointF(x + size * 0.995f, y + size * 0.5f),
                new PointF(x + size * 0.75f, y + size * 0.96f),
                new PointF(x + size * 0.25f, y + size * 0.96f),
                new PointF(x + size * 0.005f, y + size * 0.5f),
            });
        }

        return path;
    }

    private static (string Text, Color Background, Color Foreground, bool Faded) Describe(
        ServiceDefinition definition, ServiceDisplayStatus status, double? fiveHourPercent)
    {
        switch (status)
        {
            case ServiceDisplayStatus.LoginRequired:
                return ("?", definition.Color, Color.White, false);
            case ServiceDisplayStatus.Unknown:
                return ("-", definition.Color, Color.White, false);
        }

        var faded = status != ServiceDisplayStatus.Ok;
        if (fiveHourPercent is not double percent)
        {
            return ("-", definition.Color, Color.White, faded);
        }

        var level = UsageLevels.FromPercent(percent);
        var foreground = level == UsageLevel.Warning ? Color.Black : Color.White;
        return (UsageFormatter.DisplayPercent(percent).ToString(CultureInfo.InvariantCulture), LevelColor(definition, percent), foreground, faded);
    }

    /// <summary>文字をパスにして、アイコン内に収まる最大サイズへ拡大縮小する（3 桁は横幅優先で小さく）。</summary>
    private static GraphicsPath CreateTextPath(string text, int size)
    {
        var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var (maxWidth, maxHeight) = text.Length switch
        {
            >= 3 => (size * 0.98f, size * 0.50f),
            2 => (size * 0.84f, size * 0.62f),
            _ => (size * 0.56f, size * 0.62f),
        };
        var scale = Math.Min(maxWidth / bounds.Width, maxHeight / bounds.Height);

        using var matrix = new Matrix();
        matrix.Translate(size / 2f, size / 2f);
        matrix.Scale(scale, scale);
        matrix.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
        path.Transform(matrix);
        return path;
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
