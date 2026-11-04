#!/bin/bash
# Usage Limit Viewer（Linux）を入れる: 取得の常駐（systemd のユーザーサービス）を置いて起動する。
# 表示は win11-shell の Win11 トレイ（https://github.com/hinatamaxxx/win11-shell）に、トレイの部品として足す。
set -e
cd "$(dirname "$0")"
command -v python3 >/dev/null || { echo "python3 が必要です"; exit 1; }
mkdir -p ~/.local/lib/usage-limit-viewer ~/.config/systemd/user
install -m 755 ulv.py ~/.local/lib/usage-limit-viewer/ulv.py
install -m 644 usage-limit-viewer.service ~/.config/systemd/user/usage-limit-viewer.service
# タスクバーの表示: win11-shell の Win11 トレイの部品（tray-items）として置く。トレイは 10 秒以内に見つけて出す
rm -rf ~/.local/share/win11-shell/tray-items/usage-limit-viewer
mkdir -p ~/.local/share/win11-shell/tray-items/usage-limit-viewer
cp tray-item/*.qml tray-item/item.json ~/.local/share/win11-shell/tray-items/usage-limit-viewer/
systemctl --user daemon-reload
systemctl --user enable usage-limit-viewer.service >/dev/null 2>&1
systemctl --user restart usage-limit-viewer.service
echo "Usage Limit Viewer を入れました（~/.local/lib/usage-limit-viewer、サービス usage-limit-viewer.service）"
