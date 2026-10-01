# Notch

A dynamic notch for Windows 11, in the spirit of Atoll on macOS: a small black pill at the top-center of the screen that grows to show what is happening on your machine.

## Features

- **Pill and live activities.** Always on top, never takes focus, hides while an app is fullscreen. It widens for ongoing activity and briefly enlarges for system events.
- **Media.** Whatever is playing (Spotify, a browser tab, any app that reports to Windows) shows with its artwork. The expanded view has play/pause, skip and click-to-seek.
- **System HUDs.** Volume, audio output changes, brightness, charger and low battery, Bluetooth devices connecting.
- **Terminal for Claude Code and Codex.** A real terminal inside the notch that runs the `claude` and `codex` CLIs (or PowerShell) in a folder you pick. The pill shows whether an agent is working, needs input, or is done.
- **Widgets.** A countdown timer, upcoming events from iCalendar (.ics) feeds, and a Stats tab with CPU, memory, GPU, network and battery.
- **Settings.** From the tray icon: start with Windows, which display to use, what the pill shows, calendar feeds.

## Install

Download `Notch-Setup-<version>.exe` from the [releases page](https://github.com/Brick-Bread/WNotch/releases) and run it. It installs for the current user without admin rights and needs nothing else preinstalled. The installer is not code-signed, so Windows SmartScreen will ask for confirmation.

The terminal uses the WebView2 runtime, which ships with Windows 11.

## Using it

- Hover over the pill (or click it) to expand it. Move away to close it.
- **Terminal tab:** pick a folder, then `+ Claude`, `+ Codex` or `+ Shell`. While the terminal has keyboard focus the notch stays open; click any other window to close it.
- **Tray icon:** left-click for settings, right-click to quit.

Things to know:

- Windows still shows its own volume and brightness flyout next to the notch's. There is no supported way to turn it off.
- Brightness HUDs only work for built-in panels; external monitors do not report brightness changes.
- Calendar events come from .ics links. Google Calendar and Outlook both offer one in their sharing settings.
- Agent status relies on hooks the app passes when it starts a CLI (`--settings` for Claude, a `notify` override for Codex). Sessions you start in other terminals are not tracked. Codex only reports the end of a turn, so its "working" state is inferred from terminal activity.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 10 2004 or later.

```
dotnet build
dotnet test
dotnet run --project src/Notch.App -- --demo
```

Development flags for `Notch.exe`:

| Flag | Effect |
|---|---|
| `--demo` | Fake media and a loop of fake activities, to see every pill state |
| `--pin-open` | Keep the notch expanded |
| `--tab=home\|terminal\|stats` | Start on that tab |
| `--open=claude\|codex\|shell` | Start a terminal session at launch |
| `--settings` | Open the settings window at launch |

Settings live in `%AppData%\Notch\settings.json`; unexpected errors are logged to `%LocalAppData%\Notch\errors.log`.

### Releasing

Pushing a tag such as `v0.1.0` runs `.github/workflows/release.yml`, which tests, publishes a self-contained build, compiles the NSIS installer (`installer/Notch.nsi`) and attaches it to a GitHub release. Running that workflow by hand builds the installer as a workflow artifact without releasing.

## Layout

| Project | Purpose |
|---|---|
| `src/Notch.App` | WPF app: the notch window, tabs, settings window, tray icon, xterm.js assets |
| `src/Notch.Core` | UI-free logic: activity arbitration, agent status, timer, calendar parsing, settings |
| `src/Notch.Platform` | Windows integrations: media sessions, audio, power, Bluetooth, ConPTY, stats |
| `src/Notch.Hook` | Small helper the Claude and Codex CLIs run to report agent status to the app |
| `tests/Notch.Core.Tests` | xUnit tests for `Notch.Core` |

## License

MIT. The terminal bundles [xterm.js](https://xtermjs.org) (MIT).
