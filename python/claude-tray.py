#!/usr/bin/env python3
"""
Claude Account Tray Switcher — native, no cswap dependency.
Manages ~/.claude/accounts/*.json and swaps ~/.claude/.credentials.json.

Run: uv run --with pystray --with pillow claude-tray.py
Or:  see claude-tray.vbs
"""

import ctypes
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime, timedelta

from PIL import Image, ImageDraw, ImageFont
import pystray
from pystray import Menu, MenuItem as item

COLORS = ["#4A90D9", "#E8754A", "#5CB85C", "#9B59B6", "#F39C12", "#E74C3C"]
CLAUDE_EXE = os.path.join(os.path.expanduser("~"), ".local", "bin", "claude.exe")

HOME = os.path.expanduser("~")
CLAUDE_DIR     = os.path.join(HOME, ".claude")
CRED_PATH      = os.path.join(CLAUDE_DIR, ".credentials.json")
ACCOUNTS_DIR   = os.path.join(CLAUDE_DIR, "accounts")
SWITCHER_PATH  = os.path.join(CLAUDE_DIR, "claude-switcher.json")

# ── single instance (Windows Mutex) ──────────────────────────────────────────

_kernel32 = ctypes.windll.kernel32
_mutex_handle = _kernel32.CreateMutexW(None, True, "Global\\ClaudeTray_SingleInstance")
if _kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
    ctypes.windll.user32.MessageBoxW(
        0, "Claude Switcher is already running.", "Claude Switcher", 0)
    sys.exit(0)

# ── win32 dialogs ─────────────────────────────────────────────────────────────

MB_OK           = 0x00
MB_OKCANCEL     = 0x01
MB_YESNO        = 0x04
MB_ICONINFO     = 0x40
MB_ICONQUESTION = 0x20
IDOK  = 1
IDYES = 6

def msgbox(title, text, style=MB_OK):
    return ctypes.windll.user32.MessageBoxW(0, text, title, style)

def input_dialog(prompt: str, title: str) -> str | None:
    """Show a Windows InputBox via PowerShell and return the entered string."""
    ps = (
        f"Add-Type -AssemblyName Microsoft.VisualBasic; "
        f"[Microsoft.VisualBasic.Interaction]::InputBox('{prompt}', '{title}', '')"
    )
    r = subprocess.run(
        ["powershell", "-NoProfile", "-Command", ps],
        capture_output=True, text=True
    )
    val = r.stdout.strip()
    return val if val else None


# ── account store ─────────────────────────────────────────────────────────────

class AccountStore:
    """Manages named account credential files; switches by copying to .credentials.json."""

    def __init__(self):
        self._active_email: str | None = self._load_active_email() or self._detect_active_email()

    # ── public ────────────────────────────────────────────────────────────────

    def list(self) -> list[dict]:
        """Return sorted list of {num, email, active, org_name} dicts."""
        entries = []
        for f in self._account_files():
            try:
                data = json.loads(open(f).read())
                email = data.get("email")
                if email:
                    entries.append({
                        "email": email,
                        "org_name": data.get("orgName"),
                    })
            except Exception:
                pass
        entries.sort(key=lambda e: e["email"].lower())
        # Always detect from actual credentials file so display stays accurate
        # even if the user switched accounts outside of this tool.
        active_email = self._detect_active_email() or self._active_email
        return [
            {"num": i + 1, "email": e["email"], "active": e["email"] == active_email,
             "org_name": e["org_name"]}
            for i, e in enumerate(entries)
        ]

    def switch_to(self, email: str) -> str | None:
        """Copy stored credentials for email → .credentials.json. Returns error or None."""
        try:
            f = self._find_file(email) or (_ for _ in ()).throw(
                FileNotFoundError(f"Account not found: {email}"))
            data = json.loads(open(f).read())
            cred = data.get("credentials") or (_ for _ in ()).throw(
                ValueError("Account file has no 'credentials' field"))
            os.makedirs(CLAUDE_DIR, exist_ok=True)
            with open(CRED_PATH, "w") as out:
                json.dump(cred, out, indent=2)
            self._save_active_email(email)
            return None
        except Exception as e:
            return str(e)

    def add_current(self, email: str, org_name: str | None = None) -> str | None:
        """Save the current .credentials.json as a named account. Returns error or None."""
        try:
            if not os.path.exists(CRED_PATH):
                return ("No active session found (~/.claude/.credentials.json missing).\n"
                        "Log into Claude Code first.")
            cred = json.loads(open(CRED_PATH).read())
            os.makedirs(ACCOUNTS_DIR, exist_ok=True)
            obj = {"email": email, "credentials": cred}
            if org_name: obj["orgName"] = org_name
            with open(self._file_path(email), "w") as out:
                json.dump(obj, out, indent=2)
            self._save_active_email(email)
            return None
        except Exception as e:
            return str(e)

    def remove(self, email: str):
        f = self._find_file(email)
        if f:
            os.unlink(f)
        if self._active_email == email:
            self._save_active_email(None)

    def update_plan(self, email: str, org_name: str | None):
        """Update stored orgName for an existing account."""
        f = self._find_file(email)
        if not f:
            return
        try:
            data = json.loads(open(f).read())
            if org_name: data["orgName"] = org_name
            with open(f, "w") as out:
                json.dump(data, out, indent=2)
        except Exception:
            pass

    # ── internals ─────────────────────────────────────────────────────────────

    def _account_files(self) -> list[str]:
        if not os.path.isdir(ACCOUNTS_DIR):
            return []
        return [os.path.join(ACCOUNTS_DIR, f)
                for f in os.listdir(ACCOUNTS_DIR) if f.endswith(".json")]

    def _find_file(self, email: str) -> str | None:
        for f in self._account_files():
            try:
                if json.loads(open(f).read()).get("email") == email:
                    return f
            except Exception:
                pass
        return None

    def _detect_active_email(self) -> str | None:
        """Match current .credentials.json refreshToken against stored accounts."""
        try:
            current = json.loads(open(CRED_PATH).read())
            current_rt = current.get("claudeAiOauth", {}).get("refreshToken")
            if not current_rt:
                return None
            for f in self._account_files():
                try:
                    data = json.loads(open(f).read())
                    stored_rt = (data.get("credentials", {})
                                     .get("claudeAiOauth", {})
                                     .get("refreshToken"))
                    if stored_rt == current_rt:
                        return data.get("email")
                except Exception:
                    pass
        except Exception:
            pass
        return None

    def _load_active_email(self) -> str | None:
        try:
            return json.loads(open(SWITCHER_PATH).read()).get("activeEmail")
        except Exception:
            return None

    def _save_active_email(self, email: str | None):
        self._active_email = email
        try:
            try:
                data = json.loads(open(SWITCHER_PATH).read())
            except Exception:
                data = {}
            data["activeEmail"] = email
            with open(SWITCHER_PATH, "w") as f:
                json.dump(data, f, indent=2)
        except Exception:
            pass

    @staticmethod
    def _file_path(email: str) -> str:
        safe = re.sub(r"[^\w@.\-]", "_", email)
        return os.path.join(ACCOUNTS_DIR, safe + ".json")


# ── rate limit store ──────────────────────────────────────────────────────────

class RateLimitStore:
    def __init__(self):
        self._limits: dict[str, str] = {}
        self._reset_hours = 5.0
        self._load()

    def is_limited(self, email: str) -> bool:
        return email in self._limits

    def is_expired(self, email: str) -> bool:
        ts = self._limits.get(email)
        if not ts:
            return False
        try:
            since = datetime.fromisoformat(ts)
            return (datetime.now() - since).total_seconds() / 3600 >= self._reset_hours
        except Exception:
            return False

    def limited_at(self, email: str) -> datetime | None:
        ts = self._limits.get(email)
        if not ts:
            return None
        try:
            return datetime.fromisoformat(ts)
        except Exception:
            return None

    def limited_since_str(self, email: str) -> str | None:
        dt = self.limited_at(email)
        return dt.strftime("%H:%M") if dt else None

    def mark_limited(self, email: str):
        self._limits[email] = datetime.now().isoformat()
        self._save()

    def clear_limit(self, email: str):
        self._limits.pop(email, None)
        self._save()

    def _load(self):
        try:
            data = json.loads(open(SWITCHER_PATH).read())
            self._reset_hours = data.get("resetHours", 5.0)
            self._limits = data.get("rateLimits", {})
        except Exception:
            pass

    def _save(self):
        try:
            try:
                data = json.loads(open(SWITCHER_PATH).read())
            except Exception:
                data = {}
            data["resetHours"] = self._reset_hours
            data["rateLimits"] = self._limits
            os.makedirs(os.path.dirname(SWITCHER_PATH), exist_ok=True)
            with open(SWITCHER_PATH, "w") as f:
                json.dump(data, f, indent=2)
        except Exception:
            pass


# ── log watcher ───────────────────────────────────────────────────────────────

RATE_LIMIT_RE = re.compile(
    r"rate.?limit|too many requests|429|quota exceeded|capacity reached|claude is at capacity",
    re.IGNORECASE)

WATCH_DIRS = [
    os.path.join(HOME, ".claude"),
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Claude"),
]

WATCH_EXTS = {".log", ".txt", ".json", ""}


class LogWatcher:
    def __init__(self, on_rate_limit):
        self._callback = on_rate_limit
        self._last_fired = 0.0
        self._last_sizes: dict[str, int] = {}
        self._thread = threading.Thread(target=self._run, daemon=True)

    def start(self):
        self._thread.start()

    def _run(self):
        while True:
            self._poll()
            time.sleep(3)

    def _poll(self):
        if time.time() - self._last_fired < 10:
            return
        for d in WATCH_DIRS:
            if not os.path.isdir(d):
                continue
            for root, _, files in os.walk(d):
                for fname in files:
                    if os.path.splitext(fname)[1].lower() not in WATCH_EXTS:
                        continue
                    path = os.path.join(root, fname)
                    try:
                        size = os.path.getsize(path)
                        old = self._last_sizes.get(path, 0)
                        self._last_sizes[path] = size
                        if size > old:
                            self._check_file(path, size)
                    except Exception:
                        pass

    def _check_file(self, path: str, size: int):
        try:
            with open(path, "r", errors="ignore", encoding="utf-8") as f:
                f.seek(max(0, size - 8192))
                tail = f.read()
            if RATE_LIMIT_RE.search(tail):
                self._last_fired = time.time()
                self._callback()
        except Exception:
            pass


# ── auth status ───────────────────────────────────────────────────────────────

def get_auth_status() -> dict | None:
    """Run `claude auth status` and return the parsed JSON, or None on failure."""
    try:
        env = os.environ.copy()
        env["ANTHROPIC_API_KEY"] = ""   # clear any override so OAuth session is used
        r = subprocess.run(
            ["claude", "auth", "status"],
            capture_output=True, text=True, timeout=5, env=env,
        )
        data = json.loads(r.stdout)
        return data if data.get("loggedIn") else None
    except Exception:
        return None


def _format_plan(status: dict | None) -> str:
    org = (status.get("orgName") or "").strip() if status else ""
    return f" [{org}]" if org else ""


def clear_electron_profile_cache():
    """Delete Electron's Local Storage so Claude Code shows fresh profile after credential switch."""
    appdata = os.environ.get("APPDATA", "")
    leveldb = os.path.join(appdata, "Claude", "Local Storage", "leveldb")
    if os.path.isdir(leveldb):
        try:
            shutil.rmtree(leveldb)
        except Exception:
            pass


# ── helpers ───────────────────────────────────────────────────────────────────

def hex_to_rgb(h):
    return (int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16))


# ── icon building ─────────────────────────────────────────────────────────────

def get_claude_base_icon(size=128) -> Image.Image | None:
    if not os.path.exists(CLAUDE_EXE):
        return None
    try:
        tmp = os.path.join(tempfile.gettempdir(), "claude_tray_icon.png")
        exe = CLAUDE_EXE.replace("\\", "/")
        ps = (
            f"Add-Type -AssemblyName System.Drawing; "
            f"$icon = [System.Drawing.Icon]::ExtractAssociatedIcon('{exe}'); "
            f"$bmp = $icon.ToBitmap(); "
            f"$bmp.Save('{tmp}')"
        )
        r = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                           capture_output=True, timeout=8)
        if r.returncode == 0 and os.path.exists(tmp):
            img = Image.open(tmp).convert("RGBA").resize((size, size), Image.LANCZOS)
            os.unlink(tmp)
            return img
    except Exception:
        pass
    return None


def make_letter_icon(letter: str, hex_color: str, size=64) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse([2, 2, size - 2, size - 2], fill=(*hex_to_rgb(hex_color), 255))
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", size // 2)
    except Exception:
        font = ImageFont.load_default()
    bbox = draw.textbbox((0, 0), letter, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text(((size - tw) // 2, (size - th) // 2 - 2), letter,
              fill=(255, 255, 255, 255), font=font)
    return img


def overlay_badge(base_img: Image.Image, letter: str, hex_color: str, size=64) -> Image.Image:
    img = base_img.copy().resize((size, size), Image.LANCZOS)
    draw = ImageDraw.Draw(img)
    b = size // 3
    x, y = size - b - 1, size - b - 1
    r, g, bv = hex_to_rgb(hex_color)
    draw.ellipse([x - 1, y - 1, x + b + 1, y + b + 1], fill=(0, 0, 0, 220))
    draw.ellipse([x, y, x + b, y + b], fill=(r, g, bv, 255))
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", b // 2)
    except Exception:
        font = ImageFont.load_default()
    bbox = draw.textbbox((0, 0), letter, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((x + (b - tw) // 2, y + (b - th) // 2 - 1), letter,
              fill=(255, 255, 255, 255), font=font)
    return img


_claude_base: Image.Image | None = None


def build_tray_image(active, limited: bool, size=128) -> Image.Image:
    global _claude_base
    if _claude_base is None:
        _claude_base = get_claude_base_icon(size)

    badge_color = "#E8754A" if limited else (
        COLORS[(active["num"] - 1) % len(COLORS)] if active else "#888888")
    badge_letter = "!" if limited else (active["email"][0].upper() if active else "C")

    if _claude_base is not None:
        if active or limited:
            return overlay_badge(_claude_base, badge_letter, badge_color, size)
        return _claude_base.copy()

    return make_letter_icon(badge_letter, badge_color, size)


# ── app state ─────────────────────────────────────────────────────────────────

_limits = RateLimitStore()
_account_store = AccountStore()
_tray_icon: pystray.Icon | None = None
_accounts: list[dict] = []
_auth_status: dict | None = None


def refresh(tray):
    global _accounts
    _accounts = _account_store.list()
    # Sync the active account to match auth status so the header email
    # and the checkmark always point to the same account.
    auth_email = (_auth_status or {}).get("email")
    if auth_email and any(a["email"] == auth_email for a in _accounts):
        _accounts = [dict(a, active=(a["email"] == auth_email)) for a in _accounts]
    _update_ui(tray, _accounts)


def _update_ui(tray, accounts):
    for acc in accounts:
        if _limits.is_expired(acc["email"]):
            _limits.clear_limit(acc["email"])

    active = next((a for a in accounts if a["active"]), None)
    # Prefer real email from auth status over stored active account
    real_email = (_auth_status or {}).get("email") or (active["email"] if active else None)
    limited = real_email is not None and _limits.is_limited(real_email)

    tray.icon = build_tray_image(active, limited)

    plan_label = _format_plan(_auth_status)
    tray.title = (
        f"Claude: {real_email}{plan_label}{' ⚠' if limited else ''}"
        if real_email else "Claude Account Switcher"
    )
    tray.menu = build_menu(tray, accounts)


# ── rate limit auto-handler ───────────────────────────────────────────────────

def on_rate_limit_detected():
    if _tray_icon is None:
        return
    accounts = _account_store.list()
    active = next((a for a in accounts if a["active"]), None)
    if active is None or _limits.is_limited(active["email"]):
        return

    _limits.mark_limited(active["email"])
    _update_ui(_tray_icon, accounts)

    _tray_icon.notify(
        f"{active['email']} hit its limit.\nSwitch accounts from the tray.",
        "Rate limit detected")


# ── actions ───────────────────────────────────────────────────────────────────

def do_switch(tray, email: str):
    global _accounts, _auth_status
    err = _account_store.switch_to(email)
    if err:
        tray.notify(f"Switch failed: {err}", "Error")
        return

    _accounts = [dict(a, active=(a["email"] == email)) for a in _accounts]
    _update_ui(tray, _accounts)

    tray.notify(f"Switched to {email}.\nRestarting Claude Code…", "Switched")
    time.sleep(1.5)
    do_restart_claude()
    _auth_status = get_auth_status()
    if _auth_status:
        _account_store.update_plan(email, _auth_status.get("orgName"))
    refresh(tray)


def do_add_account(tray):
    email = input_dialog(
        "Save the currently logged-in Claude session as a named account.\\n\\n"
        "Enter the email address for this session:",
        "Save Account")
    if not email:
        return
    email = email.strip()
    err = _account_store.add_current(email, (_auth_status or {}).get("orgName"))
    if err:
        msgbox("Save Account", err, MB_OK)
        return
    refresh(tray)
    tray.notify(f"Saved {email}.", "Account saved")


def do_remove_account(tray, email: str):
    ret = msgbox("Remove Account", f"Remove account {email}?", MB_YESNO | MB_ICONQUESTION)
    if ret != IDYES:
        return
    _account_store.remove(email)
    _limits.clear_limit(email)
    refresh(tray)
    tray.notify(f"Removed {email}.", "Remove Account")


def do_restart_claude():
    for proc in ["claude.exe"]:
        subprocess.run(["taskkill", "/F", "/IM", proc], capture_output=True)
    time.sleep(1.2)
    clear_electron_profile_cache()
    subprocess.Popen("claude", shell=True, cwd=HOME)


# ── menu ──────────────────────────────────────────────────────────────────────

def build_menu(tray, accounts=None):
    if accounts is None:
        accounts = _accounts or _account_store.list()

    active = next((a for a in accounts if a["active"]), None)
    rows = []

    # header — use real email from auth status if available
    real_email = (_auth_status or {}).get("email") or (active["email"] if active else None)
    if real_email:
        limited_hdr = _limits.is_limited(real_email)
        # Per-account stored plan takes priority; fall back to live auth status.
        if active and active.get("org_name"):
            plan = _format_plan({"orgName": active["org_name"]})
        else:
            plan = _format_plan(_auth_status)
        header_label = f"{'⚠' if limited_hdr else '●'}  {real_email}{plan}"
    else:
        header_label = "No active account"
    rows.append(item(header_label, None, enabled=False))
    rows.append(Menu.SEPARATOR)

    # accounts
    for acc in accounts:
        limited = _limits.is_limited(acc["email"])
        prefix = "✓  " if acc["active"] else "     "

        if acc["active"]:
            rows.append(item(prefix + acc["email"], None, enabled=False))
        else:
            def make_switch(email):
                return lambda i, _: do_switch(i, email)
            rows.append(item(prefix + acc["email"], make_switch(acc["email"])))

    rows.append(Menu.SEPARATOR)

    # rate limit mark/clear per account
    if accounts:
        limit_items = []
        for acc in accounts:
            email = acc["email"]
            limited = _limits.is_limited(email)
            since = _limits.limited_since_str(email)
            clear_label = f"Clear (since {since})" if since else "Clear"

            def make_mark(e):
                return lambda i, _: [_limits.mark_limited(e), refresh(i)]

            def make_clear(e):
                return lambda i, _: [_limits.clear_limit(e), refresh(i)]

            sub = [
                item("Mark as limited", make_mark(email), enabled=not limited),
                item(clear_label, make_clear(email), enabled=limited),
            ]
            limit_items.append(item(email, Menu(*sub)))
        rows.append(item("Rate limits ▶", Menu(*limit_items)))

    rows.append(Menu.SEPARATOR)
    rows.append(item("Save current session as account…", lambda i, _: do_add_account(i)))

    if accounts:
        def make_remove(email):
            return lambda i, _: do_remove_account(i, email)
        remove_items = [item(a["email"], make_remove(a["email"])) for a in accounts]
        rows.append(item("Remove account ▶", Menu(*remove_items)))

    rows.append(Menu.SEPARATOR)
    rows.append(item("Restart Claude Code", lambda i, _: do_restart_claude()))
    rows.append(Menu.SEPARATOR)
    rows.append(item("Quit", lambda i, _: i.stop()))

    return Menu(*rows)


# ── main ──────────────────────────────────────────────────────────────────────

def main():
    global _tray_icon, _accounts, _auth_status

    _auth_status = get_auth_status()
    _accounts = _account_store.list()
    active = next((a for a in _accounts if a["active"]), None)
    limited = active is not None and _limits.is_limited(active["email"])
    img = build_tray_image(active, limited)

    real_email = (_auth_status or {}).get("email") or (active["email"] if active else None)
    plan_label = _format_plan(_auth_status)
    title = f"Claude: {real_email}{plan_label}" if real_email else "Claude Account Switcher"

    _tray_icon = pystray.Icon("claude-accounts", img, title)
    _tray_icon.menu = build_menu(_tray_icon, _accounts)

    watcher = LogWatcher(on_rate_limit_detected)
    watcher.start()

    # Refresh auth status and account list every 30 seconds
    def _periodic_refresh():
        global _auth_status
        while True:
            time.sleep(30)
            _auth_status = get_auth_status()
            if _tray_icon and _auth_status:
                active = next((a for a in _accounts if a["active"]), None)
                if active:
                    _account_store.update_plan(
                        active["email"],
                        _auth_status.get("orgName"),
                    )
            if _tray_icon:
                refresh(_tray_icon)
    threading.Thread(target=_periodic_refresh, daemon=True).start()

    _tray_icon.run()


if __name__ == "__main__":
    main()
