# Getting a plugin on the plugin list

The [plugin list](https://brick-bread.github.io/WNotch/plugins.html) on the website is a file in this repository, `site/plugins/registry.json`. Adding a plugin to it is a pull request. Once listed, the plugin gets an **Install** button on the website, and Notch can find it by id from **Settings > Plugins > Browse plugins**.

Read [plugins.md](plugins.md) first for how to write and package a plugin.

## What the list needs

Your plugin must:

1. Live in a public GitHub repository.
2. Publish its latest release with **exactly one `.zip` asset**, with `plugin.json` at the top of the zip (or inside a single folder in it). See [Publishing on GitHub](plugins.md#publishing-on-github).
3. Have a `plugin.json` whose `id` is the id you list. Notch checks this after downloading and refuses to switch on a plugin that turns out to be a different one.
4. Say honestly what it uses in `permissions` (see the manifest table in plugins.md).

## Your listing on the website

The website reads two optional files from the **root of your plugin's repository** (the default branch):

| File | Used for |
|---|---|
| `description.md` | The description on your card. Basic Markdown works: paragraphs, headings, lists, **bold**, *italic*, `code` and links. Long text is cut short with a "Show more" button. Up to 20 KB. |
| `logo.png` or `logo.webm` | The picture on your card, shown square (a silent, looping video for `.webm`). `logo.png` is tried first. |

Without them the card shows the `description` from your registry entry and a coloured tile with your plugin's first letter. GitHub caches raw files for a few minutes, so changes appear shortly after you push.

## The entry

Add an object to the `plugins` array:

```json
{
  "id": "yourname.build-status",
  "name": "Build status",
  "repository": "yourname/notch-build-status",
  "author": "Your Name",
  "description": "Shows the state of your latest CI run in the pill.",
  "tags": ["dev", "ci"],
  "permissions": ["network"],
  "apiVersion": 6
}
```

| Property | Required | Meaning |
|---|---|---|
| `id` | yes | The same id as in the plugin's `plugin.json`. Never changes. |
| `name` | yes | Shown on the website and in Notch. |
| `repository` | yes | `owner/repo` on GitHub. |
| `author`, `description` | no | Shown on the cards. Descriptions are cut at 400 characters. |
| `tags` | no | Up to 8 short words for the filter buttons. |
| `permissions` | no | The same words as in `plugin.json`. Shown before installing. |
| `apiVersion` | no | The plugin API the latest release was written for. Notch tells the user to update first when it is newer than theirs. |
| `verified` | no | Set only by the maintainers, after reading the plugin's source for that release. Do not set it yourself. |

Entries that are not usable (bad id, no repository, an id that is already listed) are skipped, and a check on the pull request fails with the reason.

## What happens when someone clicks Install

The website link is `notch://install?id=yourname.build-status`. It carries **only the id**. Notch looks the id up in this list, shows a window with the name, author, repository and permissions, and downloads the plugin only if the user presses Install. A plugin is never switched on without a separate decision. Nothing in the link can point Notch at a repository that is not on the list.

## Updates

Publish a new release. Notch checks plugins installed from GitHub for newer releases from Settings > Plugins > Check for updates. The list entry does not need to change unless the repository or id does. If a new release needs a newer plugin API, update `apiVersion`.

## Removing a plugin

Send a pull request that deletes the entry. Maintainers may also remove a plugin that misbehaves or whose repository no longer matches its entry.
