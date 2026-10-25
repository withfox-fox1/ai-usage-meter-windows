# AI Usage Meter for Windows

Claude と ChatGPT（Codex）の使用量（5 時間枠・週間枠）をタスクトレイに表示する常駐アプリです。
トレイアイコンの数字は 5 時間枠の使用率です。詳細は [docs/SPEC.md](docs/SPEC.md) を参照してください。

## 必要なもの
- Windows 10/11（WebView2 ランタイム。Windows 11 は標準搭載）
- .NET 8 SDK（ビルド時）/ .NET 8 Desktop Runtime（実行時。SDK に同梱）

## ビルド・テスト
```powershell
cd $HOME\ai-usage-meter-windows
dotnet build AIUsageMeter.sln
dotnet test tests\AIUsageMeter.Tests
```

## インストール（配置）
```powershell
dotnet publish src\AIUsageMeter\AIUsageMeter.csproj -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\AIUsageMeter"
Start-Process "$env:LOCALAPPDATA\Programs\AIUsageMeter\AIUsageMeter.exe"
```
初回起動時に「ログオン時に起動」が ON になります。各アイコンを右クリック →「ログイン…」でサインインしてください。

Windows 11 でアイコンが「^」の中に隠れる場合：
**設定 → 個人用設定 → タスクバー → その他のシステム トレイ アイコン** で「AI Usage Meter」（2 項目）をオンにします。

## 更新
```powershell
cd $HOME\ai-usage-meter-windows
Get-Process AIUsageMeter -ErrorAction SilentlyContinue | Stop-Process
dotnet publish src\AIUsageMeter\AIUsageMeter.csproj -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\AIUsageMeter"
Start-Process "$env:LOCALAPPDATA\Programs\AIUsageMeter\AIUsageMeter.exe"
```
設定・履歴・ログイン状態（`%LOCALAPPDATA%\AIUsageMeter\`）はそのまま引き継がれます。

## アンインストール
```powershell
Get-Process AIUsageMeter -ErrorAction SilentlyContinue | Stop-Process
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name AIUsageMeter -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Programs\AIUsageMeter"
# 設定・履歴・ログイン状態も消す場合
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\AIUsageMeter"
```

## 使い方
| 操作 | 内容 |
|---|---|
| 左クリック | 詳細パネル（5 時間・週間のバー、リセットまでの時間、直近 24 時間の推移） |
| 右クリック | 今すぐ更新／ログイン…／組織の選択（Claude）／このアイコンを隠す／ログオン時に起動／終了 |

設定ファイル `%LOCALAPPDATA%\AIUsageMeter\settings.json` の `RefreshIntervalMinutes`（既定 5）と
`RequestTimeoutSeconds`（既定 30）は手動で変更できます（アプリ再起動後に反映）。

## トラブルシュート
- ログ：`%LOCALAPPDATA%\AIUsageMeter\app.log`
- 「?」のまま：右クリック →「ログイン…」。Cloudflare の確認画面が出た場合もこのウィンドウで通過できます
- 表示確認：`AIUsageMeter.exe --open-panel claude`（起動中のものを終了してから実行）
