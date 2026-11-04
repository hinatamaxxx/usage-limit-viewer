# Usage Limit Viewer for Linux（KDE Plasma 6）

Windows 版と同じサービス（Codex、Antigravity CLI、Claude Code、Grok Build CLI）の残量を、KDE Plasma のタスクバーに 2 行で表示します。

- **取得**: `ulv.py`。systemd のユーザーサービス `usage-limit-viewer.service` として常駐し、結果を `~/.local/share/usage-limit-viewer/state.json` に書きます。Codex と Claude は 1 分ごと、agy と grok は 3 分ごとに取得します（Windows 版と同じ）。
- **表示**: [win11-shell](https://github.com/hinatamaxxx/win11-shell) の「Win11 トレイ」が、「^」の右に 2 行で表示します。ulv.py が入っていないときは表示しません。

## 入れ方

```bash
git clone https://github.com/hinatamaxxx/usage-limit-viewer.git
usage-limit-viewer/linux/install.sh
```

表示には、win11-shell の Win11 トレイ（この変更を含む版）がパネルにあることが必要です。

## 使い方

| 操作 | 動作 |
| --- | --- |
| 左クリック | 「使用状況」を開きます。カードをドラッグして並べ替えると、1 番目が上段、2 番目が下段になります。 |
| 右クリック | 今の残量、「使用状況を開く」「残量を今すぐ取得」「5時間の残量を先に表示」。Claude のログインが切れているときは「Claude Code にログイン」も出ます。 |

並び順と表示順は、Win11 トレイの設定（`usageOrder`、`usageFiveHourFirst`）に保存されます。

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
