# Rust rewrite: contract

Notch is being rewritten in Rust (Tauri v2) so Linux is native. The C# app in `src/` is the
reference for behaviour; it is deleted when the Rust app reaches the core slice. Work happens on
the `rust` branch. Version of the Rust app: 0.9.0.

## Running and checking the rewrite

From the repository root, with Rust and the platform's Tauri prerequisites installed:

```sh
cargo test
cargo run -p notch
```

The frontend is embedded directly from `ui/`; no bundler or frontend dev server is needed.
On Windows the debug executable is `target/debug/notch.exe`.

Browser regression tests use Playwright and a temporary local server that closes after the tests:

```sh
npm install --prefix tests
npx --prefix tests playwright install chromium
npm test --prefix tests
```

Set `PLAYWRIGHT_CHANNEL=msedge` to test with an installed Edge browser instead of downloading
Chromium. The tests cover backend expansion events, tray settings, terminal selection, and output.
They simulate the Tauri bridge; native PTY, global hotkey registration, and tray interactions
still need to be exercised in the app.

Current slice: settings, activity arbitration/glow, agent hooks, PTY terminals, tray, global
hotkey, and display placement. Media, system HUDs, shelf, widgets, plugins, automatic updates,
fullscreen detection, and taskbar placement have not yet been ported. Keep the C# implementation
as the reference until that work is complete. Windows builds/tests have been checked locally;
Linux runtime behavior is not yet verified.

## Layout

```
Cargo.toml                    workspace: crates/notch-core, src-tauri
crates/notch-core/            pure logic, no UI, no OS APIs, unit-tested
  src/lib.rs                  `pub mod` for each module below
  src/glow.rs                 port of Notch.Core/Activities/Glow.cs
  src/activity.rs             port of Activity.cs + ActivityManager.cs
  src/agents.rs               port of AgentStatusTracker.cs, AgentActivities.cs, AgentHooks.cs
  src/presets.rs              port of Terminal/TerminalPresets.cs + TerminalProfile.cs
  src/settings.rs             AppSettings + SettingsStore (JSON, atomic save, corrupt file => defaults)
src-tauri/                    the app (Tauri v2); binary name `notch`
ui/                           static frontend, no bundler; `window.__TAURI__` is available (withGlobalTauri)
  index.html, style.css, main.js, terminal.js, xterm/ (copied from src/Notch.App/Assets/terminal/xterm)
```

## notch-core public API (Rust, snake_case; serde with `rename_all = "camelCase"` for anything sent to the UI)

glow.rs
- `GlowColor { r, g, b: u8 }` with consts WHITE BLUE CYAN GREEN AMBER ORANGE RED YELLOW VIOLET (same values as C#),
  `GlowColor::named() -> &'static [(&'static str, GlowColor)]`, `from_name(&str) -> Option<GlowColor>` (case-insensitive),
  `on_light()`, `lerp(other, amount)`.
- `GlowPattern { Steady, Breathe, Pulse, Flash, Audio }` (serialises lowercase).
- `Glow { color, pattern, strength: f64 }`, `intensity_at(seconds, audio_level) -> f64`, `is_animated()`.
- `glow_output::{gain(percent), level(intensity, gain), MIN_PERCENT, MAX_PERCENT, DEFAULT_PERCENT}`.

activity.rs
- `ActivityTier { Ongoing=0, Attention=1, Transient=2 }` (serialises lowercase).
- `Activity { id, tier, title, detail: Option<String>, glyph: Option<String>, image: Option<Vec<u8>> (not serialised), progress: Option<f64>, glow: Option<Glow>, lifetime: Option<Duration> (not serialised) }`
  serialised for the UI as camelCase; glyph is a plain string (an emoji or short symbol; NOT a Segoe code point).
- `ActivityManager` (thread-safe, interior `Mutex`; time is passed in, no timers): `new()`, `set_suppressed(ids) -> bool changed`,
  `publish(activity, now: Instant) -> ()` (suppressed ids dropped; update keeps its sequence; Transient gets expiry = now + lifetime, default 2 s),
  `remove(id) -> bool`, `snapshot(now) -> Vec<Activity>` (expired excluded; highest tier first, then newest sequence first),
  `prune(now) -> bool` (drops expired, true if anything was dropped), `next_expiry() -> Option<Instant>`.

agents.rs
- `AgentKind { None, Claude, Codex }`, `AgentState { Idle, Working, NeedsInput, Done }` (both serialise lowercase).
- `AgentStatusTracker::new(kind)` with `state()`, `on_hook_event(&str)`, `on_user_input(submitted)`, `on_bell()`, `on_output_quiet()`, `on_viewed()`, `reset()`;
  every state-changing method returns `bool` (true when the state changed).
- `agent_activity(session_id, display_name, glyph, state, folder_name: Option<&str>, others: usize) -> Option<Activity>`; `activity_id(session_id) -> String` ("agent." + id).
- `hooks::{PIPE_VARIABLE="NOTCH_PIPE", SESSION_VARIABLE="NOTCH_SESSION", build_claude_settings(hook_exe) -> String, build_arguments(kind, hook_exe, claude_settings_path) -> Vec<String>, format_message(session, payload) -> String, try_parse_message(line) -> Option<(String session, String event)>}`.
  The hook executable is the app itself: the hook command line is `<notch exe> --hook` (hook_exe passed in may already include ` --hook`; the quoting rules of the C# code apply: a path with spaces is wrapped in double quotes, backslashes become `/` in the Claude JSON).
  For Codex the notify value is a TOML array of strings: `notify=['<exe>','--hook']` (no single quotes allowed in the path, else no arguments).

presets.rs
- `TerminalProfile { id, display_name, command, glyph, agent: AgentKind, install_hint, arguments: Vec<String>, environment: BTreeMap<String,String> }` (serde camelCase).
- `parse_preset(line, taken_ids) -> Option<TerminalProfile>`, `profiles_from_lines(&[String]) -> Vec<TerminalProfile>` (max 6, defaults when nothing valid),
  `to_line(&TerminalProfile) -> String`, `DEFAULT_PRESETS: [&str; 3]`. Line format: `Name = [VAR=value ...] command [args]`, same rules as the C# TerminalPresets.
  Glyphs: Claude "✳", Codex "◈", shell "▸" (plain Unicode, no icon font).
  Default shell command is `powershell` on Windows and `$SHELL`-or-`bash` on others: the default preset line is `Shell = shell` where the command word `shell` means "the platform shell" and is resolved by the app, not the core.

settings.rs
- `AppSettings` (serde camelCase, `#[serde(default)]` so old/partial files load): display_index: Option<i32>, style: `notch|island`, position: `topCenter|taskbarLeft` (keep as strings via enums),
  expand_on_hover=true, open_hotkey="Alt+Shift+N", hide_in_fullscreen=true, theme: `dark|light|system`, accent_color="Blue", glow_effects=true, glow_intensity=100,
  auto_update=true, terminal_presets: Vec<String> (defaults), recent_folders: Vec<String>, plus `remember_folder(&str)` (max 8, case-insensitive dedupe, newest first), `profiles()`.
- `SettingsStore::new(path)`, `load()`, `save(&AppSettings)`; `default_path()` = platform config dir `/notch/settings.json` (use the `dirs` crate: `dirs::config_dir()`).

## UI <-> backend contract (Tauri)

Commands (JS: `window.__TAURI__.core.invoke(name, args)`), all camelCase args:
- `get_state() -> { settings, profiles, folder, activities, version }`
- `save_settings({ settings }) -> { settings, profiles }` (backend persists, rebuilds profiles, emits `settings`)
- `open_session({ profileId, folder }) -> { id }`  (spawns the PTY; emits `session-opened`)
- `session_input({ id, data })` (string, utf-8), `session_resize({ id, cols, rows })`, `session_bell({ id })`, `close_session({ id })`
- `session_viewed({ id })` (user is looking at that session: clears the Done badge)
- `set_viewing_terminal({ viewing })`  (terminal tab visible)
- `pick_folder() -> Option<String>`; `set_expanded({ expanded })` (backend resizes the window); `quit()`

Events (JS: `window.__TAURI__.event.listen(name, cb)`), payload in `event.payload`:
- `activities` : `Activity[]` (snapshot, sorted, whenever it changes)
- `settings` : `{ settings, profiles }`
- `expanded` : `{ expanded }` (hotkey, tray, or second-instance window request)
- `open-settings` : no payload (tray settings request)
- `session-opened` : `{ id, profileId, displayName, glyph, folder, folderName, agent }`
- `session-output` : `{ id, data }` where data is base64 of the raw bytes
- `session-state` : `{ id, state }` (agent state: idle|working|needsInput|done)
- `session-exited` : `{ id, code }`

## Window model

One transparent, undecorated, always-on-top, skip-taskbar window, 980x96 collapsed-size budget; backend sizes it:
collapsed pill 180x32 (centered on the top edge of the chosen display), compact (activity showing) 360x32,
expanded 600x372. `set_expanded` grows/shrinks the OS window and keeps it centered on the top edge. The UI draws the island,
its glow and everything inside; areas outside the island are transparent. Hover opens it (when expand_on_hover), click always does,
the global hotkey toggles it, losing focus closes it (unless the terminal is in use).
