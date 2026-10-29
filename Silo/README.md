# Moonbase for Silo

This is the Silo backend of Moonbase, the server plugin for the Moonfin apps. It's a native Silo plugin written in Go against the public [silo-plugin-sdk](https://github.com/Silo-Server/silo-plugin-sdk). It doesn't use Silo's Jellyfin-compatible API, and apart from a copy of the Jellyfin admin page it shares no code or build with the Jellyfin and Emby plugins in this repo.

It covers the parts of Moonbase that Silo's plugin API can support:

- Settings sync: each Silo profile keeps its own Moonfin settings across devices, with admin defaults.
- MDBList ratings and the TMDB episode, season, next episode, person link, thumbnail, review, production company and title details routes. Studio logos link to TMDB rather than being served by the plugin, and details are in English.
- Server messages from the admin.
- Custom themes the admin uploads, up to 5 (see Settings).
- The Moonbase admin settings, on the Moonbase page.

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

Moonbase is set up on the **Moonbase** page, which opens from the menu at the top right of Silo's plugin page (Integrations > Plugins > Moonbase) or from Plugin Apps in the admin menu. It has every setting, along with Messages, Apply Defaults To Users, Broadcast, the MDBList key test and the layout editors.

The Moonbase page is the Jellyfin admin page itself, with the same tabs, fields, search and Save button, in Jellyfin's dark style. `web/host.js` stands in for the Jellyfin dashboard it expects. It says "Silo" where the Jellyfin copy says "Jellyfin", and sections for features Silo can't run say so at the top. Lists it would fill from the library stay empty, because Silo doesn't let plugins read library items, and the user picker lists accounts that have synced settings.

Earlier releases also put most settings on Silo's own plugin form. Silo keeps what was saved there, and Moonbase reads it until the Moonbase page is first saved, which keeps those values with the page's settings from then on.

Defaults reach every user who hasn't changed that setting. When an admin changes a default, users on the old default move to the new one, and so does everyone when there was no default before (the apps store every value they hold, so a value from then is the app's own default rather than a choice).

Silo signs a plugin page in for five minutes and renews that while the page keeps calling its routes and the Silo sign-in is still good. The page pings Moonbase each minute to keep it signed in. If Save fails anyway, the page says so and keeps what you tried to save. Open Moonbase again in the same browser tab within an hour and those changes come back, ready to save.

Custom themes are uploaded on the Moonbase page's Themes section and checked the way the Jellyfin plugin checks them. Moonbase on Silo keeps up to 5, gzipped together under one storage key, and refuses an upload that wouldn't fit even compressed. Apps pick up a new or changed theme the next time they sign in.

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
- **Storage is small.** Silo gives each plugin 256 keys of up to 256 KiB. Each profile with synced settings takes one key. The settings, messages, themes and a few indexes take the rest, so a server can hold settings for about 240 profiles.
- **Deleted users leave their settings behind.** Silo doesn't tell plugins when an account or profile is deleted. Remove them with **Remove Selected User's Data** on the Moonbase page's Actions tab.

## Data, backups and removal

Moonbase's settings, synced profiles and messages live in Silo's per-plugin instance state. Silo encrypts it and stores it in its Postgres database, so it’s included in a database backup and needs the server's `SECRET_KEY` to restore. Upgrades keep it. Uninstalling the plugin deletes it.

## Logs

The plugin logs to Silo's log as plugin output. Storage and provider failures are logged with the route path. API keys never appear in logs or error messages.

## Building

Needs Go 1.26 or newer.

```sh
make test   # go vet and go test -race
make dist   # dist/Moonbase.Silo-<version>-linux-{amd64,arm64} and SHA256SUMS
```

`manifest.json` holds the version. `./dist/Moonbase.Silo-<version>-linux-amd64 manifest` prints the manifest the way Silo reads it at install.

To pick up a newer Jellyfin admin page, copy `Jellyfin/backend/Pages/configPage.html` over `web/configPage.html` unchanged. If it calls a Jellyfin dashboard function or `ApiClient` method that `web/host.js` doesn't have yet, add it there.
