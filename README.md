# Cinema Mode Disabler

A Jellyfin server plugin that turns off Jellyfin Web's Cinema Mode for every user of every browser.

## Why

With Cinema Mode on, which is Jellyfin Web's default, every play from the start first asks the server for intros
(`GET /Users/{userId}/Items/{itemId}/Intros`), and only then asks for the playback info. Without an intro provider
plugin that request always returns an empty list, so it only costs a round trip, often 25 to 100 ms behind a proxy,
before playback can even start. With Cinema Mode off, Jellyfin Web skips it.

Jellyfin Web keeps Cinema Mode in each browser, for each user, in `localStorage['{userId}-enableCinemaMode']`, and treats
a missing value as on. The server cannot change it, so this plugin changes it in the browser.

## What it changes

The plugin rewrites Jellyfin Web's `index.html` (`/web/` and `/web/index.html`, under the server's base URL) to add:

- a small inline script that sets `{userId}-enableCinemaMode` to `false` for every user the browser has signed in
  with, when the page loads and again on each navigation, which covers signing in and opening an item before any play;
- a style that hides the Cinema Mode checkbox in Playback settings, since the plugin would override it anyway.

That applies to every browser and every user of Jellyfin Web on the server. There is nothing to configure.

The script carries `data-cfasync="false"`, so Cloudflare's Rocket Loader runs it at once instead of deferring it. The
plugin does not need File Transformation, and it works alongside other plugins that rewrite `index.html` themselves.

## What it does not change

Native apps, such as Jellyfin for Android TV, Wholphin, or Swiftfin, keep their own settings and do not load Jellyfin
Web's `index.html`, so they still follow their own Cinema Mode setting.

## Install

1. In Jellyfin, open Dashboard > Plugins > Repositories (Catalog > Settings on some versions), and add this repository:

   ```
   https://raw.githubusercontent.com/alchemyyy/jellyfin-plugin-cinema-mode-disabler/main/manifest.json
   ```

2. Install Cinema Mode Disabler from the catalog, and restart Jellyfin.
3. Reload Jellyfin Web in each browser. The page revalidates on every load, so a normal reload is enough.

Requires Jellyfin 12.1 or later (plugin ABI 12.1.0.0).

## Uninstall

Uninstall the plugin from Dashboard > Plugins and restart Jellyfin. The Cinema Mode checkbox shows again, but each
browser keeps the stored `false` until a user turns Cinema Mode back on in Playback settings.

## Build

```sh
dotnet test Jellyfin.Plugin.CinemaModeDisabler.slnx
node --test Jellyfin.Plugin.CinemaModeDisabler.Tests/*.test.mjs
dotnet publish Jellyfin.Plugin.CinemaModeDisabler/Jellyfin.Plugin.CinemaModeDisabler.csproj -c Release
uv run .github/scripts/release.py package
```

Releases run through the Release workflow (Actions > Release > Run workflow on `main`), which builds, tests, publishes
the GitHub release, and records it in `manifest.json` on `main`.
