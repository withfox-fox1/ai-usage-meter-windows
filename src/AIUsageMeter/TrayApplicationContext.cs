using AIUsageMeter.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace AIUsageMeter;

/// <summary>アプリ全体：2 つのトレイアイコン、定期更新、通知、パネル、メニュー。</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PanelReopenGuard = TimeSpan.FromMilliseconds(400);

    private readonly AppSettings _settings;
    private readonly UsageHistory _history;
    private readonly List<ServiceController> _services = new();
    private readonly ClaudeFetcher _claudeFetcher;
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 30_000 };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SynchronizationContext _ui;
    private readonly ServiceKind? _openPanelOnStart;
    private bool _refreshing;
    private bool _exiting;
    private UsagePanel? _panel;
    private ServiceKind? _lastClosedPanel;
    private DateTime _panelClosedAt;

    public TrayApplicationContext(ServiceKind? openPanelOnStart)
    {
        _openPanelOnStart = openPanelOnStart;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _settings = AppSettings.Load();
        _history = JsonFile.Load<UsageHistory>(AppPaths.HistoryFile);
        _history.Prune(DateTimeOffset.Now);

        _claudeFetcher = new ClaudeFetcher(() => _settings.ClaudeOrganizationId);
        AddService(ServiceDefinition.Claude, _claudeFetcher);
        AddService(ServiceDefinition.ChatGpt, new ChatGptFetcher());
        ApplyIconVisibility();

        if (!_settings.AutoStartInitialized)
        {
            AutoStart.SetEnabled(true);
            _settings.AutoStartInitialized = true;
            _settings.Save();
        }
        else
        {
            AutoStart.RefreshPathIfEnabled();
        }

        _refreshTimer.Interval = (int)TimeSpan.FromMinutes(_settings.RefreshIntervalMinutes).TotalMilliseconds;
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        _retryTimer.Tick += async (_, _) =>
        {
            _retryTimer.Stop();
            await RefreshAllAsync();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _ = StartAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ExitApplication();
            _refreshTimer.Dispose();
            _retryTimer.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }

    private ServiceController Service(ServiceKind kind) => _services.First(s => s.Kind == kind);

    private void AddService(ServiceDefinition definition, IUsageFetcher fetcher)
    {
        var service = new ServiceController(definition, fetcher, () => TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds));
        service.StateChanged += OnServiceStateChanged;
        service.LoginWindowClosed += async (_, _) => await RefreshAllAsync();
        service.Icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                TogglePanel(service);
            }
        };
        var menu = service.Icon.ContextMenuStrip!;
        menu.Opening += (_, e) =>
        {
            BuildMenu(service, menu);
            e.Cancel = false;
        };
        _services.Add(service);
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDirectory);
            foreach (var service in _services)
            {
                service.Attach(environment, _shutdown.Token);
            }
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 の初期化に失敗", ex);
            foreach (var service in _services)
            {
                service.SetFatalError("WebView2 ランタイムを初期化できません");
            }

            return;
        }

        _refreshTimer.Start();
        await RefreshAllAsync();

        if (_openPanelOnStart is { } kind && !_exiting)
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            ShowPanel(Service(kind), new Point(area.Right - 40, area.Bottom));
        }
    }

    /// <summary>全サービスを更新する。前回の更新が終わっていなければ何もしない。</summary>
    private async Task RefreshAllAsync()
    {
        if (_refreshing || _exiting)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var outcomes = await Task.WhenAll(_services.Select(s => s.RefreshAsync()));

            // 中断（キャンセル）はエラー扱いせず、少し後に 1 回だけ再試行する。
            if (!_exiting && outcomes.Any(o => o.Status == FetchStatus.Cancelled && o.Message is null))
            {
                _retryTimer.Stop();
                _retryTimer.Start();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnServiceStateChanged(object? sender, FetchOutcome outcome)
    {
        var service = (ServiceController)sender!;
        if (outcome.Status == FetchStatus.Ok && outcome.Data is { } data)
        {
            var now = DateTimeOffset.Now;
            _history.Add(new HistoryPoint(now, service.Kind, data.FiveHour?.Percent, data.Weekly?.Percent));
            JsonFile.Save(AppPaths.HistoryFile, _history);
            EvaluateNotifications(service, data, now);
        }

        if (_panel is { IsDisposed: false } panel && panel.Kind == service.Kind)
        {
            panel.Refresh(resize: true);
        }
    }

    private void EvaluateNotifications(ServiceController service, UsageData data, DateTimeOffset now)
    {
        var lines = new List<string>();
        var highest = 0;
        foreach (var (key, label, window) in new[] { ("FiveHour", "5時間", data.FiveHour), ("Weekly", "週間", data.Weekly) })
        {
            if (window is null)
            {
                continue;
            }

            var stateKey = $"{service.Kind}.{key}";
            if (!_settings.NotificationStates.TryGetValue(stateKey, out var state))
            {
                state = new ThresholdState();
                _settings.NotificationStates[stateKey] = state;
            }

            var fired = NotificationEvaluator.Evaluate(state, window);
            if (fired.Count == 0)
            {
                continue;
            }

            var threshold = fired.Max();
            highest = Math.Max(highest, threshold);
            var reset = window.ResetsAt is { } at ? $"・リセット {UsageFormatter.ResetClock(at, now)}" : string.Empty;
            lines.Add($"{label}枠が{threshold}%を超えました（現在 {UsageFormatter.Percent(window)}{reset}）");
        }

        _settings.Save();
        if (lines.Count == 0)
        {
            return;
        }

        Log.Info($"{service.Definition.DisplayName}: 通知 {highest}%");
        var icon = service.Icon.Visible ? service.Icon : _services.First(s => s.Icon.Visible).Icon;
        icon.ShowBalloonTip(10_000, $"{service.Definition.DisplayName} の使用量が {highest}% を超えました", string.Join("\n", lines), ToolTipIcon.Warning);
    }

    private void TogglePanel(ServiceController service)
    {
        if (_panel is { IsDisposed: false } open)
        {
            var same = open.Kind == service.Kind;
            open.Close();
            if (same)
            {
                return;
            }
        }
        else if (_lastClosedPanel == service.Kind && DateTime.UtcNow - _panelClosedAt < PanelReopenGuard)
        {
            // アイコンのクリックでパネルが非アクティブになり閉じた直後：開き直さない（トグル動作）。
            return;
        }

        ShowPanel(service, Cursor.Position);
    }

    private void ShowPanel(ServiceController service, Point anchor)
    {
        var panel = new UsagePanel(service, _history, () => OrganizationLabel(service));
        panel.FormClosed += (_, _) =>
        {
            if (_panel == panel)
            {
                _panel = null;
                _lastClosedPanel = panel.Kind;
                _panelClosedAt = DateTime.UtcNow;
            }
        };
        _panel = panel;
        panel.ShowNear(anchor);
    }

    private string? OrganizationLabel(ServiceController service) =>
        service.Kind == ServiceKind.Claude && _claudeFetcher.Organizations.Count > 1
            ? _claudeFetcher.ActiveOrganization?.Name
            : null;

    private void BuildMenu(ServiceController service, ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var title = new ToolStripMenuItem(service.Definition.DisplayName) { Enabled = false };
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("今すぐ更新", null, async (_, _) => await RefreshAllAsync());
        menu.Items.Add("ログイン…", null, (_, _) => service.OpenLogin());

        if (service.Kind == ServiceKind.Claude)
        {
            var organizations = new ToolStripMenuItem("組織の選択");
            var active = _claudeFetcher.ActiveOrganization?.Uuid;
            foreach (var organization in _claudeFetcher.Organizations)
            {
                var label = organization.PlanName is null ? organization.Name : $"{organization.Name}（{organization.PlanName}）";
                var item = new ToolStripMenuItem(label) { Checked = organization.Uuid == active };
                item.Click += async (_, _) =>
                {
                    _settings.ClaudeOrganizationId = organization.Uuid;
                    _settings.Save();
                    await service.RefreshAsync();
                };
                organizations.DropDownItems.Add(item);
            }

            organizations.Enabled = organizations.DropDownItems.Count > 0;
            menu.Items.Add(organizations);
        }

        menu.Items.Add(new ToolStripSeparator());
        var other = _services.First(s => s.Kind != service.Kind);
        var otherHidden = _settings.HiddenServices.Contains(other.Kind);
        menu.Items.Add(new ToolStripMenuItem("このアイコンを隠す", null, (_, _) => SetHidden(service.Kind)) { Enabled = !otherHidden });
        if (otherHidden)
        {
            menu.Items.Add($"{other.Definition.DisplayName} のアイコンを表示", null, (_, _) => SetHidden(null));
        }

        var autoStart = new ToolStripMenuItem("ログオン時に起動") { Checked = AutoStart.IsEnabled() };
        autoStart.Click += (_, _) => AutoStart.SetEnabled(!AutoStart.IsEnabled());
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
    }

    private void SetHidden(ServiceKind? kind)
    {
        _settings.HiddenServices = kind is { } k ? [k] : [];
        _settings.Save();
        if (_panel is { IsDisposed: false } panel && panel.Kind == kind)
        {
            panel.Close();
        }

        ApplyIconVisibility();
    }

    private void ApplyIconVisibility()
    {
        foreach (var service in _services)
        {
            service.Icon.Visible = !_settings.HiddenServices.Contains(service.Kind);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        // ネットワーク復帰を少し待ってから更新する。
        _ui.Post(async _ =>
        {
            await Task.Delay(ResumeDelay);
            await RefreshAllAsync();
        }, null);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _ui.Post(_ => _services.ForEach(s => s.UpdateIcon()), null);

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _shutdown.Cancel();
        _refreshTimer.Stop();
        _retryTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _panel?.Close();
        _settings.Save();
        JsonFile.Save(AppPaths.HistoryFile, _history);
        foreach (var service in _services)
        {
            service.Dispose();
        }

        ExitThread();
    }
}
