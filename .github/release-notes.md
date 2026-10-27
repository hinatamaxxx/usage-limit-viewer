## Usage Limit Viewer

Windows 11 の通知領域（タスクバー）に、AI サービスの利用枠の残り割合を 2 行で表示するアプリです。Codex、Antigravity CLI、Claude Code、Grok Build CLI から、表示する 2 つを選べます。

### 入れ方

PowerShell で次の 1 行を実行します（管理者として開く必要はありません）。最新版をダウンロードし、SHA-256 を照合してからインストールします。

```powershell
irm https://raw.githubusercontent.com/hinatamaxxx/usage-limit-viewer/main/get.ps1 | iex
```

AI エージェント（Claude Code、Codex など）に「https://github.com/hinatamaxxx/usage-limit-viewer を導入して。」と頼むこともできます。

### 主な機能

- タスクバーに 2 つのサービスの残量を表示し、クリックで「使用状況」（リセット日時、残量のバー）を開く
- 「使用状況」のカードをドラッグ＆ドロップして、タスクバーに出すサービスを選ぶ
- Claude Code のログインが切れたら、通知と「使用状況」でお知らせし、アプリからログイン画面を開ける
- 新しい版は自動で更新（毎日確認。右クリックメニューの「アップデート」から切り替え）
- 日本語・英語

### 添付ファイル

- `usage-limit-viewer.zip`: アプリ本体（約 1.4MB。Windows 標準の .NET Framework 4.8 で動作）
- `usage-limit-viewer.zip.sha256`: ZIP の SHA-256。インストールと自動更新は、これと一致したものだけを使います

非公式・未署名のアプリです。現状のまま公開しており、不具合の報告・質問・要望は受け付けていません。

---

## English

Usage Limit Viewer shows how much of your AI usage limits is left (Codex, Antigravity CLI, Claude Code, Grok Build CLI) as two lines in the Windows 11 taskbar. Install with `irm https://raw.githubusercontent.com/hinatamaxxx/usage-limit-viewer/main/get.ps1 | iex`; it updates itself. Unofficial and unsigned, provided as is; reports and requests are not accepted.
