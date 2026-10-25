using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>HKCU の Run キーによるログオン時起動。</summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageMeter";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("自動起動の設定に失敗", ex);
        }
    }

    /// <summary>有効なのに実行ファイルの場所が変わっていたら登録を更新する。</summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
        {
            SetEnabled(true);
        }
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
