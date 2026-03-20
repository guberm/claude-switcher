Set oShell = CreateObject("WScript.Shell")
Set oFSO = CreateObject("Scripting.FileSystemObject")
sDir = oFSO.GetParentFolderName(WScript.ScriptFullName)
oShell.Run "uv run --with pystray --with pillow """ & sDir & "\claude-tray.py""", 0, False
