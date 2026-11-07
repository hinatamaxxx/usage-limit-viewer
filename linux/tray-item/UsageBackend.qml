import QtQuick
import org.hinatamaxxx.win11shell.fluent

// 常駐の ulv.py が書く state.json と表示の設定（~/.config/usage-limit-viewer/settings.json）を読む。
// ulv.py wait はどちらかが変わるまで待つので、終わるたびに出し直す（変わったときだけ動く）
Item {
    id: be

    readonly property string script: "\"$HOME/.local/lib/usage-limit-viewer/ulv.py\""
    readonly property var names: ["Codex", "Antigravity", "Claude Code", "Grok"]
    property var providers: ({})
    property bool refreshing: false
    property bool available: false          // ulv.py が入っていて、state.json がある
    property string mtime: ""
    property var settings: ({})
    readonly property bool fiveHourFirst: settings.fiveHourFirst !== false

    // 並び: 設定に無いものは、入っている（ログイン済みの）サービスを先に
    readonly property var order: {
        const saved = (settings.order || []).filter(n => names.indexOf(n) >= 0)
        const rest = names.filter(n => saved.indexOf(n) < 0)
        rest.sort((a, b) => (installed(b) - installed(a)) || (names.indexOf(a) - names.indexOf(b)))
        return saved.concat(rest)
    }
    function installed(n) { return providers[n] && providers[n].installed ? 1 : 0 }
    function reading(n) { return providers[n] || { quotas: [], updated: null, error: null, installed: false } }
    function set(key, value) {
        const s = Object.assign({}, settings); s[key] = value; settings = s      // すぐ表示に反映し、ulv.py に保存させる
        exec.run("python3 " + script + " set " + key + " " + exec.quote(JSON.stringify(value)))
    }
    function setOrder(list) { set("order", list) }

    Exec { id: waitExec }
    Exec { id: exec }
    function waitChange() {
        waitExec.run("python3 " + script + " wait " + exec.quote(mtime), out => {
            let s = null
            try { s = JSON.parse(out) } catch (e) {}
            if (!s) { retry.restart(); return }       // ulv.py が無い・壊れている: 30 秒後にもう一度
            available = !s.missing
            mtime = s.mtime || ""
            refreshing = !!s.refreshing
            if (JSON.stringify(s.providers) !== JSON.stringify(providers)) providers = s.providers || {}
            if (JSON.stringify(s.settings || {}) !== JSON.stringify(settings)) settings = s.settings || {}
            Qt.callLater(waitChange)
        })
    }
    Timer { id: retry; interval: 30000; onTriggered: be.waitChange() }
    function refresh() { refreshing = true; exec.run("python3 " + script + " refresh") }
    function login() { exec.run("python3 " + script + " login claude") }

    Component.onCompleted: waitChange()

    // ── 表示の文字 ─────────────────────────
    // 「5時間」と「週間」の両方がある（Claude Code、Codex の一部のプラン）ときは「20%(80%)」、それ以外は最も少ない枠
    function compact(r) {
        // モデルごとの枠（「週間 · Fable」など）は、そのモデルを使うときだけの上限なので、タスクバーには出さない（「使用状況」に出す）
        const all = r.quotas || []
        const q = all.some(x => x.label.indexOf(" · ") < 0) ? all.filter(x => x.label.indexOf(" · ") < 0) : all
        if (q.length === 0) return "—"
        const five = q.find(x => x.label === "5時間"), week = q.find(x => x.label === "週間")
        if (five && week) {
            const a = fiveHourFirst ? five : week, b = a === five ? week : five
            return Math.round(a.remaining) + "%(" + Math.round(b.remaining) + "%)"
        }
        return pct(Math.min.apply(null, q.map(x => x.remaining)))
    }
    function pct(v) { return (Math.round(v * 10) / 10) + "%" }
    // 取得できなくなった・10 分より古い値は灰色
    function stale(r) { return !!r.error || !r.updated || Date.now() / 1000 - r.updated > 600 }
    function shortName(n) { return n === "Claude Code" ? "Claude" : n }
    function resetText(t) {
        const d = new Date(t * 1000)
        return "リセット " + (d.getMonth() + 1) + "月" + d.getDate() + "日(" + "日月火水木金土"[d.getDay()] + ") "
             + d.getHours() + ":" + String(d.getMinutes()).padStart(2, "0")
    }
    function leftText(t) {
        const m = Math.max(0, Math.floor((t * 1000 - Date.now()) / 60000))
        const days = Math.floor(m / 1440), hours = Math.floor(m % 1440 / 60), mins = m % 60
        return "あと" + (days > 0 ? days + "日" + hours + "時間" : hours > 0 ? hours + "時間" + mins + "分" : mins + "分")
    }
    function updatedText(t) {
        const d = new Date(t * 1000)
        return "取得 " + (d.getMonth() + 1) + "/" + d.getDate() + " " + Qt.formatTime(d, "H:mm:ss")
    }
}
