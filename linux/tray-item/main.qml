import QtQuick
import org.hinatamaxxx.win11shell.fluent

// Win11 トレイの部品（win11-shell の docs/tray-items.md）: AI の利用枠の残量を 2 行で出す。
// 幅 128（Windows 版の 4 つ分のトレイの枠）。左に名前、右に残量、12px。上段・下段は「使用状況」のカードの 1・2 番目
Item {
    id: part
    property var tray                    // トレイが入れる（panelTop・screenGeo）
    property Item slot: null             // 今付いている枠
    implicitWidth: 128
    readonly property bool available: backend.available
    readonly property bool active: flyout.shown || menu.shown
    readonly property string tooltip: ""

    function clicked(button) {
        if (!slot) return
        if (button === Qt.RightButton) showMenu()
        else if (button === Qt.LeftButton) flyout.showFor(slot)
    }

    UsageBackend { id: backend }

    // Windows の時計と同じ基準線（行の高さ 16、2 行を中央から 4/3 上に）
    readonly property real firstLine: (height - 32) / 2 - 4 / 3
    readonly property bool pressed: !!slot && slot.pressed
    Repeater {
        model: [0, 1]
        Item {
            id: line
            required property int modelData
            readonly property string name: backend.order[modelData] || ""
            readonly property var reading: backend.reading(name)
            x: 6; width: part.width - 12
            y: part.firstLine + modelData * 16; height: 16
            FText {
                id: label
                anchors.verticalCenter: parent.verticalCenter
                font.pixelSize: Fluent.caption
                color: part.pressed ? Fluent.textSecondary : Fluent.text
                text: backend.shortName(line.name)
                elide: Text.ElideNone
            }
            // 名前と重なるときは、残量の字を 1px ずつ 9 まで小さくする
            FText {
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                text: backend.compact(line.reading)
                elide: Text.ElideNone
                color: backend.stale(line.reading) ? (Fluent.dark ? "#BEC3CF" : Fluent.textSecondary)
                     : part.pressed ? Fluent.textSecondary : Fluent.text
                font.pixelSize: Fluent.caption
                onTextChanged: fit()
                Component.onCompleted: fit()
                function fit() {
                    let size = Fluent.caption
                    font.pixelSize = size
                    while (size > 9 && label.implicitWidth + 4 + implicitWidth > line.width) font.pixelSize = --size
                }
            }
        }
    }

    UsageFlyout {
        id: flyout
        usage: backend
        panelTop: part.tray ? part.tray.panelTop : 0
        screenGeo: part.tray ? part.tray.screenGeo : Qt.rect(0, 0, 1920, 1080)
    }
    UsageMenu {
        id: menu
        panelTop: part.tray ? part.tray.panelTop : 0
        screenGeo: part.tray ? part.tray.screenGeo : Qt.rect(0, 0, 1920, 1080)
    }
    function showMenu() {
        const top = backend.order[0], bottom = backend.order[1]
        const items = [
            { text: "Usage Limit Viewer", enabled: false },
            { text: backend.shortName(top) + " " + backend.compact(backend.reading(top)) + " · "
                  + backend.shortName(bottom) + " " + backend.compact(backend.reading(bottom)), enabled: false },
            { separator: true },
            { text: "使用状況を開く", bold: true, action: () => flyout.showFor(part.slot) },
            { text: "残量を今すぐ取得", action: () => backend.refresh() }
        ]
        if (backend.names.some(n => backend.reading(n).needsLogin))
            items.push({ text: "Claude Code にログイン", action: () => backend.login() })
        items.push({ separator: true },
                   { text: "5時間の残量を先に表示", checked: backend.fiveHourFirst,
                     action: () => backend.set("fiveHourFirst", !backend.fiveHourFirst) })
        menu.items = items
        menu.showFor(part.slot)
    }
}
