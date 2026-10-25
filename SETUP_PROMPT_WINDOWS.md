# Windows PC セットアップ用プロンプト

新しい Windows PC で Claude Code を起動し、下の「ここから」〜「ここまで」をそのまま貼り付けて送信する。
既にセットアップ済みの PC で送ると「更新」として動く（設定・ログイン状態は保持される）。

---ここから---

このWindows PCに、次の3つを導入してください。報告と会話は日本語で、親しみやすいギャル語＋絵文字でお願いします。
内容は確定済みなので、質問や計画の承認は不要です。最後まで自律的に進めてください。管理者権限が必要な操作は使わないでください（winget がUACを求めたら私が許可します）。

# 0. 前提ツール（PowerShell）
- `git` / `dotnet`（.NET 8 SDK）が無ければ入れる：
  - `winget install --id Git.Git -e --source winget --accept-source-agreements --accept-package-agreements`
  - `winget install --id Microsoft.DotNet.SDK.8 -e --source winget --accept-source-agreements --accept-package-agreements`
- 入れた直後は PATH を再読み込みする：`$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- `git --version` / `dotnet --list-sdks` で確認（8.0.x のSDKがあればOK）

# 1. Claude Code の共通指示
1. `git clone https://github.com/withfox-fox1/claude-config.git "$HOME\claude-config"`（既にあれば `git -C "$HOME\claude-config" pull --ff-only`）
2. `install.sh` はMac用なので使わない。代わりに：
   - `$HOME\.claude\CLAUDE.md` と `$HOME\AGENTS.md` が既にあり、リポジトリの内容と異なる場合だけ `<名前>.bak-<yyyyMMdd-HHmmss>` に退避
   - リポジトリの `CLAUDE.md` → `$HOME\.claude\CLAUDE.md`、`AGENTS.md` → `$HOME\AGENTS.md` へコピー（シンボリックリンクは使わない）
3. 更新用スクリプト `$HOME\claude-config\update-windows.ps1` を次の内容で作る（このPCローカルのファイル。リポジトリにはcommitしない。`$HOME\claude-config\.git\info\exclude` に `update-windows.ps1` を追記して誤commitを防ぐ）：
```powershell
# Windows 用: claude-config を更新し、共通指示ファイルをコピー配置する（退避なし）。
# このファイルはこの PC ローカル専用。リポジトリには commit しない。
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

git -C $repo pull --ff-only
if ($LASTEXITCODE -ne 0) { throw "git pull に失敗しました (exit $LASTEXITCODE)" }

$targets = @(
    @{ Src = Join-Path $repo 'CLAUDE.md'; Dest = Join-Path $HOME '.claude\CLAUDE.md' },
    @{ Src = Join-Path $repo 'AGENTS.md'; Dest = Join-Path $HOME 'AGENTS.md' }
)

foreach ($t in $targets) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $t.Dest) | Out-Null
    Copy-Item -LiteralPath $t.Src -Destination $t.Dest -Force
    $same = (Get-FileHash $t.Src).Hash -eq (Get-FileHash $t.Dest).Hash
    "{0} -> {1} : {2}" -f $t.Src, $t.Dest, $(if ($same) { 'OK' } else { 'MISMATCH' })
}
```
4. `Get-FileHash` でコピー元と先が一致することを確認して報告
5. 新しい指示は次回の Claude Code 起動から有効になることを伝える

# 2. AI使用量メーター（トレイ常駐アプリ）
ソースは https://github.com/withfox-fox1/ai-usage-meter-windows 。コードは書き換えず、そのままビルド・配置する。
1. `git clone https://github.com/withfox-fox1/ai-usage-meter-windows.git "$HOME\ai-usage-meter-windows"`（既にあれば `git -C "$HOME\ai-usage-meter-windows" pull --ff-only`）
2. リポジトリ直下で `dotnet build AIUsageMeter.sln` → 警告0・エラー0を確認。`dotnet test tests\AIUsageMeter.Tests` → 全件成功を確認（実際の出力を報告）
3. 起動中なら止める：`Get-Process AIUsageMeter -ErrorAction SilentlyContinue | Stop-Process`
4. 配置：`dotnet publish src\AIUsageMeter\AIUsageMeter.csproj -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\AIUsageMeter"`
5. 起動：`Start-Process "$env:LOCALAPPDATA\Programs\AIUsageMeter\AIUsageMeter.exe"`。数秒後に `%LOCALAPPDATA%\AIUsageMeter\app.log` を見て「起動」と各サービスの状態が出ていることを確認
6. 自動起動：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の値 `AIUsageMeter` が今の exe パスになっていることを確認
7. トレイ表示：Windows 11 では新しいアイコンが「^」の中に隠れる。`HKCU:\Control Panel\NotifyIconSettings` の各サブキーのうち、`ExecutablePath` が `...\Programs\AIUsageMeter\AIUsageMeter.exe` のもの（2つ）だけ `IsPromoted` を DWORD 1 にする（他アプリのキーは触らない）。できたら画面右下をスクリーンショットで確認し、オレンジの丸（Claude）と緑の六角形（ChatGPT）が出ていることを確認。うまくいかなければ「設定 → 個人用設定 → タスクバー → その他のシステムトレイアイコン」で表示ONにする方法を私に伝える
8. ログイン：`app.log` で `LoginRequired` になっているサービスがあれば、私に「アイコンを右クリック →『ログイン…』でログインして」と頼んで待つ。ログイン後、`app.log` に `Claude: Ok` / `ChatGPT: Ok` が出ていること、`history.json` に数値が入っていることを確認
9. 確認できなかった項目は「未確認」と明記する

最後に、導入結果・テスト結果・未確認事項・私がやること（あれば）を短くまとめて報告してください。

---ここまで---
