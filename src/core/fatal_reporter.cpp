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
#include <cstring>

#ifndef _WIN32
#include <unistd.h>
#else
#include <io.h>
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

// Returns false when this abort is not attributable to a native->managed
// callback (no active breadcrumb). In that case we stay silent so unrelated
// SIGABRTs (normal shutdown, other crashes) do not get blamed on a plugin.
static bool report(int sig)
{
    const char* cb = g_callbackName.load(std::memory_order_relaxed);
    int idx = g_callbackIndex.load(std::memory_order_relaxed);
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
}

void ClearCallbackBreadcrumb() { g_callbackIndex.store(-1, std::memory_order_relaxed); }

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

} // namespace counterstrikesharp::fatal
