# TaskBoard

Personal Trello-style to-do board for Windows. Run `TaskBoard.exe`.

- **Your data:** `%APPDATA%\TaskBoard\data.json` (saved automatically on every change).
  Daily backups for 30 days are in `%APPDATA%\TaskBoard\backups`. Settings → Backup & data can export and import.
- **Starts with Windows:** toggle in Settings → Windows & reminders, or right-click the tray icon.
- **Closing the window** keeps it running in the system tray so reminders still fire. Use the tray icon's **Quit** to exit fully.
- **If you move `TaskBoard.exe`**, open it once from the new location and the auto-start entry updates itself.

## Live wallpaper (Wallpaper Engine)

Go to Settings → Live wallpaper → **Set as wallpaper**. This installs "TaskBoard (live)" into
Wallpaper Engine's `myprojects` folder and applies it.

- The wallpaper checks TaskBoard about once a second through a private link on `localhost:47821`, which requires a secret key.
- TaskBoard must be running for the wallpaper to update. It can sit in the tray.
- In Wallpaper Engine's properties you can set the size, position, margins, which board to show, and whether the clock and agenda appear.

## Rebuilding after changes

The UI is `source/web/index.html` and the Windows host is `source/*.cs`. To rebuild, run:

```powershell
powershell -ExecutionPolicy Bypass -File ".\source\build.ps1"
```

This produces a new `TaskBoard.exe` in this folder. Your data isn't touched.
