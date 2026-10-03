# Rust parity: working rules for every agent

Goal: the Rust/Tauri app (`src-tauri/`, `crates/notch-core/`, `ui/`) must match the C# app in `src/` 1:1 in features,
behaviour, look and settings, on Windows and natively on Linux (where Windows-only APIs get a Linux equivalent, or a clean
no-op with a log line when none exists, never a compile error). The C# code is the specification: read the files named in your
task before writing anything and port the behaviour, including edge cases and the unit tests under `tests/Notch.Core.Tests`.
Read `docs/rust-rewrite.md` for the existing contract (activities, commands, events, window model) and the code that exists.

Many agents work in this tree at the same time. Rules:
- **No git commands** (no add/commit/checkout/stash). The lead commits.
- Own your files. New Rust goes in new modules: logic with no OS/UI in `crates/notch-core/src/<name>.rs` (add one `pub mod <name>;` line to
  `crates/notch-core/src/lib.rs`), app glue in `src-tauri/src/<name>.rs`. New UI goes in new files `ui/<name>.js` (+ `ui/<name>.css`, linked from index.html).
- Shared files you must touch minimally: `src-tauri/src/main.rs` (mod line, `generate_handler!` entries, setup call),
  `src-tauri/Cargo.toml`/`crates/notch-core/Cargo.toml` (dependency lines; use `cfg(windows)`/`cfg(target_os = "linux")` target tables for OS crates),
  `ui/index.html`, `ui/style.css`, `ui/main.js`, `ui/home.js`, `ui/settings-panel.js`, `src-tauri/capabilities/default.json`. For each of those: re-read the file right
  before every edit, make small single-purpose edits with the Edit tool (never rewrite the whole file), and re-read after to confirm other agents' lines survived.
  Do not edit `settings.rs`: all settings fields for parity already exist (see AppSettings); if you truly need another, tell the lead in your report.
- Activity ids are fixed by C#: `media`, `hud.volume`, `hud.audio-output`, `hud.brightness`, `hud.power`, `hud.bluetooth`, `hud.caps-lock`, `agent.<session>`, timer ids as in C#.
  Settings toggles suppress them through `AppSettings::suppressed_activity_ids()`; the app calls `hub.set_suppressed(...)` (add to ActivityHub if missing).
- Cargo: run from the PowerShell tool (Git Bash's link.exe breaks MSVC): `$env:Path="$env:USERPROFILE\.cargo\bin;$env:Path"`. Use your own target dir to avoid
  lock contention: `$env:CARGO_TARGET_DIR="$env:TEMP\notch-target-<yourname>"`. Other agents are mid-edit in other modules: if the build breaks in a file that is not yours,
  wait a minute and retry (up to a few times); never fix or revert other agents' files. Do not run the app from the shared `target/` dir.
- Quality bar: idiomatic Rust, doc comments in the tone of the C# summaries, unit tests for all pure logic (port the C# tests), `cargo clippy` clean for your files,
  no unwrap/expect in non-test paths, no dead code, no TODO placeholders for in-scope behaviour. UI: match the C# look (src/Notch.App/Shell/NotchWindow.xaml, Themes/*.xaml),
  dark+light, accessible, no framework; verify visually with the mock mode (`ui/mock.js`, `?mock`) in the Browser pane tools when you add UI, and extend the mock for your commands/events.
- Your final report: files written/changed, commands and events added (names, payloads), exact lines the lead must add if any, what was verified and how, and anything you could not do.
