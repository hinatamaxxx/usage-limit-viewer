#!/bin/bash
# Usage Limit Viewer（Linux）を外す。--keep-data で取得した値（~/.local/share/usage-limit-viewer）を残す
systemctl --user disable --now usage-limit-viewer.service 2>/dev/null || true
rm -f ~/.config/systemd/user/usage-limit-viewer.service
systemctl --user daemon-reload
rm -rf ~/.local/lib/usage-limit-viewer
rm -rf ~/.local/share/win11-shell/tray-items/usage-limit-viewer
[ "$1" = "--keep-data" ] || rm -rf ~/.local/share/usage-limit-viewer
echo "Usage Limit Viewer を外しました"
