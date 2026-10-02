# Automation: links, command line and webhook

Three ways to tell a running Notch what to do. They share one list of commands, so each one accepts exactly the same things.

| Command | What it does |
|---|---|
| `timer 5m` | Starts the timer. Lengths like `90s`, `1:30`, `1h20m`; up to 24 hours. |
| `timer stop` / `pause` / `resume` | Controls the running timer. |
| `toast "Build done"` | A short notice in the pill. Options: `detail`, `glyph`, `color` (a name such as `green`, or `#rrggbb`), `lifetime` (such as `5s`, at most 30 s). |
| `open stats` | Opens the notch on a tab: `home`, `terminal`, `stats`, `shelf`, `plugins`, `clipboard`. |
| `install <plugin-id>` | Asks to install a plugin from the plugin list. Notch shows a confirmation window first. |
| `plugin <plugin-id> --enable true` | Switches an installed plugin on or off. Switching on asks first. |
| `palette`, `settings` | Opens the command palette or Settings. |

## notch:// links

`notch://timer?d=5m`, `notch://toast?title=Done&color=green`, `notch://open?tab=stats`, `notch://install?id=acme.build-status`.

The installer registers the `notch` scheme for your account. If Notch is not running, the link starts it. A link can only do what the table above lists: unknown commands and malformed values are ignored, and anything that changes what runs on your computer (installing or switching on a plugin) shows a window and needs your click.

## notchctl

`notchctl.exe` sits in the Notch folder (`%LocalAppData%\Programs\Notch`). The installer can add the folder to your PATH.

```
notchctl timer 5m
notchctl toast "Build done" --detail "all green" --color green
notchctl open stats
notchctl install acme.build-status
notchctl webhook          # shows the webhook address and token
```

Exit code 0 means Notch did it, 1 means Notch refused (the reason is printed), 2 means Notch is not running. Add `--start` to start Notch first.

## Webhook

For programs that cannot run a command: CI jobs, scripts, Home Assistant, a phone shortcut on your own network through a tunnel you trust.

**Off by default.** Switch it on in Settings > Automation. Notch makes a random token and shows it there (and `notchctl webhook` prints it).

Safety, in short:

- It listens on `127.0.0.1` only: nothing on your network can reach it.
- Every request needs the header `Authorization: Bearer <token>`.
- Requests that carry an `Origin` header (anything from a web page) are refused, and so are requests whose `Host` is not `127.0.0.1` or `localhost` on the port. A web page cannot talk to it, even by tricking your browser's DNS.
- Bodies are limited to 64 KB, requests to 30 per second, and 20 activities that stay until removed.
- Installing or switching plugins is not possible through the webhook.

All bodies are JSON. Answers are JSON `{"ok": true, "message": "..."}`; refusals use a 4xx status.

### POST /v1/activity

Shows or updates an activity in the pill. Posting the same `id` again updates it.

```
curl -H "Authorization: Bearer TOKEN" ^
     -d "{\"id\":\"deploy\",\"title\":\"Deploying\",\"detail\":\"step 2 of 5\",\"tier\":\"ongoing\",\"progress\":0.4,\"glow\":{\"color\":\"cyan\",\"pattern\":\"breathe\"}}" ^
     http://127.0.0.1:47890/v1/activity
```

| Field | Meaning |
|---|---|
| `id` | Letters, digits, `.`, `-`, `_`, at most 64. Shown in Notch as `webhook.<id>`. |
| `title` | 1 to 120 characters. |
| `detail`, `glyph` | Optional. The glyph is one symbol or a Segoe Fluent Icons character. |
| `tier` | `transient` (default: goes away by itself), `ongoing` (stays until removed) or `attention` (stays, wants the user). |
| `progress` | 0 to 1. Draws a bar. |
| `glow` | A colour (`"green"`, `"#ff8800"`) or `{ "color", "pattern", "strength" }`. Patterns: `steady`, `breathe`, `pulse`, `flash`, `audio`. |
| `lifetimeMs` | For transient ones, up to 5 minutes. |

### DELETE /v1/activity/{id}

Removes an activity you published.

### POST /v1/notify

A short notice: `{ "title": "Build done", "detail": "all green", "color": "green" }`.

### POST /v1/command

Any command from the table above: `{ "command": "timer", "d": "25m" }`.

PowerShell:

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47890/v1/notify `
  -Headers @{ Authorization = "Bearer $token" } `
  -Body (@{ title = "Backup finished"; color = "green" } | ConvertTo-Json)
```
