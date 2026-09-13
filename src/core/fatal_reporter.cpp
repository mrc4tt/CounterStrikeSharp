/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#include "core/fatal_reporter.h"

#include <atomic>
#include <csignal>
#include <ctime>
#include <vector>
#include <tuple>
#include <string>
#include <fstream>
#include <cstring>

#ifndef _WIN32
#include <unistd.h>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#else
#include <io.h>
#include <fcntl.h>
#include <sys/stat.h>
#ifndef STDERR_FILENO
#define STDERR_FILENO 2
#endif
#endif

namespace counterstrikesharp::fatal {

// Last native->managed callback (pointer to ScriptCallback::m_name's stable
// storage) + its index. Plain relaxed atomics: written on the game thread, read
// in the signal handler. No allocation, no locks -> async-signal-safe to read.
static std::atomic<const char*> g_callbackName{ nullptr };
static std::atomic<int> g_callbackIndex{ -1 };

// Fixed buffer, written only on the game thread (load failures). Read in the
// handler. No heap involved.
static char g_suspectPlugin[256] = { 0 };

// Command breadcrumb + map + identity. Same discipline as g_suspectPlugin: fixed
// buffers written on the game thread, read from the handler. g_commandSeq lets a
// reader tell a stale breadcrumb from a fresh one.
static char g_lastCommand[128] = { 0 };
static char g_lastCommandIssuer[64] = { 0 };
static char g_mapName[64] = { 0 };
static char g_serverId[128] = { 0 };
static char g_buildVersion[64] = { 0 };
static std::atomic<int> g_tick{ -1 };

// Paths are built once at startup. The handler must not construct them: no
// allocation, no snprintf with %s into a shared buffer, nothing that can fault.
static char g_reportPath[512] = { 0 };
static char g_statePath[512] = { 0 };
static bool g_reportingEnabled = false;

// The live state region: a fixed-layout text file, mmap'd MAP_SHARED.
//
// The callback breadcrumb changes thousands of times a second, so it cannot be
// written with open/write/close. Here every update is a plain store into mapped
// memory -- no syscall, same cost as the atomics it already used -- and the
// kernel owns the page. When the process dies in a way we cannot catch (a native
// SIGSEGV, SIGKILL, the OOM killer), the file on disk still holds whatever was
// written last. That is the difference between "a server died" and "a server
// died inside OnClientPutInServer, listener 2".
//
// Fields are fixed width so an update never has to shift the rest of the file.
static char* g_live = nullptr;
static size_t g_liveSize = 0;

struct LiveField
{
    size_t offset;
    size_t width;
};

static LiveField g_fTime, g_fTick, g_fMap, g_fCallback, g_fCallbackIndex, g_fServer, g_fBuild, g_fCommand, g_fIssuer;

// Copies value into a fixed-width field, space padded. Bounded and allocation
// free, so it is safe on the dispatch path and inside a signal handler.
static void live_set(const LiveField& f, const char* value)
{
    if (!g_live) return;
    char* dst = g_live + f.offset;
    size_t i = 0;
    if (value)
    {
        while (i < f.width && value[i] != '\0')
        {
            dst[i] = value[i];
            i++;
        }
    }
    while (i < f.width)
        dst[i++] = ' ';
}

static void live_set_int(const LiveField& f, long long v)
{
    if (!g_live) return;
    char buf[32];
    int i = (int)sizeof(buf);
    bool neg = v < 0;
    unsigned long long u = neg ? (unsigned long long)(-v) : (unsigned long long)v;
    if (u == 0) buf[--i] = '0';
    while (u > 0 && i > 0)
    {
        buf[--i] = (char)('0' + (u % 10));
        u /= 10;
    }
    if (neg && i > 0) buf[--i] = '-';
    buf[sizeof(buf) - 1] = buf[sizeof(buf) - 1]; // keep the compiler quiet about buf use
    char tmp[33];
    size_t n = sizeof(buf) - (size_t)i;
    if (n > sizeof(tmp) - 1) n = sizeof(tmp) - 1;
    memcpy(tmp, buf + i, n);
    tmp[n] = '\0';
    live_set(f, tmp);
}

// Bumped by every state-changing setter so WriteStateFile can skip the write when
// nothing happened since last time.
static std::atomic<unsigned> g_stateSeq{ 0 };
static unsigned g_stateWritten = 0;

static std::atomic<bool> g_installed{ false };

#ifndef _WIN32
static struct sigaction g_prevAbrt;
#else
static void (*g_prevAbrtWin)(int) = nullptr;
#endif

// ---- async-signal-safe output helpers (write(2) only) ----

static void safe_write(const char* s)
{
    if (!s) return;
    size_t n = 0;
    while (s[n] != '\0' && n < 8192)
        n++;
    // auto: ssize_t on POSIX, int on MSVC (_write). ssize_t is not declared by MSVC.
    auto r = write(STDERR_FILENO, s, n);
    (void)r;
}

static void safe_write_int(int v)
{
    char buf[16];
    int i = (int)sizeof(buf);
    bool neg = v < 0;
    unsigned uv = neg ? (unsigned)(-(long long)v) : (unsigned)v;
    if (uv == 0)
    {
        buf[--i] = '0';
    }
    else
    {
        while (uv > 0 && i > 0)
        {
            buf[--i] = (char)('0' + (uv % 10));
            uv /= 10;
        }
    }
    if (neg && i > 0) buf[--i] = '-';
    auto r = write(STDERR_FILENO, buf + i, sizeof(buf) - (size_t)i);
    (void)r;
}

// Same as safe_write but to an arbitrary fd, for the crash report file. Still
// write(2) only, so it is safe to call from the signal handler.
static void safe_write_fd(int fd, const char* s)
{
    if (fd < 0 || !s) return;
    size_t n = 0;
    while (s[n] != '\0' && n < 8192)
        n++;
    if (n == 0) return;
#ifndef _WIN32
    auto r = write(fd, s, n);
#else
    auto r = _write(fd, s, (unsigned int)n);
#endif
    (void)r;
}

static void safe_write_fd_int(int fd, long long v)
{
    char buf[32];
    int i = (int)sizeof(buf);
    bool neg = v < 0;
    unsigned long long u = neg ? (unsigned long long)(-v) : (unsigned long long)v;
    if (u == 0) buf[--i] = '0';
    while (u > 0 && i > 0)
    {
        buf[--i] = (char)('0' + (u % 10));
        u /= 10;
    }
    if (neg && i > 0) buf[--i] = '-';
    safe_write_fd(fd, "");
#ifndef _WIN32
    auto r = write(fd, buf + i, sizeof(buf) - (size_t)i);
#else
    auto r = _write(fd, buf + i, (unsigned int)(sizeof(buf) - (size_t)i));
#endif
    (void)r;
}

// Writes the crash report next to the state file. Opened O_APPEND so several
// crashes over a server's life accumulate rather than overwrite, and so a
// half-written report from a crash inside the crash never truncates an earlier
// good one.
static void write_report_file(int sig, const char* cb, int idx)
{
    if (!g_reportingEnabled || g_reportPath[0] == '\0') return;

#ifndef _WIN32
    int fd = open(g_reportPath, O_WRONLY | O_CREAT | O_APPEND, S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH);
#else
    int fd = _open(g_reportPath, _O_WRONLY | _O_CREAT | _O_APPEND, _S_IREAD | _S_IWRITE);
#endif
    if (fd < 0) return;

    safe_write_fd(fd, "-------- CSSHARP CRASH --------\n");
    // time() is on the async-signal-safe list, and an integer is all we can format
    // without allocating. Unix seconds is also what a collector wants anyway.
    safe_write_fd(fd, "time=");
    safe_write_fd_int(fd, (long long)time(nullptr));
    safe_write_fd(fd, "\nsignal=");
    safe_write_fd_int(fd, sig);
    safe_write_fd(fd, "\nbuild=");
    safe_write_fd(fd, g_buildVersion[0] ? g_buildVersion : "(unknown)");
    safe_write_fd(fd, "\ntick=");
    safe_write_fd_int(fd, g_tick.load(std::memory_order_relaxed));
    safe_write_fd(fd, "\nserver=");
    safe_write_fd(fd, g_serverId[0] ? g_serverId : "(unset)");
    safe_write_fd(fd, "\nmap=");
    safe_write_fd(fd, g_mapName[0] ? g_mapName : "(unknown)");
    safe_write_fd(fd, "\ncallback=");
    safe_write_fd(fd, cb ? cb : "(none)");
    safe_write_fd(fd, "\ncallback_index=");
    safe_write_fd_int(fd, idx);
    safe_write_fd(fd, "\nsuspect_plugin=");
    safe_write_fd(fd, g_suspectPlugin[0] ? g_suspectPlugin : "(none)");
    safe_write_fd(fd, "\nlast_command=");
    safe_write_fd(fd, g_lastCommand[0] ? g_lastCommand : "(none)");
    safe_write_fd(fd, "\nlast_command_issuer=");
    safe_write_fd(fd, g_lastCommandIssuer[0] ? g_lastCommandIssuer : "(none)");
    safe_write_fd(fd, "\n-------- END --------\n\n");

#ifndef _WIN32
    close(fd);
#else
    _close(fd);
#endif
}

// Returns false when this abort is not attributable to a native->managed
// callback (no active breadcrumb). In that case we stay silent so unrelated
// SIGABRTs (normal shutdown, other crashes) do not get blamed on a plugin.
static bool report(int sig)
{
    const char* cb = g_callbackName.load(std::memory_order_relaxed);
    int idx = g_callbackIndex.load(std::memory_order_relaxed);

    // The file is written for EVERY abort, including ones with no active
    // callback. A crash we cannot attribute is still a crash the host needs to
    // see, and the map/command/plugin context is what makes it comparable across
    // servers. The console block below stays conditional so unrelated aborts
    // (normal shutdown) do not get loudly blamed on a plugin.
    write_report_file(sig, cb, idx);

    if (idx < 0) return false;

    safe_write("\n>>> =============== CSSHARP FATAL ===============\n");
    safe_write(">>> Caught signal ");
    safe_write_int(sig);
    safe_write(" (process is terminating).\n");
    safe_write(">>> PLUGIN LOAD FAILURE (fatal): a native -> managed callback killed the server.\n");

    if (cb)
    {
        safe_write(">>> Last native -> managed callback: '");
        safe_write(cb);
        safe_write("' (index ");
        safe_write_int(idx);
        safe_write(")\n");
    }

    if (g_suspectPlugin[0] != '\0')
    {
        safe_write(">>> Suspect plugin: ");
        safe_write(g_suspectPlugin);
        safe_write("\n");
    }

    safe_write(">>> Likely cause: this plugin left a hook/timer/callback registered, then failed\n");
    safe_write(">>> to load or unloaded, and native code invoked a garbage-collected delegate\n");
    safe_write(">>> (\"A callback was made on a garbage collected delegate\"). Remove/disable it.\n");
    safe_write(">>> =============================================\n");
    return true;
}

#ifndef _WIN32
static void handler(int sig, siginfo_t* info, void* ucontext)
{
    report(sig);

    // Chain to the previous (CLR) handler so its crash dump still runs.
    if (g_prevAbrt.sa_flags & SA_SIGINFO)
    {
        if (g_prevAbrt.sa_sigaction)
        {
            g_prevAbrt.sa_sigaction(sig, info, ucontext);
            return;
        }
    }
    else if (g_prevAbrt.sa_handler != SIG_DFL && g_prevAbrt.sa_handler != SIG_IGN && g_prevAbrt.sa_handler)
    {
        g_prevAbrt.sa_handler(sig);
        return;
    }

    // No usable previous handler: restore default and re-raise so the process
    // still dies with the right status.
    signal(sig, SIG_DFL);
    raise(sig);
}
#else
static void handlerWin(int sig)
{
    report(sig);
    if (g_prevAbrtWin && g_prevAbrtWin != SIG_DFL && g_prevAbrtWin != SIG_IGN)
    {
        g_prevAbrtWin(sig);
        return;
    }
    signal(sig, SIG_DFL);
    raise(sig);
}
#endif

void InstallHandler()
{
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) return;

#ifndef _WIN32
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = handler;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGABRT, &sa, &g_prevAbrt);
#else
    g_prevAbrtWin = signal(SIGABRT, handlerWin);
#endif
}

void SetCallbackBreadcrumb(const char* callbackName, int index)
{
    g_callbackName.store(callbackName, std::memory_order_relaxed);
    g_callbackIndex.store(index, std::memory_order_relaxed);

    // Straight into the mapped page: no syscall, and it is what survives a crash
    // the signal handler never sees.
    live_set(g_fCallback, callbackName);
    live_set_int(g_fCallbackIndex, index);
}

void ClearCallbackBreadcrumb()
{
    g_callbackIndex.store(-1, std::memory_order_relaxed);
    live_set(g_fCallback, "(idle)");
    live_set_int(g_fCallbackIndex, -1);
}

void SetSuspectPlugin(const char* pluginName)
{
    if (!pluginName)
    {
        g_suspectPlugin[0] = '\0';
        return;
    }
    strncpy(g_suspectPlugin, pluginName, sizeof(g_suspectPlugin) - 1);
    g_suspectPlugin[sizeof(g_suspectPlugin) - 1] = '\0';
}

void SetCommandBreadcrumb(const char* command, const char* issuer)
{
    if (command)
    {
        strncpy(g_lastCommand, command, sizeof(g_lastCommand) - 1);
        g_lastCommand[sizeof(g_lastCommand) - 1] = '\0';
    }
    else
    {
        g_lastCommand[0] = '\0';
    }

    if (issuer)
    {
        strncpy(g_lastCommandIssuer, issuer, sizeof(g_lastCommandIssuer) - 1);
        g_lastCommandIssuer[sizeof(g_lastCommandIssuer) - 1] = '\0';
    }
    else
    {
        g_lastCommandIssuer[0] = '\0';
    }

    g_stateSeq.fetch_add(1, std::memory_order_relaxed);
    live_set(g_fCommand, g_lastCommand);
    live_set(g_fIssuer, g_lastCommandIssuer);
}

void SetMap(const char* mapName)
{
    if (!mapName)
    {
        g_mapName[0] = '\0';
    }
    else
    {
        strncpy(g_mapName, mapName, sizeof(g_mapName) - 1);
        g_mapName[sizeof(g_mapName) - 1] = '\0';
    }

    g_stateSeq.fetch_add(1, std::memory_order_relaxed);
    live_set(g_fMap, g_mapName);
}

#ifndef _WIN32
// Lays out dumps/live_state.txt and maps it. The layout is built once here so the
// hot path only ever writes into a known offset.
static void MapLiveState(const char* directory, size_t dirLen)
{
    char path[512];
    memcpy(path, directory, dirLen);
    memcpy(path + dirLen, "/live_state.txt", sizeof("/live_state.txt"));

    // Build the template in a scratch buffer, recording where each value starts.
    char tmpl[1024];
    size_t at = 0;
    auto add = [&](const char* key, size_t width, LiveField& field) {
        size_t klen = strlen(key);
        memcpy(tmpl + at, key, klen);
        at += klen;
        tmpl[at++] = '=';
        field.offset = at;
        field.width = width;
        for (size_t i = 0; i < width; ++i)
            tmpl[at++] = ' ';
        tmpl[at++] = '\n';
    };

    add("time", 16, g_fTime);
    add("tick", 12, g_fTick);
    add("server", 96, g_fServer);
    add("build", 48, g_fBuild);
    add("map", 40, g_fMap);
    add("callback", 56, g_fCallback);
    add("callback_index", 8, g_fCallbackIndex);
    add("last_command", 96, g_fCommand);
    add("last_command_issuer", 40, g_fIssuer);

    int fd = open(path, O_RDWR | O_CREAT | O_TRUNC, S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH);
    if (fd < 0) return;

    if (write(fd, tmpl, at) != (ssize_t)at)
    {
        close(fd);
        return;
    }

    void* mapped = mmap(nullptr, at, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);

    if (mapped == MAP_FAILED) return;

    g_live = (char*)mapped;
    g_liveSize = at;

    // Seed everything known at this point.
    live_set(g_fServer, g_serverId);
    live_set(g_fBuild, g_buildVersion);
    live_set(g_fMap, g_mapName);
    live_set(g_fCallback, "(none)");
    live_set_int(g_fCallbackIndex, -1);
    live_set(g_fCommand, g_lastCommand);
    live_set(g_fIssuer, g_lastCommandIssuer);
    live_set_int(g_fTime, (long long)time(nullptr));
    live_set_int(g_fTick, -1);
}
#else
static void MapLiveState(const char*, size_t) {}
#endif

void SetBuildVersion(const char* version)
{
    if (!version) return;
    strncpy(g_buildVersion, version, sizeof(g_buildVersion) - 1);
    g_buildVersion[sizeof(g_buildVersion) - 1] = '\0';
    g_stateSeq.fetch_add(1, std::memory_order_relaxed);
    live_set(g_fBuild, g_buildVersion);
}

void SetTick(int tick)
{
    g_tick.store(tick, std::memory_order_relaxed);
    live_set_int(g_fTick, tick);
    live_set_int(g_fTime, (long long)time(nullptr));
}

void ConfigureReporting(const char* directory, const char* serverId)
{
    if (serverId)
    {
        strncpy(g_serverId, serverId, sizeof(g_serverId) - 1);
        g_serverId[sizeof(g_serverId) - 1] = '\0';
    }

    if (!directory || directory[0] == '\0')
    {
        g_reportingEnabled = false;
        return;
    }

    // Build both paths once, here, so the signal handler only has to open() a
    // string that already exists.
    size_t dirLen = 0;
    while (directory[dirLen] != '\0' && dirLen < sizeof(g_reportPath) - 32)
        dirLen++;

    memcpy(g_reportPath, directory, dirLen);
    memcpy(g_reportPath + dirLen, "/crashes.log", sizeof("/crashes.log"));

    memcpy(g_statePath, directory, dirLen);
    memcpy(g_statePath + dirLen, "/last_state.txt", sizeof("/last_state.txt"));

    g_reportingEnabled = true;
    g_stateSeq.fetch_add(1, std::memory_order_relaxed);

    MapLiveState(directory, dirLen);
}

// (callback, index) -> plugin. Small, append-only in practice, and only touched
// at plugin load/unload.
static std::vector<std::tuple<std::string, int, std::string>> g_callbackOwners;
static char g_pendingOwner[128] = { 0 };

void SetPendingCallbackOwner(const char* pluginName)
{
    if (!pluginName)
    {
        g_pendingOwner[0] = '\0';
        return;
    }
    strncpy(g_pendingOwner, pluginName, sizeof(g_pendingOwner) - 1);
    g_pendingOwner[sizeof(g_pendingOwner) - 1] = '\0';
}

void RecordCallbackOwner(const char* callbackName, int index)
{
    if (!callbackName) return;

    // No pending owner means the listener came from CS# itself rather than a
    // plugin (Server.Initialize registers OnTick before any plugin loads).
    const char* owner = g_pendingOwner[0] != '\0' ? g_pendingOwner : "core";
    g_callbackOwners.emplace_back(callbackName, index, owner);
    g_pendingOwner[0] = '\0';

    if (!g_reportingEnabled || g_statePath[0] == '\0') return;

    // Rewritten whole rather than appended: listeners come and go with plugin
    // reloads, and a stale line here would point a crash at the wrong plugin.
    std::string path(g_statePath);
    path = path.substr(0, path.find_last_of('/') + 1) + "listeners.txt";

    std::ofstream out(path, std::ios::trunc);
    if (!out.is_open()) return;

    for (const auto& [cb, idx, plugin] : g_callbackOwners)
        out << cb << '[' << idx << "] = " << plugin << '\n';
}

void WriteStateFile()
{
    if (!g_reportingEnabled || g_statePath[0] == '\0') return;

    unsigned seq = g_stateSeq.load(std::memory_order_relaxed);
    if (seq == g_stateWritten) return; // nothing changed since the last write
    g_stateWritten = seq;

    // Truncating rewrite: this file answers "what was the server doing", so only
    // the newest answer matters. Not written from the signal handler -- by the
    // time a crash happens this is already on disk, which is the entire point.
#ifndef _WIN32
    int fd = open(g_statePath, O_WRONLY | O_CREAT | O_TRUNC, S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH);
#else
    int fd = _open(g_statePath, _O_WRONLY | _O_CREAT | _O_TRUNC, _S_IREAD | _S_IWRITE);
#endif
    if (fd < 0) return;

    safe_write_fd(fd, "time=");
    safe_write_fd_int(fd, (long long)time(nullptr));
    safe_write_fd(fd, "\nbuild=");
    safe_write_fd(fd, g_buildVersion[0] ? g_buildVersion : "(unknown)");
    safe_write_fd(fd, "\ntick=");
    safe_write_fd_int(fd, g_tick.load(std::memory_order_relaxed));
    safe_write_fd(fd, "\nserver=");
    safe_write_fd(fd, g_serverId[0] ? g_serverId : "(unset)");
    safe_write_fd(fd, "\nmap=");
    safe_write_fd(fd, g_mapName[0] ? g_mapName : "(unknown)");
    safe_write_fd(fd, "\nlast_command=");
    safe_write_fd(fd, g_lastCommand[0] ? g_lastCommand : "(none)");
    safe_write_fd(fd, "\nlast_command_issuer=");
    safe_write_fd(fd, g_lastCommandIssuer[0] ? g_lastCommandIssuer : "(none)");
    safe_write_fd(fd, "\n");

#ifndef _WIN32
    close(fd);
#else
    _close(fd);
#endif
}

} // namespace counterstrikesharp::fatal
