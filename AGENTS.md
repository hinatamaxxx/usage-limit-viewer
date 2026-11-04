# Usage Limit Viewer の導入手順（AI エージェント向け）

利用者に「https://github.com/hinatamaxxx/usage-limit-viewer を導入して」と頼まれたときの手順です。返答は利用者の言語で行ってください。

## 守ること

- UAC・パスワード・ログイン画面の操作は利用者本人に任せる（このアプリの導入では UAC は出ません）。
- 各 AI サービスの CLI へのログイン（`codex login`、`claude auth login`、`grok login` など）は利用者に実行してもらう。ログイン情報やトークンをチャット・ファイル・リポジトリに書き出さない。
- 利用者の Claude Code の設定（`~/.claude/settings.json`）は、利用者が希望したときだけ変更する（下の「任意の追加設定」）。

Linux（KDE Plasma）の場合は、この手順ではなく [linux/README.md](linux/README.md) に従う。

## 1. 前提の確認

- Windows 10 / 11 であること（`[Environment]::OSVersion`）。.NET Framework 4.8 は Windows に標準で入っているので、追加のインストールは不要。
- 表示したいサービスを利用者に確認する: Codex（デスクトップアプリまたは Codex CLI）、Antigravity CLI（agy）、Claude Code CLI、Grok Build CLI。少なくとも 1 つにログイン済みであること。

## 2. インストール

PowerShell（管理者でなくてよい）で実行する:

```powershell
irm https://raw.githubusercontent.com/hinatamaxxx/usage-limit-viewer/main/get.ps1 | iex
```

最新のリリースから `usage-limit-viewer.zip` を取得し、同じリリースの `usage-limit-viewer.zip.sha256` と照合してから、`%LOCALAPPDATA%\Programs\UsageLimitViewer` に入れます。新しくインストールしたときは、Windows へのサインイン時の自動起動がオンになります。最後に完了の画面が出ます。

ソースから入れる場合: リポジトリを取得し、`.\Install.ps1`（.NET SDK が必要。ビルドしてから同じ手順でインストール）。

## 3. 初期設定

- 通知領域に 2 行の残量が出ないときは、Windows の「設定」→「個人用設定」→「タスクバー」→「その他のシステム トレイ アイコン」で、Usage Limit Viewer の 4 つのアイコンをすべてオンにし、隣り合うように並べてもらう。
- 表示するサービスは、タスクバーの残量表示をクリックして開く「使用状況」で、カードをドラッグして選ぶ（1 番目が上段、2 番目が下段）。

## 4. 任意の追加設定（希望を確かめてから）

- **Claude Code の status line 連携**: Anthropic の使用量 API が使えない場合の予備。利用者が希望したら次を実行する（既存の status line があれば変更せず、その旨を伝える）:
  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\UsageLimitViewer\EnableClaudeCode.ps1"
  ```

## 5. 確認

- `Get-Process UsageLimitViewer` が動いていること。
- 取得の診断: `& "$env:LOCALAPPDATA\Programs\UsageLimitViewer\UsageLimitViewer.exe" --diagnose` を実行し、`%LOCALAPPDATA%\UsageLimitViewer\diagnostics.json` の各サービスの `Error` が空か、理由が分かる文になっていること。

## 6. トラブル対応

- インストールの失敗理由は `%TEMP%\UsageLimitViewer-Setup.log` にある。
- 「Codex をインストールしてログインしてください」: Codex デスクトップアプリか、`codex.exe`（npm 版も可）が見つからない。
- Claude が「再ログインしてください」: `claude auth login` を利用者に実行してもらう。
- Antigravity は agy のバージョンによって取得できないことがある（README 参照）。

## 7. アンインストール

Windows の「設定」→「アプリ」→「インストールされているアプリ」から「Usage Limit Viewer」をアンインストールする。確認なしで行う場合:

```powershell
& "$env:LOCALAPPDATA\Programs\UsageLimitViewer\UsageLimitViewer.exe" --uninstall --silent              # 設定とデータも削除
& "$env:LOCALAPPDATA\Programs\UsageLimitViewer\UsageLimitViewer.exe" --uninstall --silent --keep-settings  # 設定とデータは残す
```
