# Smoke tests

The native test plugin (`managed/CounterStrikeSharp.Tests.Native`) runs on the RE server's
slim CS2 dedicated server through `cs2-smoke-test`, which lives in the server-ops repo. It
replaced the old `pr-smoke-test.yml` workflow, which deployed to the retired S2 server.

## Running it

From your PC (server-ops `smoke-test` workflow, runs on re's ops runner):

```bash
gh workflow run smoke-test -R mrc4tt/server-ops -f pr=123          # a PR's head
gh workflow run smoke-test -R mrc4tt/server-ops -f ref=my-branch   # a branch, tag or sha
gh workflow run smoke-test -R mrc4tt/server-ops -f ref=main -f boot_only=true
```

By hand on re, with the extra options:

```bash
cs2-smoke-test --pr 123
cs2-smoke-test --ref main --gamedata /path/to/gamedata.json   # try a gamedata feed before it ships
cs2-smoke-test --ref main --custom /path/to/custom-dir        # gamedata/custom/ overrides
cs2-smoke-test --ref main --keep                              # leave the server running afterwards
```

Nothing runs it automatically, and it does not need the PR to be merged.

## What runs

- Native build in the `cssharp-steamrt-build` image (Steam Runtime), the API and the native
  test plugin in the .NET 10 SDK image.
- A fresh `addons/` (Metamod, CounterStrikeSharp, the .NET 10.0.3 runtime, the test plugin) on a
  copy of the slim server, gamedata auto-update off unless `--autoupdate`.
- Boot on `127.0.0.1:27115` with one bot, wait for `css_plugins list` to show the plugin, then
  `css_smoke_test <run-id>` over RCON. The plugin discovers every test except
  `Category=Benchmark` and benchmark-named classes/methods; add
  `[Trait("Category", "Benchmark")]` to new benchmark classes.
- Wait for the report matching the run id, then stop the server. Failed tests, zero tests, a
  missing/stale report, a timeout or a server that never loads CounterStrikeSharp fail the run.

Results land in `/var/lib/cs2-smoke-test/results/<date>-<ref>-<sha>/` on re: `summary.md`,
`smoke-results.json` (per-test outcomes and messages), `server.log`, `run.log`, `steam.inf`,
`gamedata-status.json` and the CounterStrikeSharp logs. The newest 20 are kept.

## Writing tests

There are no human players on the smoke server. A test that needs a pawn must use
`TestUtils.EnsureAliveBot()`, which sets `bot_join_after_player 0` and never uses `bot_kick`
(a kick lands after a same-frame `bot_add`). A bare frame wait after bot commands leaves no pawn.

Functions the game calls by itself (with bots present) also reach a test's hook between frames:
reset counters right before the test's own call.

## Trust

**A run executes the ref's code as root on re.** Run it on your own branches and on PRs you
have read. `--pr` refuses PRs from forks; `--allow-fork` overrides that, by hand on re only.
