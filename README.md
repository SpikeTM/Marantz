# Marantz SR5010 Desktop Control

Windows desktop controller for Marantz receivers on the local network.

## What is included

- WPF desktop app (Main zone and network-audio focused controls)
- Full Zone2 controls (power, volume, mute, source, quick select, sleep)
- Live status polling from receiver XML endpoints
- Command dispatch to receiver put handlers
- Auto-discovery of available input sources and network presets
- LAN scan for Marantz/Denon receivers using SSDP/UPnP discovery
- First-run profile setup and saved multi-receiver profiles
- Inno Setup packaging script
- Build script that publishes and produces a setup executable

## Build the app

From the repository root:

```powershell
dotnet build .\MarantzDesktopControl\MarantzDesktopControl.csproj -c Release
```

## Build installer package

From the repository root:

```powershell
.\build-installer.ps1
```

Installer output:

- Installer\MarantzDesktopControl-Setup.exe

## Runtime notes

- Receiver must be reachable on the same LAN.
- Installer is self-contained and includes the required .NET runtime.
- On first launch, the app prompts for a profile and receiver IP.
- You can save multiple profiles and switch them from the top bar.
- Use "Scan Network" to discover compatible receivers, then "Use Selected" and connect.
- The app sends commands compatible with the receiver web control interface.
