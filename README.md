<div align=right>Table of Contents ↗️</div>

<h1 align=center><code>CounterStrikeSharp - Forked</code></h1>
<br>

CounterStrikeSharp is a server-side modding framework for Counter-Strike 2. This project implements a .NET 10 scripting layer on top of a Metamod Source Plugin, allowing developers to create plugins that interact with the game server in a modern language (C#) to facilitate the creation of maintainable and testable code.

**Forked version by Miksen for own servers**
- Supports Debian 13 & Ubuntu 24.04
- Supports CommandLine API (GetCommandLineString)

## Install

Download the latest build from [here](https://github.com/mrc4tt/CounterStrikeSharp/releases). (Download the with-runtime version if this is your first time installing).

Detailed installation instructions can be found in the [docs](https://docs.cssharp.dev/docs/guides/getting-started.html).

## What works?

These features are the core of the platform and work pretty well/have a low risk of causing issues.

- [x] Console Commands, Server Commands (e.g. css_mycommand)
- [x] Chat Commands with `!` and `/` prefixes (e.g. !mycommand)
- [x] Fake Console Variables (commands which mimic ConVar behaviour as these have not been fully reverse engineered) 
- [x] Game Event Handlers & Firing of Events (e.g. player_death)
  - [x] Basic event value get/set (string, bool, int32, float)
  - [x] Complex event values get/set (ehandle, pawn, player controller)
- [x] Game Tick Based Timers (e.g. repeating map timers)
  - [x] Timer Flags (REPEAT, STOP_ON_MAPCHANGE)
- [x] Listeners (e.g. client connected, disconnected, map start etc.)
  - [x] Client Listeners (e.g. connect, disconnect, put in server)
  - [x] OnMapStart
  - [x] OnTick
- [x] Server Information (current map, game time)
- [x] Schema System Access (access player values like current weapon, money, location etc.)

## Credits

A lot of code has been borrowed from [SourceMod](https://github.com/alliedmodders/sourcemod) as well as [Source.Python](https://github.com/Source-Python-Dev-Team/Source.Python), two pioneering source engine plugin frameworks which this project lends a lot of its credit to.
I've also used the scripting context & native system that is implemented in [FiveM](https://github.com/citizenfx/fivem) for GTA5. Also shoutout to the [CS2Fixes](https://github.com/Source2ZE/CS2Fixes) project for providing good reverse-engineering information so shortly after CS2 release.

## How to Build

Building requires CMake.

Clone the repository

```bash
git clone https://github.com/roflmuffin/counterstrikesharp
```

Init and update submodules

```bash
git submodule update --init --recursive
```

Make build folder

```bash
mkdir build
cd build
```

Generate CMake Build Files

```bash
cmake ..
```

Build

```bash
cmake --build . --config Debug
```

License
-------
CounterStrikeSharp is licensed under the GNU General Public License version 3. A special exemption is outlined regarding published plugins, which you can find in the [LICENSE](LICENSE) file.
