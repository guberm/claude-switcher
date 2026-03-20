# Claude Switcher

A system tray utility for switching between multiple [Claude Code](https://claude.ai/code) accounts without logging out.

No external dependencies — works by directly swapping `~/.claude/.credentials.json`.

---

## Features

- **Tray icon** — Claude's own icon with a colored account badge (turns `!` red when rate limited); rendered at 128px for sharp display on HiDPI screens
- **Real account info** — reads live email and org from `claude auth status` every 30 seconds; no stale display after switching
- **Switch accounts** — click an account; credentials are swapped, Electron profile cache is cleared, and Claude Code restarts automatically
- **Rate limit tracking** — auto-detected from Claude logs; mark/clear manually via submenu
- **Add / Remove accounts** — saves/restores the current logged-in session
- **Restart Claude Code** — kills and relaunches from your home directory; clears Electron's profile cache so account info is always fresh
- **Single instance** — Windows Mutex prevents duplicate tray processes
- **Auto-refresh** — re-reads account info from `claude auth status` every 30 seconds

Two implementations are included:

| Version | Runtime | File |
|---|---|---|
| **C#** (recommended) | .NET 9 (no console window, single `.exe`) | `csharp/publish/ClaudeTray.exe` |
| **Python** | Python + `uv` | `python/claude-tray.py` |

---

## Prerequisites

- [Claude Code](https://claude.ai/code) installed (provides `~/.claude/.credentials.json`)
- No other tools required

---

## C# Version (Recommended)

### Run (pre-built)

```
csharp\publish\ClaudeTray.exe
```

### Build from source

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download):

```powershell
cd "Claude Switcher\csharp"
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o publish
```

### Auto-start with Windows

```powershell
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\ClaudeTray.lnk")
$lnk.TargetPath = "$env:USERPROFILE\Desktop\Claude Switcher\csharp\publish\ClaudeTray.exe"
$lnk.Save()
```

---

## Python Version

Requires [uv](https://github.com/astral-sh/uv):

```powershell
# via .vbs launcher (no console window)
python\claude-tray.vbs

# or directly
uv run --with pystray --with pillow python\claude-tray.py
```

---

## First-time setup

You need to save each account once before switching.

**For each account:**

1. Log into that account in Claude Code (`claude /logout` → `claude /login` if needed)
2. Launch ClaudeTray (or if already running, right-click the tray icon)
3. Right-click → **Save current session as account…**
4. Enter the email address for this account

Repeat for each account. Saved credentials are stored in `~/.claude/accounts/`.

---

## Usage

Right-click the tray icon — the menu shows all accounts:

```
●  guberm@gmail.com [Personal]

✓  guberm@gmail.com
     michael.guber@trip-arc.com  ⚠ rate limited
──────────────────────────────────
Save current session as account…
Remove account ▶
──────────────────────────────────
Restart Claude Code
──────────────────────────────────
Quit
```

The header shows the live email and org — updated every 30 seconds and immediately after each switch.

Click an account to switch — credentials are swapped and Claude Code restarts automatically.

### Configuring the rate-limit reset window

Default is 5 hours (Claude Pro). Edit `~/.claude/claude-switcher.json`:

```json
{
  "resetHours": 5.0,
  "rateLimits": {}
}
```

---

## How it works

Each saved account's `~/.claude/.credentials.json` is stored in `~/.claude/accounts/<email>.json`.
When you switch, ClaudeTray:
1. Copies the chosen account's credentials to `.credentials.json`
2. Clears Electron's Local Storage profile cache (`%APPDATA%/Claude/Local Storage/leveldb/`) so Claude Code shows the new account's email instead of a stale cached one
3. Restarts Claude Code
4. Calls `claude auth status` to confirm the live email and updates the tray header

On startup, the active account is identified by calling `claude auth status` first; if that fails, falls back to matching the `refreshToken` in the live credentials file.

---

## License

MIT
