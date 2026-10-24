# Dim

A Windows tray app that dims the desktop. A small tab hangs from the top of the screen. Drag the slider, or hold Ctrl+Alt and scroll, to change how much of the screen still shows through.

## Build

You need Windows and the .NET Framework compiler that ships with Visual Studio or the Build Tools. `build.ps1` looks for Roslyn `csc.exe` and links against .NET Framework 4.x.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

That writes `bin\DimWidget.exe`.

## Run

Start `bin\DimWidget.exe`. It sits in the notification area. Launching it again focuses the copy that is already running.

- Drag the tab at the top of the screen.
- Ctrl+Alt and the scroll wheel changes brightness.
- Ctrl+Alt and + or - steps brightness by 5.
- Ctrl+Alt+0 sets brightness back to 100%.
- Ctrl+Alt+D pauses dimming.

Right-click the tray icon for 100%, 75%, 50%, and 25% presets, pause, start with Windows, and quit.

Settings are stored in `%AppData%\DimWidget\settings.ini`.
