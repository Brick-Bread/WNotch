# Shared test fixtures

Inputs and the answers the C# Notch gives for them. The C# tests (`tests/Notch.Core.Tests/FixtureTests.cs`) produce the answers; the Rust port's tests read the same files and must give the same answers. This is how the port is held to the C# behaviour on the details: error messages, odd inputs, what a save writes.

## Updating

When C# behaviour changes on purpose, rewrite the answers and review the difference:

```
NOTCH_UPDATE_FIXTURES=1 dotnet test tests/Notch.Core.Tests --filter FullyQualifiedName~FixtureTests
git diff tests/fixtures
```

Without the variable, the C# tests fail when an answer differs from the file. To add a case, add an input, run with the variable, and check the new answer by eye.

## What is here

### `settings/`

`input/<name>.json` is loaded with `SettingsStore.Load` and saved again; `expected/<name>.json` is what the save writes. `expected/missing-file.json` is what a missing file gives. The Rust side must:

- write the same JSON, except that it keeps keys it does not know (C# drops them; see `older-version.json`), and that line endings may be CRLF on Windows;
- treat a widget `Id` of `*generated*` in an expected file as "any new 8-character lowercase hex id";
- take everything as C# does, including the surprising parts:
  - enum names are read ignoring case, and their numbers are accepted;
  - out-of-range numbers are kept as they are, because they are clamped where they are used;
  - **one unknown enum name anywhere, even a widget's width, or a value of the wrong JSON type, makes the whole file fall back to defaults**;
  - `null` for a list or a string is kept as `null`.

### `commands/`

`input.json` lists `notch://` links (`url`) and `notchctl` arguments (`args`). `expected.json` gives, for each, `ok` and either the command or the error message shown to the user. Commands are described as `{ "kind": … }` with durations in seconds and colours as `#rrggbb`.

## More to come

Each part of the port adds its fixtures here as it is ported, in the same shape (inputs, plus answers written by `FixtureTests`). Next up are webhook requests, hook payloads, terminal preset lines, duration text, hotkey chords, and the plugin registry and release feed.
