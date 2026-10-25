using System.Drawing.Drawing2D;
using AIUsageMeter.Core;
using Microsoft.Win32;

namespace AIUsageMeter;

internal sealed record PanelTheme(bool IsDark, Color Background, Color Foreground, Color Secondary, Color Track, Color Grid, Color Alert)
{
    private static readonly PanelTheme Dark = new(true, Color.FromArgb(0x20, 0x20, 0x20), Color.FromArgb(0xF3, 0xF3, 0xF3),
        Color.FromArgb(0xA8, 0xA8, 0xA8), Color.FromArgb(0x3A, 0x3A, 0x3A), Color.FromArgb(0x34, 0x34, 0x34), Color.FromArgb(0xFF, 0x99, 0xA4));

    private static readonly PanelTheme Light = new(false, Color.FromArgb(0xF9, 0xF9, 0xF9), Color.FromArgb(0x1A, 0x1A, 0x1A),
        Color.FromArgb(0x5F, 0x5F, 0x5F), Color.FromArgb(0xE2, 0xE2, 0xE2), Color.FromArgb(0xE6, 0xE6, 0xE6), Color.FromArgb(0xC4, 0x2B, 0x1C));

    /// <summary>Windows の「アプリのモード」（ライト/ダーク）に合わせる。</summary>
    public static PanelTheme Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? Dark : Light;
        }
        catch (Exception)
        {
            return Light;
        }
    }
}

/// <summary>トレイアイコンの左クリックで出る小さなパネル。外をクリックすると閉じる。</summary>
internal sealed class UsagePanel : Form
{
    private const int LogicalWidth = 320;
    private const int Pad = 16;
    private const int HeaderHeight = 30;
    private const int StatusHeight = 20;
    private const int MeterHeight = 62;
    private const int ChartTitleHeight = 22;
    private const int ChartHeight = 84;
    private const int AxisHeight = 18;
    private const int FooterHeight = 20;
    private const int ChartLabelWidth = 28;

    /// <summary>これ以上間隔が空いた履歴点は線でつながない（スリープ等）。</summary>
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(16);

    private readonly ServiceController _service;
    private readonly UsageHistory _history;
    private readonly Func<string?> _organizationLabel;
    private PanelTheme _theme = PanelTheme.Current();
    private float _scale = 1f;

    public UsagePanel(ServiceController service, UsageHistory history, Func<string?> organizationLabel)
    {
        _service = service;
        _history = history;
        _organizationLabel = organizationLabel;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = $"{service.Definition.DisplayName} 使用量";
    }

    public ServiceKind Kind => _service.Kind;

    protected override CreateParams CreateParams
    {
        get
        {
            const int CsDropShadow = 0x20000;
            const int WsExToolWindow = 0x80;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    /// <summary>指定位置（通常はクリックしたトレイアイコン）の近くに表示する。</summary>
    public void ShowNear(Point anchor)
    {
        _theme = PanelTheme.Current();
        BackColor = _theme.Background;

        // 表示先モニターの DPI でハンドルを作ってから大きさを決める。
        Bounds = new Rectangle(anchor.X, anchor.Y, 1, 1);
        _ = Handle;
        _scale = DeviceDpi / 96f;
        NativeMethods.ApplyWindowStyle(Handle, _theme.IsDark);

        var width = S(LogicalWidth);
        var height = S(LogicalHeight());
        var area = Screen.FromPoint(anchor).WorkingArea;
        var margin = S(8);
        var x = Clamp(anchor.X - width / 2, area.Left + margin, area.Right - width - margin);
        var y = anchor.Y - height - S(12);
        if (y < area.Top + margin)
        {
            y = anchor.Y + S(12);
        }

        y = Clamp(y, area.Top + margin, area.Bottom - height - margin);
        Bounds = new Rectangle(x, y, width, height);

        Show();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
    }

    public void Refresh(bool resize)
    {
        if (resize && Visible)
        {
            Height = S(LogicalHeight());
        }

        Invalidate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // 自前でスケーリングするため、WinForms による自動リサイズは行わない。
        e.Cancel = true;
        _scale = e.DeviceDpiNew / 96f;
        Size = new Size(S(LogicalWidth), S(LogicalHeight()));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_theme.Background);

        var now = DateTimeOffset.Now;
        var definition = _service.Definition;
        var data = _service.Data;
        using var titleFont = CreateFont(15, FontStyle.Bold);
        using var bodyFont = CreateFont(13);
        using var valueFont = CreateFont(14, FontStyle.Bold);
        using var smallFont = CreateFont(11.5f);

        float x = S(Pad);
        float width = ClientSize.Width - (2 * S(Pad));
        float y = S(Pad);

        // ヘッダー：アイコン形状・サービス名・プラン名
        using (var mark = TrayIconRenderer.CreateShape(definition.Shape, S(14), x, y + S(8)))
        using (var markBrush = new SolidBrush(definition.Color))
        {
            g.FillPath(markBrush, mark);
        }

        DrawText(g, definition.DisplayName, titleFont, _theme.Foreground, new RectangleF(x + S(22), y, width - S(22), S(HeaderHeight)), TextFormatFlags.Left);
        DrawText(g, data?.PlanName ?? string.Empty, bodyFont, _theme.Secondary, new RectangleF(x, y, width, S(HeaderHeight)), TextFormatFlags.Right);
        y += S(HeaderHeight);

        if (StatusText() is { } status)
        {
            var color = _service.Status == ServiceDisplayStatus.Unknown ? _theme.Secondary : _theme.Alert;
            DrawText(g, status, smallFont, color, new RectangleF(x, y, width, S(StatusHeight)), TextFormatFlags.Left);
            y += S(StatusHeight);
        }

        var faded = _service.Status != ServiceDisplayStatus.Ok;
        y = DrawMeter(g, "5時間", data?.FiveHour, x, y, width, now, faded, bodyFont, valueFont, smallFont);
        y = DrawMeter(g, "週間", data?.Weekly, x, y, width, now, faded, bodyFont, valueFont, smallFont);

        // 直近 24 時間の折れ線
        DrawText(g, "直近24時間", bodyFont, _theme.Foreground, new RectangleF(x, y, width, S(ChartTitleHeight)), TextFormatFlags.Left);
        DrawLegend(g, new RectangleF(x, y, width, S(ChartTitleHeight)), smallFont);
        y += S(ChartTitleHeight);
        var chart = new RectangleF(x + S(ChartLabelWidth), y + S(4), width - S(ChartLabelWidth), S(ChartHeight) - S(8));
        DrawChart(g, chart, now, smallFont);
        y += S(ChartHeight);
        DrawText(g, "24時間前", smallFont, _theme.Secondary, new RectangleF(chart.Left, y, chart.Width, S(AxisHeight)), TextFormatFlags.Left);
        DrawText(g, "12時間前", smallFont, _theme.Secondary, new RectangleF(chart.Left, y, chart.Width, S(AxisHeight)), TextFormatFlags.HorizontalCenter);
        DrawText(g, "現在", smallFont, _theme.Secondary, new RectangleF(chart.Left, y, chart.Width, S(AxisHeight)), TextFormatFlags.Right);
        y += S(AxisHeight) + S(6);

        var footer = _service.LastUpdated is { } updated ? $"最終更新 {updated.ToLocalTime():HH:mm:ss}" : "最終更新 —";
        if (_organizationLabel() is { } organization)
        {
            footer += $" ・ {organization}";
        }

        DrawText(g, footer, smallFont, _theme.Secondary, new RectangleF(x, y, width, S(FooterHeight)), TextFormatFlags.Left);
    }

    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF bounds, TextFormatFlags alignment)
    {
        TextRenderer.DrawText(
            g,
            text,
            font,
            Rectangle.Round(bounds),
            color,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Fade(Color color, bool faded) => faded ? Color.FromArgb(120, color) : color;

    private int S(float logical) => (int)Math.Round(logical * _scale);

    private Font CreateFont(float pixels, FontStyle style = FontStyle.Regular) =>
        new("Yu Gothic UI", pixels * _scale, style, GraphicsUnit.Pixel);

    private int LogicalHeight() =>
        Pad + HeaderHeight + (StatusText() is null ? 0 : StatusHeight) + (2 * MeterHeight)
        + ChartTitleHeight + ChartHeight + AxisHeight + 6 + FooterHeight + Pad;

    private string? StatusText() => _service.Status switch
    {
        ServiceDisplayStatus.Unknown => "取得中…",
        ServiceDisplayStatus.LoginRequired => "要ログイン：右クリック →「ログイン」",
        ServiceDisplayStatus.Stale => $"取得に失敗（前回値を表示中）：{_service.Message}",
        ServiceDisplayStatus.Error => $"取得に失敗：{_service.Message}",
        _ => null,
    };

    private float DrawMeter(Graphics g, string label, UsageWindow? window, float x, float y, float width, DateTimeOffset now,
        bool faded, Font bodyFont, Font valueFont, Font smallFont)
    {
        DrawText(g, label, bodyFont, _theme.Foreground, new RectangleF(x, y, width, S(20)), TextFormatFlags.Left);
        DrawText(g, UsageFormatter.Percent(window), valueFont, Fade(_theme.Foreground, faded), new RectangleF(x, y, width, S(20)), TextFormatFlags.Right);
        y += S(24);

        var track = new RectangleF(x, y, width, S(8));
        using (var trackPath = RoundedRectangle(track, S(4)))
        using (var trackBrush = new SolidBrush(_theme.Track))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (window is not null && window.Percent > 0)
        {
            var fill = track with { Width = Math.Max(S(8), (float)(track.Width * window.Percent / 100)) };
            using var fillPath = RoundedRectangle(fill, S(4));
            using var fillBrush = new SolidBrush(Fade(TrayIconRenderer.LevelColor(_service.Definition, window.Percent), faded));
            g.FillPath(fillBrush, fillPath);
        }

        y += S(12);
        var reset = window switch
        {
            null => "データなし",
            { ResetsAt: { } at } => $"リセットまで {UsageFormatter.Remaining(at, now)}（{UsageFormatter.ResetClock(at, now)}）",
            _ => "リセット時刻 —",
        };
        DrawText(g, reset, smallFont, _theme.Secondary, new RectangleF(x, y, width, S(16)), TextFormatFlags.Left);
        return y + S(MeterHeight - 36);
    }

    private void DrawLegend(Graphics g, RectangleF row, Font font)
    {
        const string fiveHour = "5時間";
        const string weekly = "週間";
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var weeklyWidth = TextRenderer.MeasureText(g, weekly, font, Size.Empty, flags).Width;
        var fiveWidth = TextRenderer.MeasureText(g, fiveHour, font, Size.Empty, flags).Width;
        var sample = S(16);
        var gap = S(6);
        var midY = row.Top + (row.Height / 2);

        var weeklyX = row.Right - weeklyWidth;
        using (var pen = WeeklyPen())
        {
            g.DrawLine(pen, weeklyX - gap - sample, midY, weeklyX - gap, midY);
        }

        DrawText(g, weekly, font, _theme.Secondary, new RectangleF(weeklyX, row.Top, weeklyWidth + S(2), row.Height), TextFormatFlags.Left);

        var fiveX = weeklyX - gap - sample - S(12) - fiveWidth;
        using (var pen = FiveHourPen())
        {
            g.DrawLine(pen, fiveX - gap - sample, midY, fiveX - gap, midY);
        }

        DrawText(g, fiveHour, font, _theme.Secondary, new RectangleF(fiveX, row.Top, fiveWidth + S(2), row.Height), TextFormatFlags.Left);
    }

    private Pen FiveHourPen() => new(_service.Definition.Color, Math.Max(1.5f, 2f * _scale));

    private Pen WeeklyPen() => new(Color.FromArgb(160, _theme.Secondary), Math.Max(1.5f, 2f * _scale)) { DashStyle = DashStyle.Dash };

    private void DrawChart(Graphics g, RectangleF rect, DateTimeOffset now, Font font)
    {
        using (var gridPen = new Pen(_theme.Grid, 1))
        {
            foreach (var value in new[] { 0, 50, 100 })
            {
                var lineY = rect.Bottom - (rect.Height * value / 100f);
                g.DrawLine(gridPen, rect.Left, lineY, rect.Right, lineY);
                DrawText(g, value.ToString(), font, _theme.Secondary,
                    new RectangleF(rect.Left - S(ChartLabelWidth), lineY - S(8), S(ChartLabelWidth - 6), S(16)), TextFormatFlags.Right);
            }
        }

        var points = _history.For(_service.Kind, now);
        if (points.Count == 0)
        {
            DrawText(g, "まだ履歴がありません", font, _theme.Secondary, rect, TextFormatFlags.HorizontalCenter);
            return;
        }

        using var weeklyPen = WeeklyPen();
        DrawSeries(g, rect, points, p => p.Weekly, weeklyPen, now);
        using var fivePen = FiveHourPen();
        DrawSeries(g, rect, points, p => p.FiveHour, fivePen, now);
    }

    private void DrawSeries(Graphics g, RectangleF rect, IReadOnlyList<HistoryPoint> points, Func<HistoryPoint, double?> selector, Pen pen, DateTimeOffset now)
    {
        var start = now - UsageHistory.Retention;
        var segment = new List<PointF>();
        DateTimeOffset? previous = null;

        void Flush()
        {
            if (segment.Count >= 2)
            {
                g.DrawLines(pen, segment.ToArray());
            }
            else if (segment.Count == 1)
            {
                var r = pen.Width * 1.2f;
                using var brush = new SolidBrush(pen.Color);
                g.FillEllipse(brush, segment[0].X - r, segment[0].Y - r, r * 2, r * 2);
            }

            segment.Clear();
        }

        foreach (var point in points)
        {
            var value = selector(point);
            if (value is null || (previous is { } prev && point.Time - prev > GapThreshold))
            {
                Flush();
            }

            if (value is double v)
            {
                var px = rect.Left + (float)((point.Time - start).TotalHours / UsageHistory.Retention.TotalHours * rect.Width);
                var py = rect.Bottom - (float)(rect.Height * Math.Clamp(v, 0, 100) / 100);
                segment.Add(new PointF(Math.Clamp(px, rect.Left, rect.Right), py));
            }

            previous = point.Time;
        }

        Flush();
    }
}
