# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`mrc4tt/CounterStrikeSharp` is a fork of `roflmuffin/CounterStrikeSharp` — a server-side modding framework for Counter-Strike 2. It is a Metamod plugin (native C++) that hosts a .NET 10 scripting layer (managed C#); user plugins are written in C# and run inside isolated `AssemblyLoadContext`s.

The fork adds support for newer glibc (Debian 13 / Ubuntu 24), exposes a CommandLine API, and carries assorted patches on top of upstream (`upstream` remote = `roflmuffin/CounterStrikeSharp`).

## Upstream sync checks

When fixing build errors or modifying files, check whether the file already matches upstream before editing — and after editing, confirm the change does not diverge from upstream unless that divergence is intentional.

- Diff a file against upstream: `git diff upstream/main -- <path>`
- Show upstream version of a file: `git show upstream/main:<path>`
- Diff a directory: `git diff --stat upstream/main -- <dir>`

If a local change exists, identify whether it is an intentional fork patch (see `git log` for context) or accidental drift. Don't overwrite intentional patches when aligning with upstream; don't introduce drift when the upstream version already works.

## Build commands

The repo has two halves that build independently — the managed .NET side and the native C++ side.

**Managed (.NET 10):**
```bash
# Build the API library only (fastest iteration)
dotnet build managed/CounterStrikeSharp.API/CounterStrikeSharp.API.csproj

# Build the whole managed solution (API + SchemaGen + TestPlugin + tests)
dotnet build managed/CounterStrikeSharp.sln
```

**Native (C++):** built via CMake from the top-level `CMakeLists.txt`. The native side is what Metamod loads (`mm_plugin.cpp` is the entry point); the managed side is loaded by the native host via .NET hosting APIs in `src/scripting/`.

There is no project-wide lint or test runner. `managed/CounterStrikeSharp.API.Tests` and `managed/CounterStrikeSharp.Tests.Native` exist but most "verification" in practice is loading the built artifacts on a dev server and watching the log.

## Architecture

### Two-process-boundary design

There is no IPC — everything runs in the CS2 server process. The boundaries that *do* exist:

1. **Native ↔ managed**: `src/scripting/` exposes a function-table to the managed runtime. The managed side calls these via P/Invoke (`NativeAPI`); the native side calls back via callback handles registered by `globals::callbackManager`. Managed → native is "function ID + script context" calling convention; native → managed is "find delegate by name, push args, execute."
2. **Host ↔ plugin**: each plugin loads into its own `AssemblyLoadContext` via McMaster.NETCore.Plugins (`PluginLoader`). The host owns the C# API DLL and shares only the type identities listed in `PluginContext`'s `sharedTypes` array. Everything else is private per plugin — including dependencies that ship next to the plugin DLL.

### Plugin loading pipeline

Where to look when something about plugin discovery, loading, hot-reload, or unload misbehaves:

- `managed/CounterStrikeSharp.API/Core/Plugin/Host/PluginManager.cs` — `Load()` walks the plugins directory, picks up shared assemblies, then calls `LoadPlugin(path)` per plugin.
- `GetPluginsAssemblyPaths()` in the same file — discovery uses two tiers: (1) convention `<dir>/<dir>.dll`, (2) fallback that picks the lone DLL referencing `CounterStrikeSharp.API` via `PEReader` metadata-only inspection. Folder names don't have to match DLL names anymore.
- `managed/CounterStrikeSharp.API/Core/Plugin/PluginContext.cs` — owns one plugin's lifecycle. `Load`, `Unload`, `Reload`, the McMaster `PluginLoader` instance, and the per-plugin DI container live here. `Reload(bool)` is the entry point for `css_plugins restart` — it pre-validates the new file via metadata-only `PEReader` inspection (NOT a full assembly load — see big warning below), then disposes the old `AssemblyLoadContext` and creates a fresh one.
- `managed/CounterStrikeSharp.API/Core/Plugin/Host/SharedPluginFileWatcher.cs` — single shared `FileSystemWatcher` rooted at the plugins directory, used for delete-detection. One inotify instance for the whole host instead of N (the previous per-plugin design exhausted Linux's default 128-instance limit at high plugin counts).

### Schema / gamedata pipeline

CS2's server binary is stripped, so CSSharp finds engine functions by byte-pattern matching at runtime.

- `gamedata/gamedata.json` (in three locations: `configs/...`, `build/...`, `out/linux/...`) maps logical function names to per-platform Windows/Linux byte signatures and vtable offsets. **All three copies must stay in sync** — the source is `configs/`, the others are build artifacts that get bundled.
- `managed/CounterStrikeSharp.API/Modules/Memory/VirtualFunctions.cs` — eager-init `static` fields, each calling `GameData.GetSignature("Foo")` in its initializer. **A single missing key throws `TypeInitializationException` for the whole class**, which kills every plugin that touches *any* `VirtualFunctions.*` member, not just the consumer of the missing one. This trips frequently after a schema sync that didn't carry every key forward.
- `managed/CounterStrikeSharp.API/Generated/Schema/Classes/*.g.cs` — auto-generated wrappers for CS2 schema classes. Generated by `managed/CounterStrikeSharp.SchemaGen` from a `server.json` schema dump. Don't hand-edit these — regenerate via the schema sync workflow.
- `managed/CounterStrikeSharp.API/Generated/Natives/API.cs` — **this one IS hand-edited** despite the `Generated/` path; the natives generator is currently dormant in this fork, so changes to it should mirror surrounding style and be made directly.

### Schema sync workflow

`eng/update-schema.ts` (Deno script invoked by `.github/workflows/sync-schema.yaml`) runs:
1. RCON → `dump_schema all` on a live CS2 server (env: `GS_HOST`, `GS_PORT`, `GS_PASS`).
2. SFTP fetch of the resulting `server.json` (env: `SFTP_HOST`, `SFTP_USER`, `SFTP_PASS`).
3. C0-control-byte sanitization on the JSON (CS2 sometimes emits raw `\x00–\x1F` bytes inside string values; jq/dotnet choke on these).
4. `dotnet run --project managed/CounterStrikeSharp.SchemaGen` to regenerate `Generated/Schema/Classes/*.g.cs`.

The workflow expects repository-level Actions secrets (Settings → Secrets and variables → Actions), not environment-scoped secrets — so no `environment:` declaration on the job. RCON failures retry up to 5 times with 30s backoff; the patch-version step also runs the C0 strip as a belt-and-braces guard before invoking `jq`.

## Critical conventions

- **Don't `Invoke()` `CBaseEntity_TakeDamageFunc` directly.** It's a 2-arg compatibility binding for plugins that hook the entity TakeDamage with `(CEntityInstance, CTakeDamageInfo)`. The native function is actually 3-arg (`+CTakeDamageResult`); hooking with two is fine because DynoHook reads parameters by index regardless of declared arity, but invoking is wrong-ABI. For invocation use `CBaseEntity_TakeDamageOldFunc` (3-arg, marked `[Obsolete]` in favor of `Listeners.OnEntityTakeDamagePre`) or the listener.
- **Plugin reload pre-validation is metadata-only.** `PluginContext.TryProbeLoadAssembly` uses `PEReader` to check the PE header / AssemblyDef / AssemblyRef tables only. It does NOT attempt a real `LoadFromAssemblyPath`, because a bare `AssemblyLoadContext` lacks McMaster's plugin-folder dependency resolver and would false-positive any plugin with private deps. Genuine load-time failures (unresolvable transitive references, strong-name issues that surface at JIT) are caught by the outer `try/catch` in `Reload` after Unload — at that point the plugin stays unloaded and the operator sees the real error.
- **Reflection scans across the AppDomain must use `GetLoadableTypes`, not `assembly.GetTypes()`.** Failed reloads can leak `AssemblyLoadContext`s whose dead types break domain-wide reflection. The helper in `PluginContext` swallows `ReflectionTypeLoadException` and returns the types that did resolve. Calling `GetTypes()` directly across `AppDomain.CurrentDomain.GetAssemblies()` will eventually fail on long-running servers.
- **Inotify limit on Linux:** the framework uses a single shared `FileSystemWatcher`, but McMaster also creates one per `PluginLoader` when `EnableHotReload` is true — gated on `CoreConfig.PluginHotReloadEnabled` so disabling the user-facing flag actually frees those FDs. On dense plugin servers, raise the system limit:
  ```bash
  echo "fs.inotify.max_user_instances=8192" | sudo tee /etc/sysctl.d/60-cssharp.conf
  echo "fs.inotify.max_user_watches=524288"  | sudo tee -a /etc/sysctl.d/60-cssharp.conf
  sudo sysctl --system
  ```
- **gamedata changes need to ship with API changes.** When you add a binding in `VirtualFunctions.cs`, also add the matching key to `configs/.../gamedata.json` (and verify the build/out copies pick it up). A binding with a missing key takes down the whole `VirtualFunctions` class for every plugin via `TypeInitializationException`.

## Files that drift between fork and upstream

The largest live deltas tracked locally (check `git diff upstream/main -- <path>` before editing):

- `managed/CounterStrikeSharp.API/Modules/Memory/VirtualFunctions.cs` — fork adds `CBaseEntity_TakeDamageFunc` 2-arg compatibility binding.
- `managed/CounterStrikeSharp.API/Core/Plugin/PluginContext.cs` — fork adds `Reload`, `TryProbeLoadAssembly`, `GetLoadableTypes`, `CreateLoader` extraction, and integration with `SharedPluginFileWatcher`.
- `managed/CounterStrikeSharp.API/Core/Plugin/Host/PluginManager.cs` — fork adds `GetOrCreateSharedFileWatcher`, `IDisposable`, and the API-reference plugin discovery fallback.
- `managed/CounterStrikeSharp.API/Core/Plugin/Host/SharedPluginFileWatcher.cs` — fork-only file.
- `managed/CounterStrikeSharp.API/Core/Application.cs` — fork uses `plugin.Reload(true)` instead of `Unload + Load + OnAllPluginsLoaded` for the `css_plugins restart` path.
- `src/core/managers/entity_manager.cpp` — fork null-guards `entity/info/pResult` in the OnTakeDamage post-callback trampolines.
- `configs/.../gamedata.json` — fork-only `CBaseEntity_TakeDamage` alias entry.
- `.github/workflows/sync-schema.yaml` and `eng/update-schema.ts` — fork hardens RCON retry loop and adds C0-byte sanitization.
