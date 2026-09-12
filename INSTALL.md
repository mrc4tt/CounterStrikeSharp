# Installation

1. Download and install Metamod:Source for CS2. Detailed instructions can be found [here](https://cs2.poggu.me/metamod/installation/).
2. Download the latest release of CounterStrikeSharp from [here](https://github.com/roflmuffin/CounterStrikeSharp/actions/workflows/cmake-single-platform.yml).
   - If this is your first time installing, you will need to download the `with-runtime` version. This includes the .NET runtime, which is required to run the plugin.
   - Subsequent upgrades will not require the runtime, unless a version bump of the .NET runtime is required (i.e. from 8.0.x to 10.0.x).
   - Depending on the os you might also either need to install `libicu` / `icu-libs` / `libicu-dev` using your package manager for .NET to run or setting `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` in your servers environment variables. You can find more infos about that [here](https://github.com/dotnet/runtime/blob/main/docs/design/features/globalization-invariant-mode.md#enabling-the-invariant-mode)
3. Extract the `addons` folder to the `/csgo/` directory of the dedicated server.
4. Start the server. If everything is working correctly, you should see a message in the console that says `CounterStrikeSharp.API Loaded Successfully.`
## Crash reporting

CounterStrikeSharp writes crash evidence to `csgo/dumps/`:

| File | Written | Contents |
|---|---|---|
| `last_state.txt` | continuously, before any crash | server id, map, last console command and who ran it |
| `crashes.log` | on SIGABRT | the above plus signal, last native→managed callback, suspect plugin |
| `cssharp-<pid>-<time>.dmp` | on managed crash, when enabled | full .NET minidump |

`last_state.txt` is the one that survives crashes nothing can catch — a native
segfault (CoreCLR owns SIGSEGV, so we deliberately do not hook it), the OOM
killer, or a hang with no signal at all. It is already on disk by the time the
server dies.

Dumps are off until you enable them. Source `tools/crashdumps.env` from the
launch wrapper, then read a dump with:

```bash
dotnet-dump analyze csgo/dumps/cssharp-1234-1699999999.dmp
> clrstack -all      # managed stacks for every thread
> clrthreads
> pe -lines          # the exception that killed it
```

`createdump` and `libmscordaccore.so` ship inside the bundled runtime, so
nothing extra needs installing on the server.

Set `CSSHARP_SERVER_ID` per server when running a fleet — it is what lets you
tell one recurring fault from many unrelated ones.
