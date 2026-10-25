using AIUsageMeter.Core;

namespace AIUsageMeter;

/// <summary>%LOCALAPPDATA%\AIUsageMeter\settings.json に保存する設定。秘密情報は含めない。</summary>
internal sealed class AppSettings
{
    public string? ClaudeOrganizationId { get; set; }

    /// <summary>非表示にしたアイコン。両方は隠せないため最大 1 件。</summary>
    public List<ServiceKind> HiddenServices { get; set; } = new();

    /// <summary>初回起動時に自動起動を ON にしたかどうか。</summary>
    public bool AutoStartInitialized { get; set; }

    public int RefreshIntervalMinutes { get; set; } = 5;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>キーは「Claude.FiveHour」などの「サービス.枠」。</summary>
    public Dictionary<string, ThresholdState> NotificationStates { get; set; } = new();

    public static AppSettings Load()
    {
        var settings = JsonFile.Load<AppSettings>(AppPaths.SettingsFile);
        settings.Normalize();
        return settings;
    }

    public void Save() => JsonFile.Save(AppPaths.SettingsFile, this);

    private void Normalize()
    {
        HiddenServices = HiddenServices.Distinct().ToList();
        if (HiddenServices.Count > 1)
        {
            HiddenServices.Clear();
        }

        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes, 1, 60);
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 5, 120);
        NotificationStates ??= new();
    }
}
