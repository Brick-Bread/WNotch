# Porting C# to Rust: conventions

How code moves from `src/` (C#) to `rust/`. The goal is the same behaviour, checked by tests, in code that reads like the C# it came from.

## Behaviour

- **Port, don't redesign.** Keep the C# logic, limits, defaults, order of checks and user-visible text (error messages, notices, labels) exactly. Where C# does something odd, keep it and say so in a comment. Change behaviour only where the plan or `PARITY.md` marks it **[changed]**, or where C# would crash; then add a **[changed]** note to the matching `PARITY.md` line.
- **C# details to carry over:**
  - `string.Length` and substring limits count UTF-16 units: use `encode_utf16().count()`.
  - `Math.Round` rounds halves to even: use `f64::round_ties_even`.
  - `double.TryParse`/`int.TryParse` with the invariant culture accept ASCII digits only.
  - `StringComparison.OrdinalIgnoreCase` is close to `eq_ignore_ascii_case` for ASCII text and `to_lowercase()` comparison otherwise.
  - `.Trim()` trims Unicode whitespace, as `str::trim` does.
- **Files shared with the C# app** (anything under `%AppData%\Notch` or `%LocalAppData%\Notch`, and the agents' hook files) are read with `crate::json`, which follows System.Text.Json's rules. Write them in the same shape. An installed copy upgrades in place, so the Rust app reads what the C# app wrote.

## Structure

- One Rust module per C# folder, one file per C# file (or per close group of files). Start each file with `//! Ported from \`src/...\`.` and a one-line summary.
- Types: C# records become structs with `pub` fields; enums become enums; `static` classes become modules with free functions or constants. Names in `snake_case`/`CamelCase` as Rust expects; keep the C# names recognisable.
- Time:
  - Durations are `std::time::Duration`.
  - Wall-clock instants are `chrono::DateTime<chrono::Utc>`.
  - There are no timers or threads in `notch-core`. Where C# uses a `TimeProvider` timer, take `now` as an argument and expose when the next call is due (for example `next_deadline()` and `tick(now)`).
- Errors a user sees are `Result<T, String>` with the C# message. Internal failures use `Option` or a small error enum.
- No `unwrap()` outside tests, except where a comment explains why it cannot fail.

## Comments

Keep the C# doc comments, converted to `///` and Markdown, and the C# inline comments that explain why. Match the C# density; don't add comments that restate the code.

## Tests

- Port every C# test for the code you port (same cases, same names in `snake_case`) into a `#[cfg(test)] mod tests` in the same file, or a `tests.rs` next to it when long.
- Tests that need files use `tempfile`.
- Inputs whose answers matter across the two apps go in `tests/fixtures` (see its README); `crate::fixtures` reads them.

## Checks

From `rust/`, all of these must pass before a commit:

```
cargo fmt --all
cargo clippy -p notch-core --all-targets -- -D warnings
cargo test -p notch-core
```

Windows-only crates are checked from Linux with `cargo xwin clippy --target x86_64-pc-windows-msvc`.

## Commits

One commit per C# folder or close group, with a message saying what was ported and anything that differs. Tick the `PARITY.md` lines the new code and its tests cover.
