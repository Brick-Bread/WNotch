# Notch

A dynamic notch for Windows 11, in the spirit of Atoll on macOS: a small black pill at the top-center of the screen that grows to show what is happening on your machine.

Status: early development. The notch shell works; the feature sources are being built.

## Roadmap

- [x] Notch shell: always-on-top pill, spring-animated states, hover to expand, hides for fullscreen apps
- [ ] Media now-playing with controls and visualizer
- [ ] System HUDs and live activities: volume, brightness, battery, Bluetooth
- [ ] Built-in terminal for the Claude and Codex CLIs, with agent status in the pill
- [ ] Widgets: timer, calendar (ICS feeds), system stats
- [ ] Settings window, installer and auto-update

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 10 2004 or later (built for Windows 11).

```
dotnet build
dotnet test
dotnet run --project src/Notch.App -- --demo
```

`--demo` loops through fake activities so every pill state can be seen without real system events. Quit from the tray icon.

## Layout

| Project | Purpose |
|---|---|
| `src/Notch.App` | WPF app: the notch window, views, tray icon |
| `src/Notch.Core` | UI-free logic: activity model and arbitration, notch modes, spring animation |
| `src/Notch.Platform` | Windows integrations behind plain C# APIs |
| `src/Notch.Hook` | Small helper the Claude and Codex CLIs call to report agent status |
| `tests/Notch.Core.Tests` | xUnit tests for `Notch.Core` |

## License

MIT
