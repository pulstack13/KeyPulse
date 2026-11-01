# KeyPulse

KeyPulse is a Windows desktop utility for typing prepared text into the currently focused application. It uses Windows keyboard input and includes a stop control for an active typing run.

## What is in this repository

- `src/` — C# WinForms application source.
- `ui/` — the app's HTML interface and logo assets.
- `lib/` — Microsoft WebView2 SDK assemblies used to build the app.
- `installer/` — source and build script for the Windows setup program.
- `build.ps1` — builds the app from source.

Generated executables and installer payloads are intentionally excluded from Git. Publish `release/KeyPulse-Setup.exe` as a GitHub Release asset after building it.

## Requirements

- Windows 10 or 11.
- .NET Framework 4.8 (included with supported Windows versions).
- The .NET Framework C# compiler (`csc.exe`).
- Microsoft Edge WebView2 Runtime to display the app UI. The installer checks for it and offers Microsoft's download page if it is missing.

## Build the app

Open PowerShell in this repository and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

The app and its required WebView2 assemblies are written to `dist/`.

## Build the installer

Build the app first, then run:

```powershell
.\installer\build-installer.ps1
```

The installer is written to `release/KeyPulse-Setup.exe`. It installs KeyPulse under the current user's Local AppData, creates a Start menu shortcut, and creates a Desktop shortcut by default. No administrator rights are needed for that per-user install.

## Use

Launch KeyPulse, enter or load text, focus the destination app, then press **F5** or **Ctrl+Alt+Space**. Press **Esc** to stop typing. Input is sent to the focused app, so confirm the destination and cursor position before starting. Some elevated, secure, or protected applications may reject simulated keyboard input.

## WebView2 component notice

The WebView2 SDK assemblies in `lib/` are Microsoft components, version 1.0.2210.55. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the matching Microsoft.Web.WebView2 package terms before redistribution.

## License

No project license has been selected yet. Before publishing this repository publicly, choose and add a license if you want others to have permission to use, modify, or redistribute the project. Without a license, standard copyright restrictions apply.
