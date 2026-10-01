# Writing plugins for Notch

A plugin is a small .NET library that Notch loads at start. It can:

- show **activities** in the pill, with a glyph or image, text, a progress bar and a glow;
- show **cards** on a Plugins tab in the expanded notch, which can react to clicks;
- keep **settings** and files between runs, and write to a log.

This guide walks through a first plugin, then documents every part of the API. A complete working example is in [`samples/BreakReminder`](../samples/BreakReminder).

- [Before you start](#before-you-start)
- [Your first plugin](#your-first-plugin)
- [How a plugin is packaged](#how-a-plugin-is-packaged)
- [The manifest: plugin.json](#the-manifest-pluginjson)
- [Lifecycle](#lifecycle)
- [Activities: the pill](#activities-the-pill)
- [Cards: the Plugins tab](#cards-the-plugins-tab)
- [Settings and files](#settings-and-files)
- [Logging](#logging)
- [Threading and errors](#threading-and-errors)
- [Dependencies](#dependencies)
- [Developing and debugging](#developing-and-debugging)
- [Installing and sharing](#installing-and-sharing)
- [Publishing on GitHub](#publishing-on-github)
- [Compatibility](#compatibility)
- [Troubleshooting](#troubleshooting)

## Before you start

**Plugins are trusted code.** A plugin runs inside the Notch process with everything the user can do: files, network, other programs. There is no sandbox. For that reason Notch never runs a plugin just because it is installed; the user has to switch it on in Settings. Only install plugins whose source you trust, and say clearly what yours does.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a copy of Notch (installed, or built from this repository).

## Your first plugin

This plugin shows the time on a card and flashes a message in the pill when it starts.

**1. Create a class library.**

```
dotnet new classlib -n HelloNotch -f net10.0
```

**2. Reference Notch.Core.** Edit `HelloNotch.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Makes the build output a complete plugin folder; see "Dependencies". -->
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>

  <ItemGroup>
    <!-- The API lives in Notch.Core.dll, next to Notch.exe. Notch supplies it at run time,
         so it must not be copied into the plugin's folder (Private=false). -->
    <Reference Include="Notch.Core">
      <HintPath>$(LocalAppData)\Programs\Notch\Notch.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <None Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

If you work inside a clone of this repository, use a project reference instead, as the sample does:

```xml
<ProjectReference Include="..\..\src\Notch.Core\Notch.Core.csproj">
  <Private>false</Private>
  <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>
```

**3. Add `plugin.json`** next to the project file:

```json
{
  "id": "yourname.hello",
  "name": "Hello Notch",
  "version": "1.0.0",
  "description": "Shows the time on a card.",
  "assembly": "HelloNotch.dll",
  "apiVersion": 1
}
```

**4. Write the plugin.** Replace `Class1.cs`:

```csharp
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace HelloNotch;

public sealed class HelloPlugin : INotchPlugin
{
    private Timer? _timer;

    public void Start(IPluginHost host)
    {
        // A short notice in the pill. Transient activities go away by themselves.
        host.Activities.Publish(new Activity
        {
            Id = "hello",
            Tier = ActivityTier.Transient,
            Title = "Hello from a plugin",
            Glyph = "",
            Glow = new Glow(GlowColor.Violet, GlowPattern.Flash),
            Lifetime = TimeSpan.FromSeconds(4),
        });

        // A card on the Plugins tab, refreshed every second.
        _timer = new Timer(_ =>
        {
            try
            {
                host.Cards.Set(new PluginCard
                {
                    Id = "clock",
                    Label = "Time",
                    Value = DateTime.Now.ToString("T"),
                });
            }
            catch (Exception e)
            {
                host.Log.Error("Could not update the clock.", e);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop() => _timer?.Dispose();
}
```

**5. Build and run it** without installing anything:

```
dotnet build
"%LocalAppData%\Programs\Notch\Notch.exe" --plugin=bin\Debug\net10.0
```

Quit a running Notch from its tray icon first; a second copy exits immediately. `--plugin=` loads the plugin from that folder for this run only and treats it as enabled. Hover over the pill and open the **Plugins** tab to see the card.

## How a plugin is packaged

A plugin is one folder:

```
yourname.hello\
  plugin.json          the manifest (required)
  HelloNotch.dll       the assembly named in the manifest (required)
  HelloNotch.deps.json written by the build; lets Notch find the plugin's dependencies
  ...                  any libraries the plugin uses
```

Installed plugins live in `%AppData%\Notch\plugins\`, one folder each. The folder's name does not matter; the manifest's `id` identifies the plugin. Folders without a `plugin.json` are ignored.

The assembly must contain **exactly one public, non-abstract class that implements `INotchPlugin`** and has a public parameterless constructor. That class is the entry point.

## The manifest: plugin.json

Notch reads the manifest to list a plugin in Settings before running any of its code.

| Property | Required | Meaning |
|---|---|---|
| `id` | yes | Unique, permanent identifier. Lowercase letters and digits in groups separated by `.` or `-`, at most 64 characters, e.g. `yourname.build-status`. Prefix it with your name to avoid clashes. It names the plugin's data folder, so changing it loses the plugin's settings. |
| `name` | yes | Shown in Settings. |
| `assembly` | yes | File name of the plugin's `.dll`, in the same folder. No paths. |
| `apiVersion` | yes | The plugin API version the plugin was written for. Currently `1`. See [Compatibility](#compatibility). |
| `version` | no | The plugin's own version, shown in Settings. |
| `author` | no | Shown in the plugin's tooltip in Settings. |
| `description` | no | Shown in the plugin's tooltip in Settings. |

Property names are not case-sensitive. Comments and trailing commas are accepted. Unknown properties are ignored.

A manifest that cannot be used (missing property, invalid id, an id another installed plugin already has, an `apiVersion` newer than Notch offers) is listed in Settings, greyed out, with the reason.

## Lifecycle

```csharp
public interface INotchPlugin
{
    void Start(IPluginHost host);
    void Stop();
}
```

1. **Discovery.** At start, and each time the settings window opens, Notch reads the manifests in the plugins folder. No plugin code runs.
2. **Start.** For each plugin the user has enabled, Notch loads the assembly, creates the entry-point class and calls `Start(host)`. This happens shortly after the app starts, or when the user enables the plugin and saves settings.
3. **Running.** The plugin does its work on its own timers, tasks and event handlers, calling into `host`.
4. **Stop.** When the user disables the plugin or Notch exits, Notch calls `Stop()`. Afterwards it removes any activities and cards the plugin left behind, and ignores further calls the plugin makes on `host`.

Things to know:

- `Start` should return quickly. Start long work on a background task instead of doing it inline.
- If the constructor or `Start` throws, the plugin is marked as failed, everything it published is removed, and the error is shown in Settings and written to the log. To retry, the user switches the plugin off and on again.
- A plugin that is disabled and enabled again in the same session gets a **new instance** of its class, but its assembly stays loaded, so `static` fields keep their values. Do not keep state in statics.
- Because assemblies stay loaded, Notch has to be restarted to pick up a new build of a plugin that has already been started.
- When Notch updates itself it restarts, which stops and starts all plugins.

`IPluginHost` is the plugin's only connection to Notch:

| Member | Purpose |
|---|---|
| `Manifest` | The plugin's own `plugin.json`. |
| `PluginDirectory` | The folder the plugin was loaded from. Read-only by convention; an update replaces it. |
| `DataDirectory` | A folder for the plugin's own files. Created on first access. |
| `Activities` | [Activities: the pill](#activities-the-pill) |
| `Cards` | [Cards: the Plugins tab](#cards-the-plugins-tab) |
| `Settings` | [Settings and files](#settings-and-files) |
| `Log` | [Logging](#logging) |

## Activities: the pill

An activity is one thing the pill can show. Several can exist at once (music playing, a timer running, your plugin's status); Notch shows the most important one.

```csharp
host.Activities.Publish(new Activity
{
    Id = "build",
    Tier = ActivityTier.Ongoing,
    Title = "Building",
    Detail = "42%",
    Glyph = "",
    Glow = new Glow(GlowColor.Cyan, GlowPattern.Breathe, 0.8),
});

host.Activities.Remove("build");
```

| `Activity` property | Meaning |
|---|---|
| `Id` | Required. Identifies the activity within your plugin. Publishing the same id again updates it in place. Use a small, fixed set of ids. |
| `Tier` | Required. Its priority and behaviour; see below. |
| `Title` | Required. The main text. |
| `Detail` | Secondary text on the right, e.g. a time or a state. |
| `Glyph` | One character from the [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) font, e.g. `""`. |
| `Image` | PNG or JPEG bytes shown instead of the glyph, e.g. a cover or avatar. Drawn at 22 × 22. |
| `Progress` | 0 to 1. On a `Transient` activity it draws a bar in place of `Detail`. |
| `Glow` | The light around the pill while this activity is the one showing. Null for none. |
| `Lifetime` | `Transient` only: how long it shows. Default 2 seconds. |

**Tiers** decide which activity the pill shows. A higher tier always wins; within a tier, the most recently published wins. Updating an activity does not count as publishing it again, so a ticking progress value will not push your activity in front of others.

| Tier | Use for | Goes away |
|---|---|---|
| `Ongoing` | Background state: something is running. | When you remove it. |
| `Attention` | Something needs the user: finished, failed, waiting for input. | When you remove it. |
| `Transient` | A brief notice. The pill enlarges for it. | By itself, after `Lifetime`. |

Use `Attention` sparingly and always give the user a way to clear it (a card click, or remove it once the condition has passed), otherwise it hides everything in the lower tier indefinitely.

**Glow** is `new Glow(color, pattern, strength)`. `GlowColor` has presets (`White`, `Blue`, `Cyan`, `Green`, `Amber`, `Orange`, `Red`, `Yellow`, `Violet`) or takes RGB bytes: `new GlowColor(0xFF, 0x4F, 0xA3)`. `strength` is 0 to 1 and defaults to 1.

| Pattern | Looks like | Convention in Notch |
|---|---|---|
| `Steady` | Constant light | Done, idle but present |
| `Breathe` | Slow rise and fall | Work in progress |
| `Pulse` | Quick, strong beats | Needs the user |
| `Flash` | Bright burst that settles | One-off event |
| `Audio` | Follows the loudness of what the PC is playing | Media |

The user can change the overall glow brightness, or turn glows off, in Settings; plugins do not need to handle that.

**Ids are private to your plugin.** Notch stores your activity as `plugin.<plugin id>.<your id>`, so `"status"` in your plugin cannot collide with the app's activities or another plugin's. Pass your own short id to `Remove`. `Clear()` removes everything your plugin has published.

## Cards: the Plugins tab

The expanded notch gets a **Plugins** tab while at least one plugin has a card. Cards are laid out three to a row in the order they were first added, across all plugins.

```csharp
host.Cards.Set(new PluginCard
{
    Id = "status",
    Label = "Build",
    Value = "Passing",
    Detail = "main, 4 minutes ago",
    Progress = null,
    Clicked = () => OpenBuildPage(),
});
```

| `PluginCard` property | Meaning |
|---|---|
| `Id` | Required. Identifies the card within your plugin. `Set` with the same id replaces the card in place. |
| `Label` | Required. Small caption at the top. |
| `Value` | The headline, large, on one line. Keep it short; long text is cut off with an ellipsis. |
| `Detail` | Smaller text below the value. It wraps; about two lines fit. |
| `Progress` | 0 to 1 draws a bar along the bottom. Null for no bar. |
| `Clicked` | Called when the user clicks the card. Null makes the card non-interactive. |

A card is plain data: the plugin describes it and Notch draws it in its own style, so plugins need no UI framework and keep working when the notch's look changes. To change a card, call `Set` again with the new values. `Remove(id)` and `Clear()` take cards away.

`Clicked` runs on a background thread, not the UI thread. An exception it throws is logged and otherwise ignored.

There is currently no way to learn whether the tab is on screen, so update cards on a modest schedule: once a second at most, and less often for anything that costs something to compute or fetch.

## Settings and files

`host.Settings` stores small values as JSON in `settings.json` inside the plugin's data folder:

```csharp
int minutes = host.Settings.Get("intervalMinutes", 50);   // 50 if missing or not a number
host.Settings.Set("intervalMinutes", minutes);
host.Settings.Remove("obsoleteKey");
```

- `Get<T>(key, fallback)` returns the fallback when the key is missing or holds a value of the wrong type. It never throws for bad data.
- `Set<T>(key, value)` accepts anything `System.Text.Json` can serialize and saves immediately.
- There is no settings UI for plugins yet. Users change a plugin's options by editing `%AppData%\Notch\plugin-data\<plugin id>\settings.json` and restarting the plugin. To make options discoverable, write the defaults back on start, as shown above, so the file lists them. Validate what you read: clamp numbers, check strings.

For anything bigger (caches, downloaded files, databases) use `host.DataDirectory`, which is `%AppData%\Notch\plugin-data\<plugin id>\`. It survives plugin updates and Notch updates and is not removed when Notch is uninstalled.

Do not write into `host.PluginDirectory`; replacing the plugin with a new version discards it.

## Logging

```csharp
host.Log.Info("Connected to the build server.");
host.Log.Warn("No token configured; showing public builds only.");
host.Log.Error("Could not fetch builds.", exception);
```

All plugins share `%LocalAppData%\Notch\plugins.log`. Each line carries a timestamp, the level and the plugin's id; Notch itself (as `notch`) records every plugin start, stop and failure there. The file is rotated to `plugins.log.old` at about 1 MB, so do not log on every tick.

## Threading and errors

- `Start` and `Stop` are called on a background thread, one at a time.
- Every member of `IPluginHost` and its parts is **thread-safe**. Call them from any thread, timer or task; there is no UI thread to marshal to.
- Exceptions thrown from `Start`, `Stop` and `Clicked` are caught and logged by Notch.
- **Exceptions on threads you start are yours.** An unhandled exception in a timer callback, a `Task.Run` body that is never awaited, or an `async void` method ends the whole app, as in any .NET program. Wrap the body of every callback in `try`/`catch` and log the error, as the examples do.
- After `Stop`, calls on the host do nothing, so a timer tick that was already in flight is harmless. Still dispose timers and cancel work in `Stop`.
- Never block for long inside `Start`, `Stop` or `Clicked`.

## Dependencies

A plugin can use NuGet packages and other libraries. With `<EnableDynamicLoading>true</EnableDynamicLoading>` the build copies them into the output folder and writes a `.deps.json` that tells Notch where they are, including native libraries.

Each plugin is loaded into its own `AssemblyLoadContext`. Libraries found in the plugin's folder are loaded privately for that plugin, so two plugins can use different versions of the same package without conflict. Anything not found there comes from Notch: the .NET runtime and `Notch.Core`.

Two rules follow:

- **Never ship `Notch.Core.dll` with a plugin.** `<Private>false</Private>` on the reference keeps it out of the output. (Notch ignores a copy if one slips in, but it is dead weight.)
- Plugins may target `net10.0` or `net10.0-windows`. Notch is a 64-bit Windows app, so native dependencies must be available for `win-x64`.

## Developing and debugging

Run a plugin from its build output with `--plugin=<folder>`; the flag can be repeated for several plugins. Such plugins are always enabled, show as "(development)" in Settings, and do not change the saved list of enabled plugins.

From a clone of this repository, building the solution also builds the sample:

```
dotnet build
dotnet run --project src/Notch.App -- --plugin=samples/BreakReminder/bin/Debug/net10.0 --pin-open --tab=plugins
```

Useful flags to combine with it (see the README for all of them):

| Flag | Effect |
|---|---|
| `--pin-open` | Keep the notch expanded, to watch cards |
| `--tab=plugins` | Start on the Plugins tab |
| `--demo` | Fake media and activities, to see how yours competes for the pill |
| `--display=2` | Use another display, away from an installed copy |

A debug build of Notch runs side by side with an installed copy; two release copies cannot.

To use a debugger, start Notch with `--plugin=` and attach to the `Notch` process, or set Notch.exe as the plugin project's start program with the flag as its argument. Breakpoints in plugin code work as long as the plugin's `.pdb` is in its folder.

Rebuilding the plugin while Notch has it loaded fails because the `.dll` is locked; quit Notch first.

## Installing and sharing

There are two ways to install a plugin:

- **From GitHub.** In Settings, under **Plugins**, type the repository (`owner/name`, or paste its link) into the box and press **Install**. Notch downloads the plugin from the repository's latest release, puts it in the plugins folder and ticks it in the list. Press **Save** to switch it on. See [Publishing on GitHub](#publishing-on-github) for what the repository has to offer.
- **By hand.** Copy the plugin's folder into `%AppData%\Notch\plugins\` (Settings has an **Open folder** button for it), then open Settings, tick the plugin and save.

Either way it starts as soon as the settings are saved.

To build the folder to share:

```
dotnet publish -c Release -o dist\yourname.hello
```

Zip the resulting folder. `.pdb` files are optional.

To update a plugin installed from GitHub, install it again the same way. If the plugin has been running, the new version is set aside and takes over the next time Notch starts; Settings says so. To update by hand, quit Notch, replace the folder's contents and start Notch again. To remove a plugin, switch it off, quit Notch and delete its folder; its data folder under `plugin-data` can be deleted too.

## Publishing on GitHub

A plugin is installable from GitHub when its repository's **latest release has exactly one `.zip` attached** and that zip is the plugin folder. Users then install it by typing `owner/name` into Settings.

The rules, precisely:

- Notch looks at the latest release only: not drafts, not pre-releases, not the source code.
- The release must have exactly one asset whose name ends in `.zip`. Other assets (notes, checksums) are fine. The "Source code (zip)" link GitHub adds to every release does not count and is not used.
- Inside the zip, `plugin.json` must be at the top level, or inside one top-level folder (so zipping either the folder or its contents works). Everything next to it is installed; nothing outside it is.
- The manifest must be valid and the assembly it names must be in the zip.
- The zip may be at most 64 MB, and unpack to at most 256 MB and 2000 files.
- The plugin is installed into a folder named after its `id`. If a plugin with the same `id` is already installed, it is replaced, so keep the `id` the same across releases and raise `version`.

Notch checks the download against the size and SHA-256 digest GitHub lists for it, and only downloads from `github.com`. That guards against a corrupted or swapped download; it says nothing about whether the plugin itself is trustworthy, which is why an installed plugin still has to be switched on by the user.

**By hand:** build with `dotnet publish -c Release -o dist\yourname.hello`, zip that folder, create a release on the repository's Releases page and attach the zip.

**Automatically:** add this workflow as `.github/workflows/release.yml` in the plugin's repository. Pushing a tag such as `v1.0.0` then builds the plugin and publishes a release with the zip attached. Adjust the two paths at the top.

```yaml
name: release

on:
  push:
    tags: ['v*']

permissions:
  contents: write

env:
  PROJECT: HelloNotch.csproj        # the plugin's project file
  ZIP_NAME: hello-notch.zip         # what the attached file is called

jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      # The plugin compiles against Notch.Core.dll, which is not on NuGet. Take it from the
      # Notch source at the version the plugin targets, and point the project's reference at it.
      - uses: actions/checkout@v4
        with:
          repository: Brick-Bread/WNotch
          ref: v0.4.0
          path: notch
      - run: dotnet build notch/src/Notch.Core -c Release -o notch-core

      - run: dotnet publish $env:PROJECT -c Release -o dist -p:NotchCorePath=${{ github.workspace }}\notch-core\Notch.Core.dll
        shell: pwsh

      - name: Zip and release
        shell: pwsh
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          Remove-Item dist\*.pdb -ErrorAction SilentlyContinue
          Compress-Archive -Path dist\* -DestinationPath $env:ZIP_NAME
          gh release create $env:GITHUB_REF_NAME $env:ZIP_NAME --title $env:GITHUB_REF_NAME --generate-notes
```

For that build to find `Notch.Core.dll` both on your PC and in the workflow, let the reference in the project file take its path from a property:

```xml
<PropertyGroup>
  <NotchCorePath Condition="'$(NotchCorePath)' == ''">$(LocalAppData)\Programs\Notch\Notch.Core.dll</NotchCorePath>
</PropertyGroup>

<ItemGroup>
  <Reference Include="Notch.Core">
    <HintPath>$(NotchCorePath)</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

Build against the oldest Notch release your plugin should work with (`ref:` above), and set `apiVersion` in the manifest to match; see [Compatibility](#compatibility).

To check a release before telling anyone about it, install it yourself from Settings. Every reason an install can fail is reported there in a sentence.

This repository does the same for its sample: each Notch release has the [Break reminder](../samples/BreakReminder) plugin attached as a zip, so entering `Brick-Bread/WNotch` in Settings installs it.

## Compatibility

The plugin API is `Notch.Core.Plugins` plus the types in `Notch.Core.Activities` that it uses (`Activity`, `ActivityTier`, `Glow`, `GlowColor`, `GlowPattern`). Everything else in `Notch.Core.dll` is the app's own code: it is visible because it shares the assembly, but it can change in any release. Do not use it.

`PluginApi.Version` is the API version a build of Notch offers. It goes up when the API gains something. Notch runs plugins whose `apiVersion` is equal to or lower than its own and refuses those that ask for a newer one, telling the user to update Notch. Set `apiVersion` to the lowest version that has everything your plugin uses.

| API version | Changes |
|---|---|
| 1 | First version: activities, cards, settings, log. |

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Installing from GitHub says the release has no .zip | The latest release has no asset ending in `.zip`. GitHub's automatic "Source code" archives are not assets. |
| Installing says the download is not a Notch plugin | `plugin.json` is nested more than one folder deep in the zip, or missing. |
| The plugin is not listed in Settings | Its folder is not directly inside `%AppData%\Notch\plugins\`, or has no `plugin.json`. Close and reopen Settings after copying it. |
| Listed but greyed out, "Cannot be used: …" | The manifest is invalid; the message says what is wrong. |
| "Failed to start: … has no public class that implements INotchPlugin" | The class is not `public`, is abstract, or `assembly` names the wrong `.dll`. |
| "Failed to start: Could not load file or assembly …" | A dependency is missing from the folder. Build with `EnableDynamicLoading` and copy the whole output folder. |
| "Failed to start: Method not found …" or "Could not load type …" | The plugin was built against a newer `Notch.Core.dll` than the installed Notch has. |
| Enabled, but nothing in the pill | A higher-tier or more recent activity is showing; the user may have a fullscreen app; check `plugins.log`. |
| No Plugins tab | No plugin currently has a card. |
| Notch closes unexpectedly | An unhandled exception on a plugin's own thread. See `%LocalAppData%\Notch\plugins.log` and Windows Event Viewer, and wrap callbacks in `try`/`catch`. |
