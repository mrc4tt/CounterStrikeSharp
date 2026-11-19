# GeoIP configuration

`configs/geoip.json` holds the settings for the per-player language guess. The file is optional: without it the defaults below apply. Copy `configs/geoip.example.json` to `configs/geoip.json` to change them. `css_core_reload` re-reads it together with `core.json`.

A language chosen with `css_lang` always wins over the guess; see `PersistPlayerLanguage` in [Core configuration](core-configuration.md).

## Enabled

When enabled (the default), a player who never chose a language with `css_lang` gets one guessed from the country of their IP address, using a local country database (see `DatabasePath`). The guess is kept in memory only, so the next connect guesses again; a language chosen with `css_lang` always wins. Bots, HLTV, loopback and LAN addresses (10/8, 172.16/12, 192.168/16, 100.64/10) are never guessed. Without a usable database the guess is simply off and players get `ServerLanguage`, as before. Plugins need no change: `player.GetLanguage()` and `Localizer.ForPlayer(...)` return the guessed language, and a plugin with no file for it falls back to the region-less language (`pt-BR` to `pt`), then `ServerLanguage`.

## Notify

When enabled (the default), a player whose language was guessed is told once per connection, a few seconds after joining, in the guessed language, how to change it (`lang.geoip.applied` in the core language files).

## DatabasePath

Country database in MaxMind DB format (`.mmdb`), relative to the `counterstrikesharp` directory unless absolute. GeoLite2-Country and db-ip "IP to Country Lite" both work. Defaults to `configs/geoip/GeoLite2-Country.mmdb`.

## DownloadUrls

Where the database is downloaded from, tried in order, when the file is missing or older than `MaxAgeDays` (checked at startup and then once a day, on a background thread). Each download is limited to 30 seconds and 50 MB and must open as a country database, be larger than 1 MB and resolve `8.8.8.8` to `US` before it atomically replaces the file; otherwise the current file is kept and the failure is logged once. An empty list never downloads (bring your own file). The defaults are the P3TERX GeoLite2 mirror, a third-party copy of MaxMind's data; point this at your own MaxMind mirror or a db-ip Lite file if you prefer.

## MaxAgeDays

Re-download the database when the file is older than this many days. Defaults to 7. `0` or less never re-downloads an existing file.

## CountryLanguages

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

Countries with several common languages (BE, UA, CY, FI, EE, LV, LT, MK, ...) are left out on purpose. Example: `"CountryLanguages": { "fr": ["BE"], "ru": ["UA"] }`.

This product includes GeoLite2 data created by MaxMind, available from https://www.maxmind.com.
