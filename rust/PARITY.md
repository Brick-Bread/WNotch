# Parity checklist: C# Notch → Rust port

Every behaviour of the C# app that the Rust port must match before it replaces it (plan: "Port Notch to Rust (Tauri)", step 9). Tick an item when the Rust build does the same thing, checked by a test or by hand.

- **Source of truth:** branch `main` at `6bd9554` (v0.21.0). References are `path:line` in that commit. When `main` changes before the switch, add the change here.
- **Markers:** **[changed: …]** marks a deliberate difference decided in the plan. For those, tick the item when the new behaviour is in place.
- **Overlap:** some items appear in more than one part. A command-line flag, for example, is both a wire contract (part 1) and a user-visible behaviour (part 3). Tick each copy.
- **How this was made:** the list was drawn from the C# source and docs, and every reference was checked to exist. If you find a behaviour that is missing, add it.

Contents:

1. [Data, commands and wire formats](#1-data-commands-and-wire-formats): settings keys, files and folders, commands, pipes, flags, registry, install and update.
2. [Plugin API and plugin management](#2-plugin-api-and-plugin-management): every member of plugin API 10, themes, manifest, discovery, trust, scanner, installer, registry.
3. [User-visible features](#3-user-visible-features): window, pill, tabs, widgets, terminal, agents, Settings, tray, updates, demo mode.
4. [Port deliverables](#4-port-deliverables): work that has no C# counterpart but is needed to ship.

## 1. Data, commands and wire formats

### Settings keys (`settings.json`)
- [x] **[changed: a `null` for a list or a string that C# declares non-null reads as empty; C# keeps the `null` and then crashes or treats it as empty]** File format: `System.Text.Json` with `WriteIndented = true`, PascalCase property names (no naming policy), and enums written as strings where the property has `JsonStringEnumConverter`. A missing or corrupt file, or an IO/JSON/access error, gives all defaults — `src/Notch.Core/Settings/AppSettings.cs:225`
- [x] Atomic save: write `settings.json.tmp`, then `File.Move(overwrite)`; creates the parent folder; IO and access errors are swallowed — `src/Notch.Core/Settings/AppSettings.cs:245`
- [x] `DisplayIndex`: `int?`, default `null` (primary display); an index into the system display list — `src/Notch.Core/Settings/AppSettings.cs:17`
- [x] `Style`: `NotchStyle` enum string `"Notch"|"Island"`, default `"Notch"` — `src/Notch.Core/Settings/AppSettings.cs:21`
- [x] `NotchStyle` enum values `Notch`, `Island` — `src/Notch.Core/Settings/NotchAppearance.cs:4`
- [x] `Position`: `NotchPosition` enum string `"TopCenter"|"TaskbarLeft"`, default `"TopCenter"` — `src/Notch.Core/Settings/AppSettings.cs:25`
- [x] `NotchPosition` enum values `TopCenter`, `TaskbarLeft` — `src/Notch.Core/Settings/NotchAppearance.cs:14`
- [x] `ExpandOnHover`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:28`
- [x] `ShelfOpensOnDrag`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:31`
- [ ] `OpenHotkey`: `string`, default `"Alt+Shift+N"`; empty or unparseable means no hotkey (read by `Hotkey.TryParse`) — `src/Notch.Core/Settings/AppSettings.cs:38`
- [x] `HideInFullscreen`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:40`
- [x] `Theme`: `NotchTheme` enum string `"Dark"|"Light"|"System"`, default `"Dark"` — `src/Notch.Core/Settings/AppSettings.cs:44`
- [x] `NotchTheme` enum values `Dark`, `Light`, `System`; `IsLight(theme, systemIsLight)` rule — `src/Notch.Core/Settings/NotchTheme.cs:3`
- [ ] `PluginTheme`: `string?` `"plugin-id/theme-id"`, default `null`; kept while the plugin is off. Choosing a theme from the tray sets it to `null` — `src/Notch.Core/Settings/AppSettings.cs:51`
- [x] `AccentColor`: `string`, default `"Blue"`; anything that is not a preset name (for example `NoAccent = "None"`) means no tint — `src/Notch.Core/Settings/AppSettings.cs:57`
- [x] Accent/glow preset names, in this order: Blue `#3D8BFF`, Cyan `#2EC4FF`, Green `#3FD86B`, Yellow `#FFD84A`, Amber `#FFB01F`, Orange `#FF7A2E`, Red `#FF3B3B`, Violet `#A86BFF`. `White` `#F2F2F2` exists but is not in the named list. Name lookup ignores case — `src/Notch.Core/Activities/Glow.cs:5`
- [x] `GlowEffects`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:62`
- [x] `GlowIntensity`: `int` percent, default `100`, clamped 25..200 (`GlowOutput.MinPercent`/`MaxPercent`/`DefaultPercent`) — `src/Notch.Core/Activities/Glow.cs:115`
- [ ] `NotifyOfUpdates`: `bool`, default `true`; also controls the registry refresh and revocation check at startup — `src/Notch.Core/Settings/AppSettings.cs:68`
- [ ] `LastNotifiedUpdateTag`: `string?`, default `null`; announce each release once — `src/Notch.Core/Settings/AppSettings.cs:71`
- [x] `ShowMedia`: `bool`, default `true`; when off, suppresses the media activity id — `src/Notch.Core/Settings/AppSettings.cs:73`
- [x] `ShowVolume`: `bool`, default `true`; when off, suppresses `HudActivities.VolumeId` and `AudioOutputId` — `src/Notch.Core/Settings/AppSettings.cs:75`
- [x] `ShowBrightness`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:77`
- [x] `ShowPower`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:79`
- [x] `ShowBluetooth`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:81`
- [x] `ShowCapsLock`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:83`
- [x] `SuppressedActivityIds()`, which maps the Show* flags to activity ids — `src/Notch.Core/Settings/AppSettings.cs:179`
- [x] `PomodoroFocusMinutes`: `int`, default `25`; values outside 1..600 fall back to the classic value — `src/Notch.Core/Settings/AppSettings.cs:85`
- [x] `PomodoroShortBreakMinutes`: `int`, default `5`; values outside 1..600 fall back to the classic value — `src/Notch.Core/Settings/AppSettings.cs:87`
- [x] `PomodoroLongBreakMinutes`: `int`, default `15`; values outside 1..600 fall back to the classic value — `src/Notch.Core/Settings/AppSettings.cs:89`
- [x] `TimerPresets`: `List<TimerPreset>`, default `[{Name:"",Seconds:300},{"",900},{"",1800},{"",3600}]` — `src/Notch.Core/Settings/AppSettings.cs:105`
- [x] `TimerPreset` JSON shape `{ "Name": string, "Seconds": int }`; `Duration`, `Label` and `IsValid` are `[JsonIgnore]` — `src/Notch.Core/Widgets/TimerPreset.cs:8`
- [x] `TimerPreset.MaxNameLength = 10`; `IsValid` means 1 s..24 h (`DurationParser.Shortest`/`Longest`) — `src/Notch.Core/Widgets/TimerPreset.cs:10`
- [x] `TimerPreset.TryParse` / `ToLine` text forms: `"15m"`, `"Tea 3m"`, `"Long walk 1h 20m"` — `src/Notch.Core/Widgets/TimerPreset.cs:29`
- [x] `Timers()`: keeps only valid presets, takes at most `MaxTimerPresets = 6`, trims names and cuts them to 10 characters — `src/Notch.Core/Settings/AppSettings.cs:114`
- [x] `HomeWidgets`: `List<HomeWidget>`, default `HomeLayout.Default()` = media(Full), agents(Full), timer(Half), calendar(Half) — `src/Notch.Core/Widgets/HomeLayout.cs:214`
- [x] `HomeWidget.Id`: `string`, default 8 hex characters from a GUID `"N"` — `src/Notch.Core/Widgets/HomeLayout.cs:16`
- [x] `HomeWidget.Kind`: `string`, default `""` — `src/Notch.Core/Widgets/HomeLayout.cs:19`
- [x] `HomeWidget.Width`: `WidgetWidth` enum string `"Full"|"Half"`, default `"Full"` — `src/Notch.Core/Widgets/HomeLayout.cs:22`
- [x] `HomeWidget.HideWhenEmpty`: `bool`, default `true` — `src/Notch.Core/Widgets/HomeLayout.cs:25`
- [x] `HomeWidget.Options`: `Dictionary<string,string>`, default `{}`; toggle values are `"true"`/`"false"` — `src/Notch.Core/Widgets/HomeLayout.cs:28`
- [x] `HomeWidget.Source`: `string?` `"plugin-id/type-id"`, used by `plugin-widget` — `src/Notch.Core/Widgets/HomeLayout.cs:31`
- [x] Widget kinds (stored, never renamed): `media`, `agents`, `timer`, `calendar`, `clock`, `stats`, `shelf`, `clipboard`, `weather`, `note`, `todo`, `launcher`, `command`, `feed`, `plugin`, `plugin-widget` — `src/Notch.Core/Widgets/HomeLayout.cs:53`
- [x] Shared option keys: `title` (`TitleKey`) and note text `text` (`NoteTextKey`) — `src/Notch.Core/Widgets/HomeLayout.cs:122`
- [x] Single-instance widgets: media, agents, timer, calendar, shelf, clipboard — `src/Notch.Core/Widgets/HomeLayout.cs:130`
- [x] Clock options: `title`, `zone`, `hours24`=false, `seconds`=false (Half) — `src/Notch.Core/Widgets/HomeLayout.cs:136`
- [x] Stats options: `cpu`=true, `memory`=true, `gpu`=false, `network`=false, `battery`=false (Half) — `src/Notch.Core/Widgets/HomeLayout.cs:143`
- [x] Weather options: `city`, and `units` = `Celsius` (choices `Celsius`/`Fahrenheit`), from Open-Meteo — `src/Notch.Core/Widgets/HomeLayout.cs:153`
- [x] Note options: `title` = `"Note"`; the typed text is kept in `Options["text"]` — `src/Notch.Core/Widgets/HomeLayout.cs:158`
- [x] Todo options: `title` = `"To do"`; items kept as JSON in `Options["items"]` (`[{Text,Done}]`), at most 100 items of 300 characters each — `src/Notch.Core/Widgets/TodoList.cs:13`
- [x] Launcher options: `title` = `"Launcher"`, `items` (lines `Name = path|folder|url`) — `src/Notch.Core/Widgets/HomeLayout.cs:166`
- [x] Command options: `title` = `"Commands"`, `commands` (lines `Name = command`), `run` = `"Terminal tab"` (choices `"Terminal tab"`/`"In the background"`) — `src/Notch.Core/Widgets/HomeLayout.cs:171`
- [x] Feed options: `title` = `"Feed"`, `url`, `field` (JSON path such as `data.status` or `items.0.title`), `minutes` = `"15 min"` (choices `1/5/15/60 min`) — `src/Notch.Core/Widgets/HomeLayout.cs:177`
- [x] Plugin card options: `card` (choice filled in by the app) — `src/Notch.Core/Widgets/HomeLayout.cs:184`
- [x] `HomeLayout.MaxWidgets = 24`; `Normalize` drops unknown kinds, `plugin-widget` without a `Source`, and duplicate single widgets; it fixes ids, empty options and invalid widths — `src/Notch.Core/Widgets/HomeLayout.cs:266`
- [x] Row layout: a Full widget sits alone, consecutive Half widgets pair up, and an unpaired Half sits alone on the left — `src/Notch.Core/Widgets/HomeLayout.cs:227`
- [x] `CalendarFeeds`: `List<string>` of .ics URLs, default `[]` — `src/Notch.Core/Settings/AppSettings.cs:127`
- [x] `EnabledPlugins`: `List<string>` of plugin ids, default `[]` — `src/Notch.Core/Settings/AppSettings.cs:130`
- [x] `TerminalPresets`: `List<string>`, default `["Claude = claude","Codex = codex","opencode = opencode","Shell = powershell"]` — `src/Notch.Core/Terminal/TerminalProfile.cs:39`
- [x] Terminal preset line grammar: `Name = [VAR=value ...] command [args]`; double quotes group words, and an unbalanced quote rejects the line. A variable name is letters, digits and `_` and does not start with a digit. At most `MaxPresets = 6`; when no line is usable, the defaults are used. The id is a slug of the name, with `-2`, `-3`… on collision and `terminal` when empty. The agent is detected from the command name ("claude" substring, or `AgentCatalog.ForCommand`) — `src/Notch.Core/Terminal/TerminalPresets.cs:14`
- [x] `RecentFolders`: `List<string>`, default `[]`; most recent first, deduplicated ignoring case, at most 8 — `src/Notch.Core/Settings/AppSettings.cs:139`
- [x] `WebhookEnabled`: `bool`, default `false` — `src/Notch.Core/Settings/AppSettings.cs:142`
- [x] `WebhookPort`: `int`, default `47890` — `src/Notch.Core/Automation/WebhookRequest.cs:27`
- [ ] `WebhookToken`: `string?`, default `null`; generated and saved the first time the webhook is applied while enabled — `src/Notch.App/Automation/WebhookService.cs:47`
- [x] `PaletteHotkey`: `string`, default `"Alt+Shift+P"` — `src/Notch.Core/Settings/AppSettings.cs:151`
- [x] `DetectAgents`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:154`
- [x] `HiddenAgents`: `List<string>` of AgentCatalog ids, default `[]` — `src/Notch.Core/Settings/AppSettings.cs:157`
- [x] AgentCatalog ids: `claude`, `codex`, `gemini`, `aider`, `opencode` — `src/Notch.Core/Agents/AgentCatalog.cs:46`
- [x] `WatchAppSessions`: `bool`, default `true` — `src/Notch.Core/Settings/AppSettings.cs:160`
- [x] `GlobalAgentHooks`: `bool`, default `false` — `src/Notch.Core/Settings/AppSettings.cs:167`
- [x] `ClipboardHistory`: `bool`, default `false` — `src/Notch.Core/Settings/AppSettings.cs:170`
- [x] `MirrorNotifications`: `bool`, default `false` — `src/Notch.Core/Settings/AppSettings.cs:173`
- [x] `MirroredApps`: `List<string>`, default `[]` (empty means all apps) — `src/Notch.Core/Settings/AppSettings.cs:176`

### Data files and folders
- [x] Folder name: `Notch`, or `Notch-Debug` in a DEBUG build unless `--shared-data` is on the command line (checked ignoring case against `Environment.GetCommandLineArgs()`) — `src/Notch.Core/Settings/DataFolders.cs:15`
- [x] Roaming root `%AppData%\<Name>` (settings and plugins); Local root `%LocalAppData%\<Name>` (logs, caches, downloads) — `src/Notch.Core/Settings/DataFolders.cs:27`
- [x] `SeedSettings`: copies `%AppData%\Notch\settings.json` to this build's `settings.json` only when the target is missing, the source exists and the paths differ. The copy has `WebhookEnabled=false` and `GlobalAgentHooks=false`. Called at every startup (a no-op in release) — `src/Notch.Core/Settings/DataFolders.cs:38`
- [x] `%AppData%\Notch\settings.json` (with `settings.json.tmp` while saving) — `src/Notch.App/App.xaml.cs:116`
- [ ] `%AppData%\Notch\plugins\` holds the installed plugins, one folder each with `plugin.json` — `src/Notch.App/App.xaml.cs:152`
- [ ] Plugin folder temporaries: `.installing-<guid>` (staging), `.installing-<guid>.zip` (download), `.update-<folder>` (pending update, swapped in at the next start), `.old-<folder>`; leftovers are cleaned up — `src/Notch.Core/Plugins/PluginInstaller.cs:60`
- [ ] Plugin install limits: download at most 64 MB, unpacked at most 256 MB, at most 2000 entries; User-Agent `Notch-Plugins` — `src/Notch.Core/Plugins/PluginInstaller.cs:55`
- [ ] `ApplyPendingUpdates` runs before any plugin loads — `src/Notch.App/App.xaml.cs:166`
- [ ] `<plugin folder>\.notch-source` holds the install origin as JSON `{"Repository":"owner/repo","Tag":"vX"}` — `src/Notch.Core/Plugins/PluginUpdates.cs:16`
- [ ] Built-in plugins folder `<AppDir>\plugins\` (New Age theme at `plugins\new-age`) — `src/Notch.App/App.xaml.cs:160`
- [ ] `%AppData%\Notch\plugin-data\<id>\` is each plugin's data folder, created when first used — `src/Notch.Core/Plugins/PluginHost.cs:70`
- [ ] `%AppData%\Notch\plugin-data\<id>\settings.json` holds a plugin's settings, a JSON object of `JsonElement` values, written indented and atomically via `.tmp` — `src/Notch.Core/Plugins/PluginHost.cs:875`
- [ ] A dev build (`--plugin=`) that has an installed plugin's id uses data id `<id>.dev` — `src/Notch.Core/Plugins/PluginManager.cs:165`
- [ ] Copies of plugin dev builds go to `%TEMP%\Notch\plugin-builds\<pid>\<id>-<n>`; old copies are deleted — `src/Notch.Core/Plugins/PluginManager.cs:95`
- [ ] `%AppData%\Notch\plugin-trust.json` maps plugin id to `{Hash, Approved}`, where Hash is the lowercase-hex SHA-256 tree hash. When the file does not exist, the plugins already in `EnabledPlugins` are approved (first checked run) — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:125`
- [ ] `%AppData%\Notch\registry.json` is the cached registry from `https://brick-bread.github.io/WNotch/plugins/registry.json`: `{ "plugins":[...], "revoked":[{id,sha256?,reason}] }`, at most 500 entries and 1000 revocations — `src/Notch.Core/Plugins/PluginRegistry.cs:39`
- [ ] Revocations parse: `id` of 1..64 characters, `sha256` lowercased (null revokes every version) — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:141`
- [ ] `%AppData%\Notch\plugins-starting` is an empty marker written while plugins start and deleted after. Found at startup, it turns on safe mode and shows the "Plugins are off" notice. Not written in safe mode; deleted on exit — `src/Notch.App/App.xaml.cs:163`
- [ ] `%LocalAppData%\Notch\plugins.log` has lines `{u time} [level] source: message` (source `notch` for the plugin manager). Above 1 MB it rotates to `plugins.log.old` — `src/Notch.Core/Plugins/PluginLog.cs:6`
- [ ] `%LocalAppData%\Notch\plugin-audit.log` is NDJSON `{time(ISO "O"), plugin, kind, detail(≤500 chars)}`. Above 1 MB it rotates to `plugin-audit.log.1` — `src/Notch.Core/Plugins/Checks/PluginAudit.cs:26`
- [ ] `%AppData%\Notch\shelf.json` is an indented JSON array of paths, saved atomically via `.tmp`. Not loaded or saved under `--demo` — `src/Notch.Core/Shelf/ShelfStore.cs:6`
- [x] `%AppData%\Notch\clipboard-pins.json` holds only pinned items, as `ClipboardItem{Id,Kind(number: 0 Text,1 Image,2 Files),CopiedAt,Text,Image(base64 PNG),Files,Source,Pinned}`. The file is deleted when nothing is pinned; saved via `.tmp` — `src/Notch.Core/Clipboard/ClipboardHistory.cs:255`
- [x] The rest of the clipboard history is in memory only: capacity 50, text at most 100 000 characters, image at most 4 MB, at most 50 files — `src/Notch.Core/Clipboard/ClipboardHistory.cs:58`
- [ ] Clipboard path from the window — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:22`
- [x] Notes and to-dos have no separate files; they are stored inside `settings.json` `HomeWidgets[].Options` — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:648`
- [ ] `%LocalAppData%\Notch\errors.log`: unhandled UI exceptions are appended as `{u time} {exception}` plus a blank line — `src/Notch.App/App.xaml.cs:656`
- [ ] `%LocalAppData%\Notch\hover.log` is overwritten on each run, only with `--trace-hover` — `src/Notch.App/Shell/HoverTrace.cs:19`
- [ ] `%LocalAppData%\Notch\updates\` holds update downloads `Notch-Setup-<Version>.exe` (with `.part` while downloading). The folder is deleted at every start — `src/Notch.App/Updates/UpdateService.cs:38`
- [ ] `%LocalAppData%\Notch\update-error.txt` is written by Setup when an update fails. The app shows it once as an Attention activity `update.failed`, removes that after 15 s, and deletes the file — `src/Notch.App/Updates/UpdateService.cs:95`
- [ ] `%LocalAppData%\Notch\setup.log` is written by Setup as `yyyy-MM-dd HH:mm:ss  line`. Above 256 KB it rotates to `setup.log.old` — `src/Notch.Setup/Machine.cs:297`
- [ ] `%LocalAppData%\Notch\claude-hooks.json` is rewritten with the per-session Claude hook settings each time a Claude terminal session starts (only when `Notch.Hook.exe` exists) — `src/Notch.App/Terminal/TerminalController.cs:46`
- [ ] **[changed: becomes the user data folder of the whole app's web view]** `%LocalAppData%\Notch\WebView2\` is the WebView2 user data folder for the terminal — `src/Notch.App/Terminal/TerminalBridge.cs:80`
- [ ] Agent hook files outside Notch, written only when GlobalAgentHooks is switched on: `%CLAUDE_CONFIG_DIR%` or `~\.claude\settings.json`, `%CODEX_HOME%` or `~\.codex\config.toml`, and `%OPENCODE_CONFIG_DIR%` or `~\.config\opencode\plugins\notch.js` — `src/Notch.App/Agents/AgentHookFiles.cs:17`
- [ ] Backup `<file>.notch-backup` is made once, before the first change, and never overwritten. Writes go through `<file>.notch-tmp` and a move. The opencode plugin gets no backup and is deleted on disable — `src/Notch.App/Agents/AgentHookFiles.cs:227`
- [ ] The opencode plugin is only added when the opencode config folder exists or `opencode` is on PATH — `src/Notch.App/Agents/AgentHookFiles.cs:40`
- [ ] `RefreshPaths` at startup (when GlobalAgentHooks is on) re-points Notch's entries at the current `Notch.Hook.exe` and updates a notch.js that Notch wrote — `src/Notch.App/Agents/AgentHookFiles.cs:182`
- [ ] Files read but never written: `%AppData%\Claude\claude-code-sessions`, `%LocalAppData%\Packages\*\LocalCache\Roaming\Claude\claude-code-sessions`, `<ClaudeConfig>\projects\<folder>\<id>.jsonl`, `<CodexHome>\sessions\**\rollout-*.jsonl`, `<CodexHome>\session_index.jsonl` — `src/Notch.App/Agents/AppSessionReader.cs:131`
- [ ] Screenshot output: `<folder>\<name>.png` and `settings-<section>.png` — `src/Notch.App/Shell/ScreenshotRunner.cs:16`
- [ ] Settings-window pictures for `--with-settings` — `src/Notch.App/Settings/SettingsWindow.xaml.cs:319`
- [ ] Uninstall with "remove settings" deletes only `%AppData%\Notch` (`Machine.SettingsDir`); `%LocalAppData%\Notch` is left in place — `src/Notch.Setup/Installer.cs:199`
- [ ] Debug: `notchctl webhook` reads `DataFolders.Roaming`, so a debug notchctl reads `Notch-Debug` unless it was given `--shared-data` — `src/Notch.Cli/WebhookInfo.cs:11`

### Commands (CommandParser, notch://, notchctl, webhook)
- [x] Limits: input at most 2048 characters (URL length, or the sum of the argument lengths), title 120, detail 300, glyph 8, page id 64, timer at most 24 h, toast lifetime capped at 30 s — `src/Notch.Core/Automation/CommandParser.cs:17`
- [x] Valid tabs: `home`, `terminal`, `stats`, `shelf`, `plugins`, `clipboard` — `src/Notch.Core/Automation/CommandParser.cs:26`
- [x] URL parsing: the scheme `notch` is matched ignoring case. The verb is `uri.Host` when that is non-empty, otherwise `AbsolutePath` trimmed of `/`, so `notch://verb?..`, `notch:verb` and `notch:///verb` all work — `src/Notch.Core/Automation/CommandParser.cs:29`
- [x] Query parsing: `+` becomes a space, values are unescaped, a key without `=` gets `"true"`, the first occurrence wins, and keys ignore case — `src/Notch.Core/Automation/CommandParser.cs:349`
- [x] Argument parsing: the verb is lowercased. `--name=value`, `--name value` (when the next word does not start with `--`) and a bare `--name` (= `"true"`) are read. The first positional fills the verb's main parameter. For `open`, a second positional fills `page` — `src/Notch.Core/Automation/CommandParser.cs:54`
- [x] Main parameters: install/plugin take `id`, timer takes `d`, open takes `tab`, toast takes `title` — `src/Notch.Core/Automation/CommandParser.cs:187`
- [x] `install id=<plugin-id>` produces `InstallPluginCommand`; the error is "install needs a plugin id." — `src/Notch.Core/Automation/CommandParser.cs:115`
- [x] `plugin id=<id> enable=true|1|yes|on|false|0|no|off` produces `SetPluginEnabledCommand` — `src/Notch.Core/Automation/CommandParser.cs:125`
- [x] `timer` reads `d`, then `duration`, then `action`. `stop`/`reset`/`cancel` stop it, `pause` pauses, `resume` resumes; otherwise the value is a duration greater than 0 and at most 24 h, which produces `StartTimerCommand` — `src/Notch.Core/Automation/CommandParser.cs:196`
- [x] Duration grammar: a bare number is minutes (decimals allowed, invariant culture); `m:s` or `h:m:s` is a clock; otherwise units `h|hr|hrs|hour|hours|m|min|mins|minute|minutes|s|sec|secs|second|seconds`, which can repeat and ignore case. The result must be 1 s..24 h and is rounded to whole seconds — `src/Notch.Core/Widgets/DurationParser.cs:18`
- [x] `Describe` writes the shortest form, such as `1h30m`, `45s` or `0s` — `src/Notch.Core/Widgets/DurationParser.cs:54`
- [x] `open tab=<tab>` produces `OpenTabCommand`. `page=<id/page>`, `tab=page`, or a tab containing `/` produces `OpenPageCommand`, using page, or tab when page is empty — `src/Notch.Core/Automation/CommandParser.cs:144`
- [x] `toast title|text=..., detail, glyph, color (name or #rrggbb), lifetime (duration, capped at 30 s)` produces `ToastCommand` — `src/Notch.Core/Automation/CommandParser.cs:240`
- [x] `palette` produces `OpenPaletteCommand`; `settings` produces `OpenSettingsCommand`; any other verb gives "Unknown command." — `src/Notch.Core/Automation/CommandParser.cs:172`
- [x] Plugin id regex `^[a-z0-9]+([.-][a-z0-9]+)*$`, 1..64 characters. A page key is `<pluginid>/<page>`, where page is 1..64 characters, has no control characters, and has no surrounding whitespace — `src/Notch.Core/Automation/CommandParser.cs:318`
- [x] Colour parse: exactly `#rrggbb`, or a preset name (ignoring case) — `src/Notch.Core/Automation/CommandParser.cs:298`
- [x] Command records: Install, SetPluginEnabled, StartTimer, TimerAction(Stop/Pause/Resume), OpenTab, OpenPage, Toast, OpenPalette, OpenSettings — `src/Notch.Core/Automation/NotchCommand.cs:13`
- [ ] Dispatch of arguments: no arguments, or a first argument starting with `--`, reveals the notch, or opens settings when `--settings` is among them. This does nothing when the arguments contain `--updated` or `--wait-for=` — `src/Notch.App/Automation/CommandDispatcher.cs:28`
- [x] Quiet-launch flags list — `src/Notch.Core/Automation/ForwardedLaunch.cs:11`
- [ ] A first argument starting with `notch:` is parsed as a URL; anything else is parsed as notchctl-style arguments — `src/Notch.App/Automation/CommandDispatcher.cs:53`
- [ ] Results: a timer start answers "Timer started: <describe>."; a missing page answers "There is no page X. Is its plugin switched on?"; after a page opens the notch is revealed — `src/Notch.App/Automation/CommandDispatcher.cs:72`
- [ ] Enabling a plugin always asks the user: a review prompt when it is unapproved, otherwise a Yes/No box. Only one question at a time, otherwise "Notch is already asking about a plugin."; "Cancelled." when declined; "X is not installed." when missing — `src/Notch.App/Automation/CommandDispatcher.cs:114`
- [ ] A `notch:` link given at startup is handled at ApplicationIdle once the window is up — `src/Notch.App/App.xaml.cs:422`
- [ ] notchctl help on no arguments, `-h`, `--help`, `help` or `/?`; exits 1 when there were no arguments, else 0 — `src/Notch.Cli/Program.cs:14`
- [ ] notchctl `--start` (anywhere, ignoring case, then removed) launches `Notch.exe` from the same folder with ShellExecute, then retries 40 × 250 ms with a 1 s connect timeout — `src/Notch.Cli/Program.cs:11`
- [ ] `notchctl webhook` prints `URL: http://127.0.0.1:<port>/v1/`, the token and a curl example, exit 0. When the webhook is off it prints "The webhook is off..." and exits 1 — `src/Notch.Cli/WebhookInfo.cs:9`
- [ ] notchctl checks the arguments locally first; a parse error goes to stderr with exit 1 — `src/Notch.Cli/Program.cs:39`
- [ ] notchctl timeouts: 3 s to connect, 10 min to answer — `src/Notch.Cli/Program.cs:46`
- [ ] notchctl exit codes: 0 ok (message to stdout), 1 refused (message to stderr), 2 not running ("Notch is not running. Start it, or use --start."), 3 connected but no answer in time — `src/Notch.Cli/Program.cs:57`
- [ ] Webhook listener on `http://127.0.0.1:<port>/` only; the port must be 1024..65535, otherwise an error string — `src/Notch.Platform/Automation/WebhookServer.cs:32`
- [ ] Rate limit: 30 requests per fixed 1 s window, then 429 "Too many requests." (checked before auth) — `src/Notch.Platform/Automation/WebhookServer.cs:166`
- [x] Auth order: any `Origin` header gives 403. `Host` must be exactly `127.0.0.1:<port>`, `localhost:<port>` or `[::1]:<port>`, otherwise 403. No token configured gives 503. `Authorization: Bearer <token>` (prefix ignores case, value trimmed, compared in constant time) or 401 — `src/Notch.Core/Automation/WebhookRequest.cs:38`
- [x] Token: 32 random bytes, base64url without padding — `src/Notch.Core/Automation/WebhookRequest.cs:286`
- [ ] Body limit 64 KiB: Content-Length over the limit gives 413, a stream that grows past it while being read gives 413, and a missing or too-large body on POST gives 413 — `src/Notch.Platform/Automation/WebhookServer.cs:122`
- [x] Routing: the query is stripped and trailing `/` trimmed. `/v1/activity/<id>` needs DELETE (otherwise 405 "Use DELETE…") and an invalid id gives 400. `/v1/activity`, `/v1/notify` and `/v1/command` need POST (otherwise 405 "Use POST."). Anything else gives 404 — `src/Notch.Core/Automation/WebhookRequest.cs:66`
- [x] The body must be valid JSON (otherwise 400 "not valid JSON") and a JSON object (otherwise 400) — `src/Notch.Core/Automation/WebhookRequest.cs:103`
- [x] **[changed: a glow pattern that is not one of the five names, or a lone surrogate in a string, is refused with 400; C# accepts the undefined pattern, and answers 500 for the surrogate]** `POST /v1/activity` fields: `id` (`^[A-Za-z0-9._-]+$`, at most 64, published as `webhook.<id>`), `title` (1..120, trimmed), `detail` (≤300), `glyph` (≤8), `tier` (`transient` default, `ongoing`, `attention`, `glance`), `progress` (number clamped 0..1, NaN becomes 0), `glow` (colour string, giving Steady, or `{color, pattern: Steady|Breathe|Pulse|Flash|Audio, strength 0..1 default 1}`), `lifetimeMs` (>0, capped at 5 min, Transient only) — `src/Notch.Core/Automation/WebhookRequest.cs:162`
- [x] `POST /v1/notify` takes the same fields as the toast command — `src/Notch.Core/Automation/WebhookRequest.cs:122`
- [x] `POST /v1/command`: the verb comes from `command` or `verb`, and the other string, number and bool fields become values. install and plugin are refused with 403; parse errors give 400 — `src/Notch.Core/Automation/WebhookRequest.cs:127`
- [x] `DELETE /v1/activity/<id>` removes `webhook.<id>` — `src/Notch.Core/Automation/WebhookRequest.cs:71`
- [ ] Reply JSON is `{"ok":bool,"message":string}` with `application/json; charset=utf-8` and no CORS headers. A handler result gives 200, or 422 when ok is false. An unexpected exception gives 500 "Something went wrong." — `src/Notch.Platform/Automation/WebhookServer.cs:199`
- [ ] At most 20 non-transient webhook activities live at once; over the limit the request is refused (422) with the message "Too many activities are showing…". Publishing an id as transient removes it from the live set — `src/Notch.App/Automation/WebhookService.cs:16`
- [ ] `Apply()` on startup and on each settings save: stop when the webhook is off, otherwise generate the token if needed and start. A start problem is logged to errors.log at startup and shown in a MessageBox after save — `src/Notch.App/Automation/WebhookService.cs:38`

### IPC and wire formats
- [ ] Single-instance mutex `Local\Notch.SingleInstance` (DEBUG: `Local\Notch.SingleInstance.Debug`). A `--screenshots=` run skips the check — `src/Notch.App/App.xaml.cs:84`
- [ ] A second instance forwards its arguments (minus `--wait-for=`) through the command pipe with a 3 s timeout, then quits — `src/Notch.App/App.xaml.cs:94`
- [ ] Command pipe name `notch-command` (DEBUG `notch-command-debug`), InOut, byte mode, max instances, `CurrentUserOnly` — `src/Notch.Core/Automation/CommandPipe.cs:24`
- [x] **[changed: a `null` among the arguments gets "That request could not be read."; C# passes it on to its handler]** Command pipe request: one UTF-8 line holding a JSON array of strings plus `\n`, at most 8 KiB, read within 5 s. An unreadable request gets `{ok:false,message:"That request could not be read."}` — `src/Notch.Core/Automation/CommandPipe.cs:29`
- [x] Command pipe answer: one line of `CommandResult` JSON with web (camelCase) naming, `{"ok":bool,"message":string|null}`. When the client gets EOF without a line it treats that as ok — `src/Notch.Core/Automation/CommandPipe.cs:81`
- [ ] Client `ExchangeAsync` takes separate connect and answer timeouts and reports Connected versus not — `src/Notch.Core/Automation/CommandPipe.cs:62`
- [ ] Per-process agent pipe `notch-agent-<pid>`, passed to terminal sessions in `NOTCH_PIPE` — `src/Notch.Platform/Agents/AgentHookServer.cs:19`
- [x] Shared agent pipe `notch-agent-shared` (DEBUG `notch-agent-shared-debug`) — `src/Notch.Core/Agents/AgentHooks.cs:56`
- [ ] The agent server listens on both pipes: InOut (needed by opencode), max instances, CurrentUserOnly. It reads one line within 5 s and ignores lines over 256 KiB; nothing is written back — `src/Notch.Platform/Agents/AgentHookServer.cs:48`
- [x] Hook line format: `{"session":string,"payload":string(JSON text),"pid":int}` plus `\n` — `src/Notch.Core/Agents/AgentHooks.cs:63`
- [x] Hook parsing: the event is `payload.hook_event_name`, falling back to `payload.type`. Folder comes from `payload.cwd`, agent from `payload.agent`. An empty session means the agent was started outside Notch — `src/Notch.Core/Agents/AgentHooks.cs:74`
- [ ] Env vars set for terminal sessions: `NOTCH_PIPE=<per-process pipe>` and `NOTCH_SESSION=<session id>`, plus the preset's own variables, merged over the parent environment ignoring case — `src/Notch.App/Terminal/TerminalController.cs:182`
- [ ] Notch.Hook.exe: uses `NOTCH_PIPE` and `NOTCH_SESSION` when both are set, otherwise the shared pipe with an empty session. The payload is the last argument when it starts with `{` (Codex), otherwise stdin read within 2 s and trimmed (Claude). It connects Out-only with a 1000 ms timeout, sends its own pid, prints nothing and always exits 0 — `src/Notch.Hook/Program.cs:13`
- [ ] Claude per-session args `--settings <LocalAppData>\Notch\claude-hooks.json`, with a JSON hooks object for the 6 events, each `[{hooks:[{type:"command",command:<path with forward slashes, quoted if it has a space>}]}]` — `src/Notch.Core/Agents/AgentHooks.cs:22`
- [x] Codex per-session args `-c notify=['<hook path>']`, left out when the path contains `'` — `src/Notch.Core/Agents/AgentHooks.cs:47`
- [ ] PowerShell profile: `pwsh` is preferred over `powershell`; a bare powershell gets `-NoLogo` — `src/Notch.App/Terminal/TerminalController.cs:199`
- [x] **[changed: a hook group or command that is not an object, or a duplicate key, counts as not Notch's; C# throws and does not catch it]** Global Claude hooks: the same 6 events are added to the user's settings.json. A group counts as Notch's when its command contains `Notch.Hook` (ignoring case). Removal keeps other hooks and drops empty arrays and an empty `hooks` key. Output is indented — `src/Notch.Core/Agents/AgentHookEditor.cs:39`
- [x] Global Codex hook: the block `# Notch: reports when Codex finishes a turn. Remove it in Notch's settings.` plus `notify = ['<path>']` goes at the top of config.toml, using the file's own newline style. A top-level `notify` before the first `[table]` that is not Notch's is refused. Removal also takes the comment line and one blank line after it — `src/Notch.Core/Agents/AgentHookEditor.cs:185`
- [x] opencode plugin file `notch.js` has marker `notch-opencode-plugin`. It is only overwritten when the existing file has the marker. Comparing copies treats CRLF and LF the same and trims the end — `src/Notch.Core/Agents/OpenCodePlugin.cs:12`
- [ ] notch.js uses `NOTCH_PIPE` + `NOTCH_SESSION` when both are set, otherwise writes to both `notch-agent-shared` and `notch-agent-shared-debug`. Payload `{hook_event_name, cwd, agent:"opencode"}`, 1000 ms socket timeout, pipe path `\\.\pipe\<name>` — `src/Notch.App/Assets/opencode/notch.js:12`
- [ ] notch.js event mapping: `session.execution.started` sends UserPromptSubmit. `succeeded` and `failed` send Stop once the folder's last session ends; `interrupted` sends SessionEnd. `permission.asked` sends Notification; `permission.replied` and `permission.rejected` send PostToolUse. Plugin `id:"notch"` — `src/Notch.App/Assets/opencode/notch.js:64`
- [x] Hook event names handled: `UserPromptSubmit`, `PreToolUse` and `PostToolUse` mean Working; `Notification` means NeedsInput; `Stop` and Codex `agent-turn-complete` mean Done; `SessionEnd` means Idle — `src/Notch.Core/Agents/AgentStatusTracker.cs:44`
- [ ] Terminal inference: Codex input containing `\r` means Working; a bell while Working means NeedsInput; 15 s of silence (checked every 5 s) while Working means Idle. Any input while NeedsInput means Working. Viewing a Done session makes it Idle — `src/Notch.App/Terminal/TerminalController.cs:19`
- [ ] Shared-pipe routing: the message is ignored when the session is non-empty, when pid ≤ 0 and it is not opencode, or when DetectAgents is off. Otherwise the agent is found by hook pid, rescanning when needed. opencode is routed by folder, first to Notch's terminal sessions and then to detected opencode processes in that folder — `src/Notch.App/Agents/AgentTracker.cs:230`
- [ ] Env var `CLAUDE_CONFIG_DIR` overrides `~\.claude` (hook settings and transcripts) — `src/Notch.App/Agents/AppSessionReader.cs:45`
- [ ] Env var `CODEX_HOME` overrides `~\.codex` — `src/Notch.App/Agents/AppSessionReader.cs:49`
- [ ] Env var `OPENCODE_CONFIG_DIR` overrides `~\.config\opencode` — `src/Notch.App/Agents/AgentHookFiles.cs:31`
- [ ] Env var `NOTCH_DEV_RELOAD`: any non-empty value has the same effect as `--dev-reload` — `src/Notch.App/App.xaml.cs:263`
- [x] Env vars `PATH` and `PATHEXT` are used to resolve terminal commands — `src/Notch.Core/Terminal/CommandResolver.cs:52`
- [ ] Build-time env vars: `UPDATE_SIGNING_KEY`, `CODESIGN_PFX_BASE64`, `CODESIGN_PFX_PASSWORD` — `installer/sign-update.ps1:17`

### Notch.exe command-line flags
- [ ] Parsing: a flag matches exactly (ignoring case); an option is `--name=value` with a case-insensitive prefix, and the first one wins (`--plugin=` can repeat) — `src/Notch.App/App.xaml.cs:670`
- [ ] `--wait-for=<pid>` waits up to 20 s for that process to exit before starting; added by Restart — `src/Notch.App/App.xaml.cs:638`
- [ ] Restart starts the same exe with the same arguments, minus `--wait-for=` and `--updated`, plus `--wait-for=<own pid>`, then shuts down — `src/Notch.App/App.xaml.cs:606`
- [ ] `--screenshots=<folder>` (only with `--demo`) saves pictures, then exits with code 0, or 1 on error; it may run beside another instance — `src/Notch.App/App.xaml.cs:361`
- [ ] `--with-settings` (with `--screenshots`) adds settings-window pictures — `src/Notch.App/App.xaml.cs:364`
- [ ] `--demo`: fake media, a fake shelf (C:\Demo\…, never saved), no agent tracker or notifications, and `DemoDriver` — `src/Notch.App/App.xaml.cs:100`
- [ ] `--trace-hover` writes hover.log — `src/Notch.App/App.xaml.cs:101`
- [ ] **[changed: maps to WebView2 with GPU acceleration off]** `--software-render` sets `RenderMode.SoftwareOnly` — `src/Notch.App/App.xaml.cs:107`
- [ ] `--shared-data` (DEBUG only) uses the release `Notch` folders — `src/Notch.Core/Settings/DataFolders.cs:11`
- [ ] `--plugin=<folder>` (repeatable) runs a plugin from its build output, whether or not it is enabled — `src/Notch.App/App.xaml.cs:174`
- [ ] `--no-plugins` turns on safe mode — `src/Notch.App/App.xaml.cs:185`
- [ ] `--plugin-theme=<plugin>/<theme>` sets a plugin theme for this run only — `src/Notch.App/App.xaml.cs:204`
- [ ] `--theme=dark|light|system` sets the theme for this run only — `src/Notch.App/App.xaml.cs:207`
- [ ] `--display=<n>` uses the 1-based display (as listed in Settings) for this run — `src/Notch.App/App.xaml.cs:235`
- [ ] `--style=notch|island` and `--position=topcenter|taskbarleft` apply for this run only — `src/Notch.App/App.xaml.cs:241`
- [ ] **[changed: reloads the Svelte UI and theme CSS instead of XAML]** `--dev-reload` with `--reload-from=<source folder>` reloads XAML live, looking in `Themes` or `src\Notch.App\Themes` for a folder holding `Design.xaml` — `src/Notch.App/Shell/LiveXamlReload.cs:176`
- [ ] `--pin-open` keeps the notch open — `src/Notch.App/App.xaml.cs:268`
- [ ] `--settings` opens the settings window, at startup or when forwarded — `src/Notch.App/App.xaml.cs:273`
- [ ] `--tab=<home|terminal|stats|shelf|clipboard|plugins>` is parsed as the NotchTab enum ignoring case, with home as the fallback. The clipboard tab is refused with a notice while history is off — `src/Notch.App/Shell/NotchWindow.Terminal.cs:38`
- [ ] `--open=<terminal profile id>` (for example `claude`, `codex`, `opencode`, `shell`, or a preset's slug) starts a terminal session — `src/Notch.App/App.xaml.cs:284`
- [ ] `--updated` shows the "Notch updated <ver>" activity. Without it, a failed update is announced if one is recorded — `src/Notch.App/App.xaml.cs:379`
- [ ] A `notch:<...>` argument is run as a command after startup — `src/Notch.App/App.xaml.cs:422`

### Registry, install, update
- [ ] `HKCU\Software\Notch` `InstallDir` (REG_SZ). Read by the app (`IsInstalledCopy` compares it with `AppContext.BaseDirectory`) and by Setup (`InstalledDir`, which requires `Notch.exe` to be there) — `src/Notch.Setup/Machine.cs:18`
- [ ] `IsInstalledCopy` gates update checks and the "Update Notch now" button — `src/Notch.App/Updates/UpdateService.cs:66`
- [ ] `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Notch` values: DisplayName `Notch`, DisplayVersion, Publisher `Notch contributors`, DisplayIcon `<dir>\Notch.exe`, InstallLocation, UninstallString `"<dir>\Uninstall.exe" /uninstall`, QuietUninstallString `... /uninstall /S`, NoModify=1, NoRepair=1, EstimatedSize KB (all DWORD) — `src/Notch.Setup/Machine.cs:64`
- [ ] `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Notch` = `"<exe>"`, written by Setup (not on /UPDATE) and by the Settings checkbox — `src/Notch.Platform/Startup/StartupRegistration.cs:8`
- [ ] Settings "start with Windows" reads and writes the Run value — `src/Notch.App/Settings/SettingsWindow.xaml.cs:516`
- [ ] `HKCU\Software\Classes\notch`: default `URL:Notch`, `URL Protocol` = `""`, `DefaultIcon` = `"<exe>",0`, `shell\open\command` = `"<exe>" "%1"` — `src/Notch.Setup/Machine.cs:91`
- [ ] `HKCU\Environment` `Path`: the install dir is added or removed only. Comparison expands variables, strips quotes and trailing `\` and ignores case. The REG_EXPAND_SZ type is kept, or used when the value contains `%`. `WM_SETTINGCHANGE "Environment"` is broadcast with SMTO_ABORTIFHUNG and a 500 ms timeout (3 s cap) — `src/Notch.Setup/Machine.cs:123`
- [ ] `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` `AppsUseLightTheme` (DWORD ≠ 0 means light; unreadable means dark) — `src/Notch.Platform/Display/SystemTheme.cs:7`
- [ ] Uninstall deletes the `Classes\notch` and Uninstall keys, the `Software\Notch` key, the Run value and the PATH entry — `src/Notch.Setup/Machine.cs:176`
- [ ] Default install dir `%LocalAppData%\Programs\Notch`; app exe `Notch.exe`; companions `Notch.Hook.exe`, `notchctl.exe`, `Uninstall.exe`, `Assets\terminal\**`, `Assets\opencode\notch.js`, `plugins\new-age\` — `src/Notch.Setup/Machine.cs:23`
- [ ] Shipped assets and built-in plugins are copied by the project file — `src/Notch.App/Notch.App.csproj:33`
- [ ] Shortcuts via WScript.Shell: Start Menu `Programs\Notch.lnk` (always) and Desktop `Notch.lnk` (optional), with Description `Notch` and WorkingDirectory set to the exe folder. On uninstall the desktop shortcut is only removed when it points at this install — `src/Notch.Setup/Machine.cs:189`
- [ ] StopApp: ends `Notch`, `Notch.Hook` and `notchctl` processes whose path is under the dir. Graceful mode waits 4 s first; then Kill and wait 5 s — `src/Notch.Setup/Machine.cs:259`
- [ ] Setup flags: `/S` or `--silent`, `/UPDATE`, `/PATH`, `/uninstall` or `--uninstall`, `/D=<path>` or `/D <path>` (quotes trimmed, made a full path), all ignoring case — `src/Notch.Setup/Options.cs:30`
- [ ] A file with no payload and no switches (or with `/uninstall`) uninstalls. With install switches it refuses: "This setup file is incomplete. Download it again." (a box, or in silent mode update-error.txt plus a relaunch of the old version when /UPDATE) and exits 1 — `src/Notch.Setup/App.xaml.cs:25`
- [ ] Silent install: dir is `/D`, else the installed dir, else the default. StartWithWindows=true (not applied on update). AddToPath is `/PATH` or already on PATH. Exits 0, or 1 on failure (logged, update-error.txt written, old version relaunched without `--updated` on /UPDATE). Success with /UPDATE launches `Notch.exe --updated` — `src/Notch.Setup/App.xaml.cs:51`
- [ ] Uninstall run from inside the install dir copies itself to `%TEMP%\Notch-Uninstall-<guid>.exe`, re-runs as `/uninstall /D="<dir>" [/S]`, and that copy deletes itself on exit via `cmd /c ping 127.0.0.1 -n 3 >nul & del /f /q` — `src/Notch.Setup/App.xaml.cs:127`
- [ ] Silent uninstall keeps the settings; the UI offers a "remove settings" checkbox — `src/Notch.Setup/SetupWindow.xaml.cs:146`
- [ ] Install steps: unzip into `<dir>.new-<id8>`, rejecting zip-slip paths and requiring Notch.exe. Stop the app (graceful on update). Swap files with `<dir>.old-<id8>` as the backup, 12 tries × 300 ms, stopping the app before each retry and rolling back on failure. Then Uninstall.exe, shortcuts, Run (not on update), PATH and registration, and clear update-error.txt — `src/Notch.Setup/Installer.cs:36`
- [ ] FolderSwap naming and leftover cleanup of `<name>.new-*` and `<name>.old-*` — `src/Notch.Setup/FolderSwap.cs:19`
- [ ] Uninstall.exe is the setup program's first `programLength` bytes, without the payload — `src/Notch.Setup/Installer.cs:148`
- [ ] Setup version is the InformationalVersion without the `+metadata` suffix; Launch passes `--updated` — `src/Notch.Setup/Installer.cs:23`
- [ ] Payload format: `[setup exe][zip][int64 LE zip length][ASCII "NOTCHPAY"]`, a 16-byte trailer — `src/Notch.Setup/Payload.cs:15`
- [ ] **[changed: Rust binaries plus the self-contained .NET plugin host; zip appended to the Tauri setup stub]** Build: self-contained win-x64 publish of App, Hook and Cli into one folder; Authenticode signing; drop .pdb files; zip; append to `Notch.Setup.exe`, giving `artifacts\Notch-Setup-<ver>.exe` — `installer/build.ps1:32`
- [ ] Release assets: `Notch-Setup-<ver>.exe`, `Notch-Setup-<ver>.exe.sig`, `Notch-Update-<ver>.exe` (same bytes), `Notch-Update-<ver>.exe.sig`, `BreakReminder-plugin.zip` — `.github/workflows/release.yml:66`
- [ ] Update feed: `https://api.github.com/repos/Brick-Bread/WNotch/releases/latest` with User-Agent `Notch-Updater` and Accept `application/vnd.github+json` — `src/Notch.Core/Updates/Updater.cs:14`
- [x] **[changed: a repeated name in the feed takes the last value; C# throws and does not catch it]** Feed parsing: `tag_name` read as `v1.2.3` (a `-`/`+` suffix is dropped and the version normalised to 3 parts). `Notch-Update-*.exe` is preferred over `Notch-Setup-*.exe`. The URL must start with `https://github.com/Brick-Bread/WNotch/releases/download/`. `digest` is `sha256:<hex>`, also read are `size` and the signature `<asset name>.sig` — `src/Notch.Core/Updates/ReleaseFeed.cs:13`
- [ ] Download checks: the file must be non-empty and match `size` (when that is >0). With a public key configured, the `.sig` (≤1024 bytes) is required and must verify. Without a key the sha256 digest is required. The sha256 is checked whenever it is present — `src/Notch.Core/Updates/Updater.cs:58`
- [x] Signature format: ECDSA P-256 over SHA-256, IEEE P1363 r‖s (64 bytes) in base64. The public key is a base64 SubjectPublicKeyInfo, currently `""` (not configured); the private key is base64 PKCS#8 — `src/Notch.Core/Updates/UpdateSigning.cs:18`
- [ ] Update schedule: first check after 45 s, then every 4 h, or 5 s after settings are saved (`CheckSoon`). The HTTP timeout is 10 min — `src/Notch.App/Updates/UpdateService.cs:28`
- [ ] Update notices: activity id `update`, "Notch <tag> is ready" (Blue Flash, 10 s), once per tag. `LastNotifiedUpdateTag` is saved before the notice is shown — `src/Notch.App/Updates/UpdateService.cs:224`
- [ ] Forced update: download and confirm, then run `<installer> /S /UPDATE` with UseShellExecute=false and shut down. The messages for not-installed, already-latest and downloaded-not-restarted are specific — `src/Notch.App/Updates/UpdateService.cs:140`

## 2. Plugin API and plugin management

### Plugin API version and entry point
- [ ] **[changed: becomes 11]** `PluginApi.Version` const = 10 — `src/Notch.Core/Plugins/INotchPlugin.cs:16`
- [ ] Notch runs plugins whose `apiVersion` ≤ host version and refuses newer ones — `docs/plugins.md:931`
- [ ] API version history: v1 activities, cards, settings and log; v2 `PluginCard.Color`; v3 Pages; v4 Back and Choices; v5 Actions, Blocks, Shell, Bus, manifest settings, `Settings.Changed` and `repository`; v6 Themes; v7 Glow, Audio and the `audio` permission; v8 Widgets; v9 Palette, Hotkeys, page View/Size/Accept, PluginChoices, TextField Changed/ClearOnSubmit/Lines, OpensPage, CountingFrom, IsCardVisible, notch.* topics; v10 Glance and Order — `docs/plugins.md:933`
- [ ] The API surface is `Notch.Core.Plugins` plus `Activity`, `ActivityTier`, `Glow`, `GlowColor`, `GlowPattern`, `WidgetOption` and `WidgetOptionType`; nothing else is public contract — `docs/plugins.md:929`
- [ ] `INotchPlugin` interface — `src/Notch.Core/Plugins/INotchPlugin.cs:29`
- [ ] `INotchPlugin.Start(IPluginHost host)` (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:36`
- [ ] `INotchPlugin.Stop()` (v1); host calls after Stop are ignored, and leftover activities and cards are removed — `src/Notch.Core/Plugins/INotchPlugin.cs:43`
- [ ] Threading: Start and Stop run on a background thread and never concurrently; an exception from either marks the plugin Failed — `src/Notch.Core/Plugins/INotchPlugin.cs:24`
- [ ] **[changed: ends only the plugin host; Notch restarts it and disables a plugin that keeps crashing]** Threading: exceptions on threads the plugin starts itself are not caught and end the app (a parity decision for the out-of-process host) — `src/Notch.Core/Plugins/INotchPlugin.cs:26`
- [ ] Entry point: exactly one public, non-abstract class implementing INotchPlugin, with a public parameterless constructor — `src/Notch.Core/Plugins/PluginLoader.cs:23`
- [ ] Error text when there is no entry class / several entry classes — `src/Notch.Core/Plugins/PluginLoader.cs:28`
- [ ] Error text when there is no parameterless constructor — `src/Notch.Core/Plugins/PluginLoader.cs:34`
- [ ] Error when the assembly named in the manifest is missing — `src/Notch.Core/Plugins/PluginLoader.cs:15`
- [ ] Refuse a plugin whose Notch.Core reference version is newer than the host's (read from PE metadata without running code) — `src/Notch.Core/Plugins/PluginLoader.cs:47`
- [ ] **[changed: inside the out-of-process plugin host]** One AssemblyLoadContext per plugin, named `plugin.<id>`, never unloaded — `src/Notch.Core/Plugins/PluginLoader.cs:96`
- [ ] Notch.Core is always the host's copy, and any copy the plugin ships is ignored — `src/Notch.Core/Plugins/PluginLoader.cs:121`
- [ ] Dependencies resolve through `.deps.json` (AssemblyDependencyResolver), falling back to `<name>.dll` next to the plugin, then to the host — `src/Notch.Core/Plugins/PluginLoader.cs:126`
- [ ] Native libraries resolve through deps.json (`LoadUnmanagedDll`) — `src/Notch.Core/Plugins/PluginLoader.cs:137`
- [ ] Targets: net10.0 or net10.0-windows, with native dependencies for win-x64 — `docs/plugins.md:761`
- [ ] Disable then enable gives a new instance but the same loaded assembly (statics persist), and the factory is reused — `src/Notch.Core/Plugins/PluginManager.cs:664`
- [ ] TargetInvocationException from the constructor is unwrapped to its inner cause for the error message — `src/Notch.Core/Plugins/PluginManager.cs:678`
- [ ] On a failed start, host.Close() removes everything the plugin published — `src/Notch.Core/Plugins/PluginManager.cs:680`

### IPluginHost
- [ ] `IPluginHost` interface; every member is safe to call from any thread — `src/Notch.Core/Plugins/INotchPlugin.cs:46`
- [ ] `Manifest` (PluginManifest) (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:50`
- [ ] `PluginDirectory` (string, read-only by convention) (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:53`
- [ ] `DataDirectory` (string, created on first access; `%AppData%\Notch\plugin-data\<id>`) (v1) — `src/Notch.Core/Plugins/PluginHost.cs:66`
- [ ] `Activities` (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:62`
- [ ] `Cards` (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:65`
- [ ] `Pages` (v3) — `src/Notch.Core/Plugins/INotchPlugin.cs:68`
- [ ] `Widgets` (v8) — `src/Notch.Core/Plugins/INotchPlugin.cs:71`
- [ ] `Palette` (v9) — `src/Notch.Core/Plugins/INotchPlugin.cs:74`
- [ ] `Hotkeys` (v9) — `src/Notch.Core/Plugins/INotchPlugin.cs:77`
- [ ] `Themes` (v6) — `src/Notch.Core/Plugins/INotchPlugin.cs:80`
- [ ] `Glow` (v7) — `src/Notch.Core/Plugins/INotchPlugin.cs:83`
- [ ] `Audio` (v7) — `src/Notch.Core/Plugins/INotchPlugin.cs:86`
- [ ] `Shell` (v5) — `src/Notch.Core/Plugins/INotchPlugin.cs:89`
- [ ] `Bus` (v5) — `src/Notch.Core/Plugins/INotchPlugin.cs:92`
- [ ] `Settings` (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:95`
- [ ] `Log` (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:98`
- [ ] Close() order: activities, cards, widgets, palette, hotkeys, pages, themes, glow, audio, bus, shell; after it every service ignores calls — `src/Notch.Core/Plugins/PluginHost.cs:105`

### Activities (IPluginActivities)
- [ ] `IPluginActivities` interface (v1); ids are private to the plugin — `src/Notch.Core/Plugins/INotchPlugin.cs:105`
- [ ] `Publish(Activity)`: adds or updates by Id; throws on null or a blank Id — `src/Notch.Core/Plugins/PluginHost.cs:148`
- [ ] `Remove(string id)` → bool — `src/Notch.Core/Plugins/INotchPlugin.cs:117`
- [ ] `Clear()` removes everything the plugin published — `src/Notch.Core/Plugins/INotchPlugin.cs:120`
- [ ] Ids are scoped as `plugin.<pluginId>.<id>` — `src/Notch.Core/Plugins/PluginManager.cs:162`
- [ ] `OpensPage` is rewritten to `<pluginId>/<pageId>`, and an empty value becomes null — `src/Notch.Core/Plugins/PluginHost.cs:165`
- [x] Ordering: higher tier wins, then most recently published; an update keeps its sequence (does not re-rank) — `src/Notch.Core/Activities/ActivityManager.cs:61`
- [x] Snapshot order: Tier descending, then Sequence descending — `src/Notch.Core/Activities/ActivityManager.cs:94`
- [x] Transient expiry timer, default lifetime 2 s — `src/Notch.Core/Activities/ActivityManager.cs:9`
- [x] Ids suppressed by the user are dropped on publish — `src/Notch.Core/Activities/ActivityManager.cs:27`
- [ ] Glance row: up to six items, in Order then by id; Detail, Progress and Glow are not drawn; with CountingFrom the Title becomes the tooltip; clicking an item opens its OpensPage — `docs/plugins.md:284`
- [ ] Use Attention sparingly; it hides lower tiers indefinitely — `docs/plugins.md:298`

### Activity / ActivityTier
- [ ] `ActivityTier` enum — `src/Notch.Core/Activities/Activity.cs:4`
- [ ] `ActivityTier.Glance = -1` (v10) — `src/Notch.Core/Activities/Activity.cs:11`
- [ ] `ActivityTier.Ongoing = 0` — `src/Notch.Core/Activities/Activity.cs:14`
- [ ] `ActivityTier.Attention = 1` — `src/Notch.Core/Activities/Activity.cs:17`
- [ ] `ActivityTier.Transient = 2` — `src/Notch.Core/Activities/Activity.cs:20`
- [ ] `Activity` sealed record — `src/Notch.Core/Activities/Activity.cs:23`
- [ ] `Activity.Id` (required string) — `src/Notch.Core/Activities/Activity.cs:26`
- [ ] `Activity.Tier` (required ActivityTier) — `src/Notch.Core/Activities/Activity.cs:28`
- [ ] `Activity.Title` (required string) — `src/Notch.Core/Activities/Activity.cs:30`
- [ ] `Activity.Detail` (string?) — `src/Notch.Core/Activities/Activity.cs:32`
- [ ] `Activity.Glyph` (string?, a Segoe Fluent Icons code point) — `src/Notch.Core/Activities/Activity.cs:35`
- [ ] `Activity.AgentId` (string?, app-internal but on the public record) — `src/Notch.Core/Activities/Activity.cs:41`
- [ ] `Activity.AgentState` (AgentState?, app-internal but on the public record) — `src/Notch.Core/Activities/Activity.cs:44`
- [ ] `Activity.Image` (byte[]?, PNG/JPEG; drawn at 22×22) — `src/Notch.Core/Activities/Activity.cs:47`
- [ ] `Activity.Progress` (double?, 0..1; on Transient it is drawn as a bar in place of Detail) — `src/Notch.Core/Activities/Activity.cs:50`
- [ ] `Activity.Glow` (Glow?) — `src/Notch.Core/Activities/Activity.cs:53`
- [ ] `Activity.Lifetime` (TimeSpan?, Transient only, default 2 s) — `src/Notch.Core/Activities/Activity.cs:56`
- [ ] `Activity.OpensPage` (string?) (v9) — `src/Notch.Core/Activities/Activity.cs:62`
- [ ] `Activity.CountingFrom` (DateTimeOffset?) (v9) — `src/Notch.Core/Activities/Activity.cs:69`
- [ ] `Activity.Order` (int, Glance only) (v10) — `src/Notch.Core/Activities/Activity.cs:75`
- [x] Elapsed-clock format: "m:ss", or "h:mm:ss" from one hour on; negative values clamp to 0 — `src/Notch.Core/Activities/ElapsedClock.cs:6`

### Glow / GlowColor / GlowPattern
- [x] `GlowColor` readonly record struct (byte R, byte G, byte B) — `src/Notch.Core/Activities/Glow.cs:3`
- [x] `GlowColor.White` = F2F2F2 — `src/Notch.Core/Activities/Glow.cs:5`
- [x] `GlowColor.Blue` = 3D8BFF — `src/Notch.Core/Activities/Glow.cs:6`
- [x] `GlowColor.Cyan` = 2EC4FF — `src/Notch.Core/Activities/Glow.cs:7`
- [x] `GlowColor.Green` = 3FD86B — `src/Notch.Core/Activities/Glow.cs:8`
- [x] `GlowColor.Amber` = FFB01F — `src/Notch.Core/Activities/Glow.cs:9`
- [x] `GlowColor.Orange` = FF7A2E — `src/Notch.Core/Activities/Glow.cs:10`
- [x] `GlowColor.Red` = FF3B3B — `src/Notch.Core/Activities/Glow.cs:11`
- [x] `GlowColor.Yellow` = FFD84A — `src/Notch.Core/Activities/Glow.cs:12`
- [x] `GlowColor.Violet` = A86BFF — `src/Notch.Core/Activities/Glow.cs:13`
- [x] `GlowColor.Named` in picker order Blue, Cyan, Green, Yellow, Amber, Orange, Red, Violet (White is not included) — `src/Notch.Core/Activities/Glow.cs:16`
- [x] `GlowColor.FromName(string?)` is case-insensitive and returns null for "White" — `src/Notch.Core/Activities/Glow.cs:29`
- [x] `GlowColor.OnLight()` = Lerp toward black by 0.3 (used for light themes) — `src/Notch.Core/Activities/Glow.cs:46`
- [x] `GlowColor.Lerp(other, amount)` clamps amount to 0..1 and rounds — `src/Notch.Core/Activities/Glow.cs:49`
- [x] `GlowPattern` enum — `src/Notch.Core/Activities/Glow.cs:58`
- [x] `GlowPattern.Steady` — `src/Notch.Core/Activities/Glow.cs:61`
- [x] `GlowPattern.Breathe` — `src/Notch.Core/Activities/Glow.cs:64`
- [x] `GlowPattern.Pulse` — `src/Notch.Core/Activities/Glow.cs:67`
- [x] `GlowPattern.Flash` — `src/Notch.Core/Activities/Glow.cs:70`
- [x] `GlowPattern.Audio` — `src/Notch.Core/Activities/Glow.cs:73`
- [x] `Glow` record (Color, Pattern, Strength = 1) — `src/Notch.Core/Activities/Glow.cs:78`
- [x] Glow timing constants: Breathe period 3.2 s, Pulse period 1.1 s, Flash decay 1.2 s — `src/Notch.Core/Activities/Glow.cs:80`
- [x] `Glow.IntensityAt(seconds, audioLevel)` formulas: Breathe .45+.55w, Pulse .25+.75w, Flash .35+.65·decay, Audio .2+.8·level, Steady .8; result × Strength, clamped — `src/Notch.Core/Activities/Glow.cs:86`
- [x] `Glow.IsAnimated` (any pattern except Steady) — `src/Notch.Core/Activities/Glow.cs:102`
- [x] Wave function sin²(π·(t mod p)/p) — `src/Notch.Core/Activities/Glow.cs:105`
- [x] `GlowOutput` user brightness: 25–200 %, default 100, lift exponent 0.6 — `src/Notch.Core/Activities/Glow.cs:113`

### Cards (IPluginCards / PluginCard)
- [ ] `IPluginCards` interface (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:124`
- [ ] `Set(PluginCard)` adds, or replaces in place by Id; throws on a blank Id — `src/Notch.Core/Plugins/INotchPlugin.cs:127`
- [ ] `Remove(string id)` → bool — `src/Notch.Core/Plugins/INotchPlugin.cs:130`
- [ ] `Clear()` — `src/Notch.Core/Plugins/INotchPlugin.cs:133`
- [ ] `PluginCard` sealed record — `src/Notch.Core/Plugins/INotchPlugin.cs:140`
- [ ] `PluginCard.Id` (required) — `src/Notch.Core/Plugins/INotchPlugin.cs:143`
- [ ] `PluginCard.Label` (required) — `src/Notch.Core/Plugins/INotchPlugin.cs:146`
- [ ] `PluginCard.Value` (string?, one line, ellipsis) — `src/Notch.Core/Plugins/INotchPlugin.cs:149`
- [ ] `PluginCard.CountingFrom` (DateTimeOffset?) (v9) — `src/Notch.Core/Plugins/INotchPlugin.cs:152`
- [ ] `PluginCard.Detail` (string?, wraps to two lines) — `src/Notch.Core/Plugins/INotchPlugin.cs:155`
- [ ] `PluginCard.Progress` (double?, 0..1) — `src/Notch.Core/Plugins/INotchPlugin.cs:158`
- [ ] `PluginCard.Color` (GlowColor?, deepened on light themes) (v2) — `src/Notch.Core/Plugins/INotchPlugin.cs:161`
- [ ] `PluginCard.Clicked` (Action?; null makes the card non-interactive) — `src/Notch.Core/Plugins/INotchPlugin.cs:167`
- [ ] Threading: Clicked runs via Task.Run on a background thread, and exceptions are logged ("The click handler of card '…' failed.") — `src/Notch.Core/Plugins/PluginHost.cs:242`
- [ ] An update keeps the card's position; snapshot order is first-added order across plugins — `src/Notch.Core/Plugins/PluginCardBoard.cs:28`
- [ ] Layout: three cards to a row; the Plugins tab exists only while any card exists — `docs/plugins.md:316`
- [ ] Documented limit: update cards at most once a second — `docs/plugins.md:345`

### Pages (IPluginPages / PluginPage)
- [ ] `IPluginPages` interface (v3) — `src/Notch.Core/Plugins/PluginPages.cs:9`
- [ ] `Set(PluginPage)` keeps the console on replace; throws on a blank Id or Title — `src/Notch.Core/Plugins/PluginPages.cs:215`
- [ ] `Append(pageId, line)`; ignored for an unknown page; throws on a null line — `src/Notch.Core/Plugins/PluginPages.cs:18`
- [ ] `ClearConsole(pageId)` — `src/Notch.Core/Plugins/PluginPages.cs:21`
- [ ] `Open(pageId)` expands the notch on the page; ignored for an unknown page — `src/Notch.Core/Plugins/PluginPages.cs:24`
- [ ] `Remove(string id)` → bool — `src/Notch.Core/Plugins/PluginPages.cs:27`
- [ ] `Clear()` — `src/Notch.Core/Plugins/PluginPages.cs:30`
- [ ] `PluginPage` sealed record — `src/Notch.Core/Plugins/PluginPages.cs:34`
- [ ] `PluginPage.Id` (required) — `src/Notch.Core/Plugins/PluginPages.cs:37`
- [ ] `PluginPage.Title` (required) — `src/Notch.Core/Plugins/PluginPages.cs:40`
- [ ] `PluginPage.Status` (string?) — `src/Notch.Core/Plugins/PluginPages.cs:43`
- [ ] `PluginPage.Stats` (list of PluginStat, truncated to 8) — `src/Notch.Core/Plugins/PluginPages.cs:46`
- [ ] `PluginPage.Input` (Action<string>?; null = read-only console; runs in the background, exceptions logged) — `src/Notch.Core/Plugins/PluginPages.cs:53`
- [ ] `PluginPage.Accept` (Func<string,string?>?; null accepts, a string refuses; an exception refuses with "That could not be handled. See plugins.log.") (v9) — `src/Notch.Core/Plugins/PluginHost.cs:675`
- [ ] `PluginPage.InputHint` (string?) — `src/Notch.Core/Plugins/PluginPages.cs:64`
- [ ] `PluginPage.Back` (Action?) (v4) — `src/Notch.Core/Plugins/PluginPages.cs:70`
- [ ] `PluginPage.BackLabel` (string?, default "Back") (v4) — `src/Notch.Core/Plugins/PluginPages.cs:73`
- [ ] `PluginPage.Choices` (list of PluginChoice) (v4) — `src/Notch.Core/Plugins/PluginPages.cs:79`
- [ ] `PluginPage.Actions` (list of PluginAction, truncated to 6) (v5) — `src/Notch.Core/Plugins/PluginPages.cs:85`
- [ ] `PluginPage.Blocks` (list of PluginBlock, truncated to 100) (v5) — `src/Notch.Core/Plugins/PluginPages.cs:92`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** `PluginPage.View` (Func<object>? returning a WPF FrameworkElement; called on the UI thread when first shown and again when the function changes) (v9) — `src/Notch.Core/Plugins/PluginPages.cs:102`
- [ ] `PluginPage.Size` (PluginPageSize, default Large) (v9) — `src/Notch.Core/Plugins/PluginPages.cs:105`
- [ ] `PluginPageSize` enum (v9) — `src/Notch.Core/Plugins/PluginPages.cs:109`
- [ ] `PluginPageSize.Large` (920×540) — `src/Notch.Core/Plugins/PluginPages.cs:112`
- [ ] `PluginPageSize.Compact` (640 wide, height fits content up to the large height) — `src/Notch.Core/Plugins/PluginPages.cs:115`
- [ ] **[changed: View means an HTML view]** Body precedence: View, then Choices, then Blocks, then console — `docs/plugins.md:478`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** Page View area about 880×500 DIP; Back, Status, Actions and Input still work around it; a click in a text box gives the notch the keyboard — `docs/plugins.md:517`
- [ ] Console: keeps the newest 500 lines, follows new lines unless the user scrolled up, never echoes input, Up/Down recall earlier lines, input takes focus — `docs/plugins.md:454`
- [ ] Limit: `MaxConsoleLines` = 500 — `src/Notch.Core/Plugins/PluginPages.cs:203`
- [ ] Limit: `MaxBlocks` = 100 (also applied to widget blocks) — `src/Notch.Core/Plugins/PluginPages.cs:206`
- [ ] Limit: Stats truncated to 8 and Actions to 6 on Set — `src/Notch.Core/Plugins/PluginPages.cs:225`
- [ ] Documented limit: Set no faster than once a second — `docs/plugins.md:463`
- [ ] Page key `<pluginId>/<pageId>`; opened from outside by `notchctl open page`, `notch://open?page=` and the webhook — `src/Notch.Core/Plugins/PluginManager.cs:168`
- [ ] `PluginAction` record — `src/Notch.Core/Plugins/PluginPages.cs:119`
- [ ] `PluginAction.Label` (required) — `src/Notch.Core/Plugins/PluginPages.cs:121`
- [ ] `PluginAction.Color` (GlowColor?) — `src/Notch.Core/Plugins/PluginPages.cs:124`
- [ ] `PluginAction.Enabled` (bool, default true) — `src/Notch.Core/Plugins/PluginPages.cs:127`
- [ ] `PluginAction.Confirm` (bool; first click shows "Click again" for a few seconds) — `src/Notch.Core/Plugins/PluginPages.cs:133`
- [ ] `PluginAction.Hint` (tooltip) — `src/Notch.Core/Plugins/PluginPages.cs:136`
- [ ] `PluginAction.Clicked` (Action?, background thread) — `src/Notch.Core/Plugins/PluginPages.cs:139`
- [ ] `PluginChoice` record — `src/Notch.Core/Plugins/PluginPages.cs:143`
- [ ] `PluginChoice.Label` (required) — `src/Notch.Core/Plugins/PluginPages.cs:145`
- [ ] `PluginChoice.Value` — `src/Notch.Core/Plugins/PluginPages.cs:147`
- [ ] `PluginChoice.Detail` — `src/Notch.Core/Plugins/PluginPages.cs:149`
- [ ] `PluginChoice.Color` — `src/Notch.Core/Plugins/PluginPages.cs:152`
- [ ] `PluginChoice.Clicked` (background thread) — `src/Notch.Core/Plugins/PluginPages.cs:155`
- [ ] `PluginStat` record — `src/Notch.Core/Plugins/PluginPages.cs:159`
- [ ] `PluginStat.Label` (required) — `src/Notch.Core/Plugins/PluginPages.cs:161`
- [ ] `PluginStat.Value` — `src/Notch.Core/Plugins/PluginPages.cs:163`
- [ ] `PluginStat.CountingFrom` (v9) — `src/Notch.Core/Plugins/PluginPages.cs:166`
- [ ] `PluginStat.Detail` — `src/Notch.Core/Plugins/PluginPages.cs:168`
- [ ] `PluginStat.Progress` (double?) — `src/Notch.Core/Plugins/PluginPages.cs:171`
- [ ] `PluginStat.Color` — `src/Notch.Core/Plugins/PluginPages.cs:174`
- [ ] Threading: page Back, choice, action and block callbacks run via Task.Run and exceptions are logged ("A click handler of page '…' failed.") — `src/Notch.Core/Plugins/PluginHost.cs:662`
- [ ] Internal change kinds for the shell: Pages, Console, ConsoleCleared, OpenRequested — `src/Notch.Core/Plugins/PluginPages.cs:181`

### Blocks (PluginBlock kinds)
- [ ] `PluginBlock` abstract record (v5) — `src/Notch.Core/Plugins/PluginBlocks.cs:10`
- [ ] `PluginTextStyle` enum — `src/Notch.Core/Plugins/PluginBlocks.cs:12`
- [ ] `PluginTextStyle.Body` — `src/Notch.Core/Plugins/PluginBlocks.cs:14`
- [ ] `PluginTextStyle.Heading` — `src/Notch.Core/Plugins/PluginBlocks.cs:15`
- [ ] `PluginTextStyle.Muted` — `src/Notch.Core/Plugins/PluginBlocks.cs:18`
- [ ] `PluginTextStyle.Code` — `src/Notch.Core/Plugins/PluginBlocks.cs:21`
- [ ] `PluginText` (wraps, keeps line breaks) — `src/Notch.Core/Plugins/PluginBlocks.cs:25`
- [ ] `PluginText.Text` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:27`
- [ ] `PluginText.Style` — `src/Notch.Core/Plugins/PluginBlocks.cs:29`
- [ ] `PluginText.Color` — `src/Notch.Core/Plugins/PluginBlocks.cs:31`
- [ ] `PluginValueRow` — `src/Notch.Core/Plugins/PluginBlocks.cs:35`
- [ ] `PluginValueRow.Label` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:37`
- [ ] `PluginValueRow.Value` — `src/Notch.Core/Plugins/PluginBlocks.cs:39`
- [ ] `PluginValueRow.Color` — `src/Notch.Core/Plugins/PluginBlocks.cs:41`
- [ ] `PluginProgress` — `src/Notch.Core/Plugins/PluginBlocks.cs:45`
- [ ] `PluginProgress.Label` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:47`
- [ ] `PluginProgress.Value` — `src/Notch.Core/Plugins/PluginBlocks.cs:49`
- [ ] `PluginProgress.Progress` (double, 0..1) — `src/Notch.Core/Plugins/PluginBlocks.cs:52`
- [ ] `PluginProgress.Color` — `src/Notch.Core/Plugins/PluginBlocks.cs:54`
- [ ] `PluginTable` (limit: 8 columns and 200 rows drawn) — `src/Notch.Core/Plugins/PluginBlocks.cs:58`
- [ ] `PluginTable.Columns` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:60`
- [ ] `PluginTable.Rows` — `src/Notch.Core/Plugins/PluginBlocks.cs:62`
- [ ] `PluginChart` (limit: 240 values drawn) — `src/Notch.Core/Plugins/PluginBlocks.cs:66`
- [ ] `PluginChart.Label` — `src/Notch.Core/Plugins/PluginBlocks.cs:68`
- [ ] `PluginChart.Values` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:70`
- [ ] `PluginChart.Max` (default max(values, 1)) — `src/Notch.Core/Plugins/PluginBlocks.cs:73`
- [ ] `PluginChart.Color` — `src/Notch.Core/Plugins/PluginBlocks.cs:75`
- [ ] `PluginChart.Height` (default 72, range 40–240 DIP) — `src/Notch.Core/Plugins/PluginBlocks.cs:78`
- [ ] `PluginButtons` — `src/Notch.Core/Plugins/PluginBlocks.cs:82`
- [ ] `PluginButtons.Actions` (required list of PluginAction) — `src/Notch.Core/Plugins/PluginBlocks.cs:84`
- [ ] `PluginToggle` — `src/Notch.Core/Plugins/PluginBlocks.cs:88`
- [ ] `PluginToggle.Label` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:90`
- [ ] `PluginToggle.Detail` — `src/Notch.Core/Plugins/PluginBlocks.cs:92`
- [ ] `PluginToggle.Value` — `src/Notch.Core/Plugins/PluginBlocks.cs:94`
- [ ] `PluginToggle.Changed` (Action<bool>?, background thread) — `src/Notch.Core/Plugins/PluginBlocks.cs:97`
- [ ] `PluginSlider` — `src/Notch.Core/Plugins/PluginBlocks.cs:101`
- [ ] `PluginSlider.Label` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:103`
- [ ] `PluginSlider.Min` (default 0) — `src/Notch.Core/Plugins/PluginBlocks.cs:105`
- [ ] `PluginSlider.Max` (default 100) — `src/Notch.Core/Plugins/PluginBlocks.cs:107`
- [ ] `PluginSlider.Value` — `src/Notch.Core/Plugins/PluginBlocks.cs:109`
- [ ] `PluginSlider.Step` (default 1; 0 = continuous) — `src/Notch.Core/Plugins/PluginBlocks.cs:112`
- [ ] `PluginSlider.Unit` — `src/Notch.Core/Plugins/PluginBlocks.cs:115`
- [ ] `PluginSlider.Changed` (Action<double>?, fires on release, background thread) — `src/Notch.Core/Plugins/PluginBlocks.cs:118`
- [ ] `PluginSelect` (code says drop-down; docs say "a row of choices") — `src/Notch.Core/Plugins/PluginBlocks.cs:122`
- [ ] `PluginSelect.Label` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:124`
- [ ] `PluginSelect.Options` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:126`
- [ ] `PluginSelect.Selected` — `src/Notch.Core/Plugins/PluginBlocks.cs:128`
- [ ] `PluginSelect.Changed` (Action<string>?) — `src/Notch.Core/Plugins/PluginBlocks.cs:131`
- [ ] `PluginTextField` — `src/Notch.Core/Plugins/PluginBlocks.cs:135`
- [ ] `PluginTextField.Label` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:137`
- [ ] `PluginTextField.Value` (the user's in-progress text is preserved across updates) — `src/Notch.Core/Plugins/PluginBlocks.cs:143`
- [ ] `PluginTextField.ClearOnSubmit` (v9) — `src/Notch.Core/Plugins/PluginBlocks.cs:146`
- [ ] `PluginTextField.Lines` (default 1, range 1–12; above 1 Enter adds a newline and Ctrl+Enter submits; ignored when Secret) (v9) — `src/Notch.Core/Plugins/PluginBlocks.cs:152`
- [ ] `PluginTextField.Hint` — `src/Notch.Core/Plugins/PluginBlocks.cs:155`
- [ ] `PluginTextField.Secret` — `src/Notch.Core/Plugins/PluginBlocks.cs:158`
- [ ] `PluginTextField.SubmitLabel` (default "Save") — `src/Notch.Core/Plugins/PluginBlocks.cs:161`
- [ ] `PluginTextField.Submitted` (Action<string>?, on button or Enter) — `src/Notch.Core/Plugins/PluginBlocks.cs:164`
- [ ] `PluginTextField.Changed` (Action<string>?, debounced about 150 ms, not fired for plugin-set text) (v9) — `src/Notch.Core/Plugins/PluginBlocks.cs:170`
- [ ] `PluginImage` (PNG, JPEG or any WPF-readable format) — `src/Notch.Core/Plugins/PluginBlocks.cs:174`
- [ ] `PluginImage.Data` (required byte[]) — `src/Notch.Core/Plugins/PluginBlocks.cs:176`
- [ ] `PluginImage.Height` (default 120, range 20–400 DIP, width follows the picture) — `src/Notch.Core/Plugins/PluginBlocks.cs:179`
- [ ] `PluginChoices` (limit: 200 rows drawn) (v9) — `src/Notch.Core/Plugins/PluginBlocks.cs:186`
- [ ] `PluginChoices.Choices` (required) — `src/Notch.Core/Plugins/PluginBlocks.cs:188`
- [ ] `PluginSeparator` — `src/Notch.Core/Plugins/PluginBlocks.cs:192`
- [ ] Threading: callbacks in Buttons, Choices, Toggle, Slider, Select and TextField (Submitted and Changed) are wrapped to run off the UI thread — `src/Notch.Core/Plugins/PluginHost.cs:127`

### Widgets (IPluginWidgets / PluginWidgetType / PluginWidgetContent / WidgetOption)
- [ ] `IPluginWidgets` interface (v8) — `src/Notch.Core/Plugins/PluginWidgets.cs:89`
- [ ] `Register(PluginWidgetType)` replaces by id; existing widgets get the new callbacks; throws on a blank Id or Name — `src/Notch.Core/Plugins/PluginWidgets.cs:149`
- [ ] `Unregister(string typeId)` → bool; widgets stay in the layout and show "plugin not running" — `src/Notch.Core/Plugins/PluginWidgets.cs:98`
- [ ] `Placed` (IReadOnlyList<PluginWidget>) — `src/Notch.Core/Plugins/PluginWidgets.cs:101`
- [ ] `Show(widgetId, PluginWidgetContent)`; ignored for widgets that are not the plugin's or not placed; shows "Loading…" until the first call — `src/Notch.Core/Plugins/PluginWidgets.cs:323`
- [ ] `PluginWidgetType` record — `src/Notch.Core/Plugins/PluginWidgets.cs:13`
- [ ] `PluginWidgetType.Id` (required, persisted in the layout) — `src/Notch.Core/Plugins/PluginWidgets.cs:16`
- [ ] `PluginWidgetType.Name` (required) — `src/Notch.Core/Plugins/PluginWidgets.cs:19`
- [ ] `PluginWidgetType.Description` — `src/Notch.Core/Plugins/PluginWidgets.cs:22`
- [ ] `PluginWidgetType.Glyph` (default "") — `src/Notch.Core/Plugins/PluginWidgets.cs:25`
- [ ] `PluginWidgetType.FullWidth` — `src/Notch.Core/Plugins/PluginWidgets.cs:28`
- [ ] `PluginWidgetType.Single` — `src/Notch.Core/Plugins/PluginWidgets.cs:31`
- [ ] `PluginWidgetType.Options` (list of WidgetOption; when any exist the form opens on add) — `src/Notch.Core/Plugins/PluginWidgets.cs:37`
- [ ] `PluginWidgetType.Placed` (Action<PluginWidget>?) — `src/Notch.Core/Plugins/PluginWidgets.cs:43`
- [ ] `PluginWidgetType.Removed` — `src/Notch.Core/Plugins/PluginWidgets.cs:46`
- [ ] `PluginWidgetType.OptionsChanged` — `src/Notch.Core/Plugins/PluginWidgets.cs:49`
- [ ] `PluginWidgetType.VisibleChanged` (Action<PluginWidget,bool>?) — `src/Notch.Core/Plugins/PluginWidgets.cs:52`
- [ ] Placed fires only the first time; if Home is visible, VisibleChanged(true) follows — `src/Notch.Core/Plugins/PluginWidgets.cs:236`
- [ ] `PluginWidget` record (Id stable across restarts, TypeId, Options with defaults filled in, own keys only) — `src/Notch.Core/Plugins/PluginWidgets.cs:59`
- [ ] `PluginWidget.Option(key, fallback = "")` — `src/Notch.Core/Plugins/PluginWidgets.cs:62`
- [ ] `PluginWidgetContent` record — `src/Notch.Core/Plugins/PluginWidgets.cs:66`
- [ ] `PluginWidgetContent.Title` (null = type name, empty = none) — `src/Notch.Core/Plugins/PluginWidgets.cs:69`
- [ ] `PluginWidgetContent.Blocks` (truncated to 100) — `src/Notch.Core/Plugins/PluginWidgets.cs:72`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** `PluginWidgetContent.View` (Func<object>? WPF element; called on the UI thread once per widget and again when the function changes; a throw or a non-element shows an error) — `src/Notch.Core/Plugins/PluginWidgets.cs:82`
- [ ] `PluginWidgetContent.Clicked` (click outside controls, background thread) — `src/Notch.Core/Plugins/PluginWidgets.cs:85`
- [ ] Widget source key `<pluginId>/<typeId>` stored in `HomeWidget.Source`; kind is "plugin-widget" — `src/Notch.Core/Plugins/PluginWidgets.cs:128`
- [ ] Threading: all widget-type callbacks run via Task.Run, and exceptions are logged ("A callback of … failed.") — `src/Notch.Core/Plugins/PluginHost.cs:321`
- [ ] When a plugin stops, its widgets stay where they are and say the plugin is not running — `docs/plugins.md:425`
- [x] `WidgetOptionType` enum — `src/Notch.Core/Widgets/HomeLayout.cs:75`
- [x] `WidgetOptionType.Text` — `src/Notch.Core/Widgets/HomeLayout.cs:78`
- [x] `WidgetOptionType.Lines` — `src/Notch.Core/Widgets/HomeLayout.cs:81`
- [x] `WidgetOptionType.Toggle` ("true"/"false") — `src/Notch.Core/Widgets/HomeLayout.cs:84`
- [x] `WidgetOptionType.Choice` (null Choices are app-filled) — `src/Notch.Core/Widgets/HomeLayout.cs:87`
- [x] `WidgetOption(Key, Label, Type, Default = "", Choices = null, Hint = null)` record — `src/Notch.Core/Widgets/HomeLayout.cs:91`
- [x] `WidgetOption.On` = "true", `WidgetOption.Off` = "false" — `src/Notch.Core/Widgets/HomeLayout.cs:99`

### Palette (IPluginPalette / PluginPaletteEntry)
- [ ] `IPluginPalette` interface (v9) — `src/Notch.Core/Plugins/PluginPalette.cs:7`
- [ ] `Set(PluginPaletteEntry)` replaces by Id; throws on a blank Id, blank Title or null Invoke — `src/Notch.Core/Plugins/PluginPalette.cs:79`
- [ ] `Remove(string id)` → bool — `src/Notch.Core/Plugins/PluginPalette.cs:13`
- [ ] `Clear()` removes fixed entries only, not the provider — `src/Notch.Core/Plugins/PluginHost.cs:353`
- [ ] `SetProvider(Func<string, CancellationToken, Task<IReadOnlyList<PluginPaletteEntry>>>?)`: one per plugin, null removes it — `src/Notch.Core/Plugins/PluginPalette.cs:24`
- [ ] `PluginPaletteEntry` record — `src/Notch.Core/Plugins/PluginPalette.cs:28`
- [ ] `PluginPaletteEntry.Id` (required) — `src/Notch.Core/Plugins/PluginPalette.cs:31`
- [ ] `PluginPaletteEntry.Title` (required) — `src/Notch.Core/Plugins/PluginPalette.cs:34`
- [ ] `PluginPaletteEntry.Detail` (defaults to the plugin's name) — `src/Notch.Core/Plugins/PluginPalette.cs:37`
- [ ] `PluginPaletteEntry.Keywords` — `src/Notch.Core/Plugins/PluginPalette.cs:40`
- [ ] `PluginPaletteEntry.Glyph` — `src/Notch.Core/Plugins/PluginPalette.cs:43`
- [ ] `PluginPaletteEntry.Image` (byte[]?) — `src/Notch.Core/Plugins/PluginPalette.cs:46`
- [ ] `PluginPaletteEntry.Invoke` (required Action; runs after the palette closes, background thread, exceptions logged) — `src/Notch.Core/Plugins/PluginPalette.cs:49`
- [ ] Fixed entries are ranked with Notch's own by title, then Keywords — `docs/plugins.md:531`
- [ ] Limit: `MaxResultsPerPlugin` = 5 and `MaxResults` = 5 in total — `src/Notch.Core/Plugins/PluginPalette.cs:62`
- [ ] Limit: `MaxEntriesPerPlugin` = 100 (extra entries silently ignored) — `src/Notch.Core/Plugins/PluginPalette.cs:67`
- [ ] Limit: `ProviderTimeout` = 2 s; late or cancelled answers are dropped — `src/Notch.Core/Plugins/PluginPalette.cs:69`
- [ ] Provider: blank query → no call; all providers queried in parallel via Task.Run; results without Id, Title or Invoke filtered out; shown below the best matches in plugin order, not re-ranked — `src/Notch.Core/Plugins/PluginPalette.cs:165`
- [ ] A provider exception is logged ("The palette search failed.") — `src/Notch.Core/Plugins/PluginManager.cs:87`
- [ ] Built-in palette entries "Switch on/off <plugin>" and "Stop all plugins" — `src/Notch.App/App.xaml.cs:445`

### Hotkeys (IPluginHotkeys)
- [ ] `IPluginHotkeys` interface (v9) — `src/Notch.Core/Plugins/PluginHotkeys.cs:6`
- [ ] `Register(string chord, Action pressed)` → IDisposable; Dispose releases it, and so does plugin stop — `src/Notch.Core/Plugins/PluginHotkeys.cs:18`
- [ ] Throws ArgumentException for an unparseable chord ("…is not a hotkey Notch can read. Write it like Alt+Shift+C.") — `src/Notch.Core/Plugins/PluginHost.cs:412`
- [x] Chord grammar: modifiers Ctrl/Control, Alt, Shift and Win/Windows joined by "+"; at least one non-Shift modifier; key A–Z, 0–9, F1–F24 or Space; case-insensitive — `src/Notch.Core/Shell/Hotkey.cs:26`
- [x] Canonical form: Ctrl+Alt+Shift+Win order, key last — `src/Notch.Core/Shell/Hotkey.cs:64`
- [ ] Refused (warning logged, no-op handle) when used by Notch's own open or palette hotkey — `src/Notch.Core/Plugins/PluginHotkeys.cs:51`
- [ ] Refused when already registered by the same plugin, or used by another plugin (message names it) — `src/Notch.Core/Plugins/PluginHotkeys.cs:60`
- [ ] Refused when Windows or another app holds it (dropped afterwards, warning logged) — `src/Notch.Core/Plugins/PluginHotkeys.cs:139`
- [ ] Threading: `pressed` runs via Task.Run and exceptions are logged ("The hotkey … failed.") — `src/Notch.Core/Plugins/PluginHost.cs:423`
- [ ] Settings lists each plugin's chords — `src/Notch.Core/Plugins/PluginHotkeys.cs:112`

### Themes (IPluginThemes / PluginTheme)
- [ ] `IPluginThemes` interface (v6) — `src/Notch.Core/Plugins/PluginThemes.cs:17`
- [ ] `Set(PluginTheme)`: replacing the active theme reloads it — `src/Notch.Core/Plugins/PluginThemes.cs:24`
- [ ] Set throws ArgumentException for a blank Id or Name, an Id containing "/", or a File that is not a relative .xaml path inside the plugin folder; Name is trimmed — `src/Notch.Core/Plugins/PluginHost.cs:466`
- [ ] `Remove(string id)` → bool; falls back to Notch's own theme if it was active — `src/Notch.Core/Plugins/PluginThemes.cs:27`
- [ ] `Clear()` — `src/Notch.Core/Plugins/PluginThemes.cs:30`
- [ ] `PluginTheme` record — `src/Notch.Core/Plugins/PluginThemes.cs:34`
- [ ] `PluginTheme.Id` (required, no slash) — `src/Notch.Core/Plugins/PluginThemes.cs:37`
- [ ] `PluginTheme.Name` (required) — `src/Notch.Core/Plugins/PluginThemes.cs:40`
- [ ] `PluginTheme.File` (required, relative path, root element ResourceDictionary) — `src/Notch.Core/Plugins/PluginThemes.cs:46`
- [ ] `PluginTheme.Base` (default Dark) — `src/Notch.Core/Plugins/PluginThemes.cs:54`
- [ ] `PluginTheme.Description` — `src/Notch.Core/Plugins/PluginThemes.cs:57`
- [ ] `PluginThemeBase` enum — `src/Notch.Core/Plugins/PluginThemes.cs:4`
- [ ] `PluginThemeBase.Dark` — `src/Notch.Core/Plugins/PluginThemes.cs:7`
- [ ] `PluginThemeBase.Light` — `src/Notch.Core/Plugins/PluginThemes.cs:10`
- [ ] Saved choice key `<pluginId>/<themeId>` (settings.PluginTheme); the normal theme shows while the plugin is off, and the choice returns when it comes back — `src/Notch.Core/Plugins/PluginThemes.cs:71`
- [ ] Base decides IsLight: the light base deepens activity and card colours, sets Shell.IsDark and picks the terminal palette — `src/Notch.App/Shell/ThemeManager.cs:110`
- [ ] **[changed: translated to CSS variables layered over the base theme]** Plugin dictionary merged after the base Light/Dark dictionary (added before old ones are removed) — `src/Notch.App/Shell/ThemeManager.cs:117`
- [ ] Reloaded when the file's mtime changes and Set is called or Settings is saved — `src/Notch.App/Shell/ThemeManager.cs:101`
- [ ] Relative URIs (images, fonts, merged dictionaries) resolve against the file's folder via ParserContext.BaseUri — `src/Notch.App/Shell/ThemeManager.cs:268`
- [ ] Rejected when the root is not a ResourceDictionary; Problem text shown in Settings — `src/Notch.App/Shell/ThemeManager.cs:269`
- [ ] Any load exception rejects the theme and keeps the normal one — `src/Notch.App/Shell/ThemeManager.cs:283`
- [ ] Validation is recursive into MergedDictionaries — `src/Notch.App/Shell/ThemeManager.cs:316`
- [ ] Type rules: UiFontSize and IslandRadiusScale are double; IconFont is FontFamily; *Brush is Brush; *FontFamily is FontFamily; *Radius is CornerRadius — `src/Notch.App/Shell/ThemeManager.cs:319`
- [ ] **[changed: WPF Style keys cannot be translated to CSS; the host ignores them and logs a warning]** Style keys must be a Style with an exact TargetType — `src/Notch.App/Shell/ThemeManager.cs:30`
- [ ] A theme that defines AccentBrush (directly or merged) owns the accent, and the user's accent is not applied — `src/Notch.App/Shell/ThemeManager.cs:148`
- [ ] User accent overlay: AccentHoverBrush alpha 0x59, AccentPressedBrush alpha 0x80, OnAccentBrush black if luminance > 160 else white — `src/Notch.App/Shell/ThemeManager.cs:226`
- [ ] `--theme=` overrides and disables plugin themes; `--plugin-theme=<pid>/<tid>` applies one for a single run — `src/Notch.App/Shell/ThemeManager.cs:94`
- [ ] UseWPF projects must keep theme .xaml as loose files (`Page Remove`) — `docs/plugins.md:413`

### Theme resource keys
- [ ] `IslandBrush` (Brush; gradients and images allowed) — `src/Notch.App/Themes/Dark.xaml:4`
- [ ] `TextBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:5`
- [ ] `SecondaryTextBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:7`
- [ ] `AccentBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:10`
- [ ] `AccentHoverBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:11`
- [ ] `AccentPressedBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:12`
- [ ] `CardBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:16`
- [ ] `ControlBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:17`
- [ ] `HoverBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:18`
- [ ] `PressedBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:19`
- [ ] `CardBorderBrush` (Brush, transparent by default) — `src/Notch.App/Themes/Dark.xaml:22`
- [ ] `SelectedBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:23`
- [ ] `TrackBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:26`
- [ ] `WorkingBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:29`
- [ ] `AttentionBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:30`
- [ ] `DoneBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:31`
- [ ] `DangerBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:32`
- [ ] `WindowBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:35`
- [ ] `SidebarBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:36`
- [ ] `SwitchOnBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:37`
- [ ] `OnAccentBrush` (Brush) — `src/Notch.App/Themes/Dark.xaml:38`
- [ ] Light.xaml values for the same 21 brush keys — `src/Notch.App/Themes/Light.xaml:4`
- [ ] `UiFontFamily` (FontFamily; SF Pro if installed, else the bundled Inter) — `src/Notch.App/Themes/Design.xaml:11`
- [ ] `DisplayFontFamily` (FontFamily) — `src/Notch.App/Themes/Design.xaml:12`
- [ ] `CodeFontFamily` (FontFamily; also feeds the terminal font) — `src/Notch.App/Themes/Design.xaml:13`
- [ ] `IconFont` (FontFamily, Segoe Fluent Icons code points) — `src/Notch.App/Themes/Design.xaml:14`
- [ ] `UiFontSize` (double) — `src/Notch.App/Themes/Design.xaml:15`
- [ ] `CardRadius` (CornerRadius) — `src/Notch.App/Themes/Design.xaml:21`
- [ ] `ControlRadius` (CornerRadius, keep ≤ 12) — `src/Notch.App/Themes/Design.xaml:22`
- [ ] `SmallRadius` (CornerRadius) — `src/Notch.App/Themes/Design.xaml:23`
- [ ] `IconButtonRadius` (CornerRadius) — `src/Notch.App/Themes/Design.xaml:24`
- [ ] `RowRadius` (CornerRadius) — `src/Notch.App/Themes/Design.xaml:26`
- [ ] `IslandRadiusScale` (double, default 1, clamped 0..2 at use) — `src/Notch.App/Shell/ThemeManager.cs:222`
- [ ] `TerminalBackgroundBrush` (Brush, solid only; falls back to IslandBrush; not defined in base themes) — `src/Notch.App/Shell/ThemeManager.cs:178`
- [ ] `TerminalForegroundBrush` (Brush, solid; falls back to TextBrush) — `src/Notch.App/Shell/ThemeManager.cs:184`
- [ ] `TerminalCursorBrush` (Brush, solid; falls back to the foreground) — `src/Notch.App/Shell/ThemeManager.cs:190`
- [ ] `TerminalSelectionBrush` (Brush, solid) — `src/Notch.App/Shell/ThemeManager.cs:195`
- [ ] `TerminalAnsi0Brush` … `TerminalAnsi15Brush` (Brush, solid; mapped to black, red, green, yellow, blue, magenta, cyan, white, then the bright variants) — `src/Notch.App/Shell/ThemeManager.cs:23`
- [ ] **[changed: Style keys are ignored, see above]** `IconButton` (Style, TargetType Button) — `src/Notch.App/Themes/Styles.xaml:6`
- [ ] `PillButton` (Style, Button) — `src/Notch.App/Themes/Styles.xaml:43`
- [ ] `PillTextBox` (Style, TextBox) — `src/Notch.App/Themes/Styles.xaml:83`
- [ ] `TabButton` (Style, RadioButton) — `src/Notch.App/Themes/Styles.xaml:130`
- [ ] `Card` (Style, Border) — `src/Notch.App/Themes/Styles.xaml:169`
- [ ] `CardLabel` (Style, TextBlock) — `src/Notch.App/Themes/Styles.xaml:176`
- [ ] `CardValue` (Style, TextBlock) — `src/Notch.App/Themes/Styles.xaml:181`
- [ ] Undocumented and unvalidated key `PillTextArea` (Style TextBox, BasedOn PillTextBox via StaticResource) — `src/Notch.App/Themes/Styles.xaml:111`
- [ ] Undocumented and unvalidated keys `NotchScrollThumb` (Thumb) and `NotchScrollPage` (RepeatButton) — `src/Notch.App/Themes/Styles.xaml:206`
- [ ] Undocumented and unvalidated Window.xaml styles: NotchWindow, Heading, Help, Group, RowLabel, SidebarItem, CaptionButton, AccentButton, ComboToggle, SliderPart, ScrollThumb, ScrollPage, plus implicit styles for CheckBox, Button, TextBox, PasswordBox, ComboBox, ComboBoxItem, Slider, ScrollBar, Expander and ToolTip — `src/Notch.App/Themes/Window.xaml:8`
- [ ] Undocumented keys `AgentLogo.claude`, `AgentLogo.codex`, `AgentLogo.geminicli` and `AgentLogo.opencode` (GeometryGroup) — `src/Notch.App/Themes/AgentLogos.xaml:6`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** Widget and page views should use TextBrush, SecondaryTextBrush, CardBrush and AccentBrush by resource key — `docs/plugins.md:401`

### Glow (IPluginGlow / GlowFrame / GlowSegment)
- [ ] `IPluginGlow` interface (v7) — `src/Notch.Core/Plugins/PluginGlow.cs:45`
- [ ] `Set(GlowFrame)` — `src/Notch.Core/Plugins/PluginGlow.cs:48`
- [ ] `Clear()` (no-op without a frame) — `src/Notch.Core/Plugins/PluginGlow.cs:51`
- [ ] `GlowFrame` record — `src/Notch.Core/Plugins/PluginGlow.cs:14`
- [ ] `GlowFrame.Color` (default White) — `src/Notch.Core/Plugins/PluginGlow.cs:17`
- [ ] `GlowFrame.Intensity` (default 1, clamped 0..1, NaN → 0) — `src/Notch.Core/Plugins/PluginGlow.cs:20`
- [ ] `GlowFrame.Reach` (default 1, clamped 0.25..3, non-finite → 1) — `src/Notch.Core/Plugins/PluginGlow.cs:26`
- [ ] `GlowFrame.Segments` (list of GlowSegment; when non-empty it overrides Color and Intensity) — `src/Notch.Core/Plugins/PluginGlow.cs:33`
- [ ] `GlowSegment(GlowColor Color, double Intensity)` readonly record struct — `src/Notch.Core/Plugins/PluginGlow.cs:8`
- [ ] Limit: `MaxSegments` = 64 (extras dropped) — `src/Notch.Core/Plugins/PluginGlow.cs:58`
- [ ] Limit: a frame lasts 1 s after its last Set — `src/Notch.Core/Plugins/PluginGlow.cs:61`
- [ ] The most recent frame across plugins wins — `src/Notch.Core/Plugins/PluginGlow.cs:82`
- [ ] Sanitising rules — `src/Notch.Core/Plugins/PluginGlow.cs:126`
- [ ] Not drawn while expanded; drawn on the plain pill; respects user brightness and the glow switch; redraw about 25 fps; send at most about 30 fps — `docs/plugins.md:693`

### Audio (IPluginAudio / AudioFrame)
- [ ] `IPluginAudio` interface (v7) — `src/Notch.Core/Plugins/PluginAudio.cs:33`
- [ ] `IPluginAudio.Latest` (AudioFrame; the first read starts capture; Silent after the plugin closes) — `src/Notch.Core/Plugins/PluginHost.cs:554`
- [ ] `AudioFrame` record (Level, Bass, Mid, Treble, Bands) — `src/Notch.Core/Plugins/PluginAudio.cs:15`
- [ ] `AudioFrame.Level` (0..1) — `src/Notch.Core/Plugins/PluginAudio.cs:7`
- [ ] `AudioFrame.Bass` (below about 250 Hz) — `src/Notch.Core/Plugins/PluginAudio.cs:8`
- [ ] `AudioFrame.Mid` — `src/Notch.Core/Plugins/PluginAudio.cs:9`
- [ ] `AudioFrame.Treble` (above about 4 kHz) — `src/Notch.Core/Plugins/PluginAudio.cs:10`
- [ ] `AudioFrame.Bands` (IReadOnlyList<float>, log-spaced, pre-scaled) — `src/Notch.Core/Plugins/PluginAudio.cs:11`
- [ ] `AudioFrame.Silent` — `src/Notch.Core/Plugins/PluginAudio.cs:18`
- [ ] `PluginAudio.BandCount` = 16 — `src/Notch.Core/Plugins/PluginAudio.cs:25`
- [ ] A frame older than 150 ms reads as Silent — `src/Notch.Core/Plugins/PluginAudio.cs:50`
- [ ] Reference-counted capture: starts at the first reader, stops at the last; a starter failure means silence — `src/Notch.Core/Plugins/PluginAudio.cs:47`
- [ ] Loopback of output only, never the microphone; levels only; the plugin should declare the "audio" permission — `docs/plugins.md:678`
- [ ] App supplies the SpectrumAnalyzer as Starter — `src/Notch.App/App.xaml.cs:162`

### Shell (IPluginShell)
- [ ] `IPluginShell` interface (v5) — `src/Notch.Core/Plugins/PluginShell.cs:9`
- [ ] `IsExpanded` — `src/Notch.Core/Plugins/PluginShell.cs:12`
- [ ] `IsPageVisible(pageId)` — `src/Notch.Core/Plugins/PluginShell.cs:15`
- [ ] `AreCardsVisible` (Plugins tab, or Home with one of the plugin's cards) — `src/Notch.Core/Plugins/PluginShell.cs:21`
- [ ] `IsCardVisible(cardId)` (v9) — `src/Notch.Core/Plugins/PluginShell.cs:24`
- [ ] `IsDark` (default true) — `src/Notch.Core/Plugins/PluginShell.cs:27`
- [ ] `Accent` (GlowColor?) — `src/Notch.Core/Plugins/PluginShell.cs:30`
- [ ] `OpenSettings()` (Task.Run; does nothing if already open) — `src/Notch.Core/Plugins/PluginHost.cs:724`
- [ ] `Changed` event, raised via Task.Run with exceptions swallowed; fires only on a real state change — `src/Notch.Core/Plugins/PluginHost.cs:777`
- [ ] `Notify(title, detail, glyph, color, lifetime)`: transient activity id "notice", replaces the previous one, Flash glow if a colour is given, default lifetime 4 s, throws on a blank title — `src/Notch.Core/Plugins/PluginHost.cs:757`

### Bus (IPluginBus / PluginMessage / notch.* topics)
- [ ] `IPluginBus` interface (v5) — `src/Notch.Core/Plugins/PluginShell.cs:54`
- [ ] `Publish(topic, payload = null)` returns immediately and reaches every subscriber, including the sender; throws on a blank topic — `src/Notch.Core/Plugins/PluginShell.cs:57`
- [ ] `Subscribe(topic, Action<PluginMessage>)` → IDisposable; disposed at plugin stop; a closed host returns a no-op — `src/Notch.Core/Plugins/PluginHost.cs:827`
- [ ] Exact, case-sensitive topic match — `src/Notch.Core/Plugins/PluginShell.cs:165`
- [ ] Threading: each delivery is its own Task.Run; a throwing handler is logged ("A subscriber of '…' failed.") — `src/Notch.Core/Plugins/PluginShell.cs:172`
- [ ] `PluginMessage(Sender, Topic, Payload)` record — `src/Notch.Core/Plugins/PluginShell.cs:48`
- [ ] Plugins cannot publish on "notch." topics (case-insensitive prefix); a warning is logged and nothing is sent — `src/Notch.Core/Plugins/PluginHost.cs:818`
- [ ] `NotchEvents.Sender` = "notch" (v9) — `src/Notch.Core/Plugins/NotchEvents.cs:14`
- [ ] `NotchEvents.Prefix` = "notch." — `src/Notch.Core/Plugins/NotchEvents.cs:17`
- [ ] `notch.timer` payload {event: started|paused|resumed|cancelled|finished, kind: timer|pomodoro-focus|pomodoro-short-break|pomodoro-long-break, seconds (rounded int), name}; camelCase JSON — `src/Notch.Core/Plugins/NotchEvents.cs:26`
- [ ] Each Pomodoro phase finishes and the next starts (two messages) — `docs/plugins.md:593`
- [ ] `notch.media` payload {event:"changed", playing, title, artist, album, app} or {event:"stopped"} — `src/Notch.Core/Plugins/NotchEvents.cs:33`
- [ ] Timer publish site — `src/Notch.App/Shell/NotchWindow.Widgets.cs:314`
- [ ] Media publish site — `src/Notch.App/Shell/NotchWindow.Media.cs:42`
- [ ] `PluginManager.PublishFromNotch` — `src/Notch.Core/Plugins/PluginManager.cs:315`

### Settings (IPluginSettings) and settings schema
- [ ] `IPluginSettings` interface (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:175`
- [ ] `Get<T>(key, fallback)`: fallback when missing, wrong shape or null; never throws for bad data; throws on a blank key — `src/Notch.Core/Plugins/PluginHost.cs:917`
- [ ] `Set<T>(key, value)` serialises with System.Text.Json (indented) and saves immediately (atomic via .tmp) — `src/Notch.Core/Plugins/PluginHost.cs:940`
- [ ] `Remove(key)` → bool — `src/Notch.Core/Plugins/INotchPlugin.cs:194`
- [ ] `Changed` event (EventHandler<string> key; background thread) (v5) — `src/Notch.Core/Plugins/INotchPlugin.cs:191`
- [ ] Storage: `<DataDirectory>/settings.json`, a flat object; a corrupt file reads as empty — `src/Notch.Core/Plugins/PluginHost.cs:965`
- [ ] `PluginSettingType` enum — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:5`
- [ ] `PluginSettingType.Text` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:8`
- [ ] `PluginSettingType.Number` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:11`
- [ ] `PluginSettingType.Bool` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:14`
- [ ] `PluginSettingType.Secret` (empty input keeps the stored value; stored as typed; never returned to Settings) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:17`
- [ ] `PluginSettingType.Choice` (requires options) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:20`
- [ ] `PluginSettingType.List` (JSON array of strings) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:23`
- [ ] `PluginSettingField` record — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:31`
- [ ] `PluginSettingField.Key` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:33`
- [ ] `PluginSettingField.Label` (falls back to the key) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:35`
- [ ] `PluginSettingField.Type` (unknown or missing → Text, case-insensitive) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:74`
- [ ] `PluginSettingField.Description` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:40`
- [ ] `PluginSettingField.Hint` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:43`
- [ ] `PluginSettingField.Min` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:45`
- [ ] `PluginSettingField.Max` — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:47`
- [ ] `PluginSettingField.Step` (default 1; values ≤ 0 → 1) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:92`
- [ ] `PluginSettingField.Options` (non-string items dropped) — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:52`
- [ ] Parse rules: at most 40 fields; skip non-objects, missing, blank or non-string keys, duplicate (trimmed) keys, and choice fields without options; string props are trimmed with blank → null; number props must be JSON numbers — `src/Notch.Core/Plugins/PluginSettingsSchema.cs:63`
- [ ] Settings window: secrets are never shown — `src/Notch.Core/Plugins/PluginManager.cs:272`
- [ ] Restart on option change: unknown keys are ignored; if the plugin has no Changed subscriber it is restarted, otherwise Changed fires per key — `src/Notch.Core/Plugins/PluginManager.cs:292`
- [ ] Settings window calls ApplySettings — `src/Notch.App/Settings/SettingsWindow.xaml.cs:509`

### Log (IPluginLog)
- [ ] `IPluginLog` interface (v1) — `src/Notch.Core/Plugins/INotchPlugin.cs:201`
- [ ] `Info(message)` — `src/Notch.Core/Plugins/INotchPlugin.cs:203`
- [ ] `Warn(message)` — `src/Notch.Core/Plugins/INotchPlugin.cs:205`
- [ ] `Error(message, exception = null)` — `src/Notch.Core/Plugins/INotchPlugin.cs:207`
- [ ] Line format `<u timestamp> [info|warn|error] <source>: <message>` with the exception's ToString on the following lines; manager source "notch" — `src/Notch.Core/Plugins/PluginLog.cs:15`
- [ ] File `%LocalAppData%\Notch\plugins.log`, shared by all plugins — `src/Notch.App/App.xaml.cs:156`
- [ ] Limit: rotate to `plugins.log.old` above 1 MB; write failures are swallowed — `src/Notch.Core/Plugins/PluginLog.cs:6`

### Threading guarantees (summary)
- [ ] Every IPluginHost member is thread-safe, and no UI-thread marshalling is required — `docs/plugins.md:746`
- [ ] Exceptions from Start, Stop and Clicked are caught and logged; never block long in them — `docs/plugins.md:747`
- [ ] After Stop, host calls are silently ignored — `docs/plugins.md:749`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** Only View factories (page and widget) run on the UI thread; every other callback runs on a background thread — `src/Notch.Core/Plugins/PluginWidgets.cs:76`

### Manifest (plugin.json)
- [ ] File name `plugin.json` — `src/Notch.Core/Plugins/PluginManifest.cs:13`
- [ ] JSON parsing: case-insensitive property names, comments skipped, trailing commas allowed, unknown properties ignored — `src/Notch.Core/Plugins/PluginManifest.cs:17`
- [ ] `id` (required; `^[a-z0-9]+([.-][a-z0-9]+)*$`; ≤ 64 characters; trimmed) — `src/Notch.Core/Plugins/PluginManifest.cs:105`
- [ ] `name` (required) — `src/Notch.Core/Plugins/PluginManifest.cs:132`
- [ ] `assembly` (required; bare file name ending in .dll, case-insensitive) — `src/Notch.Core/Plugins/PluginManifest.cs:112`
- [ ] `apiVersion` (required int; ≥ 1; ≤ PluginApi.Version, else "Needs plugin API N…") — `src/Notch.Core/Plugins/PluginManifest.cs:117`
- [ ] `version` (optional, display; also used for update comparison) — `src/Notch.Core/Plugins/PluginManifest.cs:40`
- [ ] `author` (optional) — `src/Notch.Core/Plugins/PluginManifest.cs:42`
- [ ] `description` (optional) — `src/Notch.Core/Plugins/PluginManifest.cs:44`
- [ ] `repository` (optional "owner/repo", for updates) (v5) — `src/Notch.Core/Plugins/PluginManifest.cs:47`
- [ ] `settings` (optional array; see the schema section) (v5) — `src/Notch.Core/Plugins/PluginManifest.cs:50`
- [ ] `permissions` (optional string[]; trimmed, lowercased, 1–32 characters, deduplicated, at most 12) — `src/Notch.Core/Plugins/PluginManifest.cs:76`
- [ ] Known permissions and their descriptions: network, filesystem, terminal, shell, notifications, clipboard, audio (unknown words are shown verbatim) — `src/Notch.Core/Plugins/PluginManifest.cs:60`
- [ ] Optional strings: whitespace → null, otherwise trimmed — `src/Notch.Core/Plugins/PluginManifest.cs:147`
- [ ] Error messages: unreadable, empty, missing property, invalid id, invalid assembly, apiVersion < 1, apiVersion too new — `src/Notch.Core/Plugins/PluginManifest.cs:96`
- [ ] Unusable manifests (including a duplicate id) are listed greyed out as Invalid with the reason — `docs/plugins.md:196`

### Plugin management: discovery, enabling, lifecycle
- [ ] Folders: installed `%AppData%\Notch\plugins`, data `%AppData%\Notch\plugin-data`, built-ins `<AppBase>\plugins` — `src/Notch.App/App.xaml.cs:152`
- [ ] Debug builds use `%AppData%\Notch-Debug` and `%LocalAppData%\Notch-Debug`, seeded from the installed copy with webhook and hooks off; `--shared-data` uses the installed folders — `docs/plugins.md:784`
- [ ] Discovery runs at start and each time Settings opens; reads manifests only — `docs/plugins.md:208`
- [ ] Discover forgets unloaded Invalid/Disabled slots; order is pinned (`--plugin=`), then built-in, then installed; subfolders starting with "." are skipped; sorted case-insensitively; folders without plugin.json are ignored (a pinned folder is an error instead) — `src/Notch.Core/Plugins/PluginManager.cs:185`
- [ ] Duplicate id → Invalid, "already used by the plugin in …" — `src/Notch.Core/Plugins/PluginManager.cs:626`
- [ ] `PluginStatus` enum — `src/Notch.Core/Plugins/PluginManager.cs:8`
- [ ] `PluginStatus.Disabled` — `src/Notch.Core/Plugins/PluginManager.cs:11`
- [ ] `PluginStatus.Running` — `src/Notch.Core/Plugins/PluginManager.cs:13`
- [ ] `PluginStatus.Failed` — `src/Notch.Core/Plugins/PluginManager.cs:16`
- [ ] `PluginStatus.Invalid` — `src/Notch.Core/Plugins/PluginManager.cs:19`
- [ ] `PluginInfo` record (Id, Name, Version, Author, Description, Directory, Status, Error, AlwaysEnabled, Repository, InstalledTag, Settings, Permissions, BuiltIn) — `src/Notch.Core/Plugins/PluginManager.cs:27`
- [ ] Enabled list persisted as `AppSettings.EnabledPlugins` — `src/Notch.Core/Settings/AppSettings.cs:130`
- [ ] SetEnabled: wanted = pinned, built-in or in the list; Disabled+wanted → Start; Running+unwanted → Stop; Failed+unwanted → Disabled (how a retry works) — `src/Notch.Core/Plugins/PluginManager.cs:221`
- [ ] Startup SetEnabled runs off the UI thread — `src/Notch.App/App.xaml.cs:307`
- [ ] Built-in plugins run without being enabled or approved (still scanned and revocation-checked) — `src/Notch.Core/Plugins/PluginManager.cs:714`
- [ ] Retry(id) restarts a Failed plugin (used after approval) — `src/Notch.Core/Plugins/PluginManager.cs:322`
- [ ] Restart(id) — `src/Notch.Core/Plugins/PluginManager.cs:355`
- [ ] Stop: calls Instance.Stop (exception logged), then Host.Close, then Disabled; logs "Stopped <id>" — `src/Notch.Core/Plugins/PluginManager.cs:864`
- [ ] Start logs "Started <id> <version>" and "<id> failed to start." — `src/Notch.Core/Plugins/PluginManager.cs:672`
- [ ] Dispose stops all running plugins and deletes build copies — `src/Notch.Core/Plugins/PluginManager.cs:382`
- [ ] Enable and Disable from the palette, notchctl or notch:// (Enable also runs Discover and Retry) — `src/Notch.App/Plugins/PluginInstallFlow.cs:147`
- [ ] Plugin id syntax for commands and registry: 1–64 characters plus a pattern — `src/Notch.Core/Automation/CommandParser.cs:318`

### Safe mode and kill switch
- [ ] `SafeMode` property: no plugin starts; enabled ones get the error "Safe mode: plugins are switched off until Notch is started normally." — `src/Notch.Core/Plugins/PluginManager.cs:239`
- [ ] `--no-plugins` flag → safe mode — `src/Notch.App/App.xaml.cs:185`
- [ ] Crash marker `%AppData%\Notch\plugins-starting`: written before SetEnabled and deleted after; if present at the next start → safe mode, marker deleted, 8 s Orange notice "Plugins are off" — `src/Notch.App/App.xaml.cs:184`
- [ ] Safe mode also blocks Retry and ReloadBuild starts — `src/Notch.Core/Plugins/PluginManager.cs:326`
- [ ] KillAll(reason): stops every running plugin, audits Killed, logs a warning, returns ids — `src/Notch.Core/Plugins/PluginManager.cs:846`
- [ ] Stop all (palette): clears EnabledPlugins, saves, 5 s notice "Plugins stopped" — `src/Notch.App/App.xaml.cs:466`
- [ ] Stop all (Settings button) — `src/Notch.App/Settings/SettingsWindow.xaml.cs:216`

### Development builds (--plugin=) and reload
- [ ] `--plugin=<folder>` is repeatable; always enabled; shown as "(development)"; does not change the saved enabled list; skips approval but not the scan — `docs/plugins.md:765`
- [ ] A dev build with an installed plugin's id runs in its place and uses `plugin-data\<id>.dev` (from 0.19) — `src/Notch.Core/Plugins/PluginManager.cs:630`
- [ ] Loaded from a fresh copy in `%TEMP%\Notch\plugin-builds\<pid>\<id>-<n>`; if copying fails, loads in place with a warning — `src/Notch.Core/Plugins/PluginManager.cs:457`
- [ ] Copy folders of dead processes are cleaned up; this process's copies are cleaned on Dispose — `src/Notch.Core/Plugins/PluginManager.cs:493`
- [ ] FileSystemWatcher (recursive; FileName, LastWrite, Size) with a 1.5 s settle timer, then ReloadBuild — `src/Notch.Core/Plugins/PluginManager.cs:534`
- [ ] ReloadBuild: stop, drop the factory, re-parse the manifest, start the new copy, log "Reloaded the build in …"; old code stays in memory — `src/Notch.Core/Plugins/PluginManager.cs:414`
- [ ] `ReloadBuilds` option (default true) — `src/Notch.Core/Plugins/PluginManager.cs:98`
- [ ] Installed plugins need a Notch restart to pick up a new build (assemblies stay loaded) — `docs/plugins.md:218`
- [ ] Dev flags: `--pin-open`, `--tab=plugins`, `--demo`, `--display=N`, `--plugin-theme=` — `docs/plugins.md:776`

### Pending updates and updates
- [ ] `ApplyPendingUpdates` before any load: moves `.update-<folder>` over `<folder>`, deletes `.installing-*` and `.old-*`; failures retry at the next start — `src/Notch.Core/Plugins/PluginInstaller.cs:68`
- [ ] Called at app start before Discover — `src/Notch.App/App.xaml.cs:166`
- [ ] Origin file `.notch-source` in the plugin folder: JSON {Repository, Tag} — `src/Notch.Core/Plugins/PluginUpdates.cs:16`
- [ ] Update source: the recorded repository, else the manifest's `repository` — `src/Notch.Core/Plugins/PluginUpdates.cs:40`
- [ ] Version parsing: strip a leading v/V and any -/+ suffix, then System.Version — `src/Notch.Core/Plugins/PluginUpdates.cs:45`
- [ ] IsNewer: the installed tag beats the manifest version; both must parse — `src/Notch.Core/Plugins/PluginUpdates.cs:63`
- [ ] `PluginUpdate` record (PluginId, Name, InstalledVersion, LatestTag, Source) — `src/Notch.Core/Plugins/PluginUpdates.cs:8`
- [ ] CheckForUpdateAsync (when Settings opens or on "Check for updates") — `src/Notch.Core/Plugins/PluginInstaller.cs:152`
- [ ] "Update and restart": approves the new files, then prompts before restarting if terminals are open — `src/Notch.App/Settings/SettingsWindow.xaml.cs:893`

### Installer (GitHub) limits and rules
- [ ] `PluginSource` parses "owner/repo" or github.com links; owner 1–39 characters, repo 1–100 characters, ".git" stripped — `src/Notch.Core/Plugins/PluginInstaller.cs:35`
- [ ] API URL `https://api.github.com/repos/<o>/<r>/releases/latest` (latest only: no drafts or pre-releases) — `src/Notch.Core/Plugins/PluginInstaller.cs:13`
- [ ] Headers: User-Agent "Notch-Plugins", Accept "application/vnd.github+json" — `src/Notch.Core/Plugins/PluginInstaller.cs:198`
- [ ] Exactly one `.zip` asset whose URL starts with `https://github.com/` — `src/Notch.Core/Plugins/PluginInstaller.cs:215`
- [ ] Limit: download ≤ 64 MB (checked against the listed size and cut off while streaming) — `src/Notch.Core/Plugins/PluginInstaller.cs:55`
- [ ] Limit: unpacked ≤ 256 MB and ≤ 2000 entries — `src/Notch.Core/Plugins/PluginInstaller.cs:56`
- [ ] Size must match the listed size; a "sha256:" digest is verified when present — `src/Notch.Core/Plugins/PluginInstaller.cs:266`
- [ ] plugin.json at the zip root or in exactly one top-level folder; exactly one manifest — `src/Notch.Core/Plugins/PluginInstaller.cs:289`
- [ ] Zip-slip guard — `src/Notch.Core/Plugins/PluginInstaller.cs:316`
- [ ] The manifest's assembly must be present — `src/Notch.Core/Plugins/PluginInstaller.cs:328`
- [ ] Placement: replaces the folder holding the same id, else `<id>`; a loaded plugin, or a failed move-aside, makes it pending (`.update-`); otherwise the old folder moves to `.old-` and is deleted — `src/Notch.Core/Plugins/PluginInstaller.cs:337`
- [ ] HTTP error mapping: 404, 403/429 rate limit, other errors, timeout — `src/Notch.Core/Plugins/PluginInstaller.cs:123`
- [ ] `PluginInstallResult(Manifest, Tag, Pending, Directory)` — `src/Notch.Core/Plugins/PluginInstaller.cs:43`
- [ ] Registry installs: one prompt at a time; manifest id must equal the registry id; "Install and switch on" (or a previously approved plugin) approves the files; 5 s notice — `src/Notch.App/Plugins/PluginInstallFlow.cs:65`
- [ ] Installing never runs or enables anything by itself — `src/Notch.Core/Plugins/PluginInstaller.cs:47`

### Registry
- [ ] Default URL `https://brick-bread.github.io/WNotch/plugins/registry.json` — `src/Notch.Core/Plugins/PluginRegistry.cs:39`
- [ ] Format `{ "plugins": [ { id, name, repository, author, description, tags, permissions, apiVersion, verified } ], "revoked": [...] }`, case-insensitive with trailing commas — `src/Notch.Core/Plugins/PluginRegistry.cs:60`
- [ ] `RegistryEntry.Id` — `src/Notch.Core/Plugins/PluginRegistry.cs:9`
- [ ] `RegistryEntry.Name` (clipped to 80) — `src/Notch.Core/Plugins/PluginRegistry.cs:11`
- [ ] `RegistryEntry.Repository` (normalised owner/repo) — `src/Notch.Core/Plugins/PluginRegistry.cs:14`
- [ ] `RegistryEntry.Author` (clipped to 80) — `src/Notch.Core/Plugins/PluginRegistry.cs:16`
- [ ] `RegistryEntry.Description` (clipped to 400) — `src/Notch.Core/Plugins/PluginRegistry.cs:18`
- [ ] `RegistryEntry.Tags` (each ≤ 24 characters, at most 8) — `src/Notch.Core/Plugins/PluginRegistry.cs:20`
- [ ] `RegistryEntry.Permissions` (cleaned like the manifest's) — `src/Notch.Core/Plugins/PluginRegistry.cs:23`
- [ ] `RegistryEntry.ApiVersion` (int?) — `src/Notch.Core/Plugins/PluginRegistry.cs:26`
- [ ] `RegistryEntry.Verified` (bool) — `src/Notch.Core/Plugins/PluginRegistry.cs:29`
- [ ] Limit: 500 entries; skips a bad id, blank name, bad repository or duplicate id — `src/Notch.Core/Plugins/PluginRegistry.cs:79`
- [ ] Limit: response ≤ 2 MB (by Content-Length) — `src/Notch.Core/Plugins/PluginRegistry.cs:117`
- [ ] Cached to `%AppData%\Notch\registry.json`; used offline — `src/Notch.Core/Plugins/PluginRegistry.cs:124`
- [ ] Reloaded at startup (when update notices are on), then EnforceRevocations runs — `src/Notch.App/App.xaml.cs:343`
- [ ] `notch://install` resolves the id through the registry only — `src/Notch.Core/Plugins/PluginRegistry.cs:33`

### Trust store and approval
- [ ] File `%AppData%\Notch\plugin-trust.json`: {id: {Hash, Approved}}, indented, atomic writes; unreadable means nothing is approved — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:88`
- [ ] TreeHash: SHA-256 over sorted (OrdinalIgnoreCase) relative paths, each lowercased with "/" separators, then 0, file bytes, 0; lowercase hex — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:23`
- [ ] IsApproved (exact hash) — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:51`
- [ ] HasRecord — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:60`
- [ ] Approve — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:68`
- [ ] Forget — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:77`
- [ ] Unapproved refusal texts: "Its files changed since you approved it…" and "Not approved yet…" (audit Quarantined) — `src/Notch.Core/Plugins/PluginManager.cs:714`
- [ ] First run with checks (no trust file yet): currently enabled plugins are auto-approved ("carried over…") — `src/Notch.App/App.xaml.cs:177`
- [ ] Held-back plugins: 8 s notice "<name> changed" / "N plugins changed" — `src/Notch.App/App.xaml.cs:328`
- [ ] `PluginManager.Review(id)` → PluginReview — `src/Notch.Core/Plugins/PluginManager.cs:727`
- [ ] `PluginManager.Approve(id, detail)` / `ApproveFiles(id, dir, detail)` — `src/Notch.Core/Plugins/PluginManager.cs:752`
- [ ] `PluginReview` record (Info, Permissions, Scan, Approved, FilesChanged, Revoked) — `src/Notch.Core/Plugins/Checks/PluginChecks.cs:23`
- [ ] `PluginReview.Describe()` text (name, version, author, a files-changed line, permissions or "does not say", up to 6 distinct Warning details) — `src/Notch.Core/Plugins/Checks/PluginChecks.cs:32`
- [ ] `PluginChecks` (Audit, Trust, Revocations) — `src/Notch.Core/Plugins/Checks/PluginChecks.cs:9`
- [ ] Refusal order at start: revocation, then scan block, then trust — `src/Notch.Core/Plugins/PluginManager.cs:690`

### Revocations
- [ ] `Revocation(Id, Sha256?, Reason)`; no sha256 means every version — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:130`
- [ ] Parse the `revoked` array: id 1–64 characters, sha256 lowercased, reason clipped to 200, at most 1000 entries; anything unreadable gives an empty list — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:153`
- [ ] Read from the cached registry file at every start attempt — `src/Notch.App/App.xaml.cs:149`
- [ ] Find: id match and (no hash or hash equals TreeHash) — `src/Notch.Core/Plugins/Checks/PluginTrust.cs:191`
- [ ] Start refusal "Withdrawn by the plugin list[: reason]" (audit Revoked) — `src/Notch.Core/Plugins/PluginManager.cs:698`
- [ ] EnforceRevocations stops running plugins and marks them Failed — `src/Notch.Core/Plugins/PluginManager.cs:791`

### Scanner rules
- [ ] `ScanSeverity` enum: Info — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:9`
- [ ] `ScanSeverity.Warning` — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:12`
- [ ] `ScanSeverity.Block` — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:15`
- [ ] `ScanFinding(Severity, Category, Detail, File)` — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:19`
- [ ] `ScanReport`: Blocked, HasNativeCode, BlockReason ("Blocked: it uses …", up to 3 distinct categories) — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:22`
- [ ] Scans *.dll and *.exe recursively, at most 400 files, reading PE metadata without loading — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:52`
- [ ] P/Invoke import names are matched case-insensitively — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:55`
- [ ] Block keylogging: SetWindowsHookEx, SetWindowsHookExA, SetWindowsHookExW ("keyboard or mouse hooks") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:57`
- [ ] Block keylogging: GetAsyncKeyState, GetKeyboardState ("key state polling") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:60`
- [ ] Block keylogging: RegisterRawInputDevices, GetRawInputData ("raw input capture") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:62`
- [ ] Block screen-capture: BitBlt, StretchBlt ("screen capture") and PrintWindow ("window capture") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:65`
- [ ] Warning screen-capture: D3D11CreateDevice, CreateDXGIFactory, CreateDXGIFactory1 — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:68`
- [ ] Block input-injection: SendInput, keybd_event, mouse_event, SetCursorPos, AttachThreadInput — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:72`
- [ ] Block process-injection: CreateRemoteThread, CreateRemoteThreadEx, WriteProcessMemory, ReadProcessMemory, VirtualAllocEx, NtCreateThreadEx, QueueUserAPC, SetThreadContext, NtWriteVirtualMemory — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:78`
- [ ] Warning process: OpenProcess ("opening other programs") and AdjustTokenPrivileges ("changing privileges") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:88`
- [ ] Warning process: ShellExecute, ShellExecuteA, ShellExecuteW, ShellExecuteEx, ShellExecuteExW, CreateProcess, CreateProcessA, CreateProcessW ("starting programs") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:90`
- [ ] Block managed: System.Windows.Forms.SendKeys (any member) — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:103`
- [ ] Block managed: System.Drawing.Graphics.CopyFromScreen — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:104`
- [ ] Block managed: Windows.Graphics.Capture.GraphicsCaptureSession and Direct3D11CaptureFramePool — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:105`
- [ ] Warning managed: System.Diagnostics.Process.Start — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:107`
- [ ] Warning managed: Assembly.LoadFrom, LoadFile and Load ("dynamic-code") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:108`
- [ ] Warning managed: Reflection.Emit AssemblyBuilder and DynamicMethod — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:111`
- [ ] Warning managed: NativeLibrary (any) and Marshal.GetDelegateForFunctionPointer ("native-code") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:113`
- [ ] Info managed: HttpClient, Socket and ClientWebSocket ("network") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:115`
- [ ] Info: P/Invoke into a non-common library ("calls into <lib>"); common = kernel32, advapi32, ntdll, ole32, shell32, user32, gdi32, crypt32, api-ms-win*, libc — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:182`
- [ ] Warning: PE without metadata ("native code that cannot be inspected") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:150`
- [ ] Warning: unreadable or bad-image file ("a file that could not be inspected") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:158`
- [ ] Warning: folder listing failed ("unreadable") — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:138`
- [ ] Managed matches only consider MemberReferences whose parent is a TypeReference — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:191`
- [ ] Findings deduplicated by (Severity, Category, Detail, File) — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:163`
- [ ] **[changed: plugins do run in a separate process now; reword the comment and docs to match what the host does and does not restrict]** Code comment claims plugins "also run in a process that cannot do these things", but the current host is in-process — `src/Notch.Core/Plugins/Checks/PluginScanner.cs:47`

### Audit log
- [ ] File `%LocalAppData%\Notch\plugin-audit.log`, one JSON object per line {time (UTC, ISO "O"), plugin, kind, detail ≤ 500} — `src/Notch.Core/Plugins/Checks/PluginAudit.cs:35`
- [ ] Limit: rotate to `.1` above 1 MB; never throws — `src/Notch.Core/Plugins/Checks/PluginAudit.cs:26`
- [ ] `IPluginAudit.Record` and `NullPluginAudit` — `src/Notch.Core/Plugins/Checks/PluginAudit.cs:6`
- [ ] Kinds: started, stopped, failed, denied, blocked, killed, quarantined, approved, revoked, violation — `src/Notch.Core/Plugins/Checks/PluginAudit.cs:66`
- [ ] Kinds the manager records: started ("no findings" or "N findings"), blocked, quarantined ("files changed" or "not approved"), approved (with detail), revoked, killed; stopped, failed, denied and violation are defined but never recorded — `src/Notch.Core/Plugins/PluginManager.cs:701`

## 3. User-visible features

### Startup, single instance & command-line switches
- [ ] Only one copy runs per user (named mutex; debug builds use their own name so they can run next to an installed copy) — `src/Notch.App/App.xaml.cs:83`
- [ ] A second launch passes its arguments (a notch:// link, `--settings`, or nothing) to the running copy over the command pipe, then exits — `src/Notch.App/App.xaml.cs:90`
- [ ] A forwarded launch with no arguments, or only startup switches, opens the notch (or opens Settings for `--settings`). Launches made by the installer or a restart (`--updated`, `--wait-for=`) stay quiet — `src/Notch.App/Automation/CommandDispatcher.cs:28`
- [ ] `--wait-for=<pid>` waits up to 20 s for the previous copy to exit before starting (used by restart) — `src/Notch.App/App.xaml.cs:638`
- [ ] Unhandled UI exceptions are written to `%LocalAppData%\Notch\errors.log` and the app keeps running — `src/Notch.App/App.xaml.cs:76`
- [ ] `--demo` swaps in a fake media player, a fake shelf and a looping demo script, and skips agent tracking and notifications — `src/Notch.App/App.xaml.cs:100`
- [ ] `--trace-hover` turns on hover/expand trace logging — `src/Notch.App/App.xaml.cs:101`
- [ ] **[changed: maps to WebView2 with GPU acceleration off]** `--software-render` forces software rendering — `src/Notch.App/App.xaml.cs:107`
- [ ] On its first run, a debug build copies the installed copy's settings into its own data folder (`--shared-data` uses the installed folders instead) — `src/Notch.App/App.xaml.cs:113`
- [ ] Settings live in `settings.json` under Roaming. They are written atomically (tmp file, then move), and a corrupt or missing file falls back to defaults — `src/Notch.Core/Settings/AppSettings.cs:225`
- [ ] `--plugin=<folder>` loads a plugin straight from a dev folder; it is always enabled for that run — `src/Notch.App/App.xaml.cs:174`
- [ ] `--no-plugins` safe mode. Safe mode also starts automatically when the "plugins-starting" marker is left over from a crash, and shows the transient notice "Plugins are off" (orange flash, 8 s) — `src/Notch.App/App.xaml.cs:182`
- [ ] `--plugin-theme=<plugin>/<theme>` applies a plugin theme for this run only — `src/Notch.App/App.xaml.cs:204`
- [ ] `--theme=dark|light|system` overrides the theme for this run only — `src/Notch.App/App.xaml.cs:207`
- [ ] `--display=N` (1-based) overrides the display for this run only — `src/Notch.App/App.xaml.cs:235`
- [ ] `--style=notch|island` and `--position=topcenter|taskbarleft` override the look for this run only — `src/Notch.App/App.xaml.cs:241`
- [ ] When global agent hooks are on, the hook paths are refreshed at startup to point at this copy's Notch.Hook.exe — `src/Notch.App/App.xaml.cs:251`
- [ ] **[changed: reloads the Svelte UI and theme CSS instead of XAML]** `--dev-reload` (or the `NOTCH_DEV_RELOAD` env var) with `--reload-from=<src>` live-reloads Design/Styles/Dark/Light.xaml. If the source is missing it shows the notice "Live reload is off" — `src/Notch.App/Shell/LiveXamlReload.cs:37`
- [ ] `--pin-open` keeps the notch expanded no matter where the pointer is — `src/Notch.App/App.xaml.cs:268`
- [ ] `--settings` opens the Settings window at startup — `src/Notch.App/App.xaml.cs:273`
- [ ] `--tab=<name>` selects a tab at startup — `src/Notch.App/App.xaml.cs:278`
- [ ] `--open=<preset id>` starts a terminal session from a preset at startup — `src/Notch.App/App.xaml.cs:284`
- [ ] Plugins load off the UI thread after the window shows, inside a crash marker (`plugins-starting`) — `src/Notch.App/App.xaml.cs:310`
- [ ] Notice "<name> changed" / "N plugins changed" for plugins held back because their files changed ("Review it in Settings > Plugins…") — `src/Notch.App/App.xaml.cs:328`
- [ ] When update notices are on, the plugin registry is fetched at startup and revoked plugins are stopped — `src/Notch.App/App.xaml.cs:343`
- [ ] A notch:// link given on the command line is handled once the window is up — `src/Notch.App/App.xaml.cs:422`
- [ ] Restart (used by plugin updates) relaunches with the same arguments, drops `--updated` and old `--wait-for`, and adds `--wait-for=<pid>` — `src/Notch.App/App.xaml.cs:606`
- [ ] Pending plugin updates are applied before any plugin loads — `src/Notch.App/App.xaml.cs:166`

### Window & shell behaviours
- [ ] Borderless, transparent, no-resize, hidden from the taskbar and Alt+Tab (WS_EX_TOOLWINDOW), never activates on show (WS_EX_NOACTIVATE), topmost — `src/Notch.App/Shell/NotchWindow.xaml:6`
- [ ] Applies the overlay styles and re-asserts HWND_TOPMOST every second (housekeeping) and whenever the foreground window changes — `src/Notch.Platform/Display/OverlayWindow.cs:9`
- [ ] Transparent pixels pass clicks through; only the island and edge bridge are hit-testable — `src/Notch.App/Shell/NotchWindow.xaml:12`
- [ ] The window is fixed at 980 wide. It is 96 high while idle and grows to 700 just before expanding, so redraws stay cheap — `src/Notch.App/Shell/NotchWindow.xaml.cs:48`
- [ ] The window shrinks back once the island has settled, is closed, and the pointer is off it (also re-checked by housekeeping) — `src/Notch.App/Shell/NotchWindow.xaml.cs:518`
- [ ] At the bottom (taskbar) position the window always stays large, so the frame never jumps — `src/Notch.App/Shell/NotchWindow.xaml.cs:415`
- [ ] NotchStyle.Notch: attached to the screen edge, flush corners (clip extends one radius past the edge), with "shoulder" curves on both sides at the top — `src/Notch.App/Shell/NotchWindow.xaml.cs:885`
- [x] NotchStyle.Island: floating pill rounded on all sides; 8 DIP gap from the top, or centred vertically in the taskbar (at least 4 DIP) — `src/Notch.Core/Shell/IslandPlacement.cs:33`
- [ ] Edge bridge: a strip that is almost invisible (#01 alpha) between a floating island and the screen edge. It counts as the island for hover, click and drop — `src/Notch.App/Shell/NotchWindow.xaml.cs:143`
- [ ] Position TopCenter: centred on the top edge of the display; grows downward and to both sides — `src/Notch.App/Shell/NotchWindow.xaml.cs:423`
- [ ] Position TaskbarLeft: bottom-left corner with a 12 DIP side inset; grows up and to the right; content anchors bottom-left (margins 18,0,0,18) — `src/Notch.App/Shell/NotchWindow.xaml.cs:436`
- [ ] Taskbar height comes from bounds minus work area, defaulting to 48 when the taskbar auto-hides — `src/Notch.App/Shell/NotchWindow.xaml.cs:399`
- [ ] Display choice: by index in the system display list, falling back to primary when that display is unplugged — `src/Notch.App/Shell/NotchWindow.xaml.cs:392`
- [ ] Positions in physical pixels using the display's scale (per-monitor DPI) — `src/Notch.App/Shell/NotchWindow.xaml.cs:416`
- [ ] Repositions on WM_DISPLAYCHANGE and WM_DPICHANGED (after WPF's own DPI handling) — `src/Notch.App/Shell/NotchWindow.xaml.cs:306`
- [ ] On WM_SETTINGCHANGE: re-applies the theme when it is set to System, and repositions in the taskbar position (taskbar size may have changed) — `src/Notch.App/Shell/NotchWindow.xaml.cs:311`
- [ ] Idle shape: 180 wide; 10 high at the top, 32 in the taskbar; radius height/2 — `src/Notch.App/Shell/NotchWindow.xaml.cs:207`
- [ ] Compact (pill) shape: 26 high at the top, 32 in the taskbar. Width is 360 by default, 380 for media, 372 for an agent that needs input, 292 for other agent states, or measured for the glance row — `src/Notch.App/Shell/NotchWindow.Pill.cs:27`
- [ ] Peek (transient HUD) shape: 380 wide; 38 high at the top, 32 in the taskbar — `src/Notch.App/Shell/NotchWindow.xaml.cs:196`
- [ ] Expanded sizes: Terminal 920×540, Stats 640×290, Shelf 640×(100+rows×104, 1–3 rows), Plugins 640×(76+rows×107, 1–3 rows), Clipboard 640×430, Palette 640×392, Page large 920×540 or compact 640×fit, Home 640×content height. All have radius 32 — `src/Notch.App/Shell/NotchWindow.xaml.cs:193`
- [ ] Home height = measured content (+14+tabs+18), capped at 660 or display height − gap − 40. Past the cap it scrolls — `src/Notch.App/Shell/NotchWindow.Home.cs:77`
- [ ] A theme can scale the corner radius (`IslandRadiusScale`, clamped 0–2) — `src/Notch.App/Shell/ThemeManager.cs:222`
- [x] Mode resolution: expanded → Expanded; no activities → Idle; top activity transient → Peek; otherwise Compact — `src/Notch.Core/Shell/NotchMode.cs:24`
- [ ] Spring animation of width/height (stiffness 380, damping 26, visible overshoot) and radius (300/30). Steps per rendered frame with sub-stepping (max 1/240 s) — `src/Notch.App/Shell/NotchAnimator.cs:11`
- [x] The spring snaps to target when it is within 0.05 and velocity is below 0.5 — `src/Notch.Core/Animation/Spring.cs:28`
- [ ] Height never drops below the resting height while shrinking (no undershoot) — `src/Notch.App/Shell/NotchWindow.xaml.cs:845`
- [ ] Content fade: fades out within 90 ms. Fades in after the island has mostly resized (130 ms pill, 170 ms expanded, 220 ms cubic-ease fade) and scales up from 0.96 — `src/Notch.App/Shell/NotchWindow.xaml.cs:815`
- [ ] A new activity taking the pill fades the pill content out and in again. Changes within the glance row do not — `src/Notch.App/Shell/NotchWindow.xaml.cs:793`
- [ ] Expanded content keeps a fixed size per tab (shape − 36 × shape − 32); the island grows around it — `src/Notch.App/Shell/NotchWindow.xaml.cs:776`
- [ ] Hover expand: 120 ms open delay (only when "expand on hover" is on, or to cancel a pending close) — `src/Notch.App/Shell/NotchWindow.xaml.cs:593`
- [ ] Pointer leaving: closes after 350 ms. The pointer's real screen position decides (2 DIP margin, gap to the edge included), not WPF leave events — `src/Notch.App/Shell/NotchWindow.xaml.cs:604`
- [ ] While expanded, the pointer is polled every 100 ms. It closes when the pointer has really left and cancels a pending close when it comes back — `src/Notch.App/Shell/NotchWindow.xaml.cs:555`
- [ ] Clicking the island opens it (even with hover-open off) — `src/Notch.App/Shell/NotchWindow.xaml.cs:616`
- [ ] Clicking the pill while an activity with `OpensPage` is showing opens that plugin page. A click within 600 ms of hover-opening still counts as a click on the pill. In a glance row, the item clicked (or the nearest one) decides — `src/Notch.App/Shell/NotchWindow.xaml.cs:621`
- [ ] Click-away close: a mouse press outside the island closes the notch when it was opened by hotkey, has the keyboard, or has a Home sheet open — `src/Notch.App/Shell/NotchWindow.xaml.cs:564`
- [ ] Deactivated (the user clicked another window) closes the notch unless a menu or drag of its own is in use — `src/Notch.App/Shell/NotchWindow.Terminal.cs:95`
- [ ] Held open regardless of the pointer while: dragging a file out, a shelf tile menu is open, the terminal folder menu is open, a hotkey opened it, a Home widget is being dragged, or a Home sheet is open — `src/Notch.App/Shell/NotchWindow.xaml.cs:642`
- [ ] When the notch closes while it has the keyboard, focus goes back to the previous foreground window — `src/Notch.App/Shell/NotchWindow.xaml.cs:161`
- [ ] The last foreground window that is not the notch is tracked every second, so focus can be handed back — `src/Notch.App/Shell/NotchWindow.Widgets.cs:237`
- [ ] Keyboard focus model: the window refuses activation except while it is expanded and one of these is true: Terminal tab, timer entry, Home typing, a page with input, Clipboard tab, or Palette tab — `src/Notch.App/Shell/NotchWindow.Terminal.cs:171`
- [ ] The notch never closes under the user while its keyboard input is in use — `src/Notch.App/Shell/NotchWindow.Terminal.cs:35`
- [ ] Closing the notch resets: hotkey hold, page-view keyboard, timer entry, Home edit mode and Home keyboard — `src/Notch.App/Shell/NotchWindow.xaml.cs:719`
- [ ] On open: refreshes the calendar display and prunes missing shelf files — `src/Notch.App/Shell/NotchWindow.xaml.cs:710`
- [ ] Fullscreen hide (setting): the window is hidden entirely while a fullscreen app covers this display. A fullscreen app counts when it is the foreground window, or when it is the uppermost app window with only topmost windows above it — `src/Notch.Platform/Display/FullscreenDetector.cs:19`
- [ ] Fullscreen detection ignores hidden, minimised, cloaked, tool, no-activate and transparent windows, windows with a caption (maximised), and the shell's own windows (Progman/WorkerW/Shell_TrayWnd/XamlExplorerHostIslandWindow) — `src/Notch.Platform/Display/FullscreenDetector.cs:55`
- [ ] Fullscreen is checked on every foreground change and again 400 ms later, plus every second. It is not checked while typing into the notch or dragging out of it — `src/Notch.App/Shell/NotchWindow.xaml.cs:240`
- [ ] When hiding for fullscreen it collapses first. Coming back it reappears without taking focus. The hotkey and palette do nothing while it is hidden — `src/Notch.App/Shell/NotchWindow.xaml.cs:494`
- [ ] The theme refresh recolours: terminal theme, widget colours, plugin cards, Home views, page figures — `src/Notch.App/Shell/NotchWindow.xaml.cs:329`
- [ ] Applying settings live after Save: theme, reposition, both hotkeys, clipboard, housekeeping, calendar, timer presets, terminal launchers, Home views, glow gain — `src/Notch.App/Shell/NotchWindow.xaml.cs:350`
- [ ] Shell state is published to plugins: expanded, current page, Plugins tab visible, dark/light, accent, which Home plugin cards are showing — `src/Notch.App/Shell/NotchWindow.xaml.cs:735`

### Glow
- [ ] Glow layers (halo drop shadow blur 18 + 14×opacity, core blur 9, rim) follow the island outline, spread 2.5×gain past it. At the edge they are only on the sides away from the screen edge — `src/Notch.App/Shell/GlowController.cs:18`
- [ ] Glow only shows in Compact/Peek and only when glow effects are on. There is no glow while expanded or for the glance row — `src/Notch.App/Shell/NotchWindow.xaml.cs:783`
- [x] Patterns: Steady (0.8), Breathe (3.2 s sine²), Pulse (1.1 s), Flash (burst decaying over 1.2 s to 0.35), Audio (follows output loudness). Each is multiplied by strength — `src/Notch.Core/Activities/Glow.cs:86`
- [ ] Brightness gain is 25–200 % with a 0.6 lift curve. Above full opacity the extra brightness widens the halo (√gain reach) — `src/Notch.Core/Activities/Glow.cs:113`
- [ ] Intensity eases (×14/s) and colour blends (×6/s) between glows. A new pattern starts from its beginning — `src/Notch.App/Shell/GlowController.cs:208`
- [ ] Frames are capped at about 24 fps (40 ms). Rendering stops when the glow is dark — `src/Notch.App/Shell/GlowController.cs:30`
- [ ] Audio pattern: √peak from the output meter, fast attack (0.6) and slow release (0.08^dt) — `src/Notch.App/Shell/GlowController.cs:195`
- [x] Media glow colour is taken from the album art (weighted saturated pixels, brightened); null for greyscale art — `src/Notch.Core/Activities/Glow.cs:139`
- [ ] Plugin glow frames replace the activity glow, including on the plain pill. Segmented frames draw a blurred left-to-right gradient (pad 9×√gain, blur 9×1.6×reach) and hide the halo — `src/Notch.App/Shell/GlowController.cs:184`
- [ ] Accent colour presets: Blue, Cyan, Green, Yellow, Amber, Orange, Red, Violet (plus White for glows). Colours are deepened (30 % toward black) in the light theme — `src/Notch.Core/Activities/Glow.cs:16`

### Pill: activities, tiers & layout
- [ ] Activity model: id, tier, title, detail, glyph (Segoe Fluent), agentId, agentState, image, progress, glow, lifetime, opensPage, countingFrom, order — `src/Notch.Core/Activities/Activity.cs:23`
- [ ] Tiers: Glance (−1, shown side by side), Ongoing (0), Attention (1), Transient (2, expires itself, default 2 s) — `src/Notch.Core/Activities/Activity.cs:4`
- [x] Ordering: highest tier first, then the most recently published. Republishing the same id keeps its place in line — `src/Notch.Core/Activities/ActivityManager.cs:60`
- [x] Suppressed ids (the user's "show in pill" settings) are dropped when published, and removed if already showing — `src/Notch.Core/Activities/ActivityManager.cs:27`
- [ ] Layout: left icon (album art, else agent logo, else glyph tinted in the glow colour); middle marquee "Title" + secondary-coloured subtitle; right state area — `src/Notch.App/Shell/NotchWindow.Pill.cs:147`
- [ ] Marquee: text that does not fit holds 1.4 s, scrolls at 30 DIP/s, holds, scrolls back, forever, with 10 DIP edge fades — `src/Notch.App/Shell/Marquee.cs:15`
- [ ] Detail on the right (max 130 wide, ellipsis). For a `CountingFrom` activity it shows an elapsed clock ticking every second — `src/Notch.App/Shell/NotchWindow.Plugins.cs:71`
- [ ] Transient with progress (a level HUD): glyph + 84 px bar + numeric level 0–100; title hidden — `src/Notch.App/Shell/NotchWindow.Pill.cs:185`
- [ ] Glance row: up to 6 glance activities in order then id. Each shows a picture (16 px) or glyph (glow-tinted) and its title or a ticking clock (max 150 wide, tooltip = title when it is a clock). Width = content + 40, clamped 120–520 — `src/Notch.App/Shell/NotchWindow.Pill.cs:61`
- [ ] Media in pill: art or music glyph; title + " · artist"; animated 4-bar wave (0.45 s, staggered), bars tinted with the art accent — `src/Notch.App/Shell/NotchWindow.Pill.cs:187`
- [x] Media activity is Ongoing while playing, with a violet Audio glow (0.85) that the art accent replaces; it is removed when paused or stopped — `src/Notch.Core/Media/MediaActivityPublisher.cs:27`
- [ ] Agent in pill: with a logo, the subtitle shows only the folder. Working = three blinking dots (0.6 s, staggered 0.2 s); NeedsInput = "Needs you" in the attention colour; Done = check glyph + "Done" in green. A "+N" suffix counts other sessions — `src/Notch.App/Shell/NotchWindow.Pill.cs:193`
- [x] Agent activities: Working = Ongoing, cyan breathe 0.8; NeedsInput = Attention, amber pulse; Done = Attention, green steady 0.8; Idle = none — `src/Notch.Core/Agents/AgentActivities.cs:20`
- [x] Every board entry is republished with a count of the other non-idle sessions — `src/Notch.Core/Agents/AgentBoard.cs:149`
- [ ] Agent logos (claude, codex, …) are geometry resources — `src/Notch.App/Themes/AgentLogos.xaml:1`

### Pill: HUDs & notices
- [ ] Volume HUD: "Volume" or "Muted", progress, glyph by level (mute/low/mid/high), white flash scaled by level. Comes from the default render device's volume notifications — `src/Notch.Core/Hud/HudActivities.cs:17`
- [ ] Audio output HUD: the new default device's name with "Output" when the default output changes (3.5 s) — `src/Notch.Platform/Hud/VolumeWatcher.cs:38`
- [ ] Brightness HUD: "Brightness" + progress, yellow flash scaled by level. Built-in panel only (WMI WmiMonitorBrightnessEvent) — `src/Notch.Platform/Hud/BrightnessWatcher.cs:44`
- [x] Power HUD: "Charging" (green flash) or "On battery" (white 0.6) with "%", 3.5 s, shown on plug/unplug. The first reading only sets a baseline — `src/Notch.Core/Hud/PowerHudTracker.cs:33`
- [x] Low battery HUD: "Low battery" with "%", red pulse, 6 s, when crossing 20/10/5 % while unplugged — `src/Notch.Core/Hud/PowerHudTracker.cs:38`
- [ ] Bluetooth HUD: device name, "Connected"/"Disconnected", blue flash, 3.5 s. Covers paired Classic and LE devices; the starting state is only a baseline — `src/Notch.Platform/Hud/BluetoothWatcher.cs:66`
- [ ] Caps Lock HUD: "Caps Lock" On/Off, 2 s. Caps Lock is polled every 150 ms (no keyboard hook) — `src/Notch.App/Shell/NotchWindow.Keyboard.cs:47`
- [ ] Missing hardware (no battery, no dimmable panel, no BT radio) is skipped quietly — `src/Notch.Platform/Hud/SystemHudSources.cs:44`
- [x] Notification mirror: Windows toast → transient "sender/subject" + app name, blue flash 0.8, 5 s. The body is never shown. Title max 80, app max 40, one line — `src/Notch.Core/Shell/NotificationMirror.cs:31`
- [ ] Notifications are polled every 2 s. The first poll only primes (no replay of the action centre). Dismissed ids are forgotten — `src/Notch.Platform/Notifications/NotificationWatcher.cs:62`
- [ ] App filter: show all when the list is empty, otherwise apps whose name contains a listed word (case-insensitive) — `src/Notch.Core/Shell/NotificationMirror.cs:23`
- [ ] Windows notification access is requested when the feature is switched on. If refused, a message box explains it (only when the user just saved) — `src/Notch.Platform/Notifications/NotificationWatcher.cs:29`
- [ ] Timer in pill: Ongoing "<title>" + remaining time with a breathing glow in the timer colour (focus 0.55, others 0.5); "<title> paused" without glow; on finish Attention "Done" with a pulse glow and a chime — `src/Notch.App/Shell/NotchWindow.Widgets.cs:388`
- [ ] Pomodoro phase notice: "Back to focus" (red) or "Break time" (green), 4 s, with an Asterisk chime — `src/Notch.App/Shell/NotchWindow.Widgets.cs:282`
- [x] Shelf drop notice "On the shelf · N items" when files are dropped while the notch is closed — `src/Notch.Core/Shelf/ShelfActivities.cs:10`
- [ ] Clipboard "Copied again" notice (1.5 s) — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:245`
- [ ] "Clipboard history is off · Switch it on in Settings" notice when the Clipboard tab is requested while history is off — `src/Notch.App/Shell/NotchWindow.Terminal.cs:42`
- [ ] "Could not find that window" notice when jumping to a detected agent fails — `src/Notch.App/Shell/NotchWindow.Agents.cs:77`
- [ ] Launcher/command notices: "Could not open X" (red), "X finished" (green), "X failed · Exit code N" (red), "Could not run X" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:1003`
- [ ] Update notices: "Notch vX is ready · Install it from Settings" (blue, 10 s), "Notch updated · version" (green, 6 s), "The update did not install" (Attention, red pulse, removed after 15 s) — `src/Notch.App/Updates/UpdateService.cs:80`
- [ ] Plugin notices: "Plugins stopped" (5 s), "<plugin> installed [and switched on]" (green, 5 s) — `src/Notch.App/App.xaml.cs:472`
- [ ] Generic toast via command/webhook: title/detail/glyph/colour flash/lifetime (default 4 s, max 30 s). A new one replaces the last — `src/Notch.App/Shell/NotchWindow.Automation.cs:44`

### Tab strip & expanded chrome
- [ ] Tab strip of icon radio buttons: Home, Terminal, Stats, Shelf, Clipboard (only while history is on), Plugins (only while a plugin card exists), then one text tab per plugin page — `src/Notch.App/Shell/NotchWindow.xaml:111`
- [ ] Shelf tab tooltip shows the item count ("Shelf, N items") — `src/Notch.App/Shell/NotchWindow.Shelf.cs:100`
- [ ] Pencil "Edit Home" / "Done" button, shown only on Home — `src/Notch.App/Shell/NotchWindow.Home.cs:264`
- [ ] Settings gear button opens the Settings window — `src/Notch.App/Shell/NotchWindow.Terminal.cs:87`
- [ ] Leaving Home closes the timer entry and Home edit mode — `src/Notch.App/Shell/NotchWindow.Terminal.cs:135`
- [ ] Switching to Terminal refreshes the skills library and focuses the terminal; switching to Clipboard activates the window and focuses search; switching to a Page shows it and focuses its input — `src/Notch.App/Shell/NotchWindow.Terminal.cs:146`
- [ ] If the Plugins tab is open when the last card goes away, it falls back to Home — `src/Notch.App/Shell/NotchWindow.Plugins.cs:53`

### Home tab: layout & editing
- [x] Widgets laid out in rows: a full-width widget alone; two consecutive half-width widgets share a row (14 px gap); a lone half-width widget sits left. Hidden widgets take no room — `src/Notch.Core/Widgets/HomeLayout.cs:227`
- [x] Default layout: Media (full), Agents (full), Timer (half) + Calendar (half) — `src/Notch.Core/Widgets/HomeLayout.cs:214`
- [x] Normalize on load: drop unknown kinds, plugin widgets without a source, and duplicates of single widgets; fix ids; max 24 widgets — `src/Notch.Core/Widgets/HomeLayout.cs:266`
- [ ] Empty Home message: "Nothing on Home. Click the pencil to add widgets." — `src/Notch.App/Shell/NotchWindow.xaml:139`
- [ ] "Hide when there is nothing to show" (default on) for widgets that can be empty (Media, Agents, Shelf, Clipboard, Plugin card); otherwise an empty-state card with text is shown — `src/Notch.App/Shell/NotchWindow.Home.cs:161`
- [ ] Edit mode: every widget is covered by chrome (88 % backdrop, outline, glyph, name, and a "Hidden while empty" note) — `src/Notch.App/Shell/NotchWindow.Home.cs:770`
- [ ] Edit chrome button: toggle width ("Make half width" / "Make full width") — `src/Notch.App/Shell/NotchWindow.Home.cs:788`
- [ ] Edit chrome button: Settings gear (only when the type has options or can be empty) opens the widget form — `src/Notch.App/Shell/NotchWindow.Home.cs:789`
- [ ] Edit chrome button: "Remove from Home" — `src/Notch.App/Shell/NotchWindow.Home.cs:791`
- [ ] Drag to reorder in edit mode (past the system drag threshold, 50 % opacity): drops before or after the target by half (left/right if side by side, top/bottom otherwise). Saved on release — `src/Notch.App/Shell/NotchWindow.Home.cs:326`
- [ ] Edit bar: "+ Add widget", "Reset" (needs a second click within 4 s, reading "Click again to reset"), hint "Drag a widget to move it" — `src/Notch.App/Shell/NotchWindow.Home.cs:267`
- [ ] "Add a widget" picker sheet: 3-column tiles (glyph, name, description, hover highlight). Lists built-in types minus singles already present, then plugin widget types sorted by name. Empty at 24 widgets — `src/Notch.App/Shell/NotchWindow.Home.cs:495`
- [ ] Adding a type that needs setup (Weather, Launcher, Commands, Feed, Plugin card, or a plugin widget with options) opens its form at once; Cancel removes the new widget again — `src/Notch.App/Shell/NotchWindow.Home.cs:544`
- [ ] Widget form sheet: title "Add x" / "X settings", Save/Cancel; "Close" for detail views — `src/Notch.App/Shell/NotchWindow.Home.cs:426`
- [ ] Form editors: Text (Enter saves, Esc cancels), Lines (multi-line, 78 high), Toggle (On/Off pill button), Choice (pill button group, or a list of plugin cards). Field hints sit under the editors — `src/Notch.App/Shell/NotchWindow.Home.cs:619`
- [ ] A form with no options says "<Type> has nothing to set up here." The first text box gets focus and the notch takes the keyboard — `src/Notch.App/Shell/NotchWindow.Home.cs:595`
- [ ] Home widget changes save to settings straight away — `src/Notch.App/Shell/NotchWindow.Home.cs:261`
- [ ] Home ticks every second only while Home is visible (clocks, weather, feeds). Plugins are told whether their widgets are visible — `src/Notch.App/Shell/NotchWindow.Home.cs:203`
- [ ] Home's scroller spills 66 px past its bounds so the media colour wash is not cut off, except while it scrolls — `src/Notch.App/Shell/NotchWindow.Home.cs:94`

### Home widget: Media
- [ ] Catalog entry: "What is playing, with its controls", full, single, can be empty ("Nothing playing") — `src/Notch.Core/Widgets/HomeLayout.cs:132`
- [ ] 96×96 art (placeholder music glyph) plus a blurred, radially masked colour wash of the art behind the card (40 % opacity, blur 40) — `src/Notch.App/Shell/NotchWindow.xaml:177`
- [ ] Title in a marquee ("Unknown title" fallback), artist with ellipsis — `src/Notch.App/Shell/NotchWindow.Media.cs:58`
- [ ] Timeline: elapsed, progress bar, "-remaining" (h:mm:ss over an hour), ticking every 500 ms only while expanded and playing; hidden without a timeline — `src/Notch.App/Shell/NotchWindow.Media.cs:77`
- [ ] Click-to-seek on a 14 px hit area when the source can seek (hand cursor) — `src/Notch.App/Shell/NotchWindow.Media.cs:105`
- [ ] Previous / Play-Pause (glyph shows state) / Next, each enabled per the session's capabilities — `src/Notch.App/Shell/NotchWindow.Media.cs:23`
- [ ] Follows the system's current media session (GSMTC, the same one the Windows flyout shows); thumbnail max 8 MB — `src/Notch.Platform/Media/GsmtcMediaService.cs:9`
- [ ] Publishes `notch.media` to plugins on track or play-state change, and "stopped" when nothing plays — `src/Notch.App/Shell/NotchWindow.Media.cs:39`

### Home widget: Agents
- [ ] Catalog entry: "Coding agents and what they are doing", full, single, can be empty ("No coding agents running") — `src/Notch.Core/Widgets/HomeLayout.cs:133`
- [ ] Up to 4 rows sorted NeedsInput, Done, Working, then others. Each row: logo or glyph, "Name · folder", where it runs ("Notch terminal" or the host app), and state text in its colour — `src/Notch.App/Shell/NotchWindow.Agents.cs:25`
- [x] State text: Working / Needs input / Done / Idle (Notch sessions) / Open (detected sessions with no hook state) — `src/Notch.Core/Agents/AgentBoard.cs:33`
- [ ] Clicking a row opens a Notch session's terminal tab, or brings a detected agent's window to the front. Tooltips: "Show this session" / "Bring its window to the front" — `src/Notch.App/Shell/NotchWindow.Agents.cs:61`

### Home widget: Timer / Pomodoro
- [ ] Catalog entry: "Pomodoro and one-click timers", half, single, min height 150 — `src/Notch.Core/Widgets/HomeLayout.cs:134`
- [ ] Large digits ("0:00" when idle, "Done" when finished; m:ss or h:mm:ss, rounded up) coloured red for focus, green for breaks, orange for a plain timer — `src/Notch.App/Shell/NotchWindow.Widgets.cs:160`
- [x] Label shows "Timer", the preset name, or the Pomodoro phase ("Focus 2/4", "Short break", "Long break") — `src/Notch.Core/Widgets/PomodoroCycle.cs:36`
- [ ] Idle row: "Pomodoro" button plus a button per preset (label = name or length; tooltip = length when it has a name). Wraps to a second row — `src/Notch.App/Shell/NotchWindow.Widgets.cs:168`
- [ ] "+ Custom" opens an entry box (label becomes the hint "Timer: 12, 1:30, 90s, 1h20m"). Start / Cancel buttons; Enter starts, Esc cancels; a red border marks input it cannot read. The notch takes the keyboard while it is open and gives it back after — `src/Notch.App/Shell/NotchWindow.Widgets.cs:202`
- [ ] Duration parsing: a bare number is minutes (decimals allowed); h:mm:ss or m:ss; units h/m/s and their words; 1 s to 24 h — `src/Notch.Core/Widgets/DurationParser.cs:18`
- [ ] Running row: Pause/Resume and Cancel. When finished: only "Dismiss" — `src/Notch.App/Shell/NotchWindow.Widgets.cs:384`
- [x] Pomodoro cycles focus → short break, with a long break every 4 focuses. Moves on automatically with a chime and a notice — `src/Notch.Core/Widgets/PomodoroCycle.cs:44`
- [ ] Publishes `notch.timer` events (started/paused/resumed/cancelled/finished; kind; seconds; name) to plugins — `src/Notch.App/Shell/NotchWindow.Widgets.cs:300`

### Home widget: Calendar
- [ ] Catalog entry: "Upcoming events from your feeds", half, single, min height 150 — `src/Notch.Core/Widgets/HomeLayout.cs:135`
- [ ] Header shows today's date ("dddd, d MMMM"). Up to 4 upcoming events (not yet ended): a coloured dot per feed (7-colour cycle), when ("Today"/time, or day + time), and title with ellipsis and tooltip — `src/Notch.App/Shell/NotchWindow.Widgets.cs:439`
- [ ] Empty texts: "Add a calendar feed in Settings…", "Your calendar feed could not be loaded.", "Nothing in the next 7 days." — `src/Notch.App/Shell/NotchWindow.Widgets.cs:451`
- [ ] ICS feeds refreshed every 15 minutes and after Save. webcal:// is treated as https. 7-day horizon with recurrences expanded; all-day and floating times are handled; failed feeds keep the previous events — `src/Notch.Core/Widgets/CalendarService.cs:20`

### Home widget: Clock
- [x] Options: Label, Time zone (empty = local; matches UTC/GMT, a zone id, standard name, display name or city), 24-hour toggle, Show seconds toggle — `src/Notch.Core/Widgets/HomeLayout.cs:136`
- [ ] Large time; date line with " · tomorrow"/" · yesterday" when the zone's date differs; "Time zone not found; showing this computer's" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:113`

### Home widget: System stats
- [x] Toggles: CPU (on), Memory (on), GPU, Network, Battery — `src/Notch.Core/Widgets/HomeLayout.cs:143`
- [ ] A line per metric in its colour. With 2 or fewer metrics, CPU/Memory/GPU get sparklines. With none chosen: "Pick what to show with the gear in edit mode." — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:152`
- [ ] Sampled every second while it is visible on Home — `src/Notch.App/Shell/NotchWindow.Widgets.cs:470`

### Home widget: Shelf
- [ ] "Shelf · N items" header, "Open shelf" button (switches to the Shelf tab), up to 6 tiles. Tiles behave like the Shelf tab: double-click opens, drag out, right-click menu — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:212`

### Home widget: Clipboard
- [ ] The last 3 copied items (preview + source · age). Click copies again. Empty texts: "Nothing copied yet" or "Clipboard history is off in Settings" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:288`

### Home widget: Weather
- [x] Options: City, Units (Celsius/Fahrenheit) — `src/Notch.Core/Widgets/HomeLayout.cs:153`
- [ ] Card: place name, temperature, sun/moon/cloud glyph, description, "H x° L y° · wind N km/h|mph". Without a city: "Set a city with the gear in edit mode" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:395`
- [ ] Open-Meteo geocoding (cached per city) and forecast; refreshes every 30 min; on error shows "Could not reach Open-Meteo / Trying again in a few minutes" and retries after 2 min — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:407`
- [ ] Clicking the card opens the "Weather in X" detail sheet: now (big temperature, description, feels-like, H/L), six tiles (Humidity, Wind + compass, Rain %, UV, Sunrise, Sunset), the next 12 hours (time, glyph with tooltip, temperature, rain %), and the next 7 days (name, glyph, words, rain %, low, high) — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:447`

### Home widget: Note
- [ ] Option: Title (default "Note"). A free-text box (multi-line, wraps). Clicking into it takes the keyboard; Esc leaves it. Autosaves 800 ms after typing stops and when focus leaves — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:574`

### Home widget: To-do list
- [ ] Option: Title (default "To do"). An entry box ("Add a task, then Enter", max 300 characters, single line); max 100 items — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:654`
- [ ] Rows: a tick button (mark done/not done), text (struck through and faded when done), and a delete × that shows on hover. Open items come first, ticked ones below — `src/Notch.Core/Widgets/TodoList.cs:62`
- [ ] Double-click edits the text inline (Enter keeps, Esc reverts, empty deletes). Drag reorders within the item's own group. "Clear done" header button; up to 7 rows visible, then it scrolls — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:816`

### Home widget: Launcher
- [x] Options: Title, Buttons (lines "Name = path/folder/URL"; names default to the file name or link host; # comments; max 8) — `src/Notch.Core/Widgets/HomeWidgetData.cs:21`
- [ ] Clicking a button shell-opens the target (environment variables expanded); tooltip = target; hint when there are no buttons — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:995`

### Home widget: Commands
- [x] Options: Title, Buttons ("Name = command"), Run in ("Terminal tab" / "In the background") — `src/Notch.Core/Widgets/HomeLayout.cs:171`
- [ ] "Terminal tab" opens a terminal session that runs the command. "In the background" runs it through `cmd /d /s /c` in the terminal folder; the button is disabled while it runs, then a finished/failed notice appears — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:1007`

### Home widget: Web feed
- [x] Options: Title, Link, JSON field (dotted path, array indices), Refresh every (1/5/15/60 min) — `src/Notch.Core/Widgets/HomeLayout.cs:177`
- [ ] Fetches with User-Agent "Notch". HTML is stripped of tags, JSON fields are extracted, text is capped at 600 characters. Shows "Updated <time>" or "Could not load the link. / Trying again in a minute" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:1086`

### Home widget: Plugin card / plugin widget
- [ ] Plugin card widget: choose a card from those running plugins publish. Shows label, value (or clock), detail, progress and colour; clicking invokes the card's action. Empty texts: "Pick a card with the gear" / "That plugin card is not there right now" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:1236`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** Plugin widget: a plugin-registered type (name, glyph, description, width, single, options). Drawn as page blocks or the plugin's own WPF view; card click callback; text boxes take the keyboard. Status texts: "Loading…" / "This widget's plugin is not running…" — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:1125`
- [ ] Placing, removing and saving options of a plugin widget are reported to the plugin — `src/Notch.App/Shell/NotchWindow.HomeWidgets.cs:1214`

### Terminal tab
- [ ] **[changed: xterm.js runs inside the main Tauri web view; no second WebView2]** One WebView2 (composition control) hosting an xterm.js page for all sessions, served under a virtual host. Context menu, devtools, zoom, status bar and browser accelerators are off; dropping files is blocked; clipboard read is allowed — `src/Notch.App/Terminal/TerminalBridge.cs:78`
- [x] Presets: lines "Name = [VAR=value …] command args". Arguments with spaces in quotes; up to 6; defaults are Claude/Codex/opencode/Shell(powershell). Unique ids — `src/Notch.Core/Terminal/TerminalPresets.cs:17`
- [x] Agent kind is detected from the command name (claude, codex, gemini, aider, opencode) to pick the glyph and status tracking — `src/Notch.Core/Terminal/TerminalPresets.cs:76`
- [ ] Launcher buttons "+ Name" (tooltip = preset line), one per preset — `src/Notch.App/Shell/NotchWindow.Terminal.cs:102`
- [ ] Folder button "📁 name" (tooltip "New sessions start in …"). Its menu lists recent folders that still exist (checked = current), then a separator and "Browse…" (folder dialog). The menu holds the notch open — `src/Notch.App/Shell/NotchWindow.Terminal.cs:234`
- [ ] Recent folders: up to 8, most recent first, saved whenever a session starts or the folder is chosen. Starts in the first recent folder that exists, else the user profile — `src/Notch.Core/Settings/AppSettings.cs:213`
- [ ] Session tabs: logo or glyph, "Name · folder" (max 150), status dot (working/attention/done colour), close × button. The active tab is highlighted; clicking a tab activates it and focuses the terminal — `src/Notch.App/Shell/NotchWindow.xaml:370`
- [ ] Closing a session kills the process, removes it from the agent board and activates the neighbouring session — `src/Notch.App/Terminal/TerminalController.cs:97`
- [ ] Empty state: "Start a session with the buttons above"; on failure: "The terminal could not start: …" — `src/Notch.App/Shell/NotchWindow.Terminal.cs:205`
- [ ] The process starts in a pseudo console (ConPTY) at the size xterm measured. `powershell` resolves to pwsh first; plain PowerShell gets `-NoLogo` — `src/Notch.App/Terminal/TerminalController.cs:165`
- [ ] Missing command: a grey inline notice with the install hint. Exit: "Process exited with code N." — `src/Notch.App/Terminal/TerminalController.cs:172`
- [ ] Per-preset environment variables plus NOTCH_PIPE/NOTCH_SESSION are injected — `src/Notch.App/Terminal/TerminalController.cs:182`
- [ ] Claude sessions get `--settings <claude-hooks.json>` (6 hooks). Codex sessions get `-c notify=['Notch.Hook.exe']` — `src/Notch.Core/Agents/AgentHooks.cs:42`
- [ ] Resize: xterm FitAddon on a ResizeObserver; the new size is sent to ConPTY — `src/Notch.App/Assets/terminal/terminal.js:147`
- [ ] xterm options: font size 13, blinking cursor, 5000 lines of scrollback, Cascadia Mono/Code/Consolas font — `src/Notch.App/Assets/terminal/terminal.js:63`
- [ ] Ctrl+C copies when text is selected (otherwise it interrupts); Ctrl+V pastes from the clipboard — `src/Notch.App/Assets/terminal/terminal.js:80`
- [ ] Output is batched once per dispatcher pass, base64 over JSON — `src/Notch.App/Terminal/TerminalController.cs:249`
- [ ] Themes: dark (#000 background) and light (#f3f3f5, darkened ANSI). A plugin theme can override any xterm colour, ANSI 0–15, cursor, selection and font family — `src/Notch.App/Assets/terminal/terminal.js:9`
- [ ] Agent status per session: hooks (UserPromptSubmit/Pre/PostToolUse → Working, Notification → NeedsInput, Stop/agent-turn-complete → Done, SessionEnd → Idle). Typing answers NeedsInput — `src/Notch.Core/Agents/AgentStatusTracker.cs:44`
- [ ] Codex infers Working from Enter, NeedsInput from the bell, and Idle after 15 s without output (checked every 5 s) — `src/Notch.App/Terminal/TerminalController.cs:340`
- [ ] Done is cleared when the session is viewed (Terminal tab showing with that session active) — `src/Notch.App/Terminal/TerminalController.cs:305`
- [ ] opencode events with no session are routed to opencode sessions in the same folder — `src/Notch.App/Terminal/TerminalController.cs:139`
- [ ] Skills & agents side panel (212 wide): skills and subagents from `~/.claude` (CLAUDE_CONFIG_DIR respected) and `<folder>/.claude`. The project's entries shadow the user's by name. Each row shows its scope label and a description tooltip; empty state "None found in this folder or your .claude folder" — `src/Notch.App/Shell/NotchWindow.Library.cs:16`
- [ ] Clicking a subagent row starts a Claude session `--agent <name> --dangerously-skip-permissions` in that agent's root folder — `src/Notch.App/Shell/NotchWindow.Library.cs:60`

### Stats tab
- [ ] 3×2 cards: CPU % (blue) + temperature + sparkline; Memory % (violet) + "X of Y" + sparkline; GPU % or "n/a" (green) + temperature + sparkline; Download rate (cyan); Upload rate (orange); Battery % or "None" (green above 50, amber 21–50, red at 20 or below) — `src/Notch.App/Shell/NotchWindow.Widgets.cs:492`
- [ ] Sampled every second off the UI thread, only while visible. Temperatures in °C or °F by region. Sparklines hold 60 points with a gradient fill — `src/Notch.App/Shell/Sparkline.cs:9`
- [x] Formats: bytes with one decimal under 10 (B…TB), "/s" rates, whole percents — `src/Notch.Core/Widgets/SystemStats.cs:28`

### Shelf tab
- [ ] Dropping files anywhere on the notch (terminal and text boxes included) adds them, using copy or link effects only, never move. Other drags are refused — `src/Notch.App/Shell/NotchWindow.Shelf.cs:166`
- [ ] Dragging files over the pill opens the Shelf tab after 120 ms (when that setting is on) — `src/Notch.App/Shell/NotchWindow.Shelf.cs:184`
- [ ] Holds up to 24 paths, newest first. Re-adding moves a path to the front. Stored in `shelf.json` (not in the demo) — `src/Notch.Core/Shelf/FileShelf.cs:10`
- [ ] Tiles: 6 columns, 92 high, shell thumbnail (88 px, loaded lazily) or a file/folder glyph, name, tooltip = path, × remove button — `src/Notch.App/Shell/NotchWindow.xaml:558`
- [ ] Double-click opens the file with its default app — `src/Notch.App/Shell/NotchWindow.Shelf.cs:239`
- [ ] Dragging a tile out drags the file (copy/move/link) and holds the notch open; missing files are pruned afterwards — `src/Notch.App/Shell/NotchWindow.Shelf.cs:250`
- [ ] Right-click menu: Open, Show in folder (`explorer /select`), separator, Take off the shelf — `src/Notch.App/Shell/NotchWindow.Shelf.cs:290`
- [ ] Summary "N items · drag one out to use it", "Clear" button (tooltip: files stay where they are), empty text "Drag files onto the notch to keep them here" — `src/Notch.App/Shell/NotchWindow.Shelf.cs:106`
- [ ] Missing files are pruned off the UI thread each time the notch opens — `src/Notch.App/Shell/NotchWindow.Shelf.cs:154`

### Clipboard tab
- [ ] Off by default. Turning it on shows the tab and registers a clipboard listener; turning it off clears everything, pins included, and deletes the pins file — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:48`
- [ ] Captures text (max 100 k characters), images (stored as PNG, max 4 MB) and file lists (max 50) with the source program. Retries 5× if the clipboard is busy — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:93`
- [x] Privacy: skips ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory=0, and copies from known password managers — `src/Notch.Core/Clipboard/ClipboardHistory.cs:239`
- [x] Keeps 50 unpinned items. Re-copying the same content moves it up. Pinned items come first, then newest — `src/Notch.Core/Clipboard/ClipboardHistory.cs:159`
- [ ] Search box ("Search what you copied"): text contents, file paths, "picture/image"; "Nothing matches" — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:163`
- [ ] Rows: glyph or image thumbnail, preview (first line, 200 characters / file names / "Picture (N KB)"), source · age ("just now", "N min ago", time, date). Clicking copies it again without adding a new history entry — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:220`
- [ ] Row buttons: "Put these files on the shelf" (file items only), Pin/Unpin (pins survive restarts in `clipboard-pins.json`), "Forget this one" — `src/Notch.App/Shell/NotchWindow.xaml:686`
- [ ] "Clear" forgets everything except pinned items — `src/Notch.App/Shell/NotchWindow.Clipboard.cs:39`

### Plugins tab
- [ ] Cards from plugins: 3 columns, 95 high, label, value (or clock) in the card's colour, detail, optional progress bar. Clicking runs the card's action (hand cursor only when it has one) — `src/Notch.App/Shell/NotchWindow.xaml:705`
- [ ] Scrolls with the wheel only, past 3 rows — `src/Notch.App/Shell/NotchWindow.Plugins.cs:20`

### Plugin pages
- [ ] One text tab per plugin page, renamed and removed live — `src/Notch.App/Shell/NotchWindow.Pages.cs:195`
- [ ] Large (920×540) or compact (640 wide, height fitted to content, 180–540) size — `src/Notch.App/Shell/NotchWindow.Pages.cs:46`
- [ ] Top row: "‹ Back" button (custom label), status line (tooltip), action buttons (colour, hint, enabled). Actions marked Confirm need "Click again" within 3.5 s — `src/Notch.App/Shell/NotchWindow.Pages.cs:294`
- [ ] Stats row: one row of figure cards (label, value or clock, detail, progress, colour) — `src/Notch.App/Shell/NotchWindow.xaml:753`
- [ ] Body is one of: a choices list (clickable rows with label, detail and coloured value), page blocks, the plugin's own view, or a read-only console (code font, auto-scroll when at the end) — `src/Notch.App/Shell/NotchWindow.Pages.cs:261`
- [ ] Input line with hint, Send button, Enter submits, ↑/↓ history (50 entries). An Accept validator runs off the UI thread and shows a red error while keeping the text — `src/Notch.App/Shell/NotchWindow.Pages.cs:396`
- [ ] Page blocks: Text (Normal/Heading/Muted/Code, colour), ValueRow, Progress, Table (≤8 columns, ≤200 rows), Chart (filled line, height 40–240, max), Buttons (with confirm), Choices, Toggle switch, Slider (min/max/step/unit, reports on release), Select (pill group), TextField (secret, multi-line with Ctrl+Enter, typing debounced 150 ms, submit label, clear on submit), Image (height 20–400), Separator — `src/Notch.App/Shell/PageBlocks.cs:72`
- [ ] Blocks update in place (caret, typing and slider drag are preserved) — `src/Notch.App/Shell/PageBlocks.cs:25`
- [ ] If a page disappears while open, the notch goes to Home. If it comes back within 5 s (plugin restart), it reopens. A plugin can ask for its page to be opened — `src/Notch.App/Shell/NotchWindow.Pages.cs:149`
- [ ] **[changed: applies to HTML views]** Clicking a text box in a plugin's own view takes the keyboard; it is given back when focus leaves — `src/Notch.App/Shell/NotchWindow.Pages.cs:102`
- [ ] **[changed: WPF views are replaced by HTML views in plugin API 11; a WPF `View` shows a "this plugin needs an update" placeholder]** A plugin view that throws or is not a WPF element shows a fallback text instead — `src/Notch.App/Shell/PluginViews.cs:11`

### Command palette
- [ ] Opened by its own global hotkey (default Alt+Shift+P), `notch://palette`, or `notchctl palette`. It toggles. Opening remembers the foreground window and the previous tab, unlights the tab buttons, and focuses the input — `src/Notch.App/Shell/NotchWindow.Palette.cs:101`
- [ ] Search box with icon and hint "Type a command: timer, open stats, new Claude session…" — `src/Notch.App/Shell/NotchWindow.xaml:603`
- [ ] Keys: Esc closes and hands focus back; ↑/↓ move with wraparound; Enter runs; clicking a row runs it — `src/Notch.App/Shell/NotchWindow.Palette.cs:178`
- [ ] Closing restores the previous tab and collapses the notch if the palette opened it. Entries that open a tab or Settings do not hand focus back — `src/Notch.App/Shell/NotchWindow.Palette.cs:148`
- [ ] Ranking: every word must match. Score 100 for a prefix, 80 at a word start, 50 inside a word, 5–30 for a letter subsequence; keywords count ×0.7; 7 rows shown — `src/Notch.Core/Shell/PaletteSearch.cs:14`
- [ ] Entry: typing a duration offers "Start a <d> timer" (up to 24 h) — `src/Notch.App/Shell/NotchWindow.Palette.cs:316`
- [ ] Entries: "Open Home/Terminal/Stats/Shelf", plus "Open Clipboard" (when history is on) and "Open Plugins" (when visible) — `src/Notch.App/Shell/NotchWindow.Palette.cs:321`
- [ ] Entries: "Open <plugin page>" — `src/Notch.App/Shell/NotchWindow.Palette.cs:336`
- [ ] Entries: "Pause/Resume/Cancel the timer" (depending on state), "Start a Pomodoro", "Start a <preset> timer" — `src/Notch.App/Shell/NotchWindow.Palette.cs:382`
- [ ] Entries: "New <preset> session" for each terminal preset — `src/Notch.App/Shell/NotchWindow.Palette.cs:348`
- [ ] Entries: "Go to <agent · folder>" (subtitle: state · where) — `src/Notch.App/Shell/NotchWindow.Palette.cs:359`
- [ ] Entry: "Open settings" — `src/Notch.App/Shell/NotchWindow.Palette.cs:367`
- [ ] Entries: "Switch on/off <plugin>" for each installed plugin that is not always-on, and "Stop all plugins" (kill switch) — `src/Notch.App/App.xaml.cs:445`
- [ ] Plugin palette entries (static), plus live plugin results requested 120 ms after typing pauses, cancelled by newer typing, shown below the ranked rows (up to 5). Rows have a glyph or image icon — `src/Notch.App/Shell/NotchWindow.Palette.cs:202`
- [ ] The selected row is highlighted with the accent hover brush — `src/Notch.App/Shell/NotchWindow.Palette.cs:441`

### Keyboard shortcuts & hotkeys
- [ ] Open/close hotkey (default Alt+Shift+N). It opens and holds the notch open; pressed again it closes and hands focus back. It is ignored while hidden for fullscreen — `src/Notch.App/Shell/NotchWindow.Keyboard.cs:153`
- [x] Hotkey format: Ctrl/Alt/Shift/Win + A–Z, 0–9, F1–F24 or Space, with at least one modifier other than Shift; normalised spelling — `src/Notch.Core/Shell/Hotkey.cs:26`
- [ ] If registering a hotkey fails ("already used by Windows or another app" / "not a hotkey Notch can use"), a message box appears after saving settings — `src/Notch.App/App.xaml.cs:587`
- [ ] Plugin global hotkeys are registered and released as plugins change; chords Notch already uses are refused; registration failures are reported to the plugin — `src/Notch.App/Shell/NotchWindow.Keyboard.cs:111`
- [ ] In-notch keys: timer entry Enter/Esc; widget form Enter (single-line field)/Esc; note Esc; to-do Enter/Esc and edit Enter/Esc; palette Esc/↑/↓/Enter; page input Enter/↑/↓; page text field Enter or Ctrl+Enter; terminal Ctrl+C/Ctrl+V; Settings plugin source box Enter installs — `src/Notch.App/Shell/NotchWindow.Widgets.cs:99`

### Agents (detection, states, app sessions, hooks)
- [ ] Process scan every 3 s for Claude, Codex, Gemini, Aider and opencode. Agents are matched by executable name, or by a runtime (node/bun/python/…) plus a command-line marker — `src/Notch.Core/Agents/AgentCatalog.cs:46`
- [x] Excluded from detection: Electron helpers (`--type=`), background/server/print modes, desktop-app folders, opencode subcommands (serve/run/…), Notch's own children, and helper processes nested under an agent — `src/Notch.Core/Agents/AgentCatalog.cs:90`
- [x] Host name from the ancestor process (Windows Terminal, VS Code, Cursor, Windsurf, WezTerm, Alacritty, kitty, Hyper, Tabby, Console, JetBrains IDEs, Visual Studio) — `src/Notch.Core/Agents/AgentDetector.cs:25`
- [ ] Per-agent hide toggles (HiddenAgents) and a master on/off — `src/Notch.App/Agents/AgentTracker.cs:118`
- [ ] Global hook messages (shared pipe; session empty; the hook's pid is mapped to an agent through its ancestors) update a detected agent's state and folder. If the agent is unknown, a rescan runs — `src/Notch.App/Agents/AgentTracker.cs:231`
- [ ] opencode plugin events are routed by folder: to Notch terminal sessions first, then to detected opencode windows in that folder (or the only one) — `src/Notch.App/Agents/AgentTracker.cs:267`
- [ ] Focus window: raises the first window among host/ancestor/conhost candidates, restoring it if minimised and attaching input to beat the foreground lock — `src/Notch.Platform/Agents/WindowFocus.cs:19`
- [ ] Done is cleared when the agent's window is in front or after jumping to it — `src/Notch.App/Agents/AgentTracker.cs:157`
- [ ] App sessions: reads the Claude desktop app's Code-tab session files and transcripts, and ChatGPT/Codex thread transcripts. States Working/NeedsInput/Done/Idle with a 10-minute staleness limit. Claude sessions are listed from the last 6 h, ChatGPT from the last 1 h (max 5). Tail-reads 256 KB; unchanged files are skipped — `src/Notch.App/Agents/AppSessionReader.cs:24`
- [ ] App sessions are marked seen when that app's window is in front (Claude: the shown session; ChatGPT: all of its sessions) — `src/Notch.App/Agents/AgentTracker.cs:183`
- [ ] Hook install plan/apply: adds 6 hooks to `~/.claude/settings.json` (CLAUDE_CONFIG_DIR respected), a notify line to `~/.codex/config.toml` (CODEX_HOME respected; an existing notify is never replaced), and `plugins/notch.js` to opencode's config (only if opencode is installed) — `src/Notch.App/Agents/AgentHookFiles.cs:59`
- [ ] A one-time `.notch-backup` is made of each edited file; writes are atomic; removal takes out only Notch's entries and deletes the opencode plugin — `src/Notch.App/Agents/AgentHookFiles.cs:227`
- [ ] The "installed" state is read from the files themselves, not the setting — `src/Notch.App/Agents/AgentHookFiles.cs:56`
- [x] Hook paths are written with forward slashes, quoted if they contain spaces (Claude runs hooks through bash) — `src/Notch.Core/Agents/AgentHooks.cs:144`

### Settings window (shell)
- [ ] 880×640, minimise only, centred; custom title bar (drag area 44 px, rounded corners via a glass frame); a single instance (re-activated if already open) — `src/Notch.App/Settings/SettingsWindow.xaml:1`
- [ ] Sidebar: logo, "Notch", version; sections General, Appearance, Pill, Agents, Notifications, Clipboard, Terminal, Timer, Calendar, Automation, Plugins, About (each with an icon). The title bar shows the section name; switching scrolls to the top — `src/Notch.App/Settings/SettingsWindow.xaml:73`
- [ ] Caption buttons: Minimise, Close ("Close without saving") — `src/Notch.App/Settings/SettingsWindow.xaml:94`
- [ ] Footer: Cancel (Esc), Save (Enter, accent), status text — `src/Notch.App/Settings/SettingsWindow.xaml:103`
- [ ] Save validates terminal presets, timer presets, both hotkeys and the webhook port (1024–65535) with specific message boxes, then applies everything and closes — `src/Notch.App/Settings/SettingsWindow.xaml.cs:413`
- [ ] Window height is capped to the work area — `src/Notch.App/Settings/SettingsWindow.xaml.cs:68`

### Settings: General
- [ ] "Start with Windows" (HKCU Run key, read live; a message box if it cannot be changed) — `src/Notch.Platform/Startup/StartupRegistration.cs:21`
- [ ] "Tell me when an update is available" (tooltip explains nothing installs without asking) — `src/Notch.App/Settings/SettingsWindow.xaml:123`
- [ ] "Open the notch when the pointer hovers over it" — `src/Notch.App/Settings/SettingsWindow.xaml:131`
- [ ] "Open the Shelf when files are dragged onto the pill" — `src/Notch.App/Settings/SettingsWindow.xaml:132`
- [ ] "Hide while a game or other fullscreen app is showing" — `src/Notch.App/Settings/SettingsWindow.xaml:134`
- [ ] Display combo: "Primary display", then "Display N (W × H[, primary])" — `src/Notch.App/Settings/SettingsWindow.xaml.cs:115`
- [ ] Position combo: "Top of the screen" / "Taskbar, far left" — `src/Notch.App/Settings/SettingsWindow.xaml:147`
- [ ] Style combo: "Notch (attached to the screen edge)" / "Dynamic Island (floating pill)" — `src/Notch.App/Settings/SettingsWindow.xaml:154`
- [ ] Hotkey text boxes: "Open and close the notch" and "Command palette" (empty = none; normalised on save) — `src/Notch.App/Settings/SettingsWindow.xaml:165`

### Settings: Appearance
- [ ] Theme combo: Dark / Light / Follow Windows — `src/Notch.App/Settings/SettingsWindow.xaml:186`
- [ ] Plugin theme combo: "None", plus "<theme> (<plugin>)" for running plugins (description tooltip). A chosen theme whose plugin is not running is kept as "(its plugin is not running)". The list refreshes as plugins start — `src/Notch.App/Settings/SettingsWindow.xaml.cs:352`
- [ ] Plugin theme problem text in red (bad XAML, wrong root, a key of the wrong type) — `src/Notch.App/Settings/SettingsWindow.xaml.cs:385`
- [ ] Accent swatches: 8 round colours plus "No accent colour" (in the text colour), with a selection ring — `src/Notch.App/Settings/SettingsWindow.xaml.cs:390`

### Settings: Pill
- [ ] "Glow effects (colour and rhythm follow what is happening)" — `src/Notch.App/Settings/SettingsWindow.xaml:213`
- [ ] Brightness slider 25–200 % in steps of 5 with a live "%" label; disabled while glow is off — `src/Notch.App/Settings/SettingsWindow.xaml:214`
- [ ] Show in the pill: Media, Volume and audio output, Brightness, Charging and low battery, Bluetooth, Caps Lock (each one suppresses activity ids) — `src/Notch.App/Settings/SettingsWindow.xaml:229`

### Settings: Agents
- [ ] Explanatory text at the top — `src/Notch.App/Settings/SettingsWindow.xaml:241`
- [ ] "Show agents running outside Notch's terminal" with indented per-agent checkboxes (Claude, Codex, Gemini, Aider, opencode), disabled while it is off — `src/Notch.App/Settings/SettingsWindow.xaml:246`
- [ ] "Show sessions open in the Claude and ChatGPT apps" — `src/Notch.App/Settings/SettingsWindow.xaml:249`
- [ ] "Let agents report what they are doing, wherever they run". On save it shows a confirmation listing each file and what changes (plus backup notes and problems). Yes applies; No reverts the checkbox and aborts the save; errors appear in a message box — `src/Notch.App/Settings/SettingsWindow.xaml.cs:531`

### Settings: Notifications
- [ ] "Show Windows notifications in the pill" with privacy help text — `src/Notch.App/Settings/SettingsWindow.xaml:270`
- [ ] "Only these apps, one per line (empty means all)" multi-line box (deduplicated on save), disabled while mirroring is off — `src/Notch.App/Settings/SettingsWindow.xaml:276`

### Settings: Clipboard
- [ ] "Keep a history of what I copy (adds a Clipboard tab)" with privacy help text — `src/Notch.App/Settings/SettingsWindow.xaml:285`
- [ ] "Clear the history now" button (changes to "Cleared"; clears pins too) — `src/Notch.App/Settings/SettingsWindow.xaml.cs:149`

### Settings: Terminal
- [ ] Terminal buttons box (one preset per line, up to 6; environment-variable syntax help). Each line is validated with an error naming the bad line — `src/Notch.App/Settings/SettingsWindow.xaml.cs:583`

### Settings: Timer
- [ ] Presets box: up to 6 lines such as "Tea 3m" or "15m" (name max 10 characters), each validated — `src/Notch.App/Settings/SettingsWindow.xaml.cs:614`
- [ ] Pomodoro minutes: Focus (25), Short break (5), Long break (15), each 1–600; an invalid value keeps the previous one — `src/Notch.App/Settings/SettingsWindow.xaml.cs:1032`

### Settings: Calendar
- [ ] Calendar feeds box, one .ics link per line (deduplicated) — `src/Notch.App/Settings/SettingsWindow.xaml:334`

### Settings: Automation
- [ ] Help text about notchctl, notch:// and the webhook — `src/Notch.App/Settings/SettingsWindow.xaml:340`
- [ ] "Accept requests on a local webhook" (switching it on creates a token if there is none) — `src/Notch.App/Settings/SettingsWindow.xaml.cs:194`
- [ ] Port box (default from WebhookRequest), read-only token box (code font), "Copy" and "New token" buttons — `src/Notch.App/Settings/SettingsWindow.xaml:349`
- [ ] A live curl example that updates with the port — `src/Notch.App/Settings/SettingsWindow.xaml.cs:249`
- [ ] A message box after Save if the webhook cannot start (for example, the port is taken) — `src/Notch.App/App.xaml.cs:581`

### Settings: Plugins
- [ ] "Browse plugins" (accent): loads the registry fresh and opens the browser dialog; errors appear in a message box — `src/Notch.App/Settings/SettingsWindow.xaml.cs:255`
- [ ] "Open folder" opens the plugins folder (creating it if needed) — `src/Notch.App/Settings/SettingsWindow.xaml.cs:1018`
- [ ] "Check for updates" checks plugin repositories on GitHub, with status text: checking, N available, all up to date, failures, or "nothing to check" — `src/Notch.App/Settings/SettingsWindow.xaml.cs:829`
- [ ] A quiet update check runs whenever the window opens — `src/Notch.App/Settings/SettingsWindow.xaml.cs:201`
- [ ] "Stop all" kill switch: stops every plugin, unticks them all, saves, and shows a status message — `src/Notch.App/Settings/SettingsWindow.xaml.cs:214`
- [ ] Trust warning text — `src/Notch.App/Settings/SettingsWindow.xaml:377`
- [ ] Installed list: a checkbox per plugin ("Name version", tooltip with description, author and folder). "(comes with Notch)" or "(development)" for always-on plugins; disabled for an invalid manifest; "No plugins installed." — `src/Notch.App/Settings/SettingsWindow.xaml.cs:669`
- [ ] Per-plugin rows: "Failed to start: …" / "Cannot be used: …"; "Changed since you approved it…" with "Review…" (approve, then retry); "Hotkey(s): …" — `src/Notch.App/Settings/SettingsWindow.xaml.cs:710`
- [ ] Per-plugin "Options" expander from the manifest settings schema: Bool → checkbox, Number → box (range tooltip, clamped, integer if the step is whole), Secret → password box ("leave empty to keep"), Choice → combo, List → multi-line box, Text → box (hint tooltip). Only changed values are saved; running plugins are told off-thread — `src/Notch.App/Settings/OptionEditors.cs:65`
- [ ] Per-plugin update row: "<tag> is available (you have x)" with "Update and restart". If terminals are open it asks first; it keeps the approval and ticks, and installs at once if the plugin is not loaded — `src/Notch.App/Settings/SettingsWindow.xaml.cs:893`
- [ ] On Save, each newly ticked plugin, or one whose files changed, gets the review prompt; declining unticks it. Approved plugins that were held back are retried — `src/Notch.App/Settings/SettingsWindow.xaml.cs:474`
- [ ] "Install from GitHub": an owner/name or URL box (Enter installs) and an "Install" button. Status: getting, installed and ticked ("Save to switch it on"), or downloaded pending restart; errors are shown — `src/Notch.App/Settings/SettingsWindow.xaml.cs:978`
- [ ] The list updates live when a plugin is switched on or off elsewhere (palette or command) or installed through a link — `src/Notch.App/Settings/SettingsWindow.xaml.cs:791`

### Settings: About
- [ ] Large logo, "Notch", "Version x.y.z" — `src/Notch.App/Settings/SettingsWindow.xaml:397`
- [ ] "Update Notch now" button with status text: checking, latest, downloaded (asks before restarting if terminals are open), installing, not an installed copy, or an error — `src/Notch.App/Settings/SettingsWindow.xaml.cs:953`
- [ ] `--with-settings` with screenshots saves `settings-<section>.png` for each section at 1.5× — `src/Notch.App/Settings/SettingsWindow.xaml.cs:297`

### Tray icon
- [ ] Drawn icon (black pill with a white outline), tooltip "Notch"; efficiency mode disabled — `src/Notch.App/Shell/TrayIcon.cs:40`
- [ ] Left click opens Settings — `src/Notch.App/Shell/TrayIcon.cs:46`
- [ ] Menu "Settings…" — `src/Notch.App/Shell/TrayIcon.cs:12`
- [ ] Menu "Theme" submenu: Dark / Light / Follow Windows, with a check mark refreshed on open. Choosing one clears the plugin theme, saves and applies — `src/Notch.App/Shell/TrayIcon.cs:15`
- [ ] Menu separator and "Quit Notch" — `src/Notch.App/Shell/TrayIcon.cs:37`

### Plugin browser / confirm / review windows
- [ ] Browser window (520×560): search box filtering by name, author, description and tags; summary "N of M plugins" / empty / no match — `src/Notch.App/Plugins/PluginBrowserWindow.xaml.cs:26`
- [ ] Browser rows: name + "✔ checked" when verified, author · repo, description, "Says it uses: …", and an Install or Reinstall button — `src/Notch.App/Plugins/PluginBrowserWindow.xaml.cs:41`
- [ ] Confirm window (topmost, 460 wide): name, "by author · github.com/repo", verified note, description, the list "IT SAYS IT USES" (or "It does not say…"), and a trust warning box — `src/Notch.App/Plugins/PluginConfirmWindow.xaml:13`
- [ ] Confirm buttons: Cancel / Install / "Install and switch on". Already installed → "Reinstall" with a note. A plugin needing a newer API is blocked — `src/Notch.App/Plugins/PluginConfirmWindow.xaml.cs:48`
- [ ] Install flow: one at a time; checks the downloaded id matches the registry; approves the files on "switch on" or when updating an approved plugin; shows a notice — `src/Notch.App/Plugins/PluginInstallFlow.cs:65`
- [ ] The registry is cached for 10 minutes and stored in `registry.json` for offline revocation checks — `src/Notch.App/Plugins/PluginInstallFlow.cs:35`
- [ ] Review prompt (Yes/No warning) showing the review description. Revoked or blocked plugins get an OK-only refusal — `src/Notch.App/Plugins/PluginReviewPrompt.cs:16`
- [ ] Turning a plugin on by command always asks (the review prompt, or a plain Yes/No), and refuses while another question is open — `src/Notch.App/Automation/CommandDispatcher.cs:114`
- [ ] Dialog windows follow the dark/light title bar of the theme (DWM immersive dark mode) — `src/Notch.App/Shell/WindowTheming.cs:15`

### Updates
- [ ] Background check 45 s after start, then every 4 h, only for an installed copy (HKCU\Software\Notch InstallDir matches) with notices on. Each release is announced once (LastNotifiedUpdateTag) — `src/Notch.App/Updates/UpdateService.cs:208`
- [ ] Saving settings brings the next check forward to 5 s — `src/Notch.App/Updates/UpdateService.cs:127`
- [ ] Forced update: check, download (reusing the same tag's download), confirm the restart, run the installer `/S /UPDATE`, quit. Not available for copies that were not installed — `src/Notch.App/Updates/UpdateService.cs:140`
- [ ] Downloaded installers are cleaned up at startup — `src/Notch.App/Updates/UpdateService.cs:188`
- [ ] After an update, `--updated` shows "Notch updated"; a failed update leaves `update-error.txt`, which is shown once and deleted — `src/Notch.App/Updates/UpdateService.cs:95`

### Automation (notch://, notchctl, webhook)
- [ ] Verbs: install, plugin (enable), timer (duration / stop / pause / resume, max 24 h), open (tab or plugin page), toast (title ≤120, detail ≤300, glyph, colour name or #rrggbb, lifetime ≤30 s), palette, settings — `src/Notch.Core/Automation/CommandParser.cs:107`
- [ ] Opening a tab or page also reveals and holds the notch open — `src/Notch.App/Shell/NotchWindow.Automation.cs:56`
- [ ] notchctl: `--start`, `webhook` info, exit codes 0/1/2/3, a 10-minute answer timeout — `src/Notch.Cli/Program.cs:10`
- [ ] Webhook: localhost only, Bearer token, no Origin header, 64 KB body. `POST /v1/activity` (id, title, detail, glyph, tier, progress, glow, lifetimeMs), `DELETE /v1/activity/{id}`, `POST /v1/notify`, `POST /v1/command` (no plugin install or enable) — `src/Notch.Core/Automation/WebhookRequest.cs:66`
- [ ] At most 20 non-transient webhook activities can be live at once — `src/Notch.App/Automation/WebhookService.cs:16`

### Themes
- [ ] Dark, Light and System themes (System follows AppsUseLightTheme and live changes) — `src/Notch.App/Shell/ThemeManager.cs:110`
- [ ] Accent colour applied to the Accent, AccentHover (0x59), AccentPressed (0x80) and OnAccent (black/white by luminance) brushes. A plugin theme's own accent wins — `src/Notch.App/Shell/ThemeManager.cs:226`
- [ ] **[changed: the plugin host loads the XAML and translates it to CSS variables]** Plugin themes are XAML ResourceDictionaries merged last. They reload when the file changes, are validated (style target types, brush, font, radius and double keys), and may supply terminal colours — `src/Notch.App/Shell/ThemeManager.cs:261`
- [ ] A built-in plugin ships with Notch (the New Age theme) and is always enabled — `src/Notch.App/App.xaml.cs:159`

### Demo & screenshot mode
- [ ] Demo media: made-up Linkin Park tracks (one picked at random per run), generated gradient art, starts 72 s in, with working play/pause, next, previous and seek — `src/Notch.App/Shell/DemoMediaService.cs:12`
- [ ] Demo shelf: 5 made-up paths that are kept even though the files do not exist; never saved — `src/Notch.App/App.xaml.cs:36`
- [ ] Demo driver: every 3 s loops through volume, Claude working, needs input, done, charging, Bluetooth headphones, brightness, Caps Lock, two sessions (+1), clear, low battery 9 % — `src/Notch.App/Shell/DemoDriver.cs:20`
- [ ] `--screenshots=<folder>` (with `--demo`) saves transparent PNGs at 2× with a 48 px margin for the glow: pill-media, pill-agent-working, pill-agent-needs-input, pill-agent-done, pill-volume, pill-charging, pill-bluetooth, pill-low-battery, pill-caps-lock, home, stats (after 10 s), shelf, home-agents, clipboard, palette, palette-search ("tim"), then quits — `src/Notch.App/Shell/ScreenshotRunner.cs:14`
- [ ] Screenshot helpers: pin open, wait for the animation to settle (+400 ms), made-up agents (Claude/website, Codex/api, Gemini/notes), made-up clipboard items (one pinned), demo palette text — `src/Notch.App/Shell/NotchWindow.Automation.cs:64`
- [ ] Screenshot mode runs beside a live copy (the single-instance check is skipped) — `src/Notch.App/App.xaml.cs:90`

## 4. Port deliverables

### Compatibility checks
- [ ] Rust reads every settings file in `tests/fixtures/settings/` the way C# does, and a save keeps keys it does not know
- [ ] The Rust `notchctl`, `Notch.Hook.exe` and `notch.js` work against the Rust app, and the C# `notchctl` and `Notch.Hook.exe` work against it too (wire compatibility in both directions)
- [ ] Hooks installed by the C# app (Claude `settings.json`, Codex `config.toml`, opencode `notch.js`) keep working after the switch, with no reinstall
- [ ] Upgrade test on a clean Windows VM: last C# release → Rust build through the in-app updater. Settings, plugins including themes, trust approvals, agent hooks, webhook, shelf and clipboard pins all carry over
- [ ] Uninstall of the Rust build removes what the C# uninstaller removes, and nothing more
- [ ] `UpdateSigning.PublicKey` is set in a C# release before the switch, so the hand-over update is verified

### Plugin host
- [ ] `Notch.PluginHost.exe` (.NET 10, self-contained) starts when any plugin is enabled, including the built-in New Age theme
- [ ] `Notch.Core.dll` keeps its assembly name and version (= app version) as the contract plugins compile against
- [ ] IPC protocol covers every service in part 2, with round-trip tests
- [ ] Host crash: restart, find the plugin that keeps crashing with the `plugins-starting` marker, disable it, tell the user
- [ ] The C# plugin tests (PluginApi, PluginChecker, Plugin, PluginInstaller, PluginGlow, PluginPage, PluginRegistry, PluginTheme, PluginWidget, PluginPalette, PluginHotkey) pass against the host
- [ ] Audio frames reach plugins in time (16 bands, about every 25 ms, a frame older than 150 ms reads as silent)

### Themes and HTML views
- [ ] The base Dark, Light, Design, Styles, Window and AgentLogos XAML is converted to CSS variables and SVG
- [ ] XAML → CSS: SolidColorBrush, Linear/RadialGradientBrush, ImageBrush, FontFamily with the plugin's font files, CornerRadius, Double; unknown keys logged
- [ ] Neon (`samples/NeonTheme`), New Age (`builtin/NewAgeTheme`) and the registry themes (catppuccin, glass) look like they do in the C# app (side-by-side screenshots)
- [ ] HTML views (API 11): page and widget `View` points at an HTML file, shown in a sandboxed iframe with theme variables, plus a message channel to the plugin
- [ ] `docs/plugins.md` version table and the Widgets, Pages and Themes sections updated for API 11

### Tests, build and release
- [ ] The Core tests (about 417) are ported to Rust tests, reading the shared fixtures
- [ ] The Platform tests (AgentHookServer, ProcessDirectory) are ported
- [ ] `build.yml` runs cargo fmt, clippy and test on windows-latest
- [ ] `installer/build.ps1` builds the Rust payload and the Tauri setup stub. The payload is appended after the stub is built, and Authenticode verifies on the final file
- [ ] `release.yml` and `pages.yml` (screenshots with `--demo --screenshots`) work for the Rust build
- [ ] Performance compared with the C# build: idle CPU, memory (app + WebView2 + plugin host), animation smoothness
- [ ] README, `docs/automation.md` and `docs/publishing.md` updated
