using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIUsageMeter;

/// <summary>
/// サービスごとの WebView2 を持つウィンドウ。通常は非表示のまま使用量の取得に使い、
/// ログイン時だけ表示する。ログイン状態は共有の WebView2 プロファイルに残る。
/// </summary>
internal sealed class ServiceBrowser : Form
{
    private readonly WebView2 _webView;
    private readonly string _host;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private Task? _initTask;
    private bool _allowClose;

    public ServiceBrowser(string title, string host)
    {
        _host = host;
        Text = title;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 760);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
    }

    /// <summary>ユーザーがログインウィンドウを閉じた。</summary>
    public event EventHandler? LoginWindowClosed;

    public Uri? CurrentUri =>
        _webView.CoreWebView2 is { } core && Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsOnServiceHost => string.Equals(CurrentUri?.Host, _host, StringComparison.OrdinalIgnoreCase);

    private CoreWebView2 Core => _webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 が初期化されていません");

    public Task EnsureInitializedAsync(CoreWebView2Environment environment)
    {
        if (_initTask is null || _initTask.IsFaulted)
        {
            _initTask = InitializeAsync(environment);
        }

        return _initTask;
    }

    public bool IsAt(Uri uri) =>
        CurrentUri is { } current
        && Uri.Compare(current, uri, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public void Navigate(Uri uri) => Core.Navigate(uri.AbsoluteUri);

    /// <summary>
    /// 遷移完了を待つ。HTTP エラーページでも完了扱いにし、判定はページ内 fetch の結果に任せる。
    /// 遷移が中断された場合は <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
    {
        var core = Core;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnCompleted;
            if (e.IsSuccess)
            {
                completion.TrySetResult();
            }
            else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                completion.TrySetCanceled();
            }
            else if (IsConnectionFailure(e.WebErrorStatus))
            {
                completion.TrySetException(new IOException($"ページを開けませんでした ({e.WebErrorStatus})"));
            }
            else
            {
                completion.TrySetResult();
            }
        }

        core.NavigationCompleted += OnCompleted;
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            core.Navigate(uri.AbsoluteUri);
            await completion.Task;
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

    /// <summary>
    /// ページ内でスクリプトを実行し、<c>__post({...})</c> で返された結果を待つ。
    /// <c>ExecuteScriptAsync</c> は Promise を待たないため、結果は postMessage で受け取る。
    /// スクリプト内では <c>__get(url, headers)</c>（同一 Cookie で fetch）を使える。
    /// </summary>
    public async Task<JsonElement> RunScriptAsync(string body, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            await Core.ExecuteScriptAsync(Wrap(id, body)).WaitAsync(cancellationToken);
            return await completion.Task;
        }
        finally
        {
            _pending.Remove(id);
        }
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        Close();
        Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            LoginWindowClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnFormClosing(e);
    }

    private static bool IsConnectionFailure(CoreWebView2WebErrorStatus status) => status
        is CoreWebView2WebErrorStatus.CannotConnect
        or CoreWebView2WebErrorStatus.ConnectionAborted
        or CoreWebView2WebErrorStatus.ConnectionReset
        or CoreWebView2WebErrorStatus.Disconnected
        or CoreWebView2WebErrorStatus.HostNameNotResolved
        or CoreWebView2WebErrorStatus.ServerUnreachable
        or CoreWebView2WebErrorStatus.Timeout;

    private static string Wrap(string id, string body) => $$"""
        (async () => {
          const __post = (o) => window.chrome.webview.postMessage(JSON.stringify(Object.assign({ __id: '{{id}}' }, o)));
          const __get = async (url, headers) => {
            const r = await fetch(url, { credentials: 'include', cache: 'no-store', headers: Object.assign({ accept: 'application/json' }, headers || {}) });
            const text = await r.text();
            return { status: r.status, url: r.url, body: text.length > 500000 ? text.slice(0, 500000) : text };
          };
          try {
        {{body}}
          } catch (e) {
            __post({ error: String((e && e.message) || e), errorName: (e && e.name) || '' });
          }
        })();
        """;

    private async Task InitializeAsync(CoreWebView2Environment environment)
    {
        // 非表示のままでもコントロールのハンドルを作って初期化できるようにする。
        _ = Handle;
        _ = _webView.Handle;
        await _webView.EnsureCoreWebView2Async(environment);
        Core.Settings.IsStatusBarEnabled = false;
        Core.WebMessageReceived += OnWebMessageReceived;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || !string.Equals(source.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        if (message is null)
        {
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(message);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("__id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
            && idElement.GetString() is { } id
            && _pending.Remove(id, out var completion))
        {
            completion.TrySetResult(root);
        }
    }
}
