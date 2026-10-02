# Notch

A dynamic notch for Windows 11, in the spirit of Atoll on macOS: a small black pill at the top-center of the screen that grows to show what is happening on your machine.

## Features

- **Pill and live activities.** Always on top, never takes focus. It widens for ongoing activity and briefly enlarges for system events.
- **Out of the way of games.** While a game or any other fullscreen app shows on its display the notch is taken off the screen completely, HUDs and glow included, also when you are working on another display.
- **Media.** Whatever is playing (Spotify, a browser tab, any app that reports to Windows) shows with its artwork. The expanded view has play/pause, skip and click-to-seek.
- **System HUDs.** Volume, audio output changes, brightness, charger and low battery, Bluetooth devices connecting, Caps Lock switching on or off.
- **Shelf.** Drag files or folders onto the pill to put them aside, then drag them out into any app or folder when you need them. The shelf remembers where the files are; it does not copy them.
- **Terminal for Claude Code and Codex.** A real terminal inside the notch that runs the `claude` and `codex` CLIs (or PowerShell) in a folder you pick. The pill shows which agent is working, needs input, or is done, the folder it is in, and how many other sessions have something to report.
- **Agents anywhere.** Claude Code, Codex, Gemini CLI and Aider are found wherever they run (Windows Terminal, VS Code, any shell), listed on the Home tab with where they run, and clicking one brings its window to the front. Whether an agent is working, waiting or done shows for the ones that report it through hooks, which you can switch on for all your sessions in settings (see [Agents](#agents-anywhere)).
- **Command palette.** A second hotkey (Alt+Shift+P) opens a search box over everything the notch can do: timers, tabs, terminal sessions, jumping to an agent, switching plugins.
- **Clipboard history.** Optional Clipboard tab to search what you copied, pin things and copy them again. Kept in memory (only pins survive a restart), and copies that password managers mark as private are never kept.
- **Windows notifications in the pill.** Optional and off by default: shows who a notification is from and which app, never the message.
- **Scriptable.** `notch://` links, the `notchctl` command and an optional local webhook; see [docs/automation.md](docs/automation.md).
- **Glow effects.** The pill lights up in a colour and rhythm that matches what is happening: it pulses to the music in the album art's colour, breathes blue while an agent works, pulses amber when it needs you, turns green when it is done, and flashes for volume, charging, Bluetooth and low battery. Brightness is adjustable in settings.
- **Notch or Dynamic Island, top or taskbar.** The pill can grow out of the screen edge like a notch or float clear of it as an island, and sit at the top of the screen or in the far left of the taskbar, where it opens upwards.
- **Themes and colour.** Dark, light, or following Windows, with an accent colour of your choice. Stats, the timer and calendars each have their own colour.
- **Widgets.** A countdown timer with a Pomodoro mode (focus and break sessions back to back), your own presets and any length you type (`12`, `1:30`, `90s`, `1h20m`), upcoming events from iCalendar (.ics) feeds, and a Stats tab with CPU, memory, GPU, network and battery.
- **Plugins.** Small .NET libraries can add their own activities to the pill and cards to a Plugins tab. See [Plugins](#plugins).
- **Update notices.** An installed copy checks this repository's releases and shows a notice in the pill when a newer one is ready. Nothing is installed until you press **Update Notch now** in settings. The notices can be switched off.
- **Hotkey.** Alt+Shift+N opens and closes the notch from any app. Change it, or switch it off, in settings.
- **Settings.** From the tray icon: start with Windows, update notices, which display to use, position and style, the hotkey, theme (including themes offered by plugins) and accent colour, what the pill shows, glow brightness, timer presets, calendar feeds, plugins.

## Install

Download `Notch-Setup-<version>.exe` from the [releases page](https://github.com/Brick-Bread/WNotch/releases) and run it. It installs for the current user without admin rights and needs nothing else preinstalled. The installer is not code-signed, so Windows SmartScreen will ask for confirmation.

The terminal uses the WebView2 runtime, which ships with Windows 11.

## Using it

- Hover over the pill (or click it) to expand it. Move away to close it. The hotkey (Alt+Shift+N unless you changed it) opens it and keeps it open until you press it again or move the pointer onto the notch and away.
- **Shelf tab:** drag files from Explorer onto the pill; the notch opens and they land on the shelf. Drag a tile out to use the file, double-click to open it, right-click for more. A file that has been moved or deleted leaves the shelf by itself.
- **Terminal tab:** pick a folder, then a launcher button (`+ Claude`, `+ Codex`, `+ Shell` by default). Settings → Terminal buttons lets you edit them or add your own, such as a Codex fork or a second account with its own `CLAUDE_CONFIG_DIR`. While the terminal has keyboard focus the notch stays open; click any other window to close it.
- **Timer:** press a preset, or **+ Custom** and type a length; a bare number is minutes. Enter starts it, Esc cancels.
- **Tray icon:** left-click for settings, right-click to switch theme or quit.

Things to know:

- Windows still shows its own volume and brightness flyout next to the notch's. There is no supported way to turn it off.
- Brightness HUDs only work for built-in panels; external monitors do not report brightness changes.
- The hotkey only works when no other app, and not Windows itself, already uses the same keys; settings tells you when it is taken. Windows keeps most combinations with the Win key for itself.
- Dragging a file from the shelf into an Explorer folder on the same drive moves it there, as dragging between two folders does; hold Ctrl to copy.
- Calendar events come from .ics links. Google Calendar and Outlook both offer one in their sharing settings.
- Updates are checked shortly after start and every four hours. The installer is only taken from this repository's release downloads and is checked against the size and SHA-256 digest GitHub lists before it runs. Updates are never installed in the background: pressing **Update Notch now** downloads the installer and restarts the app. Copies not installed by the installer (development builds) neither check nor update.
- Agent status in Notch's own terminal relies on hooks the app passes when it starts a CLI (`--settings` for Claude, a `notify` override for Codex). Codex only reports the end of a turn, so its "working" state is inferred from terminal activity. For agents started elsewhere see [Agents anywhere](#agents-anywhere).
- Updates are checked against the size and SHA-256 digest GitHub lists. Once the maintainer has set an update signing key (`installer/new-update-key.ps1`), an installer also needs a valid signature from that key before it is run.

## Agents anywhere

Notch looks at the running programs every few seconds and lists any Claude Code, Codex, Gemini CLI or Aider session it finds, wherever it runs. Sessions that only run in the background of a desktop app (such as the Claude or Codex desktop apps' own servers) and one-shot `claude -p` runs are left out. Settings has a switch for it and one per agent.

What an agent is *doing* is known only when it reports it. In **Settings > Agents**, tick *Let agents report what they are doing, wherever they run*: Notch then adds its hooks to `~\.claude\settings.json` and `~\.codex\config.toml` (after showing you the files, keeping a backup of each next to it, and never replacing a `notify` command of your own). Unticking removes them again. Without this, an agent found elsewhere is listed as "Open" and does not light the pill.

## Plugins

Browse the [plugin list on the website](https://brick-bread.github.io/WNotch/plugins.html) and press **Install**: Notch opens, shows who made the plugin and what it says it uses, and installs it only if you agree. It lands switched off. **Settings > Plugins > Browse plugins** shows the same list inside Notch. To get your own plugin listed, see [docs/publishing.md](docs/publishing.md).

A plugin is a folder in `%AppData%\Notch\plugins` holding a `plugin.json` and a .NET library. To install one from GitHub, type its repository (`owner/name` or a link) into the box under **Plugins** in Settings and press **Install**, then save. Entering `Brick-Bread/WNotch` installs the sample plugin. A plugin folder can also be copied in by hand (Settings has an **Open folder** button) and ticked in the same list.

Plugins are not sandboxed: they run inside Notch and can do anything Notch can, so only switch on plugins you trust. Notch does check them first. **Plugin checks** (new in 0.11):

- A plugin runs only after you approved its files. Switching one on shows who made it, what it says it uses and anything risky Notch noticed inside it; if the files change later, you are asked again.
- Notch looks inside a plugin's libraries, without running them, and refuses one that reads the keyboard, captures the screen, fakes keyboard or mouse input or reaches into other programs.
- The plugin list on the website can withdraw a plugin for being harmful; Notch stops it, even while it is running.
- **Stop all** in Settings (or "Stop all plugins" in the command palette) ends every plugin at once. `--no-plugins` starts Notch without any, and Notch does that by itself after it stopped while plugins were starting.
- What each plugin did (started, blocked, approved) is written to `%LocalAppData%\Notch\plugin-audit.log`.

These checks catch plugins that announce what they do; a determined author can hide it from a scan. They are a safeguard, not a sandbox: see [Plugin checks](docs/plugins.md#plugin-checks).

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
| `--tab=home\|terminal\|stats\|shelf\|clipboard\|plugins` | Start on that tab |
| `--open=claude\|codex\|shell` | Start a terminal session at launch |
| `--settings` | Open the settings window at launch |
| `--theme=dark\|light\|system` | Show that theme for this run without changing the saved setting |
| `--no-plugins` | Start without running any plugin (safe mode) |
| `--plugin-theme=<plugin id>/<theme id>` | Show a theme a plugin offers for this run (see the plugin guide) |
| `--style=notch\|island`, `--position=topcenter\|taskbarleft` | Show that style or position for this run without changing the saved settings |
| `--display=<n>` | Use display *n* (as numbered in settings) for this run, e.g. to keep a debug build off the screen an installed copy is on |
| `--plugin=<folder>` | Load a plugin from its build output and run it whether or not it is enabled; can be repeated |
| `--trace-hover` | Log pointer enter/leave, opening, closing, window resizes and the hotkey to `%LocalAppData%\Notch\hover.log`, to diagnose hover problems |
| `--software-render` | Draw without the GPU, which makes rendering-timing problems easier to reproduce |

Settings live in `%AppData%\Notch\settings.json` and the shelf in `shelf.json` beside it; unexpected errors are logged to `%LocalAppData%\Notch\errors.log`, and plugin starts, stops and failures to `%LocalAppData%\Notch\plugins.log`.

### Releasing

Set `<Version>` in `Directory.Build.props` to match the tag; installed copies compare it with the latest release to decide whether a newer release exists.

Pushing a tag such as `v0.2.0` runs `.github/workflows/release.yml`, which tests, publishes a self-contained build, builds the installer (`installer/build.ps1`, with the setup program in `src/Notch.Setup`) and attaches it to a GitHub release, together with the sample plugin as a zip. Running that workflow by hand builds the installer as a workflow artifact without releasing.

## Layout

| Project | Purpose |
|---|---|
| `src/Notch.App` | WPF app: the notch window, tabs, settings window, tray icon, xterm.js assets |
| `src/Notch.Core` | UI-free logic: activity arbitration, agent status, timer, calendar parsing, settings, the plugin API and loader |
| `src/Notch.Platform` | Windows integrations: media sessions, audio, power, Bluetooth, ConPTY, stats |
| `src/Notch.Hook` | Small helper the Claude and Codex CLIs run to report agent status to the app |
| `samples/BreakReminder` | Example plugin |
| `samples/NeonTheme` | Example plugin that offers a theme |
| `tests/Notch.Core.Tests` | xUnit tests for `Notch.Core` |
| `docs/plugins.md` | Guide to writing plugins |



## License

MIT. The terminal bundles [xterm.js](https://xtermjs.org) (MIT).

- made by Brick_Bread
