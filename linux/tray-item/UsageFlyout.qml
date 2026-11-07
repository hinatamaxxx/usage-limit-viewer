import QtQuick
import org.hinatamaxxx.win11shell.fluent

// 「使用状況」: 残量表示の上に出す。サービスごとのカード（残量のバー・リセット日時・取得時刻）。
// カードはドラッグで並べ替え、1 番目がタスクバーの上段、2 番目が下段。3 番目からはたたみ、押すと開く
FlyoutWindow {
    id: win
    property var usage                 // UsageBackend
    property real panelTop: 0
    property rect screenGeo
    readonly property int side: 16
    property var expanded: ({})        // たたんだカードのうち、開いたもの
    property real tick: Date.now()     // 「あと◯日」を 1 分ごとに出し直す

    openDuration: Fluent.normal
    closeDuration: 0            // 閉じるときはアニメーションなしで、すぐ消す
    contentWidth: card.width + side * 2
    contentHeight: card.height + side + 12

    function showFor(from) {
        if (shown) { close(); return }
        owner = from
        tick = Date.now()
        const p = from.mapToGlobal(from.width / 2, 0)
        Qt.callLater(() => {
            x = Math.max(screenGeo.x, Math.min(screenGeo.x + screenGeo.width - contentWidth, Math.round(p.x - contentWidth / 2)))
            y = Math.round(panelTop - contentHeight)
            open()
        })
    }
    // 中身の高さが変わったら（カードを開いた・値が届いた）下端をタスクバーに合わせ直す
    onContentHeightChanged: if (visible) y = Math.round(panelTop - contentHeight)
    Timer { interval: 60000; repeat: true; running: win.visible; onTriggered: win.tick = Date.now() }

    // ドラッグ中の並べ替え
    property string dragName: ""
    property int dragFrom: -1
    property int dragTo: -1
    property real dragHeight: 0

    MouseArea { anchors.fill: parent; onClicked: win.close() }
    Card {
        id: card
        x: win.side; y: win.side
        width: 320
        height: list.y + list.height + 8 + 2
        color: Fluent.flyoutSurface
        borderColor: Fluent.flyoutStroke
        MouseArea { anchors.fill: parent }

        FText {
            x: 16; y: 0; height: 48
            text: "使用状況"
            font.pixelSize: 16
            font.weight: Font.DemiBold
        }
        Row {
            anchors.right: parent.right; anchors.rightMargin: 4
            y: 4
            IconButton {
                glyph: ""
                onClicked: win.usage.refresh()
                enabledInput: !win.usage.refreshing
                opacity: win.usage.refreshing ? 0.5 : 1
            }
            IconButton { glyph: ""; onClicked: win.close() }
        }

        Column {
            id: list
            x: 8; y: 48
            width: parent.width - 16
            spacing: 8
            Repeater {
                model: win.usage.order
                Item {
                    id: slot
                    required property string modelData
                    required property int index
                    readonly property var r: win.usage.reading(modelData)
                    readonly property bool open: index < 2 || !!win.expanded[modelData]
                    readonly property bool dragging: win.dragName === modelData
                    width: list.width
                    height: box.height
                    z: dragging ? 10 : 0
                    // ドラッグ中、通り過ぎたカードは 1 枚ぶんずれる
                    readonly property real shift: win.dragName === "" || dragging ? 0
                        : (win.dragFrom < win.dragTo && index > win.dragFrom && index <= win.dragTo) ? -(win.dragHeight + 8)
                        : (win.dragTo < win.dragFrom && index >= win.dragTo && index < win.dragFrom) ? win.dragHeight + 8 : 0
                    property real dragY: 0
                    transform: Translate {
                        y: slot.dragging ? slot.dragY : slot.shift
                        Behavior on y { enabled: !slot.dragging; NumberAnimation { duration: Fluent.normal; easing.type: Easing.BezierSpline; easing.bezierCurve: Fluent.decelerate } }
                    }

                    Rectangle {
                        id: box
                        width: parent.width
                        height: body.height
                        radius: Fluent.radiusOverlay
                        color: Fluent.dark ? "#0DFFFFFF" : "#B3FFFFFF"
                        border.width: 1
                        border.color: slot.dragging ? Fluent.accent : Fluent.cardStroke

                        // カード全体: 押して動かすと並べ替え、動かさずに離すと（たたむカードは）開閉
                        MouseArea {
                            anchors.fill: parent
                            preventStealing: true
                            cursorShape: slot.dragging ? Qt.ClosedHandCursor : Qt.ArrowCursor
                            property real startY: 0
                            onPressed: mouse => startY = mapToItem(list, mouse.x, mouse.y).y
                            onPositionChanged: mouse => {
                                const dy = mapToItem(list, mouse.x, mouse.y).y - startY
                                if (win.dragName === "" && Math.abs(dy) > 4) {
                                    win.dragName = slot.modelData; win.dragFrom = slot.index; win.dragTo = slot.index
                                    win.dragHeight = slot.height
                                }
                                if (!slot.dragging) return
                                slot.dragY = dy
                                // 動かしたカードの中央より上にある、ほかのカードの数が新しい位置
                                const center = slot.y + dy + slot.height / 2
                                let to = 0
                                for (let i = 0; i < list.children.length; i++) {
                                    const c = list.children[i]
                                    if (c === slot || c.modelData === undefined) continue
                                    if (c.y + c.height / 2 < center) to++
                                }
                                win.dragTo = to
                            }
                            onReleased: {
                                if (slot.dragging) {
                                    const list2 = win.usage.order.filter(n => n !== slot.modelData)
                                    list2.splice(win.dragTo, 0, slot.modelData)
                                    slot.dragY = 0
                                    win.dragName = ""; win.dragFrom = -1; win.dragTo = -1
                                    win.usage.setOrder(list2)
                                } else if (slot.index >= 2) {
                                    const e = Object.assign({}, win.expanded)
                                    e[slot.modelData] = !e[slot.modelData]
                                    win.expanded = e
                                }
                            }
                            onCanceled: { slot.dragY = 0; win.dragName = "" }
                        }

                        Column {
                            id: body
                            width: parent.width
                            topPadding: 12; bottomPadding: slot.open ? 12 : 12
                            spacing: 8

                            // 見出し: たたむカードは「>」、名前、上段・下段のタグ、取得元（たたんだときは残量）
                            Item {
                                x: 16; width: parent.width - 32; height: 24
                                Text {
                                    id: chev
                                    visible: slot.index >= 2
                                    anchors.verticalCenter: parent.verticalCenter
                                    font.family: "Segoe Fluent Icons"; font.pixelSize: 12
                                    color: Fluent.textSecondary
                                    text: slot.open ? "" : ""
                                    width: visible ? 20 : 0
                                }
                                FText {
                                    id: title
                                    anchors.left: chev.right
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: slot.modelData
                                    font.weight: Font.DemiBold
                                }
                                Rectangle {
                                    visible: slot.index < 2
                                    anchors.left: title.right; anchors.leftMargin: 8
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: tag.implicitWidth + 12; height: 20; radius: 4
                                    color: Qt.rgba(Fluent.accent.r, Fluent.accent.g, Fluent.accent.b, 0.18)
                                    FText {
                                        id: tag
                                        anchors.centerIn: parent
                                        text: slot.index === 0 ? "上段" : "下段"
                                        font.pixelSize: Fluent.caption
                                        color: Fluent.accentText
                                    }
                                }
                                FText {
                                    anchors.right: parent.right
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: Math.min(implicitWidth, parent.width - title.x - title.width - 70)
                                    horizontalAlignment: Text.AlignRight
                                    text: slot.open ? (slot.r.source || "") : win.usage.compact(slot.r)
                                    font.pixelSize: slot.open ? Fluent.caption : Fluent.body
                                    font.weight: slot.open ? Font.Normal : Font.DemiBold
                                    color: slot.open ? Fluent.textSecondary
                                         : win.usage.stale(slot.r) ? Fluent.textSecondary : Fluent.text
                                }
                            }

                            // 取得できない理由（Claude のログイン切れはログインのボタン）
                            Item {
                                visible: slot.open && !!slot.r.error
                                x: 16; width: parent.width - 32
                                height: visible ? Math.max(err.implicitHeight, loginButton.visible ? 32 : 0) : 0
                                FText {
                                    id: err
                                    width: parent.width - (loginButton.visible ? loginButton.width + 8 : 0)
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: slot.r.error || ""
                                    wrapMode: Text.WordWrap
                                    elide: Text.ElideNone
                                    font.pixelSize: Fluent.caption
                                    color: slot.r.needsLogin ? Fluent.attention : Fluent.textSecondary
                                }
                                FButton {
                                    id: loginButton
                                    visible: !!slot.r.needsLogin
                                    anchors.right: parent.right
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: "ログイン"
                                    fontSize: Fluent.caption
                                    onClicked: win.usage.login()
                                }
                            }

                            // 枠ごとの残量
                            Repeater {
                                model: slot.open ? (slot.r.quotas || []) : []
                                Column {
                                    id: q
                                    required property var modelData
                                    x: 16; width: body.width - 32
                                    spacing: 4
                                    Item {
                                        width: parent.width; height: 20
                                        FText { anchors.verticalCenter: parent.verticalCenter; text: q.modelData.label }
                                        FText {
                                            anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter
                                            text: win.usage.pct(q.modelData.remaining)
                                            font.weight: Font.DemiBold
                                            color: win.usage.stale(slot.r) ? Fluent.textSecondary : Fluent.text
                                        }
                                    }
                                    Rectangle {
                                        width: parent.width; height: 4; radius: 2
                                        color: Fluent.dark ? "#1FFFFFFF" : "#1A000000"
                                        Rectangle {
                                            width: parent.width * Math.max(0, Math.min(100, q.modelData.remaining)) / 100
                                            height: parent.height; radius: 2
                                            color: win.usage.stale(slot.r) ? Fluent.textTertiary
                                                 : q.modelData.remaining <= 10 ? Fluent.attention : Fluent.accent
                                        }
                                    }
                                    Item {
                                        visible: !!q.modelData.reset
                                        width: parent.width; height: visible ? 18 : 0
                                        FText {
                                            anchors.verticalCenter: parent.verticalCenter
                                            text: q.modelData.reset ? win.usage.resetText(q.modelData.reset) : ""
                                            font.pixelSize: Fluent.caption; color: Fluent.textSecondary
                                        }
                                        FText {
                                            anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter
                                            text: { win.tick; return q.modelData.reset ? win.usage.leftText(q.modelData.reset) : "" }
                                            font.pixelSize: Fluent.caption; color: Fluent.textSecondary
                                        }
                                    }
                                }
                            }

                            FText {
                                visible: slot.open && !!slot.r.updated
                                x: 16
                                text: slot.r.updated ? win.usage.updatedText(slot.r.updated) : ""
                                font.pixelSize: Fluent.caption
                                color: Fluent.textTertiary
                            }
                        }
                    }
                }
            }
        }
    }
}
