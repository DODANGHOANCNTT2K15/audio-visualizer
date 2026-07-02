# Audio Visualizer

Audio Visualizer is a lightweight Windows desktop audio spectrum overlay built with WPF and NAudio. It stays out of the way, reacts to system audio, shows current media metadata, and adapts its position between desktop and active-app workflows.

## Features

- Real-time system audio spectrum visualization.
- Floating always-on-top WPF overlay.
- Smart desktop positioning: center-bottom on desktop, corner position while working in apps.
- Hover behavior for showing current artist and title.
- Tray icon with reset, exit, and start-with-Windows toggle.
- Single-instance startup guard.
- Windows startup support through the current user's Run registry key.
- Lightweight long-running behavior with reused audio/render buffers.

## Requirements

- Windows 10 version 2004 or newer.
- .NET 8 SDK.

## Run

```powershell
dotnet run -c Release
```

## Build

```powershell
dotnet build -c Release
```

The built executable is created at:

```powershell
bin\Release\net8.0-windows10.0.19041.0\AudioVisualizer.exe
```

## Start With Windows

Run the app, right-click the tray icon, then enable:

```text
Start with Windows
```

This writes the executable path to:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

No administrator permission is required.

## Development Notes

- `bin/` and `obj/` are ignored by git.
- The app uses `WasapiLoopbackCapture` from NAudio to read system audio.
- Media metadata is read from Windows media transport controls.
- Desktop detection uses lightweight Win32 polling to reposition the overlay.

## Repository

Suggested GitHub description:

```text
Lightweight WPF system audio visualizer overlay for Windows with tray controls, media metadata, and smart desktop positioning.
```
