<div align="center">
  <h1><code>CounterStrikeSharp (mrc4tt Fork)</code></h1>
  <p>A server-side modding framework for Counter-Strike 2 implementing a <strong>.NET 10</strong> scripting layer on top of Metamod:Source.</p>

  <a href="https://github.com/mrc4tt/CounterStrikeSharp/releases">
    <img src="https://img.shields.io/github/v/release/mrc4tt/CounterStrikeSharp?label=Latest%20Release&style=flat-square" alt="Latest Release" />
  </a>
  <a href="https://discord.gg/zuQN3U2ZEh">
    <img src="https://img.shields.io/badge/Discord-Join%20the%20server-5865F2?style=flat-square&logo=discord&logoColor=white" alt="Discord" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-GPLv3-blue.svg?style=flat-square" alt="License" />
  </a>
</div>

<br>

## ✨ What's different in this fork?

This is a maintained fork of [roflmuffin/CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp), tailored for modern server environments and extended stability:

* **.NET 10 Runtime:** Updated scripting layer running on .NET 10.
* **Extended Platform Support:** Built and tested for **Windows**, **Debian 13**, and **Ubuntu 24.04**.
* **CommandLine API:** Added support for `GetCommandLineString`.
* **Automatic GameData Updates:** Keeps offsets and signatures up-to-date automatically after CS2 updates.

---

## 🚀 Installation

1. Download the latest build for your OS from the **[Releases page](https://github.com/mrc4tt/CounterStrikeSharp/releases)**. 
   *(Choose the `with-runtime` archive if this is your first time installing or if you don't have the .NET 10 runtime installed globally).*
2. Extract the contents into your server's `game/csgo/` directory.
3. Ensure [Metamod:Source](https://www.sourcemm.net/) is installed and configured properly.

For general plugin development and configuration guides, refer to the official [CounterStrikeSharp Documentation](https://docs.cssharp.dev/docs/guides/getting-started.html).

---

## 💬 Support & Community

Need help, found a bug, or have a feature idea? Join the community Discord server:

[![Discord](https://img.shields.io/badge/Discord-mrc4tt%20world-5865F2?logo=discord&logoColor=white)](https://discord.gg/zuQN3U2ZEh)

* **Questions & Setup Help:** Ask in the support channels on Discord.
* **Bug Reports:** Open a [GitHub Issue](https://github.com/mrc4tt/CounterStrikeSharp/issues) with your server logs, plugin version, and steps to reproduce. *(Feel free to do a quick check on Discord first).*
* **Feature Requests:** Post them on Discord or open an [issue](https://github.com/mrc4tt/CounterStrikeSharp/issues).

> *Please do not send support requests via direct message. Asking in public channels ensures others can help and find the answers later.*

---

## 🛠️ Building from Source

Building requires **CMake** and a compatible C++ compiler along with the **.NET 10 SDK**.

```bash
# Clone the repository
git clone [https://github.com/mrc4tt/CounterStrikeSharp.git](https://github.com/mrc4tt/CounterStrikeSharp.git)
cd CounterStrikeSharp

# Initialize and update submodules
git submodule update --init --recursive

# Generate CMake build files
mkdir build && cd build
cmake ..

# Build the project
cmake --build . --config Release
```

---

## 🙏 Credits

* **[roflmuffin](https://github.com/roflmuffin/CounterStrikeSharp)** for creating the original CounterStrikeSharp framework.
* **[SourceMod](https://github.com/alliedmodders/sourcemod)** & **[Source.Python](https://github.com/Source-Python-Dev-Team/Source.Python)** for pioneering Source engine plugin architecture.
* **[FiveM](https://github.com/citizenfx/fivem)** for inspiration on the scripting context and native system.
* **[CS2Fixes](https://github.com/Source2ZE/CS2Fixes)** for early CS2 reverse-engineering work.

---

## 📄 License

CounterStrikeSharp is licensed under the **GNU General Public License v3.0**. A special exemption is outlined regarding published plugins, which you can find in the [LICENSE](LICENSE) file.
