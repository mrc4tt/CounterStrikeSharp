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

When enabled, CS# checks `AutoUpdateURL` for a newer `gamedata.json` at startup, before gamedata is loaded, and replaces the local file if the server has a new version. Enabled by default.

Before downloading, CS# reads `latest/manifest.json` next to the URL and compares the game build it was generated for with this server's `PatchVersion` from `csgo/steam.inf` (`1.41.8.5` is build `14185`). If they differ, or either cannot be read, the update is skipped and the current file is kept, so a server that has not taken a CS2 update yet never receives signatures for the next build. URLs without a `/latest/` segment skip this check.

After downloading, every signature whose text changed is checked against the server binaries that are already loaded. If a signature that resolves with the current file would stop resolving with the new one (or a resolving key was removed), the update is rejected and the current file is kept; the log names each such key. Offsets cannot be checked this way.

A download only replaces the local file if it parses as a JSON object and has at least half as many keys as the current file; otherwise it is rejected and the existing file is used. A failed or skipped update is logged and never stops CS# from loading.

## AutoUpdateURL
The full URL of the `gamedata.json` to track. Defaults to `https://sig.miksen.me/latest/CounterStrikeSharp/gamedata.json`, the newest generated gamedata from CS2_VibeSignatures. The server must send an `ETag` header for the "already up to date" check to work; it is stored next to the file as `gamedata.etag`.

On Linux, https is supported through the Steam Runtime's `libcurl`. The Windows build supports `http://` URLs only.

## MaximumFrameTasksExecutedPerTick
The maximum amount of `NextFrame` and `NextWorldUpdate` tasks that can be executed on a single game frame. The queue totals are tracked separately. Defaults to 1024. This value can be reduced to prevent long frame burstiness.