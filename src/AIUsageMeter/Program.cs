using AIUsageMeter.Core;

namespace AIUsageMeter;

internal static class Program
{
    private const string MutexName = @"Local\AIUsageMeter.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI スレッドで未処理の例外", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("未処理の例外", e.ExceptionObject as Exception);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Log.Info($"起動 v{Application.ProductVersion}");
        using var context = new TrayApplicationContext(ParseOpenPanel(args));
        Application.Run(context);
        Log.Info("終了");
    }

    /// <summary>
    /// 診断用：<c>--open-panel claude|chatgpt</c> で起動直後（初回取得後）にパネルを開く。
    /// </summary>
    private static ServiceKind? ParseOpenPanel(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--open-panel", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return args[index + 1].ToLowerInvariant() switch
        {
            "claude" => ServiceKind.Claude,
            "chatgpt" => ServiceKind.ChatGpt,
            _ => null,
        };
    }
}
