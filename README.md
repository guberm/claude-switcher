# Claude Switcher

A system tray utility for switching between multiple [Claude Code](https://claude.ai/code) accounts without logging out.

No external dependencies — works by directly swapping `~/.claude/.credentials.json`.

---

## Features

- **Tray icon** — Claude's own icon with a colored account badge (turns `!` red when rate limited)
- **Switch accounts** — click an account; credentials are swapped and Claude Code restarts automatically
- **Usage bar** — per-account availability bar filling up as the rate limit window passes:
  `████████████████████ ✓ available` or `██████░░░░░░░░░░░░░░ ⚠ 3h 42m`
- **Rate limit tracking** — auto-detected from Claude logs; mark/clear manually via submenu
- **Auto-switch** — on rate limit, automatically switches to the next available account (toggle in menu)
- **Add / Remove accounts** — saves/restores the current logged-in session
- **Restart Claude Code** — kills and relaunches from your home directory
- **Single instance** — Windows Mutex prevents duplicate tray processes

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

Right-click the tray icon — the menu shows all accounts with usage bars:

```
●  michael.guber@trip-arc.com

✓  michael.guber@trip-arc.com
   ████████████████████  ✓ available

     guberm@gmail.com
   ██████░░░░░░░░░░░░░░  ⚠ 3h 42m
──────────────────────────────────
☑  Auto-switch on rate limit
```

Click an account to switch — credentials are swapped and Claude Code restarts automatically.

### Configuring the rate-limit reset window

Default is 5 hours (Claude Pro). Edit `~/.claude/claude-switcher.json`:

```json
{
  "resetHours": 5.0,
  "autoSwitch": true,
  "rateLimits": {}
}
```

---

## How it works

Each saved account's `~/.claude/.credentials.json` is stored in `~/.claude/accounts/<email>.json`.
When you switch, ClaudeTray copies the chosen account's credentials to `.credentials.json` and restarts Claude Code. On startup, the active account is identified by matching the `refreshToken` in the live credentials file.

---

## License

MIT
