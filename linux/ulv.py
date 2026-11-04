#!/usr/bin/env python3
"""Usage Limit Viewer for Linux: reads the AI usage limits and leaves them for the Plasma widget.

  ulv.py daemon        poll the services and write state.json (run by the systemd user service)
  ulv.py wait <mtime>  print state.json once its mtime differs from <mtime> (the widget calls this in a loop)
  ulv.py refresh       ask the running daemon to read every service now
  ulv.py set <key> <json>  change a display setting (order: list of services, fiveHourFirst: bool)
  ulv.py login claude  open "claude auth login" in a terminal
  ulv.py diagnose      read every service once and print the result

The same services and the same reading rules as the Windows app (Codex, Antigravity CLI, Claude Code, Grok Build CLI).
Sign-in tokens are only sent to the service they belong to and are never written to state.json or the log.
"""
import fcntl
import json
import os
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone

VERSION = "2.1.0"
HOME = os.path.expanduser("~")
DATA = os.path.join(os.environ.get("XDG_DATA_HOME") or os.path.join(HOME, ".local", "share"), "usage-limit-viewer")
STATE = os.path.join(DATA, "state.json")
PIDFILE = os.path.join(DATA, "daemon.pid")
LOCKFILE = os.path.join(DATA, "daemon.lock")
CLAUDE_CACHE = os.path.join(DATA, "claude-usage-cache.json")
# Display settings (card order, "5-hour%(weekly%)" or the reverse); the taskbar part reads them with the state.
SETTINGS = os.path.join(os.environ.get("XDG_CONFIG_HOME") or os.path.join(HOME, ".config"), "usage-limit-viewer", "settings.json")
SETTING_KEYS = {"order": list, "fiveHourFirst": bool}
PROVIDERS = ["Codex", "Antigravity", "Claude Code", "Grok"]
# Codex and Claude every minute; agy and grok start a CLI, so every 3 minutes.
INTERVALS = {"Codex": 60, "Claude Code": 60, "Antigravity": 180, "Grok": 180}


class LoginRequired(Exception):
    pass


def now():
    return time.time()


def which(name, extra=()):
    for p in extra:
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p
    return shutil.which(name, path=os.environ.get("PATH", "") + os.pathsep + os.path.join(HOME, ".local", "bin"))


def iso_epoch(text):
    if not isinstance(text, str) or not text:
        return None
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return None


def clamp(v):
    return max(0.0, min(100.0, v))


# ── JSON-RPC over a CLI's stdio ────────────────────────────

class Rpc:
    def __init__(self, argv, cwd=None):
        self.p = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  cwd=cwd, start_new_session=True)
        self.buf = b""

    def send(self, obj):
        self.p.stdin.write((json.dumps(obj) + "\n").encode())
        self.p.stdin.flush()

    def read(self, rid, deadline):
        fd = self.p.stdout.fileno()
        while True:
            while b"\n" in self.buf:
                line, self.buf = self.buf.split(b"\n", 1)
                line = line.strip()
                if not line.startswith(b"{"):
                    continue   # log lines
                try:
                    msg = json.loads(line)
                except ValueError:
                    continue
                if isinstance(msg, dict) and msg.get("id") == rid:
                    return msg
            left = deadline - now()
            if left <= 0:
                raise TimeoutError
            r, _, _ = select.select([fd], [], [], left)
            if not r:
                raise TimeoutError
            chunk = os.read(fd, 65536)
            if not chunk:
                raise EOFError
            self.buf += chunk

    def close(self):
        if self.p.poll() is None:
            try:
                os.killpg(self.p.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        self.p.wait()


# ── Codex ───────────────────────────────────────────────────

def find_codex():
    return which("codex", [os.path.join(HOME, ".local", "bin", "codex")])


def codex():
    exe = find_codex()
    if not exe:
        raise RuntimeError("Codexをインストールしてログインしてください")
    rpc = Rpc([exe, "app-server"])
    deadline = now() + 25
    try:
        rpc.send({"id": 1, "method": "initialize", "params": {"clientInfo": {"name": "usage-limit-viewer", "version": VERSION}}})
        if "error" in rpc.read(1, deadline):
            raise RuntimeError("Codexのログイン状態を確認してください")
        rpc.send({"method": "initialized", "params": {}})
        rpc.send({"id": 2, "method": "account/rateLimits/read"})
        msg = rpc.read(2, deadline)
    except (TimeoutError, EOFError, BrokenPipeError):
        raise RuntimeError("Codexとの接続が終了しました")
    finally:
        rpc.close()
    if "error" in msg:
        raise RuntimeError("Codexのログイン状態を確認してください")
    result = msg.get("result") or {}
    rows = []

    def bucket(b, prefix):
        if not isinstance(b, dict):
            return
        for key in ("primary", "secondary"):
            w = b.get(key)
            if not isinstance(w, dict) or not isinstance(w.get("usedPercent"), (int, float)):
                continue
            mins = w.get("windowDurationMins") or 0
            label = {300: "5時間", 10080: "週間"}.get(mins) or (f"{mins / 60:g}時間" if mins > 0 else key)
            reset = w.get("resetsAt") if isinstance(w.get("resetsAt"), (int, float)) else None
            rows.append({"label": prefix + label, "remaining": clamp(100 - w["usedPercent"]), "reset": reset})

    by_id = result.get("rateLimitsByLimitId")
    if isinstance(by_id, dict):
        for name, b in by_id.items():
            bucket(b, "" if name == "codex" else name + " · ")
    if not rows:
        bucket(result.get("rateLimits"), "")
    if not rows:
        raise RuntimeError("残量が返されませんでした")
    return {"quotas": rows, "source": "Codex CLI経由"}


# ── Antigravity (agy) ──────────────────────────────────────

agy_spends_quota = False


def antigravity():
    global agy_spends_quota
    if agy_spends_quota:
        raise RuntimeError("このagyでは /usage がモデル呼び出しになるため、残量取得を停止しています")
    exe = which("agy", [os.path.join(HOME, ".local", "share", "agy", "bin", "agy"), os.path.join(HOME, ".agy", "bin", "agy")])
    if not exe:
        raise RuntimeError("agyが見つかりません")
    try:
        out = subprocess.run([exe, "-p", "/usage", "--output-format", "json"], stdin=subprocess.DEVNULL,
                             capture_output=True, timeout=30, cwd=DATA, start_new_session=True)
    except subprocess.TimeoutExpired:
        raise RuntimeError("agyの残量取得に失敗しました。ログイン状態を確認してください")
    if out.returncode != 0:
        raise RuntimeError("agyの残量取得に失敗しました。ログイン状態を確認してください")
    try:
        root = json.loads(out.stdout)
    except ValueError:
        raise RuntimeError("agyから残量応答が返されませんでした")
    usage = root.get("usage") if isinstance(root, dict) else None
    # A reply that spent tokens without a "command" result means agy sent "/usage" to the model as a prompt.
    if isinstance(root, dict) and "command" not in root and isinstance(usage, dict) and (usage.get("total_tokens") or 0) > 0:
        agy_spends_quota = True
        raise RuntimeError("このagyでは /usage がモデル呼び出しになるため、残量取得を停止しています")
    cmd = root.get("command") if isinstance(root, dict) else None
    if root.get("status") != "SUCCESS" or not isinstance(cmd, dict) or cmd.get("name") != "usage":
        raise RuntimeError("agyから残量応答が返されませんでした")
    rows = {}
    for group in (cmd.get("data") or {}).get("groups") or []:
        for b in group.get("buckets") or []:
            f = b.get("remaining_fraction")
            if isinstance(f, (int, float)) and 0 <= f <= 1:
                rows[b.get("id")] = {"label": b.get("id"), "remaining": f * 100, "reset": iso_epoch(b.get("reset_time"))}
    if not rows:
        raise RuntimeError("agyの残量応答が空でした")
    return {"quotas": list(rows.values()), "source": "agy CLI経由"}


# ── Claude Code ────────────────────────────────────────────

CREDENTIALS = os.path.join(HOME, ".claude", ".credentials.json")
# Same endpoint and client as the Claude Code CLI.
TOKEN_URL = "https://platform.claude.com/v1/oauth/token"
CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
claude_retry_after = 0.0


def read_token():
    try:
        with open(CREDENTIALS) as f:
            o = json.load(f).get("claudeAiOauth")
    except (OSError, ValueError, AttributeError):
        return None
    if not isinstance(o, dict) or not isinstance(o.get("accessToken"), str):
        return None
    exp = o.get("expiresAt")
    return {"access": o["accessToken"], "refresh": o.get("refreshToken"),
            "expires": exp / 1000 if isinstance(exp, (int, float)) else float("inf"),
            "scopes": [s for s in o.get("scopes") or [] if isinstance(s, str)]}


def http(method, url, headers=None, body=None, timeout=15):
    req = urllib.request.Request(url, method=method, headers=headers or {},
                                 data=json.dumps(body).encode() if body is not None else None)
    if body is not None:
        req.add_header("Content-Type", "application/json")
    # Cloudflare refuses urllib's own User-Agent (error 1010).
    if not req.has_header("User-agent"):
        req.add_header("User-Agent", "UsageLimitViewer/" + VERSION)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(), r.headers
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers


def renew(token):
    """Renews the CLI sign-in the way the CLI does and writes it back, so the CLI keeps the rotated refresh token."""
    if not token.get("refresh"):
        raise LoginRequired
    body = {"grant_type": "refresh_token", "refresh_token": token["refresh"], "client_id": CLIENT_ID}
    if token["scopes"]:
        body["scope"] = " ".join(token["scopes"])
    status, data, _ = http("POST", TOKEN_URL, body=body)
    if status != 200:
        latest = read_token()   # the CLI may have rotated it first
        if latest and latest["expires"] > now() + 60:
            return latest
        raise LoginRequired
    renewal = json.loads(data)
    with open(CREDENTIALS) as f:
        root = json.load(f)
    oauth = root["claudeAiOauth"]
    if oauth.get("refreshToken") != token["refresh"] and (oauth.get("expiresAt") or 0) / 1000 > now() + 60:
        return read_token()
    t = now()
    oauth["accessToken"] = renewal["access_token"]
    if isinstance(renewal.get("refresh_token"), str):
        oauth["refreshToken"] = renewal["refresh_token"]
    oauth["expiresAt"] = int((t + float(renewal["expires_in"])) * 1000)
    if isinstance(renewal.get("refresh_token_expires_in"), (int, float)):
        oauth["refreshTokenExpiresAt"] = int((t + renewal["refresh_token_expires_in"]) * 1000)
    if isinstance(renewal.get("scope"), str) and renewal["scope"].strip():
        oauth["scopes"] = renewal["scope"].split()
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(CREDENTIALS), prefix=".credentials.", suffix=".tmp")
    with os.fdopen(fd, "w") as f:
        json.dump(root, f, separators=(",", ":"))
    os.chmod(tmp, 0o600)
    os.replace(tmp, CREDENTIALS)
    return read_token() or (_ for _ in ()).throw(LoginRequired())


def claude_usage(access):
    return http("GET", "https://api.anthropic.com/api/oauth/usage",
                {"Authorization": "Bearer " + access, "anthropic-beta": "oauth-2025-04-20",
                 "User-Agent": "UsageLimitViewer/" + VERSION})


def claude_parse(root):
    rows = []
    for key, label in (("five_hour", "5時間"), ("seven_day", "週間"), ("seven_day_opus", "週間 · Opus"), ("seven_day_sonnet", "週間 · Sonnet")):
        w = root.get(key) if isinstance(root, dict) else None
        if not isinstance(w, dict) or not isinstance(w.get("utilization"), (int, float)) or w["utilization"] < 0:
            continue
        reset = iso_epoch(w.get("resets_at"))
        if reset is not None and reset <= now():
            continue
        # Model-specific weekly windows without a reset time are placeholders for unused models.
        if reset is None and key.startswith("seven_day_"):
            continue
        rows.append({"label": label, "remaining": clamp(100 - w["utilization"]), "reset": reset})
    return rows


def claude(force=False):
    global claude_retry_after
    try:
        with open(CLAUDE_CACHE) as f:
            cache = json.load(f)
        if now() - cache["captured_at"] < (30 if force else 55):
            return {"quotas": claude_parse(cache["usage"]), "source": "Claude Code CLI経由", "updated": cache["captured_at"]}
    except (OSError, ValueError, KeyError, TypeError):
        pass
    if now() < claude_retry_after:
        raise RuntimeError("Claudeの使用状況の取得を一時的に控えています")
    token = read_token()
    if not token:
        raise LoginRequired
    # The CLI only renews its sign-in when it calls the model, so an idle CLI leaves the token expired.
    if token["expires"] <= now() + 300:
        token = renew(token)
    status, data, headers = claude_usage(token["access"])
    if status == 401 and token.get("refresh"):
        latest = read_token()
        token = latest if latest and latest["access"] != token["access"] and latest["expires"] > now() + 60 else renew(latest or token)
        status, data, headers = claude_usage(token["access"])
    if status == 429:
        try:
            wait = float(headers.get("Retry-After") or 120)
        except ValueError:
            wait = 120
        claude_retry_after = now() + wait
        raise RuntimeError("Claudeの使用状況の取得を一時的に控えています")
    if status in (401, 403):
        raise LoginRequired
    if status != 200:
        raise RuntimeError("Claudeの使用状況を取得できません")
    usage = json.loads(data)
    rows = claude_parse(usage)
    if not rows:
        raise RuntimeError("Claude Codeの残量は利用できません")
    with open(CLAUDE_CACHE, "w") as f:
        json.dump({"captured_at": now(), "usage": usage}, f)
    return {"quotas": rows, "source": "Claude Code CLI経由"}


# ── Grok ───────────────────────────────────────────────────

def grok():
    exe = which("grok", [os.path.join(HOME, ".grok", "bin", "grok")])
    if not exe:
        raise RuntimeError("Grok CLIが見つかりません")
    rpc = Rpc([exe, "agent", "stdio"])
    deadline = now() + 15
    try:
        rpc.send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                  "params": {"protocolVersion": 1, "clientCapabilities": {}, "clientInfo": {"name": "UsageLimitViewer", "version": VERSION}}})
        # The CLI loads its sign-in for a second or two after starting; ask again in the same process before giving up.
        for attempt in range(8):
            rid = 2 + attempt
            rpc.send({"jsonrpc": "2.0", "id": rid, "method": "_x.ai/billing", "params": {}})
            msg = rpc.read(rid, deadline)
            err = msg.get("error")
            if not err:
                result = msg.get("result")
                break
            if err.get("code") == -32601:
                raise RuntimeError("Grok CLIを更新してください")
            pending = "credentials" in str(err.get("data") or "").lower()
            if (pending or err.get("code") == -32603) and attempt < 7:
                time.sleep(1)
                continue
            raise RuntimeError("Grok CLIで再ログインしてください（grok login）" if pending else "Grok CLIの利用状況を取得できません")
    except (TimeoutError, EOFError, BrokenPipeError):
        raise RuntimeError("Grok CLIとの接続が終了しました")
    finally:
        rpc.close()
    config = (result or {}).get("config") or {}
    period = config.get("currentPeriod") or {}
    used = config.get("creditUsagePercent")
    reset = iso_epoch(period.get("end"))
    if not isinstance(used, (int, float)) or used < 0 or period.get("type") != "USAGE_PERIOD_TYPE_WEEKLY" or not reset or reset <= now():
        raise RuntimeError("Grokの週次残量は利用できません")
    return {"quotas": [{"label": "週間", "remaining": clamp(100 - used), "reset": reset}], "source": "Grok CLI経由"}


READERS = {"Codex": codex, "Antigravity": antigravity, "Claude Code": claude, "Grok": grok}
INSTALLED = {"Codex": lambda: bool(find_codex()), "Claude Code": lambda: os.path.exists(CREDENTIALS) or bool(which("claude")),
             "Antigravity": lambda: bool(which("agy")), "Grok": lambda: bool(which("grok", [os.path.join(HOME, ".grok", "bin", "grok")]))}


def read_one(name, force=False):
    """One service's reading. On failure the last values stay (the widget grays them) with the reason."""
    try:
        r = READERS[name](force) if name == "Claude Code" else READERS[name]()
        return {"quotas": r["quotas"], "updated": r.get("updated", now()), "source": r["source"], "error": None, "needsLogin": False}
    except LoginRequired:
        return {"error": "Claude Code のログインが切れています", "needsLogin": True}
    except Exception as e:  # noqa: BLE001 — one service failing never stops the others
        msg = str(e) if isinstance(e, RuntimeError) else f"{name}の残量を取得できません"
        return {"error": msg, "needsLogin": False}


# ── state.json ─────────────────────────────────────────────

def load_state():
    try:
        with open(STATE) as f:
            s = json.load(f)
        if isinstance(s.get("providers"), dict):
            return s
    except (OSError, ValueError, AttributeError):
        pass
    return {"version": VERSION, "providers": {}}


def save_state(state):
    os.makedirs(DATA, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=DATA, prefix=".state.", suffix=".tmp")
    with os.fdopen(fd, "w") as f:
        json.dump(state, f, ensure_ascii=False)
    os.replace(tmp, STATE)


def notify(title, body):
    try:
        subprocess.Popen(["notify-send", "-a", "Usage Limit Viewer", "-i", "dialog-warning", title, body],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError:
        pass


def daemon():
    os.makedirs(DATA, exist_ok=True)
    lock = open(LOCKFILE, "w")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        print("already running", file=sys.stderr)
        return 1
    with open(PIDFILE, "w") as f:
        f.write(str(os.getpid()))
    wake = threading.Event()
    forced = {"on": False}

    def on_refresh(*_):
        forced["on"] = True
        wake.set()
    signal.signal(signal.SIGUSR1, on_refresh)
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))

    state = load_state()
    state["version"] = VERSION
    due = {p: 0.0 for p in PROVIDERS}
    while True:
        force = forced["on"]
        forced["on"] = False
        if force:
            due = {p: 0.0 for p in PROVIDERS}
        state["refreshing"] = True
        save_state(state)
        for name in PROVIDERS:
            if now() < due[name]:
                continue
            installed = INSTALLED[name]()
            prev = state["providers"].get(name, {})
            if not installed:
                state["providers"][name] = {"installed": False, "quotas": [], "updated": None,
                                            "error": READERS_MISSING[name], "needsLogin": False, "source": None}
            else:
                r = read_one(name, force)
                entry = dict(prev)
                entry.update(r)
                entry["installed"] = True
                entry.setdefault("quotas", [])
                if r["needsLogin"] and not prev.get("needsLogin"):
                    notify("Claude Code のログインが切れています", "使用状況の「ログイン」から、ログインし直してください。")
                state["providers"][name] = entry
            due[name] = now() + INTERVALS[name]
            state["updated"] = now()
            save_state(state)
        state["refreshing"] = False
        save_state(state)
        wake.wait(max(1.0, min(due.values()) - now()))
        wake.clear()


READERS_MISSING = {"Codex": "Codexをインストールしてログインしてください", "Antigravity": "agyが見つかりません",
                   "Claude Code": "Claude Code CLIをインストールしてログインしてください", "Grok": "Grok CLIが見つかりません"}


def load_settings():
    try:
        with open(SETTINGS) as f:
            d = json.load(f)
        return {k: v for k, v in d.items() if k in SETTING_KEYS and isinstance(v, SETTING_KEYS[k])}
    except (OSError, ValueError, AttributeError):
        return {}


def set_setting(key, value):
    if key not in SETTING_KEYS:
        return 2
    v = json.loads(value)
    if not isinstance(v, SETTING_KEYS[key]):
        return 2
    d = load_settings()
    d[key] = v
    os.makedirs(os.path.dirname(SETTINGS), exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(SETTINGS), prefix=".settings.", suffix=".tmp")
    with os.fdopen(fd, "w") as f:
        json.dump(d, f, ensure_ascii=False)
    os.replace(tmp, SETTINGS)
    return 0


def mtime(path):
    try:
        return os.stat(path).st_mtime_ns
    except OSError:
        return 0


def wait(last):
    """Print state.json and the settings once either file changed from <last>. Gives up after 10 minutes so the caller re-asks."""
    end = now() + 600
    while True:
        m = mtime(STATE)
        key = f"{m}-{mtime(SETTINGS)}"
        if key != last or now() > end:
            s = load_state() if m else {"providers": {}, "missing": True}
            s["mtime"] = key
            s["settings"] = load_settings()
            print(json.dumps(s, ensure_ascii=False))
            return 0
        time.sleep(0.5)


def refresh():
    try:
        with open(PIDFILE) as f:
            os.kill(int(f.read().strip()), signal.SIGUSR1)
        return 0
    except (OSError, ValueError):
        subprocess.run(["systemctl", "--user", "restart", "usage-limit-viewer.service"])
        return 0


def login(service):
    if service != "claude":
        return 2
    cli = which("claude")
    if not cli:
        subprocess.Popen(["xdg-open", "https://code.claude.com/docs/en/setup"])
        return 0
    for term in (["konsole", "-e"], ["x-terminal-emulator", "-e"], ["gnome-terminal", "--"]):
        if shutil.which(term[0]):
            subprocess.Popen(term + [cli, "auth", "login"], start_new_session=True)
            return 0
    return 1


def diagnose():
    out = {}
    for name in PROVIDERS:
        out[name] = read_one(name, True) if INSTALLED[name]() else {"error": READERS_MISSING[name]}
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    cmd = argv[0]
    if cmd == "daemon":
        return daemon()
    if cmd == "wait":
        return wait(argv[1] if len(argv) > 1 else "")
    if cmd == "refresh":
        return refresh()
    if cmd == "set" and len(argv) == 3:
        return set_setting(argv[1], argv[2])
    if cmd == "login":
        return login(argv[1] if len(argv) > 1 else "")
    if cmd == "diagnose":
        os.makedirs(DATA, exist_ok=True)
        return diagnose()
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
