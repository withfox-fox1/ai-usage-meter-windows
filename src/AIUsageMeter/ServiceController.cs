using AIUsageMeter.Core;
using Microsoft.Web.WebView2.Core;

namespace AIUsageMeter;

/// <summary>1 サービス分のトレイアイコン・WebView2・取得状態をまとめる。</summary>
internal sealed class ServiceController : IDisposable
{
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly System.Windows.Forms.Timer _loginPoll = new() { Interval = 3000 };
    private readonly Func<TimeSpan> _timeout;
    private CoreWebView2Environment? _environment;
    private CancellationToken _shutdown;
    private bool _loginPollBusy;

    public ServiceController(ServiceDefinition definition, IUsageFetcher fetcher, Func<TimeSpan> timeout)
    {
        Definition = definition;
        Fetcher = fetcher;
        _timeout = timeout;
        Browser = new ServiceBrowser($"{definition.DisplayName} にログイン - AI Usage Meter", definition.Host);
        Browser.LoginWindowClosed += (_, _) =>
        {
            _loginPoll.Stop();
            LoginWindowClosed?.Invoke(this, EventArgs.Empty);
        };
        Icon = new NotifyIcon { ContextMenuStrip = new ContextMenuStrip() };
        _loginPoll.Tick += OnLoginPollTick;
        UpdateIcon();
    }

    /// <summary>取得結果を反映した（中断以外）。</summary>
    public event EventHandler<FetchOutcome>? StateChanged;

    public event EventHandler? LoginWindowClosed;

    public ServiceDefinition Definition { get; }

    public ServiceKind Kind => Definition.Kind;

    public IUsageFetcher Fetcher { get; }

    public NotifyIcon Icon { get; }

    public ServiceBrowser Browser { get; }

    public ServiceDisplayStatus Status { get; private set; } = ServiceDisplayStatus.Unknown;

    /// <summary>最後に取得できた値（失敗時も保持し、薄く表示する）。</summary>
    public UsageData? Data { get; private set; }

    public string? Message { get; private set; }

    public DateTimeOffset? LastUpdated { get; private set; }

    public void Attach(CoreWebView2Environment environment, CancellationToken shutdown)
    {
        _environment = environment;
        _shutdown = shutdown;
    }

    public void SetFatalError(string message)
    {
        Status = Data is null ? ServiceDisplayStatus.Error : ServiceDisplayStatus.Stale;
        Message = message;
        UpdateIcon();
    }

    /// <summary>使用量を取得して反映する。同じサービスの取得が実行中なら何もしない。</summary>
    public async Task<FetchOutcome> RefreshAsync()
    {
        if (_environment is null || !await _fetchLock.WaitAsync(0))
        {
            return FetchOutcome.Cancelled("取得中");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
            timeout.CancelAfter(_timeout());
            FetchOutcome outcome;
            try
            {
                outcome = await FetchCoreAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                outcome = FetchOutcome.Error($"タイムアウトしました（{_timeout().TotalSeconds:0}秒）");
            }
            catch (OperationCanceledException)
            {
                outcome = FetchOutcome.Cancelled();
            }
            catch (PageScriptException ex) when (ex.IsAbort)
            {
                outcome = FetchOutcome.Cancelled();
            }
            catch (Exception ex)
            {
                Log.Error($"{Definition.DisplayName}: 取得エラー", ex);
                outcome = FetchOutcome.Error($"通信エラー: {ex.Message}");
            }

            if (!_shutdown.IsCancellationRequested)
            {
                Apply(outcome);
            }

            return outcome;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public async void OpenLogin()
    {
        if (_environment is null)
        {
            return;
        }

        try
        {
            await Browser.EnsureInitializedAsync(_environment);
            if (!Browser.Visible)
            {
                Browser.Show();
                Browser.Navigate(Definition.LoginPage);
            }

            if (Browser.WindowState == FormWindowState.Minimized)
            {
                Browser.WindowState = FormWindowState.Normal;
            }

            Browser.Activate();
            _loginPoll.Start();
            Log.Info($"{Definition.DisplayName}: ログインウィンドウを表示");
        }
        catch (Exception ex)
        {
            Log.Error($"{Definition.DisplayName}: ログインウィンドウを開けません", ex);
            MessageBox.Show($"ログインウィンドウを開けませんでした。\n{ex.Message}", "AI Usage Meter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void UpdateIcon()
    {
        var previous = Icon.Icon;
        Icon.Icon = TrayIconRenderer.Render(Definition, Status, Data?.FiveHour?.Percent, SystemInformation.SmallIconSize.Width);
        previous?.Dispose();
        Icon.Text = UsageFormatter.Tooltip(Definition.DisplayName, Status, Data, DateTimeOffset.Now);
    }

    public void Dispose()
    {
        _loginPoll.Stop();
        _loginPoll.Dispose();
        Icon.Visible = false;
        Icon.Icon?.Dispose();
        Icon.ContextMenuStrip?.Dispose();
        Icon.Dispose();
        Browser.CloseForShutdown();
    }

    private async Task<FetchOutcome> FetchCoreAsync(CancellationToken cancellationToken)
    {
        await Browser.EnsureInitializedAsync(_environment!).WaitAsync(cancellationToken);

        if (Browser.Visible)
        {
            // ログイン操作中はページを動かさない。認証プロバイダ等の別サイトにいる間は取得を見送る。
            if (!Browser.IsOnServiceHost)
            {
                return FetchOutcome.Cancelled("ログイン操作中");
            }
        }
        else if (!Browser.IsAt(Definition.BackgroundPage))
        {
            await Browser.NavigateAsync(Definition.BackgroundPage, cancellationToken);
            if (!Browser.IsOnServiceHost)
            {
                return FetchOutcome.Error("サイトを開けませんでした");
            }
        }

        return await Fetcher.FetchAsync(Browser, cancellationToken);
    }

    private void Apply(FetchOutcome outcome)
    {
        var previousStatus = Status;
        var previousMessage = Message;
        switch (outcome.Status)
        {
            case FetchStatus.Ok:
                Data = outcome.Data;
                Status = ServiceDisplayStatus.Ok;
                Message = null;
                LastUpdated = DateTimeOffset.Now;
                break;
            case FetchStatus.LoginRequired:
                Status = ServiceDisplayStatus.LoginRequired;
                Message = outcome.Message;
                break;
            case FetchStatus.Error:
                Status = Data is null ? ServiceDisplayStatus.Error : ServiceDisplayStatus.Stale;
                Message = outcome.Message;
                break;
            default:
                return;
        }

        if (Status != previousStatus || Message != previousMessage)
        {
            Log.Info($"{Definition.DisplayName}: {Status}{(Message is null ? string.Empty : $" ({Message})")}");
        }

        UpdateIcon();
        StateChanged?.Invoke(this, outcome);
    }

    private async void OnLoginPollTick(object? sender, EventArgs e)
    {
        if (_loginPollBusy)
        {
            return;
        }

        if (!Browser.Visible)
        {
            _loginPoll.Stop();
            return;
        }

        _loginPollBusy = true;
        try
        {
            var outcome = await RefreshAsync();
            if (outcome.Status == FetchStatus.Ok && Browser.Visible)
            {
                _loginPoll.Stop();
                Browser.Hide();
                Log.Info($"{Definition.DisplayName}: ログインを確認し、ウィンドウを閉じました");
            }
        }
        finally
        {
            _loginPollBusy = false;
        }
    }
}
