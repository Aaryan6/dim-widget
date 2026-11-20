# Dim

<p align="center">
  <img src="assets/notch.gif" alt="The notch at the top of the screen, with a slider for brightness" width="100%">
</p>

Dim makes your screen darker. It is a small Windows program. You do not install anything else.

## Download

1. Click [Download Dim](https://github.com/Aaryan6/dim-widget/releases/latest/download/DimWidget.exe) and save the file.
2. Open your Downloads folder.
3. Double-click `DimWidget.exe`.

Windows may say it protected your PC. That happens because Dim is not from the Microsoft Store. Click **More info**, then **Run anyway**.

An icon shows up by the clock. A small tab also appears at the top of the screen. If you do not see the icon, click the upward arrow next to the clock and look for it there.

## Make the screen darker or brighter

Click the tab at the top of the screen, then drag the slider.

On supported laptops, one slider controls the whole range. Move it left to lower Windows brightness. Past the small minimum marker, Windows stays at its lowest brightness and Dim adds extra dimming. Move right to brighten the screen again.

The percentage shows Windows brightness in the normal range. A negative percentage shows extra dimming below the hardware minimum, such as -50%. The leftmost fifth of the slider is reserved for extra dimming.

Dim reads your current Windows brightness when it starts and stays in sync with Windows and your brightness keys. Pausing or quitting Dim removes extra dimming and leaves Windows brightness as you set it. Displays without Windows brightness support use software dimming across the whole slider.

You can also hold the Ctrl and Alt keys and roll the mouse wheel.

On Windows 10 version 2004 or later and Windows 11, the dimming layer is excluded from screenshots and screen recordings that use Windows capture APIs. Your screen stays dim while the captured image keeps its original brightness.

## Shortcuts

- Hold Ctrl and Alt, then press the + key. The screen gets brighter.
- Hold Ctrl and Alt, then press the - key. The screen gets darker.
- Hold Ctrl and Alt, then press 0. Brightness goes back to normal.
- Hold Ctrl and Alt, then press D. Dimming pauses.

## The menu

Right-click the icon by the clock.

- **Show controls** opens the tab.
- **Pause dimming** turns the dark layer off until you turn it back on.
- **Brightness** has 100%, 75%, 50%, and 25%. 100% is a normal screen.
- **Start with Windows** opens Dim each time the computer turns on.
- **Check for updates** opens the latest release page in your browser.
- **Quit Dim** closes the program. The screen goes back to normal.

## Updates

Dim checks for a newer release shortly after startup and once a day while running.
A tray notification appears once for each new version. Click it to open the download page.
To install an update, quit Dim, replace your existing `DimWidget.exe` with the downloaded file, and open it again. Your settings are kept.

If you have an older version without update notifications, download the latest version manually once to enable future notifications.

Before publishing a release, update `AppVersion.Current` in `src/AppVersion.cs` to match the release tag, such as `1.0.7` for `v1.0.7`, then run `build.ps1` and upload `bin/DimWidget.exe`.

Run `.\test-updates.ps1` to test update handling without network requests or visible notifications. Add `-Live` to also verify the GitHub connection.

Run `.\test-capture.ps1` on an interactive desktop to verify screenshot brightness, including after an overlay is recreated. It briefly displays a small test swatch.

Run `.\test-brightness.ps1` to test Windows brightness handling. Add `-Live` to briefly change the laptop's actual brightness and restore its original value.

## Turn it off

Right-click the icon by the clock and choose **Quit Dim**.

Dim works on Windows 10 and Windows 11.
