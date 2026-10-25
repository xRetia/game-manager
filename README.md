# GameManager

A game launcher for multi-seat Windows setups. It lists every `.lnk` under
`C:\MultiSeatGames\Links` as a tile grid and launches games in the right user
session. The UI is brought up by opening `steam://open/bigpicture` from
anywhere - browser, Run dialog, whatever the seat has access to.

Single-file WPF app on .NET 9, around 210 KB, framework-dependent.

## How it works

The `steam://` protocol is registered in HKLM, so it works for every user.
The protocol handler passes the target session ID to GameManagerService (a
Windows service running as SYSTEM) over a named pipe. The service then takes
that user's elevated token (WTSQueryUserToken + TokenLinkedToken) and starts
the UI inside their session with CreateProcessAsUser. The UI never runs as
SYSTEM.

Each user gets one UI instance, guarded by a named mutex. If an instance is
already running, the service sends ACTIVATE over the pipe and the existing
window comes to the front instead of spawning a new one.

A process started by a service has no foreground rights, so on startup the
window re-asserts topmost and takes keyboard focus for about two seconds
(AttachThreadInput) or until the user interacts.

## Input

- Click a tile to highlight it, click again to launch. Double-click also
  launches.
- Arrow keys move, Enter launches.
- Gamepad: d-pad or left stick to move, A or X to confirm.
- Touch: flick scrolling with inertia.
- F5 reloads the shortcut folder, F11 toggles borderless fullscreen, Esc
  leaves fullscreen.

## Build

Needs the .NET 9 SDK.

    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

`GameManager.json` is written next to the exe on first run.

## Configuration

GameManager.json, next to the exe:

- `LinksFolder` - where .lnk files are read from. Default
  `C:\MultiSeatGames\Links`.
- `StartFullScreen` - open as borderless fullscreen. The `--fullscreen`
  command-line flag overrides it.

## Installation

The Advanced menu in the UI covers it, all elevated:

- Register / unregister the `steam://` protocol for all users
- Install / remove the service (auto start)

## Remote-session GPU acceleration

RDP-type sessions (including multi-seat seats) enumerate the software
display adapter first by default, so this WPF UI silently renders on the
CPU - while native D3D11/12 games (Unity, Unreal) still get the physical
GPU. The Advanced menu has a one-click policy switch to fix that:

- **Enable GPU acceleration for remote sessions** - sets the
  "Use hardware graphics adapters for all Remote Desktop Services
  sessions" policy (`bEnumerateHWBeforeSW`), prefers AVC444 for quality
  and hardware encoding so the RDP encoder spares the CPU. Elevated,
  machine-wide.
- The change applies to **newly created sessions** - reconnect existing
  seats or reboot to pick it up.
- The About box render diagnostics tell you the current state: whether
  you are in a remote session, whether the GPU policy is on, and the
  measured active framerate.

Equivalent registry values (HKLM
`SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services`, all DWORD 1):
`bEnumerateHWBeforeSW`, `bAVC444Mode`, `AVCHardwareEncodePreferred`.
Requires Windows 10 / Server 2016 or newer.

## Rebuilding while it's running

The service and any running UI instance lock the exe. Before publishing:

    taskkill /f /im GameManager.exe
    sc stop GameManagerService

then build, then `sc start GameManagerService`. Both need an elevated prompt.

## Files

- `App.xaml(.cs)` - styles, mode dispatch on startup
- `MainWindow.xaml(.cs)` - the UI, keyboard/gamepad/touch/scroll handling
- `ServiceHost.cs` - service host, pipe server, per-session startup
- `Launcher.cs` - protocol registration, elevation, config path
- `SingleInstance.cs` - per-user mutex and activation
- `GameInfo.cs` - tile model
- `GamePad.cs` - joyGetPosEx polling
- `ShellIcons.cs` - icon extraction from .lnk
- `Config.cs` - json read/write
- `Strings.cs` - bilingual (Chinese/English) UI strings, auto-detected

## License

Copyright (C) 2026 xRetia

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program in the [LICENSE](LICENSE) file. If not, see
<https://www.gnu.org/licenses/>.
