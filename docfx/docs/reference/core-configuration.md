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

## PluginPreJitEnabled

When enabled (the default), a plugin's methods are compiled right after it loads, in small slices spread over the next frames, instead of the first time each one runs. That moves the plugin's one-off JIT cost out of gameplay: without it, the first event, command or round a plugin handles pays several milliseconds of compilation inside that game frame. Disable it if a plugin relies on a static initializer running at a particular moment.

## ServerLanguage

Configures the default language to use for server commands & messages. The format for the culture name based on RFC 4646 is `languagecode2-country`/`regioncode2`, where `languagecode2` is the two-letter language code and `country/regioncode2` is the two-letter subculture code. Examples include `ja-JP` for Japanese (Japan) and `en-US` for English (United States). Defaults to "en".

## PersistPlayerLanguage

When enabled (the default), the language a player picks with `css_lang <code>` is stored per SteamID64 in `configs/player_languages.json` and applied again on every later connect, map change and server restart. `css_lang reset` (or `css_lang auto`) forgets the choice, so the GeoIP guess or the server language applies again. `css_lang` without an argument shows the current language and where it comes from (chosen, guessed from location, or server default).

## GeoIpLanguageEnabled

When enabled (the default), a player who never chose a language with `css_lang` gets one guessed from the country of their IP address, using a local country database (see `GeoIpDatabasePath`). The guess is kept in memory only, so the next connect guesses again; a language chosen with `css_lang` always wins. Bots, HLTV, loopback and LAN addresses (10/8, 172.16/12, 192.168/16, 100.64/10) are never guessed. Without a usable database the guess is simply off and players get `ServerLanguage`, as before. Plugins need no change: `player.GetLanguage()` and `Localizer.ForPlayer(...)` return the guessed language, and a plugin with no file for it falls back to the region-less language (`pt-BR` to `pt`), then `ServerLanguage`.

## GeoIpLanguageNotify

When enabled (the default), a player whose language was guessed is told once per connection, a few seconds after joining, in the guessed language, how to change it (`lang.geoip.applied` in the core language files).

## GeoIpDatabasePath

Country database in MaxMind DB format (`.mmdb`), relative to the `counterstrikesharp` directory unless absolute. GeoLite2-Country and db-ip "IP to Country Lite" both work. Defaults to `configs/geoip/GeoLite2-Country.mmdb`.

## GeoIpDownloadUrls

Where the database is downloaded from, tried in order, when the file is missing or older than `GeoIpMaxAgeDays` (checked at startup and then once a day, on a background thread). Each download is limited to 30 seconds and 50 MB and must open as a country database, be larger than 1 MB and resolve `8.8.8.8` to `US` before it atomically replaces the file; otherwise the current file is kept and the failure is logged once. An empty list never downloads (bring your own file). The defaults are the P3TERX GeoLite2 mirror, a third-party copy of MaxMind's data; point this at your own MaxMind mirror or a db-ip Lite file if you prefer.

## GeoIpMaxAgeDays

Re-download the database when the file is older than this many days. Defaults to 7. `0` or less never re-downloads an existing file.

## GeoIpCountryLanguages

Extra or changed country-to-language mappings, as `"language": ["ISO country code", ...]`, merged over the built-in table (a country listed here wins). Only languages the runtime knows are accepted. The built-in table maps:

| Language | Countries |
|---|---|
| `da` | DK, GL, FO |
| `de` | DE, AT, CH, LI, LU |
| `es` | ES, MX, AR, CO, CL, PE, VE, EC, GT, CU, BO, DO, HN, PY, SV, NI, CR, PA, UY, PR |
| `fr` | FR, MC, SN, CI, CM, ML, BF, NE, TG, BJ, GA, CG, CD, MG, HT |
| `nb` | NO, SJ |
| `pl` | PL |
| `pt-BR` | BR, PT, AO, MZ, CV, GW, ST, TL |
| `ru` | RU, BY, KZ, KG |
| `sv` | SE, AX |
| `tr` | TR |
| `sq` | AL, XK |
| `en` | GB, IE, US, CA, AU, NZ, ZA, IN, SG, PH |

Countries with several common languages (BE, UA, CY, FI, EE, LV, LT, MK, ...) are left out on purpose. Example: `"GeoIpCountryLanguages": { "fr": ["BE"], "ru": ["UA"] }`.

This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com.

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