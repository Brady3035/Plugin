# Moonbase for Silo

This is the Silo backend of Moonbase, the server plugin for the Moonfin apps. It's a native Silo plugin written in Go against the public [silo-plugin-sdk](https://github.com/Silo-Server/silo-plugin-sdk). It doesn't use Silo's Jellyfin-compatible API, and apart from a copy of the Jellyfin admin page it shares no code or build with the Jellyfin and Emby plugins in this repo.

It covers the parts of Moonbase that Silo's plugin API can support:

- Settings sync: each Silo profile keeps its own Moonfin settings across devices, with admin defaults.
- MDBList ratings and the TMDB episode, season, next episode, person link and thumbnail routes.
- Server messages from the admin.
- The Moonbase admin settings, in Silo's own plugin settings and on the Moonbase page.

The rest needs things Silo's plugin API doesn't offer. See Known limits below.

## Supported versions

| | Version |
|---|---|
| Silo server | `9b77105` (#3021, 2026-10-28) or newer. Older builds don't verify the profile or the acting admin on plugin routes, see Known limits. |
| Plugin SDK | `v0.30.0`, the version that server pins |
| Platforms | `linux/amd64` (tested live), `linux/arm64` (builds, not tested on hardware) |

## Install

1. Download `Moonbase.Silo-<version>-linux-amd64` (or `-linux-arm64`) from the release, and check it against `SHA256SUMS`.
2. In Silo's admin pages, open Plugins and install it from a file. Silo runs the file once with `manifest` to read it, then starts it.
3. Set it up in **Settings** on the plugin's page in Silo.

The same upload works from a shell with an admin token:

```sh
curl -X POST "$SILO/api/v2/admin/plugins/uploads" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -F "archive=@Moonbase.Silo-<version>-linux-amd64;type=application/octet-stream"
```

Uploading a newer file replaces the installed one and keeps its installation id and its data.

## Settings

Moonbase's settings are in Silo's own plugin page (Integrations > Plugins > Moonbase), in the **Settings** cards, with the same settings, labels, help text and grouping as the Jellyfin admin page:

- **Moonbase**: settings sync and the server-wide MDBList and TMDB keys.
- One card per feature Silo can't run (Seerr and push notifications, charts, recommendations and studio logos, anime markers, games, web client). Each says why, and keeps its settings folded away.
- **User Defaults**, one card per sub-tab of the Jellyfin page (Account, General, Appearance, Home Screen, Libraries, Details Page, Playback, Integrations), with that sub-tab's groups as sections (folded on the longer cards) and the same "Not set (user decides)" choices. Each card lists anything of its own that's on the Moonbase page instead.

Fields hide and show with the page's rules where Silo's form can express them. The segment actions, bottom bar tabs and seasonal holidays, which the page builds from several controls, are plain fields here too.

`tools/genform` builds that form from `web/configPage.html`, an unedited copy of `Jellyfin/backend/Pages/configPage.html` that's refreshed by hand (see Building). It writes `config_schema.json`, which goes into the manifest, and `internal/api/form_gen.go`, which the plugin uses to read the values. CI fails if they're out of date with the page.

Saving a card restarts the plugin, which is how Silo hands a plugin new settings. Silo keeps a secret field's stored value when it's saved blank, so use the field's clear option to remove a key. Silo also shows its own Check Connection button on every card, and for Moonbase it answers that connection checks aren't supported.

A few things don't fit Silo's form and stay on the **Moonbase** page, which opens from the menu at the top right of the plugin page or from Plugin Apps in the admin menu:

- Messages, Apply Defaults To Users and Broadcast.
- Test key and Clear cache for MDBList.
- Settings with drag-and-drop or pick lists: the home row layout, per-row image types, detail button, player button and metadata order, detail sections, rating sources, Seerr rows, and the library, collection and genre pickers.

The Moonbase page is the Jellyfin admin page itself, with the same tabs, fields, search and Save button, in Jellyfin's dark style. `web/host.js` stands in for the Jellyfin dashboard it expects. It edits the same settings as Silo's form: what it saves goes back into Silo's store, so the two never disagree. It says "Silo" where the Jellyfin copy says "Jellyfin", and sections for features Silo can't run say so at the top. Lists it would fill from the library stay empty, because Silo doesn't let plugins read library items, and the user picker lists accounts that have synced settings.

Silo signs a plugin page in for five minutes and renews that while the page keeps calling its routes and the Silo sign-in is still good. If Save fails anyway, the page says so and keeps what you tried to save. Open Moonbase again in the same browser tab within an hour and those changes come back, ready to save.

Both API keys reach every signed-in user through `Settings/Resolved`, the same as on Jellyfin, since Moonfin calls TMDB directly with the key for some rows. Don't use keys here you wouldn't hand to your users.

## Where the routes are

Silo serves plugin routes under the plugin's installation id, not at the server root:

```
https://silo.example/api/v2/plugin-content/plugins/<installation id>/Moonfin/...
```

`/api/v1/plugins/<installation id>/Moonfin/...` is an older alias for the same thing. Clients find the installation id with `GET /api/v2/settings/plugins`, which lists Moonbase as `plugin_id` `moonfin.moonbase`. Requests use Silo's own headers: `Authorization: Bearer <token>`, plus `X-Profile-Id` (and `X-Profile-Token` for a PIN-locked profile). Silo checks the profile, and its PIN proof, before the request reaches Moonbase, and passes the verified profile on as `X-Silo-Profile-Id`. The routes only answer on Silo's native port (8090 by default). Moonbase doesn't use or need the Jellyfin-compatible port.

## Known limits

These come from Silo's plugin API, not from the plugin.

- **No live updates.** Silo can't stream a plugin's response or pass a plugin's events to apps, so a settings change on one device reaches the others at their next sign-in or manual sync. `Settings/Stream` answers 501.
- **Older Silo builds.** Before #3021, Silo didn't pass plugins the profile id, didn't check `X-Profile-Token` on plugin routes and marked every profile of an admin account as admin. On those builds settings are kept per profile name, and anyone signed in to an account can read and change its PIN-locked profiles' Moonfin settings. Settings stored that way aren't carried over: on a newer Silo each profile starts a new record keyed by its id, and Moonfin fills it from the device at the next sign-in.
- **No library items.** Silo lets plugins list the libraries an account may see, but not the items in them. So similar items, the seasonal row and the media bar aren't available, and the Moonbase page's library pickers stay empty.
- **No plugin icon.** Silo's plugin pages don't take icons from plugins, so Moonbase shows as "MO".
- **Admin routes need the primary profile.** Silo marks a request as admin only for an admin account acting through its primary profile, the rule its own admin API uses.
- **Storage is small.** Silo gives each plugin 256 keys of up to 256 KiB. Each profile with synced settings takes one key, so a server can hold settings for about 240 profiles.
- **Deleted users leave their settings behind.** Silo doesn't tell plugins when an account or profile is deleted.

## Data, backups and removal

Moonbase's settings live in Silo's plugin configuration, and its other data (synced profiles, messages and the settings only the Moonbase page edits) in Silo's per-plugin instance state. Silo encrypts both and stores them in its Postgres database. It's included in a database backup and needs the server's `SECRET_KEY` to restore. Upgrades keep it. Uninstalling the plugin deletes it.

## Logs

The plugin logs to Silo's log as plugin output. Storage and provider failures are logged with the route path. API keys never appear in logs or error messages.

## Building

Needs Go 1.26 or newer.

```sh
make test   # go vet and go test -race
make dist   # dist/Moonbase.Silo-<version>-linux-{amd64,arm64} and SHA256SUMS
```

`manifest.json` holds the version. `./dist/Moonbase.Silo-<version>-linux-amd64 manifest` prints the manifest the way Silo reads it at install.

To pick up a newer Jellyfin admin page, copy `Jellyfin/backend/Pages/configPage.html` over `web/configPage.html` unchanged and run `go run ./tools/genform`. If it calls a Jellyfin dashboard function or `ApiClient` method that `web/host.js` doesn't have yet, add it there.
