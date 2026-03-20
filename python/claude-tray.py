#!/usr/bin/env python3
"""
Claude Account Tray Switcher
Run: uv run --with pystray --with pillow claude-tray.py
Or:  see claude-tray.vbs
"""

import ctypes
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime

from PIL import Image, ImageDraw, ImageFont
import pystray
from pystray import Menu, MenuItem as item

COLORS = ["#4A90D9", "#E8754A", "#5CB85C", "#9B59B6", "#F39C12", "#E74C3C"]
CLAUDE_EXE = os.path.join(os.path.expanduser("~"), ".local", "bin", "claude.exe")

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

# ── rate limit store ──────────────────────────────────────────────────────────

class RateLimitStore:
    _path = os.path.join(os.path.expanduser("~"), ".claude", "claude-switcher.json")

    def __init__(self):
        self._limits: dict[str, str] = {}
        self._auto_switch = True
        self._load()

    @property
    def auto_switch(self):
        return self._auto_switch

    @auto_switch.setter
    def auto_switch(self, value):
        self._auto_switch = value
        self._save()

    def is_limited(self, email: str) -> bool:
        return email in self._limits

    def limited_since(self, email: str) -> str | None:
        ts = self._limits.get(email)
        if ts:
            try:
                return datetime.fromisoformat(ts).strftime("%H:%M")
            except Exception:
                return ts
        return None

    def mark_limited(self, email: str):
        self._limits[email] = datetime.now().isoformat()
        self._save()

    def clear_limit(self, email: str):
        self._limits.pop(email, None)
        self._save()

    def _load(self):
        try:
            with open(self._path) as f:
                data = json.load(f)
            self._auto_switch = data.get("autoSwitch", True)
            self._limits = data.get("rateLimits", {})
        except Exception:
            pass

    def _save(self):
        try:
            os.makedirs(os.path.dirname(self._path), exist_ok=True)
            with open(self._path, "w") as f:
                json.dump({"autoSwitch": self._auto_switch, "rateLimits": self._limits},
                          f, indent=2)
        except Exception:
            pass


# ── log watcher ───────────────────────────────────────────────────────────────

RATE_LIMIT_RE = re.compile(
    r"rate.?limit|too many requests|429|quota exceeded|capacity reached|claude is at capacity",
    re.IGNORECASE)

WATCH_DIRS = [
    os.path.join(os.path.expanduser("~"), ".claude"),
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


# ── helpers ───────────────────────────────────────────────────────────────────

def parse_accounts() -> list[dict]:
    try:
        r = subprocess.run(["cswap", "--list"], capture_output=True, text=True, timeout=5)
        accounts = []
        for line in r.stdout.splitlines():
            m = re.match(r"\s*(\d+):\s+(.+?)(\s+\(active\))?\s*$", line)
            if m:
                accounts.append({
                    "num": int(m.group(1)),
                    "email": m.group(2).strip(),
                    "active": m.group(3) is not None,
                })
        return accounts
    except Exception:
        return []


def active_account(accounts):
    return next((a for a in accounts if a["active"]), None)


def hex_to_rgb(h):
    return (int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16))


# ── icon building ─────────────────────────────────────────────────────────────

def get_claude_base_icon(size=64) -> Image.Image | None:
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


def build_tray_image(active, limited: bool, size=64) -> Image.Image:
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
_tray_icon: pystray.Icon | None = None


def refresh(tray):
    accounts = parse_accounts()
    active = active_account(accounts)
    limited = active is not None and _limits.is_limited(active["email"])
    tray.icon = build_tray_image(active, limited)
    tray.title = (
        f"Claude: {active['email']}{' ⚠' if limited else ''}"
        if active else "Claude Account Switcher"
    )
    tray.menu = build_menu(tray)


# ── rate limit auto-handler ───────────────────────────────────────────────────

def on_rate_limit_detected():
    """Called from background watcher thread."""
    if _tray_icon is None:
        return

    accounts = parse_accounts()
    active = active_account(accounts)
    if active is None or _limits.is_limited(active["email"]):
        return

    _limits.mark_limited(active["email"])
    refresh(_tray_icon)

    if _limits.auto_switch:
        nxt = next((a for a in accounts if not a["active"] and not _limits.is_limited(a["email"])), None)
        if nxt:
            do_switch(_tray_icon, nxt["num"], nxt["email"], auto_restart=True)
            return

    _tray_icon.notify(
        f"{active['email']} hit its limit.\nSwitch accounts from the tray.",
        "Rate limit detected")


# ── actions ───────────────────────────────────────────────────────────────────

def do_switch(tray, num, email, auto_restart=False):
    subprocess.run(["cswap", "--switch-to", str(num)], capture_output=True, timeout=5)
    refresh(tray)
    if auto_restart:
        tray.notify(
            f"Rate limit detected → switched to {email}.\nRestarting Claude Code…",
            "Auto-switched")
        time.sleep(1.5)
        do_restart_claude(tray)
    else:
        tray.notify(f"Switched to {email}\nRestart Claude Code to apply.",
                    "Claude Account Switcher")


def do_add_account(tray):
    ret = msgbox(
        "Add Account",
        "Log into the new account in Claude Code first.\n\n"
        "  claude /logout\n  claude /login\n\n"
        "Click OK when done.",
        MB_OKCANCEL | MB_ICONINFO)
    if ret != IDOK:
        return
    r = subprocess.run(["cswap", "--add-account"], capture_output=True, text=True, timeout=5)
    refresh(tray)
    tray.notify((r.stdout or r.stderr or "Done.").strip(), "Add Account")


def do_remove_account(tray, num, email):
    ret = msgbox("Remove Account", f"Remove account {email}?", MB_YESNO | MB_ICONQUESTION)
    if ret != IDYES:
        return
    subprocess.run(["cswap", "--remove-account", str(num)], capture_output=True, timeout=5)
    _limits.clear_limit(email)
    refresh(tray)
    tray.notify(f"Removed {email}.", "Remove Account")


def do_restart_claude(_tray=None):
    subprocess.run(["taskkill", "/F", "/IM", "claude.exe"], capture_output=True)
    time.sleep(1)
    subprocess.Popen("claude", shell=True, cwd=os.path.expanduser("~"))


# ── menu ──────────────────────────────────────────────────────────────────────

def build_menu(tray):
    accounts = parse_accounts()
    active = active_account(accounts)
    rows = []

    # header
    if active:
        limited = _limits.is_limited(active["email"])
        label = f"{'⚠' if limited else '●'}  {active['email']}"
    else:
        label = "No active account"
    rows.append(item(label, None, enabled=False))
    rows.append(Menu.SEPARATOR)

    # switch accounts
    for acc in accounts:
        limited = _limits.is_limited(acc["email"])
        prefix = "✓  " if acc["active"] else "    "
        suffix = "  ⚠" if limited else ""
        label = prefix + acc["email"] + suffix

        if acc["active"]:
            rows.append(item(label, None, enabled=False))
        else:
            def make_switch(num, email):
                return lambda i, _: do_switch(i, num, email)
            rows.append(item(label, make_switch(acc["num"], acc["email"])))

    rows.append(Menu.SEPARATOR)

    # rate limits submenu per account
    if accounts:
        limit_items = []
        for acc in accounts:
            email = acc["email"]
            limited = _limits.is_limited(email)
            since = _limits.limited_since(email)
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
        rows.append(item("Rate limits", Menu(*limit_items)))

    # auto-switch toggle
    def toggle_auto_switch(i, _):
        _limits.auto_switch = not _limits.auto_switch
        refresh(i)

    auto_label = ("✓  " if _limits.auto_switch else "    ") + "Auto-switch on rate limit"
    rows.append(item(auto_label, toggle_auto_switch))

    rows.append(Menu.SEPARATOR)

    # add / remove
    rows.append(item("Add account…", lambda i, _: do_add_account(i)))

    if accounts:
        def make_remove(num, email):
            return lambda i, _: do_remove_account(i, num, email)
        remove_items = [item(a["email"], make_remove(a["num"], a["email"])) for a in accounts]
        rows.append(item("Remove account", Menu(*remove_items)))

    rows.append(Menu.SEPARATOR)
    rows.append(item("Restart Claude Code", lambda i, _: do_restart_claude(i)))
    rows.append(Menu.SEPARATOR)
    rows.append(item("Quit", lambda i, _: i.stop()))

    return Menu(*rows)


# ── main ──────────────────────────────────────────────────────────────────────

def main():
    global _tray_icon

    accounts = parse_accounts()
    active = active_account(accounts)
    limited = active is not None and _limits.is_limited(active["email"])
    img = build_tray_image(active, limited)
    title = f"Claude: {active['email']}" if active else "Claude Account Switcher"

    _tray_icon = pystray.Icon("claude-accounts", img, title)
    _tray_icon.menu = build_menu(_tray_icon)

    watcher = LogWatcher(on_rate_limit_detected)
    watcher.start()

    _tray_icon.run()


if __name__ == "__main__":
    main()
