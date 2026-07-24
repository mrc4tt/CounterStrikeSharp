# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`mrc4tt/CounterStrikeSharp` is a hard fork of `roflmuffin/CounterStrikeSharp` — a server-side modding framework for Counter-Strike 2. It is a Metamod plugin (native C++, `counterstrikesharp.so` / `.dll`) that hosts a .NET 10 runtime; user plugins are written in C# and load into isolated `AssemblyLoadContext`s.

Fork deltas vs upstream: .NET 10 (upstream is .NET 8), newer-glibc/Debian 13 support, spdlog symbol isolation, lazy gamedata resolution, an SIGABRT crash reporter, a CommandLine API, and assorted stability patches. The fork has diverged by 1600+ public API members — **treat it as its own product, not a patch series on upstream.**

## Upstream comparison

**There is no `upstream` remote configured in this checkout.** Only `origin` (= `mrc4tt/CounterStrikeSharp`). Diffing against upstream requires adding it first:

```bash
git remote add upstream https://github.com/roflmuffin/CounterStrikeSharp.git
git fetch upstream
git diff upstream/main -- <path>       # file delta
git show upstream/main:<path>          # upstream version
```

Because the fork has diverged this far, "does this match upstream?" is a weak signal. Prefer `git log --oneline -- <path>` on this repo to find out *why* a line looks the way it does — most non-obvious code here carries an explanatory comment block naming the crash or breakage it prevents. Read those comments before "simplifying" anything.

## Build

Two halves that build independently.

**Managed (.NET 10):**
```bash
dotnet build managed/CounterStrikeSharp.API/CounterStrikeSharp.API.csproj   # fastest iteration
dotnet build managed/CounterStrikeSharp.sln                                 # API + SchemaGen + tests + examples
```
The solution also pulls in `tooling/CodeGen.Natives` and every project under `examples/`, so a solution build is slow and can break on an unrelated example.

**Native (C++, CMake + Ninja):**
```bash
mkdir -p build && cd build
cmake -G Ninja -DCMAKE_BUILD_TYPE=Release ..
cmake --build . -- -j16
```
Output lands in `build/addons/counterstrikesharp/bin/linuxsteamrt64/` (or `win64/`). A `PRE_BUILD` step `copy_directory`s `configs/` into `build/`.

**Release-shaped build:** `./build.sh` (Docker, Steam Runtime image from `Dockerfile`; `--with-runtime` bundles the .NET runtime, `--both` builds both variants). `Build.config` holds the env knobs. `quick-build.sh` is an interactive menu wrapper. These scripts are fork-local convenience, not upstream.

**Submodules matter.** `libraries/` carries 9 submodules (hl2sdk-cs2, metamod-source, DynoHook, funchook, dyncall, asmjit, spdlog, Protobufs, Catch2). A CS2 game update usually means bumping `hl2sdk-cs2` and rebuilding native — undefined-symbol and "Invalid base path" errors at load are the usual symptom.

## Tests

Three separate things, none wired into a single runner:

- `managed/CounterStrikeSharp.API.Tests` — real xUnit, runs off-server. `dotnet test managed/CounterStrikeSharp.API.Tests/CounterStrikeSharp.API.Tests.csproj`. Covers SteamID, admin, translations, core logging. CI runs this.
- `managed/CounterStrikeSharp.Tests.Native` — **not a unit test project.** It is a CSSharp plugin (`NativeTestsPlugin`) that must be loaded on a live CS2 server; it exercises natives, schema, entities, timers, usermessages. `InternalsVisibleTo NativeTestsPlugin` in the API csproj exists for it.
- `src/core/tests` — Catch2, opt-in: `cmake -DBUILD_CSS_TESTS=ON .. && ctest --output-on-failure`. Built in a **separate CI job** so a test failure cannot block release-artifact upload.

Beyond that, verification in practice is deploying the build to a dev server and reading the log.

## Formatting / lint

`src/**` is clang-format-20-gated (`.clang-format`, `.clang-tidy`). CI job `lint-code.yaml` checks `check-path: src` excluding `sdk|.proto`. Locally: `./check-format.sh` (requires `clang-format-20` specifically) or `eng/formatting/format.sh`. Managed code has no lint gate — but `WarningsAsErrors` includes `CS0108,CS0114` (accidental hiding/override), and **nullable warnings are deliberately NOT suppressed** — they're the null-deref radar. Don't add `CS8xxx` to `NoWarn`.

## Architecture

### Boundaries

Everything runs in the CS2 server process; there is no IPC. Two real boundaries:

1. **Native ↔ managed** (`src/scripting/`). Native exposes one exported entry point, `InvokeNative`; the managed side P/Invokes it with a function ID + script context (`ScriptContext.cs`, `Generated/Natives/API.cs`). Native → managed goes through `globals::callbackManager` — find delegate by name, push args, execute (`script_engine.cpp`, `callback_manager.cpp`). Native subsystem entry point is `src/mm_plugin.cpp`; the .NET runtime is booted from `src/scripting/dotnet_host.cpp` via hostfxr.
2. **Host ↔ plugin** (managed). Each plugin gets its own `AssemblyLoadContext` via McMaster.NETCore.Plugins. Only these type identities are shared with the host: `IPlugin`, `ILogger`, `IServiceCollection`, `IPluginServiceCollection<>`, `ICommandManager` (see the `PluginLoader.CreateFromAssemblyFile` call in `PluginContext`'s ctor). Everything else — including deps shipped next to the plugin DLL — is private per plugin.

### Native layout (`src/`)

- `mm_plugin.cpp` — Metamod entry, subsystem init/teardown order.
- `core/managers/` — one manager per engine domain: `player_manager`, `entity_manager`, `event_manager`, `con_command_manager`, `chat_manager`, `server_manager`, `usermessage_manager`, `voice_manager`.
- `core/gameconfig.cpp` + `gameconfig_updater.cpp` — gamedata load and **auto-update over HTTP** (see gotcha below).
- `core/timer_system.cpp` / `tick_scheduler.cpp` — timers and next-tick work queues.
- `core/fatal_reporter.cpp` — async-signal-safe SIGABRT handler that prints the last native→managed callback and suspect plugin as the final console line, then chains to the CLR handler. Only SIGABRT is hooked — **never hook SIGSEGV**, CoreCLR uses it for normal null-ref handling.
- `scripting/natives/natives_*.cpp` — the native function table, one file per domain.

### Managed layout (`managed/CounterStrikeSharp.API/`)

- `Core/` — `Application.cs` (the `css_plugins` console command lives here), `BasePlugin.cs`, `GameData.cs`, `CoreConfig.cs`, `ScriptContext.cs`, `UpdateWatcher.cs`.
- `Core/Plugin/` — plugin lifecycle (below).
- `Modules/` — the public plugin-facing API surface (Memory, Entities, Commands, Cvars, Events, Timers, Admin, Menu, UserMessages, Utils).
- `Generated/Schema/Classes/*.g.cs` — machine-generated, don't hand-edit.
- `Generated/Natives/API.cs` — **hand-edited despite the path.** The natives generator (`tooling/CodeGen.Natives`) is dormant in this fork; edit directly and mirror surrounding style.

### Plugin loading pipeline

- `Core/Plugin/Host/PluginManager.cs` — `Load()` walks the plugins dir and calls `LoadPlugin(path)` per plugin. `GetPluginsAssemblyPaths()` does a **recursive** DFS per top-level plugin dir looking for the convention `<dir>/<dir>.dll`; it skips a root-level `disabled/` folder and skips any nested `addons/` subtree (that's a full release bundle extracted into the plugins folder — descending into it registers shared libs as plugins and produces duplicate copies).
- `Core/Plugin/PluginContext.cs` — one plugin's lifecycle: `Load`, `Unload`, the McMaster `PluginLoader`, the per-plugin DI container, and a **per-plugin `FileSystemWatcher`** created only when `CoreConfig.PluginHotReloadEnabled`. The watcher handles delete-detection (unload + `OnRequestRemoval` so the manager drops the context instead of leaking its ALC); McMaster's own `Loader.Reloaded` event drives the change-detected hot reload via `OnReloadedAsync` → `Unload(true)` / `Load(true)` on the next world update.
- `Core/Plugin/Host/PluginContextQueryHandler.cs` — plugin lookup by id (`#1`) or name, used by `css_plugins`.
- `Core/Plugin/Host/PluginContextNuGetDependencyResolver.cs` — resolves plugin NuGet deps.
- `PluginTerminationException` / `ISelfPluginControl.TerminateSelf` — a plugin can kill itself with a reason; `TerminationReason` surfaces in `css_plugins list`.

`css_plugins restart|reload` (in `Application.cs`) is `Unload(true)` → `Load(true)` → `OnAllPluginsLoaded(true)`, guarded on `plugin.State == PluginState.Loaded` before firing the last one. There is no `PluginContext.Reload()` method — don't reintroduce references to one.

### gamedata pipeline

CS2's server binary is stripped, so engine functions are found by byte-pattern matching at runtime.

- **`configs/addons/counterstrikesharp/gamedata/gamedata.json` is the single source of truth and the only git-tracked copy.** Copies under `build/` and `out/` are gitignored artifacts regenerated by CMake's `copy_directory` step. Edit `configs/`, never the artifacts.
- `GameDataProvider` (`Core/GameData.cs`) merges **every** `*.json` in the gamedata directory, not just `gamedata.json` — plugins can ship their own (MatchZy does; fork-side MatchZy sigs were dropped in `50a4f8c8` for exactly that reason). Duplicate keys across files log a warning and the later file wins.
- `Modules/Memory/VirtualFunctions.cs` — see the convention below; the header comment there is the authoritative explanation.

### Schema sync workflow

`eng/update-schema.ts` (Deno, invoked by `.github/workflows/sync-schema.yaml`):
1. RCON → `dump_schema all` on a live CS2 server (env `GS_HOST`, `GS_PORT`, `GS_PASS`; binary at `eng/rcon`).
2. `lftp` SFTP fetch of `server.json` → `managed/CounterStrikeSharp.SchemaGen/Schema/server.json` (env `SFTP_HOST`, `SFTP_PORT`, `SFTP_USER`, `SFTP_PASS`).
3. C0-control-byte sanitization (CS2 emits raw `\x00–\x1F` inside string values; jq and dotnet both choke).
4. `dotnet run --project managed/CounterStrikeSharp.SchemaGen` → regenerates `Generated/Schema/Classes/*.g.cs`.

Secrets are repository-level Actions secrets, so the job carries **no `environment:` declaration**. RCON retries 5× with 30s backoff; the patch-version step re-runs the C0 strip before `jq` as a belt-and-braces guard.

## Critical conventions

- **`VirtualFunctions` members must stay public static FIELDS.** Converting one to a `Lazy`-backed property removes the field from metadata, and every plugin compiled against the field-based API emits `ldsfld FooFunc` → `MissingFieldException` at load. Laziness is achieved instead by the deferred ctor `new(() => GameData.GetSignature("Foo"))`, which pushes signature resolution to first invoke/hook. That gives per-member isolation: a missing key throws only for the binding that uses it, instead of `TypeInitializationException` taking down every plugin touching *any* `VirtualFunctions.*` member.
- **Three bindings are deliberately eager** — `ClientPrintFunc`, `ClientPrintAllFunc`, `GiveNamedItemFunc` (plus their `Action`/`Func` companions). They resolve inside the static ctor, so a missing `ClientPrint` / `UTIL_ClientPrintAll` / `GiveNamedItem` key *does* nuke the whole class. Accepted, because those sigs are always present. Companion `Func`/`Action` fields must be declared **after** their backing `*Func` field — static field init is textual order.
- **`CBaseEntity_TakeDamage` has no gamedata key right now.** The 2-arg compatibility binding exists in `VirtualFunctions.cs` (for older third-party plugins like WC3 that hook with `(CEntityInstance, CTakeDamageInfo)`) but `configs/.../gamedata.json` only defines `CBaseEntity_TakeDamageOld`. Deferred resolution means this only throws when a plugin actually hooks/invokes it. If you touch this area, either add the alias key back or confirm the omission is intended.
- **Never `Invoke()` `CBaseEntity_TakeDamageFunc`.** It is declared 2-arg on purpose; the native function is 3-arg (`+CTakeDamageResult`). Hooking with two params is fine — DynoHook reads params by index regardless of declared arity — but invoking is wrong-ABI. To invoke, use `CBaseEntity_TakeDamageOldFunc` (3-arg, `[Obsolete]`) or `Listeners.OnEntityTakeDamagePre`.
- **gamedata changes ship with API changes.** Adding a binding in `VirtualFunctions.cs` without the matching `configs/.../gamedata.json` key produces a runtime failure at first use (or a class-wide `TypeInitializationException` if you made it eager).
- **Reflection scans across the AppDomain must use `GetLoadableTypes`, not `assembly.GetTypes()`** (`PluginContext.cs:336`). Failed reloads can leak ALCs whose dead types break domain-wide reflection; the helper swallows `ReflectionTypeLoadException` and returns what resolved. Direct `GetTypes()` over `AppDomain.CurrentDomain.GetAssemblies()` eventually fails on long-running servers.
- **Don't add exported symbols to the Linux `.so`.** `makefiles/exports.ver` exports exactly `CreateInterface` and `InvokeNative` and localizes everything else. Without it, our header-only spdlog's global logger registry interposes with the CS2 server's, the engine re-registers its `RayTrace` logger into an already-populated registry, and `spdlog_ex` aborts the server via `std::terminate`.
- **ApiCompat validation is intentionally OFF** (`ApiCompatValidateAssemblies=false`, `EnablePackageValidation=false`, `CP0003` in `NoWarn`). The baseline `ApiCompat/v202.dll` is an *upstream* assembly this fork no longer matches, and schema sync churns members every run. Re-enabling it requires re-baselining against a recent **fork** release, not deleting the suppressions.
- **Inotify limit on Linux.** Hot reload creates one `FileSystemWatcher` per plugin plus one per McMaster `PluginLoader`, both gated on `CoreConfig.PluginHotReloadEnabled` — turning the flag off genuinely frees the FDs. On dense plugin servers raise the limit:
  ```bash
  echo "fs.inotify.max_user_instances=8192" | sudo tee /etc/sysctl.d/60-cssharp.conf
  echo "fs.inotify.max_user_watches=524288"  | sudo tee -a /etc/sysctl.d/60-cssharp.conf
  sudo sysctl --system
  ```

## Operational gotchas

- **gamedata auto-update points at upstream.** `AutoUpdateEnabled` defaults to `true` and `AutoUpdateURL` defaults to `http://gamedata.cssharp.dev` (`src/core/coreconfig.h:38`). On a live server, `gameconfig_updater.cpp` ETag-checks that URL and **overwrites `gamedata/gamedata.json` in place**. Any fork-only key you add to `configs/` can be silently replaced at runtime by upstream's file. If a sig "disappears" on a deployed server, check `gamedata/gamedata.etag` and the `AutoUpdate` settings in `configs/core.json` before suspecting the build.
- **Never overwrite a deployed CSSharp in place.** `counterstrikesharp.so` is mmap'd into the running server; truncate-and-rewrite of the same inode corrupts the mapping and hard-crashes mid-match (often surfacing as the garbage-collected-delegate crash). `tools/css-update.sh` does the correct atomic write-temp-then-`rename()` swap via rsync. The new native binary only takes effect after the process restarts — nothing hot-swaps it.
- **Bundled-runtime version and the hostfxr the native side looks for can disagree.** `load_hostfxr()` (`src/scripting/dotnet_host.cpp:120`) defaults to `10.0.3`, overridable with the `CSSHARP_HOSTFXR_VERSION` env var. `build.sh` defaults to `DOTNET_VERSION=8.0.3`. So a plain `./build.sh --with-runtime` bundles a .NET 8 runtime that the native loader will not find ("Required hostfxr version 10.0.3 not found under ..."), and the managed assemblies are `net10.0` anyway. Use `./build.sh --with-runtime --net10`.
- `Core/UpdateWatcher.cs` watches the deployed dirs and, per `configs/core.json`, either logs that a restart is pending or restarts at a safe point.

## Where the fork's own patches live

Non-obvious, fork-specific code, each with an in-file comment explaining the failure it prevents:

- `managed/.../Modules/Memory/VirtualFunctions.cs` — deferred-resolution field pattern + `CBaseEntity_TakeDamage` 2-arg alias.
- `managed/.../Core/Plugin/PluginContext.cs` — `GetLoadableTypes`, self-termination, delete-detection + `OnRequestRemoval`, hot-reload failure containment.
- `managed/.../Core/Plugin/Host/PluginManager.cs` — recursive plugin discovery with `disabled/` and nested-`addons/` skips.
- `managed/.../Core/GameData.cs` — multi-file gamedata merge, duplicate-key warning, missing-file diagnostics.
- `makefiles/exports.ver` + the `--version-script` link option in `CMakeLists.txt` — spdlog symbol isolation.
- `src/core/fatal_reporter.cpp` — SIGABRT crash breadcrumb.
- `src/core/managers/entity_manager.cpp` — null-guards on `entity`/`info`/`pResult` in the OnTakeDamage post-callback trampolines.
- `src/core/managers/chat_manager.cpp` — `bind c "say !cmd"` parsing.
- `CMakeLists.txt` — `BUILD_CSS_TESTS` opt-in test target.
- `.github/workflows/sync-schema.yaml`, `eng/update-schema.ts` — RCON retry loop + C0-byte sanitization.
- `tools/css-update.sh`, `build.sh`, `Build.config` — fork-local deploy/build tooling.
