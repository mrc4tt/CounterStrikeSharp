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

#include "core/crash_recorder.h"

#include <chrono>
#include <cstdio>
#include <cstddef>
#include <cstring>

#include "core/globals.h"
#include "core/managers/player_manager.h"
#include <entity2/entitysystem.h>
#include "entity/dump.h"

namespace counterstrikesharp::crash {

static CssCrashContext g_context = [] {
    CssCrashContext context{};
    context.magic = CSS_CRASH_CONTEXT_MAGIC;
    context.version = CSS_CRASH_CONTEXT_VERSION;
    context.size = sizeof(CssCrashContext);
    return context;
}();

static_assert(sizeof(CssCrashEntry) == 256, "CssCrashEntry layout is shared with managed code and AcceleratorCS2");
static_assert(sizeof(CssCrashContext) == 45096, "CssCrashContext layout is shared with managed code (CrashRecorder.cs)");
static_assert(offsetof(CssCrashContext, activity) == 2088, "CssCrashContext layout is shared with managed code (CrashRecorder.cs)");

// Player slots go up to 64 (MAXPLAYERS is not available here).
constexpr int kMaxSlots = 64;

// m_steamID last journaled per slot, so a spoofed ID is reported once and not every check.
static uint64_t g_reportedSteamId[kMaxSlots];

// Checking every frame is cheap but pointless; 16 frames is ~0.25s at 64 tick.
constexpr int kSteamIdCheckInterval = 16;

// A journal entry is blamed on the plugin of the latest activity when that activity is this recent.
constexpr int64_t kSuspectWindowMs = 2000;

static int64_t NowMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

static void CopyField(char* dest, size_t size, const char* src)
{
    if (!src) src = "";
    strncpy(dest, src, size - 1);
    dest[size - 1] = '\0';
}

CssCrashContext* Get() { return &g_context; }

void Journal(const char* plugin, const char* kind, const char* detail)
{
    const int64_t now = NowMs();

    char suspect[CSS_CRASH_PLUGIN_LENGTH] = "unknown";
    if (plugin && plugin[0])
    {
        CopyField(suspect, sizeof(suspect), plugin);
    }
    else
    {
        const CssCrashEntry& last = g_context.activity[(g_context.activityNext + CSS_CRASH_ACTIVITY_SIZE - 1) % CSS_CRASH_ACTIVITY_SIZE];
        if (last.unixMs && now - last.unixMs <= kSuspectWindowMs) CopyField(suspect, sizeof(suspect), last.plugin);
    }

    // Fill the slot first, publish the index last, so a reader never sees a half-written newest entry.
    uint32_t slot = g_context.journalNext % CSS_CRASH_JOURNAL_SIZE;
    CssCrashEntry& entry = g_context.journal[slot];
    entry.unixMs = now;
    CopyField(entry.plugin, sizeof(entry.plugin), suspect);
    CopyField(entry.kind, sizeof(entry.kind), kind);
    CopyField(entry.detail, sizeof(entry.detail), detail);
    g_context.journalNext = (slot + 1) % CSS_CRASH_JOURNAL_SIZE;
}

static void CheckSteamIds()
{
    if (!globals::entitySystem) return;

    const int maxClients = globals::playerManager.MaxClients();
    for (int i = 0; i < maxClients && i < kMaxSlots; i++)
    {
        auto player = globals::playerManager.GetPlayerBySlot(i);
        const CSteamID* authenticated = player && player->IsConnected() && !player->IsFakeClient() ? player->GetSteamId() : nullptr;
        if (!authenticated || !authenticated->IsValid())
        {
            g_reportedSteamId[i] = 0;
            continue;
        }

        auto controller = (CBasePlayerController*)globals::entitySystem->GetEntityInstance(CEntityIndex(i + 1));
        if (!controller) continue;

        const uint64_t real = authenticated->ConvertToUint64();
        const uint64_t shown = controller->m_steamID();
        if (shown == real || shown == 0)
        {
            g_reportedSteamId[i] = 0;
            continue;
        }
        if (g_reportedSteamId[i] == shown) continue;
        g_reportedSteamId[i] = shown;

        char detail[CSS_CRASH_DETAIL_LENGTH];
        snprintf(detail, sizeof(detail), "slot %d m_steamID changed to %llu, authenticated SteamID is %llu", i, (unsigned long long)shown,
                 (unsigned long long)real);
        Journal(nullptr, "steamid", detail);
    }
}

void OnGameFrame()
{
    static int frame = 0;
    if (++frame % kSteamIdCheckInterval == 0) CheckSteamIds();
}

} // namespace counterstrikesharp::crash
