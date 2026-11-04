# Usage Limit Viewer for Linux（KDE Plasma 6）

Windows 版と同じサービス（Codex、Antigravity CLI、Claude Code、Grok Build CLI）の残量を、KDE Plasma のタスクバーに 2 行で表示します。

- **取得**: `ulv.py`。systemd のユーザーサービス `usage-limit-viewer.service` として常駐し、結果を `~/.local/share/usage-limit-viewer/state.json` に書きます。Codex と Claude は 1 分ごと、agy と grok は 3 分ごとに取得します（Windows 版と同じ）。
- Codex は、PATH の `codex`（Codex CLI）を使います。見つからないときは、ChatGPT デスクトップアプリに同梱の Codex（`/usr/lib/chatgpt/resources/codex`）を使います。
- **表示**: `tray-item/`。[win11-shell](https://github.com/hinatamaxxx/win11-shell) の Win11 トレイに、[トレイの部品](https://github.com/hinatamaxxx/win11-shell/blob/main/docs/tray-items.md)として足します（`~/.local/share/win11-shell/tray-items/usage-limit-viewer/`）。幅 128 の項目で、最初は「^」のすぐ右に出ます。ほかのアイコンと同じく、掴んで並べ替えられます。

## 入れ方

```bash
git clone https://github.com/hinatamaxxx/usage-limit-viewer.git
usage-limit-viewer/linux/install.sh
```

表示には、トレイの部品に対応した win11-shell の Win11 トレイがパネルにあることが必要です。

## Claude Code の statusLine 連携（任意）

Claude の残量を取る API は、問い合わせが多いと 429（問い合わせ過多）を返します。Claude Code（ターミナルの CLI）の statusLine につなぐと、Claude Code が描画のたびに渡す残量を受け取ります。届いた値が 5 分以内なら、API には問い合わせません。

```bash
python3 ~/.local/lib/usage-limit-viewer/ulv.py statusline-enable
```

`~/.claude/settings.json` に statusLine を足します（元の設定は `settings.json.usage-limit-viewer.bak` に残します）。すでに別の statusLine があるときは変更しません。ステータス行には「残り 5時間 ◯% · 週間 ◯%」と表示されます。デスクトップアプリの Claude では statusLine が呼ばれないため、その間は API から取得します（約 3 分おき。429 のときは 2・4・8 分…最大 30 分待ちます）。

## 使い方

| 操作 | 動作 |
| --- | --- |
| 左クリック | 「使用状況」を開きます。カードをドラッグして並べ替えると、1 番目が上段、2 番目が下段になります。 |
| 右クリック | 今の残量、「使用状況を開く」「残量を今すぐ取得」「5時間の残量を先に表示」。Claude のログインが切れているときは「Claude Code にログイン」も出ます。 |

カードの並びと「5時間の残量を先に表示」は `~/.config/usage-limit-viewer/settings.json` に保存されます。

## 確認とトラブル対応

```bash
systemctl --user status usage-limit-viewer
python3 ~/.local/lib/usage-limit-viewer/ulv.py diagnose
```

`diagnose` は各サービスを 1 回ずつ読み、結果か、取得できない理由を表示します。ログイン情報やトークンは表示しません。

Claude のログイン情報（`~/.claude/.credentials.json`）の期限が切れているときは、Claude Code CLI と同じ方法で更新し、同じファイルに書き戻します。CLI はそのまま使えます。

## 外し方

```bash
usage-limit-viewer/linux/uninstall.sh            # 取得した値も削除
usage-limit-viewer/linux/uninstall.sh --keep-data
```
