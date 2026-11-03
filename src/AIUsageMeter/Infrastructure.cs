using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageMeter.Core;
using Microsoft.Win32;

namespace AIUsageMeter;

internal static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMeter");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");

    public static string LogFile => Path.Combine(DataDirectory, "app.log");

    public static string WebViewDirectory => Path.Combine(DataDirectory, "WebView2");
}

/// <summary>状態遷移とエラーのみを記録する簡易ログ。応答本文や秘密情報は書かない。</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }

                File.AppendAllText(AppPaths.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // ログ失敗でアプリを止めない。
        }
    }
}

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path)
        where T : new()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T()
                : new T();
        }
        catch (Exception ex)
        {
            Log.Error($"{Path.GetFileName(path)} の読み込みに失敗（初期値で続行）", ex);
            return new T();
        }
    }

    /// <summary>一時ファイルに書いてから置き換え、書き込み途中の破損を防ぐ。</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"{Path.GetFileName(path)} の保存に失敗", ex);
        }
    }
}

/// <summary>
/// ログオン時起動。Windows が順番に処理する Run キーでは起動が数分遅れることがあるため、
/// タスク スケジューラのログオン トリガーを使う。登録できない環境では Run キーで代替する。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageMeter";
    private const int SchtasksTimeoutMilliseconds = 15_000;

    /// <summary>タスクはコンピューター単位で登録されるため、ユーザーごとに名前を分ける。</summary>
    private static string TaskName => $"AIUsageMeter_{Environment.UserName}";

    private static string ExecutablePath => Environment.ProcessPath!;

    private static string RunKeyCommand => $"\"{ExecutablePath}\"";

    public static bool IsEnabled()
    {
        try
        {
            return ReadRunKey() is not null || TaskExists();
        }
        catch (Exception ex)
        {
            Log.Error("自動起動の状態確認に失敗", ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (TaskExists())
                {
                    RunSchtasks("/Delete", "/TN", TaskName, "/F");
                }

                DeleteRunKey();
            }
            else if (RegisterTask())
            {
                DeleteRunKey();
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(ValueName, RunKeyCommand);
                Log.Info("タスクを登録できなかったため Run キーで自動起動します");
            }
        }
        catch (Exception ex)
        {
            Log.Error("自動起動の設定に失敗", ex);
        }
    }

    /// <summary>
    /// 自動起動が有効なときだけ登録を最新にする：Run キーからタスクへの移行と、実行ファイルの場所の変更。
    /// 無効にされている場合は何もしない。
    /// </summary>
    public static void RefreshIfEnabled()
    {
        try
        {
            var runKey = ReadRunKey();
            var (exitCode, definition) = RunSchtasks("/Query", "/TN", TaskName, "/XML");
            var taskExists = exitCode == 0;
            if (!taskExists && runKey is null)
            {
                return;
            }

            // 文字コードの違いで一致しない場合は再登録になるだけで、害はない。
            var taskIsCurrent = taskExists
                && definition.Contains($"<Command>{SecurityElement.Escape(ExecutablePath)}</Command>", StringComparison.OrdinalIgnoreCase);
            if (taskIsCurrent && runKey is null)
            {
                return;
            }

            if (taskIsCurrent)
            {
                DeleteRunKey();
            }
            else
            {
                SetEnabled(true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("自動起動の更新に失敗", ex);
        }
    }

    private static bool TaskExists() => RunSchtasks("/Query", "/TN", TaskName).ExitCode == 0;

    private static bool RegisterTask()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"AIUsageMeter-{Guid.NewGuid():N}.xml");
        try
        {
            var user = WindowsIdentity.GetCurrent().Name;
            var xml = LogonTaskXml.Build(ExecutablePath, Path.GetDirectoryName(ExecutablePath)!, user);
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            var (exitCode, output) = RunSchtasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            if (exitCode != 0)
            {
                Log.Info($"タスクの登録に失敗 (schtasks 終了コード {exitCode}): {output.Trim()}");
            }

            return exitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Error("タスクの登録に失敗", ex);
            return false;
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(SchtasksTimeoutMilliseconds))
        {
            process.Kill();
            throw new TimeoutException($"schtasks が {SchtasksTimeoutMilliseconds / 1000} 秒以内に終了しませんでした");
        }

        return (process.ExitCode, output.Result + error.Result);
    }

    private static string? ReadRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    private static void DeleteRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

internal static class NativeMethods
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11 の角丸とダークモード枠を適用する（非対応 OS では何もしない）。</summary>
    public static void ApplyWindowStyle(IntPtr hwnd, bool dark)
    {
        try
        {
            var corner = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
        }
        catch (Exception)
        {
            // DWM が使えない環境では標準の見た目のまま。
        }
    }
}
