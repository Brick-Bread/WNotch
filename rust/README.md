# Notch in Rust

The Rust port of Notch, built with Tauri 2 and a Svelte UI. It replaces the C# app once it does everything that one does; [`PARITY.md`](PARITY.md) lists what that means and tracks the progress.

| Folder | What it is | Ported from |
|---|---|---|
| `crates/notch-core` | Logic that does not touch Windows | `src/Notch.Core` (minus plugins) |
| `crates/notch-platform` | Windows integrations | `src/Notch.Platform` |
| `crates/notch-ipc` | The protocol to the .NET plugin host | new |
| `crates/notch-app` | `Notch.exe` | `src/Notch.App` |
| `crates/notch-setup` | The installer, updater and uninstaller | `src/Notch.Setup` |
| `crates/notchctl` | `notchctl.exe` | `src/Notch.Cli` |
| `crates/notch-hook` | `Notch.Hook.exe` (built as `notch-hook.exe`, renamed when packaged) | `src/Notch.Hook` |
| `ui` | The pages: the notch, Settings, Setup (Svelte 5, Vite, TypeScript) | the WPF views |

## Building

The UI is built first, because the app embeds it:

```
cd rust/ui
npm ci
npm run build
```

On Windows:

```
cd rust
cargo build --workspace
cargo test --workspace
```

### From Linux

Notch only runs on Windows, but the Windows build can be checked from Linux with [cargo-xwin](https://github.com/rust-cross/cargo-xwin) (needs `clang`, `lld` and `llvm`):

```
cargo xwin clippy --workspace --all-targets --target x86_64-pc-windows-msvc
cargo xwin build --workspace --target x86_64-pc-windows-msvc
```

Crates that do not touch Windows (`notch-core`, `notch-ipc`) also build and test natively: `cargo test -p notch-core -p notch-ipc`.

## Tests against the C# behaviour

`tests/fixtures` at the repository root holds inputs and the answers the C# code gives for them; see its README. The Rust tests read the same files.
