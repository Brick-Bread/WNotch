# Notch

A dynamic notch for Windows 11 and Linux, in the spirit of Atoll on macOS: a small black pill at the top-center of the screen that grows to show what is happening on your machine. Written in Rust with Tauri.

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
- **Plugins.** Plugins can add their own activities to the pill and cards to a Plugins tab. See [Plugins](#plugins).
- **Automatic updates.** An installed copy (the Windows installer, or the Linux AppImage) checks this repository's releases, installs a newer one in the background and restarts itself. Can be switched off in settings.
- **Hotkey.** Alt+Shift+N opens and closes the notch from any app. Change it, or switch it off, in settings.
- **Settings.** From the tray icon: start with Windows, automatic updates, which display to use, position and style, the hotkey, theme and accent colour, what the pill shows, glow brightness, timer presets, calendar feeds, plugins.

## Install

Download the file for your system from the [releases page](https://github.com/Brick-Bread/WNotch/releases/latest).

**Windows 11 (and 10 2004 or later).** Run `Notch-Setup-<version>.exe`. It installs for the current user without admin rights, adds a Start menu entry and an uninstall entry in Settings, and needs nothing else preinstalled except the WebView2 runtime, which ships with Windows 11 (the installer fetches it on older systems). The installer is not code-signed, so Windows SmartScreen will ask for confirmation.

**Linux (x86_64).**

- AppImage: `chmod +x Notch-<version>-x86_64.AppImage` and run it. This is the build that updates itself.
- Debian and Ubuntu: `sudo apt install ./Notch-<version>-amd64.deb`. Updates are installed the same way; the app only tells you that a newer release exists.

Linux notes:

- Notch is a transparent, always-on-top window. That works on X11, and under Wayland through XWayland; on a native Wayland session most compositors do not let a program place itself or stay on top, so start it with `GDK_BACKEND=x11 notch`.
- The tray icon needs an app-indicator host. KDE, Xfce and others have one; GNOME needs the AppIndicator extension. Without a tray, open the notch with the hotkey or by hovering the pill.
- The global hotkey needs X11 (or XWayland, where the focused app is an X11 one); some compositors reserve keys.
- Media comes from MPRIS players, volume from PulseAudio or PipeWire, and brightness from the laptop backlight.
- Linux support is new and not yet verified on every desktop: expect rough edges and please report them.

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
- Updates are checked shortly after start and every four hours. The installer is only taken from this repository's release downloads and is checked against the size and SHA-256 digest GitHub lists before it runs. The restart waits until the notch is closed, no terminal session is open and no timer is running. Copies that were not put in place by a package (development builds, a `.deb`) never update themselves; a `.deb` install is only told that a newer release exists.
- Agent status relies on hooks the app passes when it starts a CLI (`--settings` for Claude, a `notify` override for Codex). Sessions you start in other terminals are not tracked. Codex only reports the end of a turn, so its "working" state is inferred from terminal activity.

## Plugins

Plugins add their own activities to the pill and cards to a Plugins tab. They are not sandboxed: they run inside Notch and can do anything Notch can, and an installed plugin does nothing until you switch it on, so only switch on plugins you trust.

How to install, switch on and write a plugin, including publishing one on GitHub so others can install it by name, is in the [plugin guide](docs/plugins.md).

## Building

Requires the [Rust toolchain](https://rustup.rs) (stable) and, on Linux, the Tauri system libraries:

```
sudo apt install libwebkit2gtk-4.1-dev libgtk-3-dev libayatana-appindicator3-dev librsvg2-dev \
  libxdo-dev libssl-dev libasound2-dev libpulse-dev libdbus-1-dev libudev-dev patchelf
```

On Windows the Visual Studio C++ build tools are needed (the Rust installer asks for them). The user interface is plain HTML, CSS and JavaScript in `ui/`; there is no bundler and no Node step.

```
cargo test --workspace             # unit tests
cargo clippy --workspace --all-targets
cargo run -p notch                 # run the app
cargo run -p notch -- --demo       # a loop of fake activities, to see every pill state
```

Browser regression tests of the interface (Playwright, against a simulated backend):

```
npm install --prefix tests
npx --prefix tests playwright install chromium
npm test --prefix tests
```

The interface also runs on its own in a browser with a mock backend: serve `ui/` with any static server and open `index.html?mock`.

Packages are built with the Tauri CLI (`cargo install tauri-cli --version "^2" --locked`):

```
cargo tauri build --bundles nsis           # Windows: target/release/bundle/nsis/*.exe
cargo tauri build --bundles appimage,deb   # Linux: target/release/bundle/{appimage,deb}
```

Development flags for the `notch` program:

| Flag | Effect |
|---|---|
| `--demo` | Fake activities in a loop, to see every pill state |
| `--screenshots=<folder>` | With `--demo`: save a picture of every state the website shows into the folder, then quit (see below) |
| `--pin-open` | Keep the notch expanded |
| `--tab=home\|terminal\|stats\|shelf\|plugins` | Start on that tab |
| `--open=claude\|codex\|shell` | Start a terminal session at launch |
| `--settings` | Open the settings panel at launch |
| `--theme=dark\|light\|system` | Show that theme for this run without changing the saved setting |
| `--style=notch\|island`, `--position=topcenter\|taskbarleft` | Show that style or position for this run without changing the saved settings |
| `--display=<n>` | Use display *n* (as numbered in settings) for this run, e.g. to keep a debug build off the screen an installed copy is on |

Settings live in `settings.json` in the `notch` folder of your configuration directory (`%AppData%\notch` on Windows, `~/.config/notch` on Linux). Set `RUST_LOG=debug` to see the app's log in the terminal it was started from.

### Pictures for the website

`notch --demo --screenshots=<folder>` goes through each state the website uses (the pill with media, an agent working, needing input and done, the HUDs, then the Home, Stats and Shelf tabs). For each one the page draws itself into a PNG (`ui/demo-capture.js`: an SVG `foreignObject` copy of the page, rasterised on a canvas, so it needs no OS screenshot API and keeps the transparent background). It saves `pill-media.png`, `pill-agent-working.png` and so on, and exits with code 0 only if all twelve were saved. The website currently publishes the pictures made by the old Windows app (`.github/workflows/pages.yml`); switching it over is a matter of running this command in that workflow.

### Releasing

Set `version` under `[workspace.package]` in the root `Cargo.toml` to match the tag; it is the only place the version is written, and installed copies compare it with the latest release to decide whether to update.

Pushing a tag such as `v0.9.0` runs `.github/workflows/release.yml`, which tests on Windows and Linux, builds the NSIS installer, the AppImage and the `.deb`, and attaches them to a GitHub release with a `SHA256SUMS.txt`. The file names are what the updater looks for: `Notch-Setup-<version>.exe`, `Notch-<version>-x86_64.AppImage` and `Notch-<version>-amd64.deb`. Running that workflow by hand builds the packages as workflow artifacts without releasing. `.github/workflows/build.yml` does the same build, with clippy, on every push and pull request.

## Layout

| Path | Purpose |
|---|---|
| `src-tauri` | The app: windows, tray, hotkey, terminals, media, system HUDs, shelf, widgets, updater |
| `crates/notch-core` | Logic without a UI or OS APIs: activity arbitration, agent status, timer, calendar parsing, settings, update rules |
| `ui` | The interface: static HTML, CSS and JavaScript, and the bundled [xterm.js](https://xtermjs.org) |
| `tests` | Browser tests of the interface |
| `docs` | Plugin guide and notes on the rewrite |
| `src`, `installer` | The previous .NET app and its installer, kept until the Rust app has replaced it |

## License

MIT. The terminal bundles [xterm.js](https://xtermjs.org) (MIT).

- made by Brick_Bread
