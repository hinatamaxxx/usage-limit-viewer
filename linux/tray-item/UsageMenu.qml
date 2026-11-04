import QtQuick
import org.hinatamaxxx.win11shell.fluent

// 残量表示の右クリックメニュー。先頭に今の残量（押せない行）、区切り、操作。
// 項目: { text, action, enabled, bold, checked }、区切りは { separator: true }。角丸 8、項目の高さ 32（余白 4,2）
FlyoutWindow {
    id: win
    property var items: []
    property real panelTop: 0
    property rect screenGeo
    readonly property int side: 16

    isMenu: true
    openDuration: Fluent.normal
    contentWidth: card.width + side * 2
    contentHeight: card.height + side + 12

    property real anchorX: 0
    function place() {
        x = Math.max(screenGeo.x, Math.min(screenGeo.x + screenGeo.width - contentWidth, Math.round(anchorX - contentWidth / 2)))
        y = Math.round(panelTop - contentHeight)
    }
    function showFor(from) {
        anchorX = from.mapToGlobal(from.width / 2, 0).x
        Qt.callLater(() => { place(); open() })
    }
    // 中身の大きさは窓を出してから決まるので、変わったら位置を合わせ直す
    onContentHeightChanged: if (visible) place()
    onContentWidthChanged: if (visible) place()

    MouseArea { anchors.fill: parent; onClicked: win.close() }
    Card {
        id: card
        x: win.side; y: win.side
        color: Fluent.flyoutSurface
        borderColor: Fluent.flyoutStroke
        width: Math.max(200, col.implicitWidth) + 2
        height: col.implicitHeight + 2
        MouseArea { anchors.fill: parent }
        Column {
            id: col
            width: card.width - 2
            topPadding: 4; bottomPadding: 4
            Repeater {
                model: win.items
                Item {
                    id: entry
                    required property var modelData
                    readonly property bool sep: !!modelData.separator
                    readonly property bool active: modelData.enabled !== false
                    width: col.width
                    implicitWidth: sep ? 0 : label.implicitWidth + 8 + 24 + 28
                    height: sep ? 9 : 36
                    Rectangle {
                        visible: entry.sep
                        x: 0; y: 4; width: parent.width; height: 1
                        color: Fluent.divider
                    }
                    Plate {
                        visible: !entry.sep
                        enabledInput: entry.active
                        colorDuration: 0
                        x: 4; y: 2; width: parent.width - 8; height: 32
                        onClicked: { win.close(); entry.modelData.action() }
                        Text {
                            visible: entry.modelData.checked === true
                            x: 10; anchors.verticalCenter: parent.verticalCenter
                            font.family: "Segoe Fluent Icons"; font.pixelSize: 12
                            color: Fluent.text
                            text: ""
                        }
                        FText {
                            id: label
                            x: 40; anchors.verticalCenter: parent.verticalCenter
                            text: entry.modelData.text || ""
                            font.weight: entry.modelData.bold ? Font.DemiBold : Font.Normal
                            color: entry.active ? Fluent.text : Fluent.textDisabled
                        }
                    }
                }
            }
        }
    }
}
