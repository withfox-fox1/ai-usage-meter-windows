# 進捗

## 完了（2026-10-25）
- Core：Claude / ChatGPT 応答の読み取り、通知判定、履歴、表示文字列
- アプリ：トレイアイコン 2 つ、非表示 WebView2 によるページ内 fetch 取得、ログインウィンドウ（成功で自動クローズ）、
  パネル（ライト/ダーク追従）、右クリックメニュー、5 分ごと・スリープ復帰・手動更新、通知、自動起動、Mutex
- `dotnet build`：警告 0・エラー 0（WebView2 パッケージ由来の MSB3277 は WPF 参照の除外で解消）
- xUnit：56 件すべて成功
- 実機確認：トレイに 2 アイコン表示、ログイン後に実数値（Claude 5h 17% / 週 19%、ChatGPT 5h 0% / 週 0%）、
  両パネルの表示、Run キー登録、二重起動防止

## 配布（2026-10-25）
- GitHub 公開リポジトリ：https://github.com/withfox-fox1/ai-usage-meter-windows（`main`。匿名 clone できることを確認）
- commit 作成者は claude-config と同じ `遠藤聖弥 <agents@FOX1M.local>`（リポジトリローカル設定。会社メールを公開しないため）
- 別 PC 用プロンプト：`SETUP_PROMPT_WINDOWS.md`（前提ツール → claude-config → 本アプリの導入・ログイン確認まで。質問・承認なしで完走する想定）
- 開発 PC では GitHub の資格情報を Git Credential Manager に保存済み（以後の push は認証不要）

## 開発 PC の環境メモ
- claude-config：`$HOME\claude-config` を clone、`CLAUDE.md` / `AGENTS.md` をコピー配置。更新は `$HOME\claude-config\update-windows.ps1`（ローカル専用・`.git\info\exclude` 済み）
- トレイ表示：`HKCU:\Control Panel\NotifyIconSettings` の本アプリ 2 エントリを `IsPromoted=1` に設定済み

## 未確認（実機で起きるのを待つ必要があるもの）
- `SETUP_PROMPT_WINDOWS.md` を別の Windows PC で実行した結果（まだ一度も試していない）
- 80% / 95% の通知バルーンの実表示（判定ロジックは単体テスト済み）
- スリープ復帰時の更新
- 右クリックメニューのうち「組織の選択」「このアイコンを隠す／表示」「ログオン時に起動」の切替、「終了」
- ダークモード時のパネル表示、高 DPI（125% 以上）での表示
- 実際にログオンしたときの自動起動

## 既知の注意点
- ChatGPT のログイン途中（ページ遷移中）に 1 回だけタイムアウト（30 秒）が記録された。ログイン完了後は正常。
  ログインウィンドウ表示中のポーリングはページ内 fetch のため、遷移中は応答が返らないことがある
- 初回観測時にすでに 80% 以上でも通知しない（いつ超えたか分からないため）。仕様どおり
- API（claude.ai / chatgpt.com の非公開エンドポイント）の形式が変わると取得できなくなる。その場合は
  `src/AIUsageMeter.Core/ClaudeParser.cs` / `ChatGptParser.cs` を修正する

## 次の作業候補（未着手・要望があれば）
- 設定画面（更新間隔・しきい値）
