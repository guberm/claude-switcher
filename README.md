# Claude Switcher

A system tray utility for switching between multiple [Claude Code](https://claude.ai/code) accounts without logging out.

Built on top of [claude-swap](https://github.com/denysvitali/claude-swap).

---

## Features

- **Tray icon** — Claude's own icon extracted from `claude.exe`, with a colored account badge in the corner
- **Switch accounts** — click an account in the menu, get a balloon notification
- **Add account** — guided flow: log in via `claude /login`, then confirm
- **Remove account** — submenu with confirmation dialog
- **Restart Claude Code** — kills and relaunches from your home directory

Two implementations are included:

| Version | Runtime | File |
|---|---|---|
| **C#** (recommended) | .NET 9 (no console window, single `.exe`) | `csharp/publish/ClaudeTray.exe` |
| **Python** | Python + `uv` | `python/claude-tray.py` |

---

## Prerequisites

- [Claude Code](https://claude.ai/code) installed
- [claude-swap](https://github.com/denysvitali/claude-swap) installed:
  ```powershell
  uv tool install claude-swap
  ```

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
# via batch file (no console window with the included .vbs launcher)
python\claude-tray.vbs

# or directly
uv run --with pystray --with pillow python\claude-tray.py
```

---

## Usage

1. Launch `ClaudeTray.exe` (or the Python equivalent)
2. Right-click the tray icon
3. Click an account name to switch
4. When prompted — restart Claude Code (or use **Restart Claude Code** from the menu)

### Adding a second account

1. In Claude Code: `claude /logout` → `claude /login` (log in with the new account)
2. Right-click tray → **Add account…** → click OK

---

## How it works

`claude-swap` stores Claude Code session tokens per account in the Windows credential store (via `keyring`). Switching swaps the active token file that Claude Code reads on startup.

---

## License

MIT
