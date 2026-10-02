# Notch

A dynamic notch for Windows 11, in the spirit of Atoll on macOS: a small black pill at the top-center of the screen that grows to show what is happening on your machine.

## Features

- **Pill and live activities.** Always on top, never takes focus. It widens for ongoing activity and briefly enlarges for system events.
- **Out of the way of games.** While a game or any other fullscreen app shows on its display the notch is taken off the screen completely, HUDs and glow included, also when you are working on another display.
- **Media.** Whatever is playing (Spotify, a browser tab, any app that reports to Windows) shows with its artwork. The expanded view has play/pause, skip and click-to-seek.
- **System HUDs.** Volume, audio output changes, brightness, charger and low battery, Bluetooth devices connecting, Caps Lock switching on or off.
- **Shelf.** Drag files or folders onto the pill to put them aside, then drag them out into any app or folder when you need them. The shelf remembers where the files are; it does not copy them.
- **Terminal for Claude Code and Codex.** A real terminal inside the notch that runs the `claude` and `codex` CLIs (or PowerShell) in a folder you pick. The pill shows which agent is working, needs input, or is done, the folder it is in, and how many other sessions have something to report.
- **Glow effects.** The pill lights up in a colour and rhythm that matches what is happening: it pulses to the music in the album art's colour, breathes blue while an agent works, pulses amber when it needs you, turns green when it is done, and flashes for volume, charging, Bluetooth and low battery. Brightness is adjustable in settings.
- **Notch or Dynamic Island, top or taskbar.** The pill can grow out of the screen edge like a notch or float clear of it as an island, and sit at the top of the screen or in the far left of the taskbar, where it opens upwards.
- **Themes and colour.** Dark, light, or following Windows, with an accent colour of your choice. Stats, the timer and calendars each have their own colour.
- **Widgets.** A countdown timer with a Pomodoro mode (focus and break sessions back to back), your own presets and any length you type (`12`, `1:30`, `90s`, `1h20m`), upcoming events from iCalendar (.ics) feeds, and a Stats tab with CPU, memory, GPU, network and battery.
- **Plugins.** Small .NET libraries can add their own activities to the pill and cards to a Plugins tab. See [Plugins](#plugins).
- **Automatic updates.** An installed copy checks this repository's releases, installs a newer one in the background and restarts itself. Can be switched off in settings.
- **Hotkey.** Alt+Shift+N opens and closes the notch from any app. Change it, or switch it off, in settings.
- **Settings.** From the tray icon: start with Windows, automatic updates, which display to use, position and style, the hotkey, theme and accent colour, what the pill shows, glow brightness, timer presets, calendar feeds, plugins.

## Install

Download `Notch-Setup-<version>.exe` from the [releases page](https://github.com/Brick-Bread/WNotch/releases) and run it. It installs for the current user without admin rights and needs nothing else preinstalled. The installer is not code-signed, so Windows SmartScreen will ask for confirmation.

The terminal uses the WebView2 runtime, which ships with Windows 11.

## Using it

- Hover over the pill (or click it) to expand it. Move away to close it. The hotkey (Alt+Shift+N unless you changed it) opens it and keeps it open until you press it again or move the pointer onto the notch and away.
- **Shelf tab:** drag files from Explorer onto the pill; the notch opens and they land on the shelf. Drag a tile out to use the file, double-click to open it, right-click for more. A file that has been moved or deleted leaves the shelf by itself.
- **Terminal tab:** pick a folder, then `+ Claude`, `+ Codex` or `+ Shell`. While the terminal has keyboard focus the notch stays open; click any other window to close it.
- **Timer:** press a preset, or **+ Custom** and type a length; a bare number is minutes. Enter starts it, Esc cancels.
- **Tray icon:** left-click for settings, right-click to switch theme or quit.

Things to know:

- Windows still shows its own volume and brightness flyout next to the notch's. There is no supported way to turn it off.
- Brightness HUDs only work for built-in panels; external monitors do not report brightness changes.
- The hotkey only works when no other app, and not Windows itself, already uses the same keys; settings tells you when it is taken. Windows keeps most combinations with the Win key for itself.
- Dragging a file from the shelf into an Explorer folder on the same drive moves it there, as dragging between two folders does; hold Ctrl to copy.
- Calendar events come from .ics links. Google Calendar and Outlook both offer one in their sharing settings.
- Updates are checked shortly after start and every four hours. The installer is only taken from this repository's release downloads and is checked against the size and SHA-256 digest GitHub lists before it runs. The restart waits until the notch is closed, no terminal session is open and no timer is running. Copies not installed by the installer (development builds) never update themselves.
- Agent status relies on hooks the app passes when it starts a CLI (`--settings` for Claude, a `notify` override for Codex). Sessions you start in other terminals are not tracked. Codex only reports the end of a turn, so its "working" state is inferred from terminal activity.

## Plugins

A plugin is a folder in `%AppData%\Notch\plugins` holding a `plugin.json` and a .NET library. To install one from GitHub, type its repository (`owner/name` or a link) into the box under **Plugins** in Settings and press **Install**, then save. Entering `Brick-Bread/WNotch` installs the sample plugin. A plugin folder can also be copied in by hand (Settings has an **Open folder** button) and ticked in the same list.

Plugins are not sandboxed: they run inside Notch and can do anything Notch can. An installed plugin does nothing until you switch it on, so only switch on plugins you trust.

To write one and publish it on GitHub so others can install it by name, read the [plugin guide](docs/plugins.md). [`samples/BreakReminder`](samples/BreakReminder) is a complete example: a card that counts down to your next break and a nudge in the pill when it is due.

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
| `--tab=home\|terminal\|stats\|shelf\|plugins` | Start on that tab |
| `--open=claude\|codex\|shell` | Start a terminal session at launch |
| `--settings` | Open the settings window at launch |
| `--theme=dark\|light\|system` | Show that theme for this run without changing the saved setting |
| `--style=notch\|island`, `--position=topcenter\|taskbarleft` | Show that style or position for this run without changing the saved settings |
| `--display=<n>` | Use display *n* (as numbered in settings) for this run, e.g. to keep a debug build off the screen an installed copy is on |
| `--plugin=<folder>` | Load a plugin from its build output and run it whether or not it is enabled; can be repeated |
| `--trace-hover` | Log pointer enter/leave, opening, closing, window resizes and the hotkey to `%LocalAppData%\Notch\hover.log`, to diagnose hover problems |
| `--software-render` | Draw without the GPU, which makes rendering-timing problems easier to reproduce |

Settings live in `%AppData%\Notch\settings.json` and the shelf in `shelf.json` beside it; unexpected errors are logged to `%LocalAppData%\Notch\errors.log`, and plugin starts, stops and failures to `%LocalAppData%\Notch\plugins.log`.

### Releasing

Set `<Version>` in `Directory.Build.props` to match the tag; installed copies compare it with the latest release to decide whether to update.

Pushing a tag such as `v0.2.0` runs `.github/workflows/release.yml`, which tests, publishes a self-contained build, compiles the NSIS installer (`installer/Notch.nsi`) and attaches it to a GitHub release, together with the sample plugin as a zip. Running that workflow by hand builds the installer as a workflow artifact without releasing.

## Layout

| Project | Purpose |
|---|---|
| `src/Notch.App` | WPF app: the notch window, tabs, settings window, tray icon, xterm.js assets |
| `src/Notch.Core` | UI-free logic: activity arbitration, agent status, timer, calendar parsing, settings, the plugin API and loader |
| `src/Notch.Platform` | Windows integrations: media sessions, audio, power, Bluetooth, ConPTY, stats |
| `src/Notch.Hook` | Small helper the Claude and Codex CLIs run to report agent status to the app |
| `samples/BreakReminder` | Example plugin |
| `tests/Notch.Core.Tests` | xUnit tests for `Notch.Core` |
| `docs/plugins.md` | Guide to writing plugins |



## License

MIT. The terminal bundles [xterm.js](https://xtermjs.org) (MIT).

- made by Brick_Bread
