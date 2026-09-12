# Installation

1. Download and install Metamod:Source for CS2. Detailed instructions can be found [here](https://cs2.poggu.me/metamod/installation/).
2. Download the latest release of CounterStrikeSharp from [here](https://github.com/roflmuffin/CounterStrikeSharp/actions/workflows/cmake-single-platform.yml).
   - If this is your first time installing, you will need to download the `with-runtime` version. This includes the .NET runtime, which is required to run the plugin.
   - Subsequent upgrades will not require the runtime, unless a version bump of the .NET runtime is required (i.e. from 8.0.x to 10.0.x).
   - Depending on the os you might also either need to install `libicu` / `icu-libs` / `libicu-dev` using your package manager for .NET to run or setting `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` in your servers environment variables. You can find more infos about that [here](https://github.com/dotnet/runtime/blob/main/docs/design/features/globalization-invariant-mode.md#enabling-the-invariant-mode)
3. Extract the `addons` folder to the `/csgo/` directory of the dedicated server.
4. Start the server. If everything is working correctly, you should see a message in the console that says `CounterStrikeSharp.API Loaded Successfully.`
## Crash reporting

CounterStrikeSharp writes crash evidence to `csgo/dumps/`. **No setup is
required** — this is on by default, per server, with no launch-wrapper change.

| File | Written | Contents |
|---|---|---|
| `last_state.txt` | continuously, before any crash | server id, map, last console command and who ran it |
| `crashes.log` | on SIGABRT | the above plus signal, last native→managed callback, suspect plugin |
| `cssharp-<pid>-<time>.dmp` | on a managed crash | full .NET minidump |

`last_state.txt` is the one that survives crashes nothing can catch — a native
segfault (CoreCLR owns SIGSEGV, so we deliberately do not hook it), the OOM
killer, or a hang with no signal at all. It is already on disk by the time the
server dies.

Dumps work without installing anything: `createdump` and `libmscordaccore.so`
ship inside the bundled runtime, and the plugin sets the runtime's dump
variables on the process before booting it. Read one with:

```bash
dotnet-dump analyze csgo/dumps/cssharp-1234-1699999999.dmp
> clrstack -all      # managed stacks for every thread
> clrthreads
> pe -lines          # the exception that killed it
```

Tuning lives in `configs/core.json`:

| Key | Default | Meaning |
|---|---|---|
| `CrashDumpsEnabled` | `true` | Write .NET minidumps at all |
| `CrashDumpType` | `2` | 1=Mini, 2=Heap, 3=Triage, 4=Full |
| `CrashDumpRetention` | `5` | Keep the newest N dumps, delete the rest at startup |

Retention matters: a crash loop writes one dump per restart, and a Heap dump of
a CS2 server is not small. Pruning runs at startup so a repeating crash cannot
fill the disk.

The server id defaults to `hostname:port`, which is already unique across a
fleet. Set `CSSHARP_SERVER_ID` only if you want friendlier names — it is what
turns "a server crashed" into "these four crashed, same signature".
`tools/crashdumps.env` holds optional per-server overrides; anything already set
on the process wins, the plugin never overwrites it.
