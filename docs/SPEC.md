# AI Usage Meter for Windows — 仕様書

## 1. 目的
Claude（claude.ai）と ChatGPT（Codex の上限）の使用量を、Windows のタスクトレイに常時表示する。
macOS のメニューバーアプリ 2 本に相当する機能を、1 つのアプリ・トレイアイコン 2 つで提供する。

## 2. 技術構成
| 項目 | 内容 |
|---|---|
| 言語・FW | C# / .NET 8 / WinForms（`NotifyIcon`） |
| ブラウザ | Microsoft.Web.WebView2（NuGet 1.0.2903.40）。Windows 11 標準の WebView2 ランタイムを使用 |
| 権限 | 管理者権限不要（すべて HKCU と %LOCALAPPDATA% 内で完結） |
| 実行ファイル | `%LOCALAPPDATA%\Programs\AIUsageMeter\AIUsageMeter.exe`（framework-dependent, win-x64） |
| データ | `%LOCALAPPDATA%\AIUsageMeter\`（下表） |
| 二重起動防止 | 名前付き Mutex `Local\AIUsageMeter.SingleInstance` |

### データファイル
| パス | 内容 |
|---|---|
| `settings.json` | 選択中の Claude 組織、非表示アイコン、自動起動初期化済みフラグ、更新間隔（分）、タイムアウト（秒）、通知状態 |
| `history.json` | 取得ごとの 5 時間・週間の使用率（24 時間より古いものは削除） |
| `WebView2\` | WebView2 プロファイル（ログイン Cookie を含む。Claude・ChatGPT で共有） |
| `app.log` | 状態遷移・エラーのみ（1MB 超で `.old` にローテーション）。応答本文や秘密情報は書かない |

### プロジェクト構成
```
src/AIUsageMeter.Core   … UI 非依存ロジック（応答の読み取り、通知判定、履歴、表示文字列）
src/AIUsageMeter        … WinForms アプリ（トレイ、WebView2、パネル、メニュー）
tests/AIUsageMeter.Tests … xUnit（Core を対象）
```

## 3. 使用量の取得
**Cookie やトークンをアプリ側で取り出して HttpClient で呼ぶことはしない。**
サービスごとに非表示の WebView2 で対象サイトのページを開き、ページ内の `fetch` で API を呼び、
結果を `window.chrome.webview.postMessage` で C# に返す（`ExecuteScriptAsync` は Promise を待たないため）。

- 非表示時に開くページは同一オリジンの `robots.txt`（`https://claude.ai/robots.txt` 等）。
  SPA 本体を常駐させないことでメモリ・CPU 消費を抑える。Cookie は同一オリジンなのでそのまま送られる。
- ログインウィンドウ表示中はページを遷移させず、サービスのドメイン上にいるときだけ取得を試みる。
- C# 側は、メッセージの送信元ホストと要求 ID が一致するものだけを受け付ける。

### Claude
1. `GET /api/organizations` → `uuid` / `name` / `capabilities` / `rate_limit_tier`
   - 組織の選択順：保存済みの選択 → `chat` 機能を持つ組織 → 先頭
   - プラン名は `rate_limit_tier` / `capabilities` から推定（Max 20x / Max 5x / Max / Pro / Team / Enterprise / Free）
2. `GET /api/organizations/{uuid}/usage`
   - 5 時間：`limits[]` の `kind=="session"` の `percent`・`resets_at`。無い／壊れていれば `five_hour.utilization`・`five_hour.resets_at`
   - 週間：`kind=="weekly_all"`。無ければ `seven_day.utilization`・`seven_day.resets_at`
   - 項目ごとに独立して読み取り、片方が壊れていても取れた方を表示する。両方無ければエラー
- 401 / 403、または `/login` へリダイレクトされた場合は「要ログイン」

### ChatGPT（Codex の上限）
1. `GET /api/auth/session` → `accessToken`（無ければ「要ログイン」。5xx はエラー）
2. `GET https://chatgpt.com/backend-api/wham/usage`（`Authorization: Bearer <accessToken>`）
   - 5 時間：`rate_limit.primary_window` の `used_percent`・`limit_window_seconds`・`reset_at`（epoch 秒。無ければ `reset_after_seconds` から算出）
   - 週間：`rate_limit.secondary_window`
   - プラン名：`plan_type`
- アクセストークンはページ内でのみ扱い、C# 側には「有無」しか渡さない。保存もしない。

### 結果の扱い
| 結果 | 表示 |
|---|---|
| 成功 | 値を更新、履歴に追加、通知判定 |
| 要ログイン | アイコン「?」 |
| エラー（HTTP エラー・形式不正・タイムアウト 30 秒・通信エラー） | 前回値を薄く表示（前回値が無ければ薄い「-」） |
| 中断（遷移による中断・`AbortError`） | 表示を変えない。30 秒後に 1 回だけ再試行 |

## 4. 画面・動作
### トレイアイコン
- Claude：オレンジ（#D97757）の丸、ChatGPT：緑（#10A37F）の六角形
- 上に **5 時間の使用率**の数字を描画（文字をパス化して最大サイズに拡縮。100% は小さめの「100」）
- 50% 以上は背景を黄（#F5C218、文字は黒）、80% 以上は赤（#D93025、文字は白）
- 取得失敗時は前回値を薄く、要ログインは「?」、起動直後は「-」
- サイズは `SystemInformation.SmallIconSize`（高 DPI 対応）。ディスプレイ設定変更時に再描画
- ツールチップ例：`Claude 5h 42% / 週 18%（リセット 14:30）`（前回値表示中は末尾に「※前回値」）

### パネル（左クリック）
- 枠なし・最前面・アイコン付近に表示。外をクリック（非アクティブ化）または Esc で閉じる。表示中に同じアイコンをクリックすると閉じる
- 配色は Windows のアプリモード（ライト／ダーク）に追従。Windows 11 では角丸
- 内容：サービス名・プラン名／状態（要ログイン・失敗時）／5 時間と週間の横バー（%・リセットまでの残り時間とリセット時刻）／直近 24 時間の折れ線（5 時間＝実線・週間＝破線。16 分以上空いた点は結ばない）／最終更新時刻（Claude で組織が複数ある場合は組織名も）

### 右クリックメニュー
今すぐ更新／ログイン…／組織の選択（Claude のみ）／このアイコンを隠す／（もう片方が非表示のとき）「◯◯ のアイコンを表示」／ログオン時に起動／終了
- 両方のアイコンを隠すことはできない（もう片方が非表示のとき「隠す」は無効）
- ログイン：取得用の WebView2 をそのままウィンドウとして表示し、3 秒ごとに取得を試行。成功したら自動で閉じて反映

### 更新
- 5 分ごと（`settings.json` の `RefreshIntervalMinutes`）＋スリープ復帰時（`SystemEvents.PowerModeChanged`、10 秒待機後）＋手動
- 更新は同時に走らせない（全体のフラグ＋サービスごとのセマフォ）。1 回のタイムアウトは 30 秒

### 通知
- 5 時間・週間それぞれ、80%・95% を**下から超えた瞬間だけ** `NotifyIcon.ShowBalloonTip` で通知
- 上にいる間は再通知しない。`resets_at` が 60 秒を超えて変わったらリセットとみなし状態を戻す。しきい値を下回った場合もそのしきい値を解除する
- 初回観測時は状態の記録のみ（いつ超えたか分からないため通知しない）
- 状態は `settings.json` に保存し、再起動後も重複通知しない

### 自動起動
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` の値 `AIUsageMeter`
- 初回起動時に ON。メニューで切り替え可能。実行ファイルの場所が変わっていれば起動時に登録を更新

## 5. セキュリティ
- Cookie・トークン・パスワードをファイルやレジストリに保存しない（ログイン状態は WebView2 プロファイルのみ）
- ログに応答本文を書かない
- ページからのメッセージは送信元ホストと要求 ID で照合

## 6. 診断用オプション
- `AIUsageMeter.exe --open-panel claude|chatgpt`：起動後の初回取得が終わったらパネルを開く（表示確認用）

## 7. 完成条件
- `dotnet build` で警告・エラーなし
- xUnit テスト（応答の読み取り：正常・項目欠け・壊れた JSON、通知判定：超えた瞬間だけ・リセットで戻る）がすべて成功
- トレイに 2 つのアイコンが表示され、ログイン後に実際の使用率が表示される
- 自動起動がレジストリに登録される
