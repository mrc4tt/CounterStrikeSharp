---
title: Core Configuration
description: Summary for core configuration values
---

# Core Configuration

Summary for core configuration values

## PublicChatTrigger

List of characters to use for public chat triggers.

## SilentChatTrigger

List of characters to use for silent chat triggers.

## FollowCS2ServerGuidelines

Per [CS2 Server Guidelines](https://blog.counter-strike.net/index.php/server_guidelines/), certain plugin
functionality will trigger all of the game server owner's Game Server Login Tokens
(GSLTs) to get banned when executed on a Counter-Strike 2 game server.

Enabling this option will block plugins from using functionality that is known to cause this.
This option only has any effect on CS2. Note that this does NOT guarantee that you cannot
receive a ban.

> [!NOTE]
> Disable this option at your own risk.

## PluginHotReloadEnabled

When enabled, plugins are automatically reloaded when their .dll file is updated.

## PluginAutoLoadEnabled

When enabled, plugins are automatically loaded from the plugins directory on server start.

## ServerLanguage

Configures the default language to use for server commands & messages. The format for the culture name based on RFC 4646 is `languagecode2-country`/`regioncode2`, where `languagecode2` is the two-letter language code and `country/regioncode2` is the two-letter subculture code. Examples include `ja-JP` for Japanese (Japan) and `en-US` for English (United States). Defaults to "en".

## UnlockConCommands

When enabled, will remove the `FCVAR_HIDDEN`,`FCVAR_DEVELOPMENTONLY`, `FCVAR_MISSING0`, `FCVAR_MISSING1`, `FCVAR_MISSING2`, `FCVAR_MISSING3` flags from all console commands.

## UnlockConVars

When enabled, will remove the `FCVAR_HIDDEN`,`FCVAR_DEVELOPMENTONLY`, `FCVAR_MISSING0`, `FCVAR_MISSING1`, `FCVAR_MISSING2`, `FCVAR_MISSING3` flags from all console variables.

## AutoUpdateEnabled

When enabled, CS# checks the `AutoUpdateURLs` mirrors for a newer `gamedata.json` at startup, before gamedata is loaded, and replaces the local file if the server has a new version. Enabled by default.

Before downloading, CS# reads `latest/manifest.json` next to the URL and compares the game build it was generated for with this server's `PatchVersion` from `csgo/steam.inf` (`1.41.8.5` is build `14185`). If they differ, or either cannot be read, the update is skipped and the current file is kept, so a server that has not taken a CS2 update yet never receives signatures for the next build. URLs without a `/latest/` segment skip this check.

After downloading, every signature whose text changed is checked against the server binaries that are already loaded. If a signature that resolves with the current file would stop resolving with the new one (or a resolving key was removed), the update is rejected and the current file is kept; the log names each such key. Offsets cannot be checked this way.

When the manifest lists a `sha256` for the file, the download must match it; a mirror caught mid-sync (new manifest, old file) is rejected that way.

A download only replaces the local file if it parses as a JSON object and has at least half as many keys as the current file; otherwise it is rejected and the existing file is used. A failed or skipped update is logged and never stops CS# from loading.

## AutoUpdateURLs
The `gamedata.json` URLs to track, tried in order. The first mirror whose manifest matches this server's game build and whose file passes every check above wins; a mirror that is down, behind or serves a rejected file is skipped with a warning and the next one is tried. When every mirror fails, the local `gamedata.json` is used as it is. Each mirror gets up to 15 seconds, all of them together up to 25.

```json
"AutoUpdateURLs": [
  "https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json",
  "https://my-mirror.example/latest/CounterStrikeSharp/gamedata.json"
]
```

When it is not set, CS# uses its built-in mirror list. A mirror should serve the same `latest/` layout (`latest/manifest.json` next to `latest/CounterStrikeSharp/gamedata.json`) so the game-build check applies.

The server should send an `ETag` header for the "already up to date" check; it is stored with the mirror it came from in `gamedata.etag`, and only sent back to that mirror.

On Linux, https is supported through the Steam Runtime's `libcurl`; on Windows through WinHTTP.

## AutoUpdateURL
The single-URL form from before `AutoUpdateURLs`, still read when `AutoUpdateURLs` is not set: a custom URL here is a mirror list of one. Its old default (`https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json`) means the built-in mirror list.

## MaximumFrameTasksExecutedPerTick
The maximum amount of `NextFrame` and `NextWorldUpdate` tasks that can be executed on a single game frame. The queue totals are tracked separately. Defaults to 1024. This value can be reduced to prevent long frame burstiness.