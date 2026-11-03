using System.Security;

namespace AIUsageMeter.Core;

/// <summary>ログオン時にアプリを起動するタスク スケジューラ定義（schtasks /xml 用）を生成する。</summary>
public static class LogonTaskXml
{
    /// <summary>
    /// 既定値のままでは常駐アプリとして問題になる設定を明示する：
    /// 実行時間制限（既定 72 時間で強制終了）、バッテリー駆動時の起動抑止・停止、低い優先度（既定 7）。
    /// </summary>
    public static string Build(string executablePath, string workingDirectory, string userId)
    {
        var path = SecurityElement.Escape(executablePath);
        var directory = SecurityElement.Escape(workingDirectory);
        var user = SecurityElement.Escape(userId);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>AI Usage Meter をログオン直後に起動します。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{path}</Command>
                  <WorkingDirectory>{directory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
