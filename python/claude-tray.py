#!/usr/bin/env python3
"""
Claude Account Tray Switcher
Run: uv run --with pystray --with pillow claude-tray.py
Or:  see claude-tray.bat
"""

import ctypes
import os
import re
import subprocess
import tempfile
import time

from PIL import Image, ImageDraw, ImageFont
import pystray
from pystray import Menu, MenuItem as item

COLORS = ["#4A90D9", "#E8754A", "#5CB85C", "#9B59B6", "#F39C12", "#E74C3C"]
CLAUDE_EXE = os.path.join(os.path.expanduser("~"), ".local", "bin", "claude.exe")

# ── win32 dialogs (no tkinter needed) ────────────────────────────────────────

MB_OK          = 0x00
MB_OKCANCEL    = 0x01
MB_YESNO       = 0x04
MB_ICONINFO    = 0x40
MB_ICONQUESTION= 0x20
IDOK  = 1
IDYES = 6

def msgbox(title, text, style=MB_OK):
    return ctypes.windll.user32.MessageBoxW(0, text, title, style)

# ── helpers ───────────────────────────────────────────────────────────────────

def parse_accounts():
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


def hex_to_rgb(hex_color):
    return (int(hex_color[1:3], 16), int(hex_color[3:5], 16), int(hex_color[5:7], 16))


# ── icon building ─────────────────────────────────────────────────────────────

def get_claude_base_icon(size=64):
    """Extract icon from claude.exe via PowerShell, return PIL Image or None."""
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
        r = subprocess.run(
            ["powershell", "-NoProfile", "-Command", ps],
            capture_output=True, timeout=8,
        )
        if r.returncode == 0 and os.path.exists(tmp):
            img = Image.open(tmp).convert("RGBA").resize((size, size), Image.LANCZOS)
            os.unlink(tmp)
            return img
    except Exception:
        pass
    return None


def make_letter_icon(letter, hex_color, size=64):
    """Fallback: coloured circle with initial."""
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


def overlay_badge(base_img, letter, hex_color, size=64):
    """Overlay a small coloured badge (bottom-right) on top of base_img."""
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


# cached base icon so we don't re-extract every refresh
_claude_base: Image.Image | None = None

def build_tray_image(active, size=64):
    global _claude_base
    if _claude_base is None:
        _claude_base = get_claude_base_icon(size)

    if active:
        color = COLORS[(active["num"] - 1) % len(COLORS)]
        letter = active["email"][0].upper()
        if _claude_base is not None:
            return overlay_badge(_claude_base, letter, color, size)
        return make_letter_icon(letter, color, size)
    else:
        if _claude_base is not None:
            return _claude_base.copy()
        return make_letter_icon("C", "#888888", size)


# ── refresh ───────────────────────────────────────────────────────────────────

def refresh(tray_icon):
    accounts = parse_accounts()
    active = active_account(accounts)
    tray_icon.icon = build_tray_image(active)
    tray_icon.title = f"Claude: {active['email']}" if active else "Claude Account Switcher"
    tray_icon.menu = build_menu(tray_icon)


# ── actions ───────────────────────────────────────────────────────────────────

def do_switch(tray_icon, num, email):
    subprocess.run(["cswap", "--switch-to", str(num)], capture_output=True, timeout=5)
    refresh(tray_icon)
    tray_icon.notify(f"Switched to {email}\nRestart Claude Code to apply.",
                     "Claude Account Switcher")


def do_add_account(tray_icon):
    ret = msgbox(
        "Add Account",
        "Log into the new account in Claude Code first.\n\n"
        "  claude /logout\n  claude /login\n\n"
        "Click OK when done.",
        MB_OKCANCEL | MB_ICONINFO,
    )
    if ret != IDOK:
        return
    r = subprocess.run(["cswap", "--add-account"], capture_output=True, text=True, timeout=5)
    refresh(tray_icon)
    msg = (r.stdout or r.stderr or "Done.").strip()
    tray_icon.notify(msg, "Add Account")


def do_remove_account(tray_icon, num, email):
    ret = msgbox(
        "Remove Account",
        f"Remove account {email}?",
        MB_YESNO | MB_ICONQUESTION,
    )
    if ret != IDYES:
        return
    subprocess.run(["cswap", "--remove-account", str(num)], capture_output=True, timeout=5)
    refresh(tray_icon)
    tray_icon.notify(f"Removed {email}.", "Remove Account")


def do_restart_claude(tray_icon):
    subprocess.run(["taskkill", "/F", "/IM", "claude.exe"], capture_output=True)
    time.sleep(1)
    subprocess.Popen("claude", shell=True, cwd=os.path.expanduser("~"))


# ── menu ──────────────────────────────────────────────────────────────────────

def build_menu(tray_icon):
    accounts = parse_accounts()
    active = active_account(accounts)
    rows = []

    # header
    if active:
        rows.append(item(f"● {active['email']}", None, enabled=False))
        rows.append(Menu.SEPARATOR)

    # switch accounts
    for acc in accounts:
        def make_switch(num, email):
            return lambda i, _: do_switch(i, num, email)

        label = ("✓  " if acc["active"] else "    ") + acc["email"]
        enabled = not acc["active"]
        rows.append(item(label, make_switch(acc["num"], acc["email"]), enabled=enabled))

    rows.append(Menu.SEPARATOR)

    # add account
    rows.append(item("Add account…", lambda i, _: do_add_account(i)))

    # remove account (submenu)
    if accounts:
        def make_remove(num, email):
            return lambda i, _: do_remove_account(i, num, email)

        remove_items = [item(acc["email"], make_remove(acc["num"], acc["email"]))
                        for acc in accounts]
        rows.append(item("Remove account", Menu(*remove_items)))

    rows.append(Menu.SEPARATOR)
    rows.append(item("Restart Claude Code", lambda i, _: do_restart_claude(i)))
    rows.append(Menu.SEPARATOR)
    rows.append(item("Quit", lambda i, _: i.stop()))

    return Menu(*rows)


# ── main ──────────────────────────────────────────────────────────────────────

def main():
    accounts = parse_accounts()
    active = active_account(accounts)

    img = build_tray_image(active)
    title = f"Claude: {active['email']}" if active else "Claude Account Switcher"

    tray = pystray.Icon("claude-accounts", img, title)
    tray.menu = build_menu(tray)
    tray.run()


if __name__ == "__main__":
    main()
