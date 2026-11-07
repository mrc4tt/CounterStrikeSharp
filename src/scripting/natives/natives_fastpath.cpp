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
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>.
 */

// Fast paths for the hottest natives.
//
// Every InvokeNative call costs ~35-40 ns before the handler does any work: the managed side fills a
// ScriptContext, native looks the handler up in a hash map, and managed checks the error flag and
// copies the result back. For a handler that is itself a few nanoseconds that overhead is the whole
// cost. GET_FAST_NATIVE_TABLE hands managed one struct of plain C function pointers, fetched once,
// which it then calls through `delegate* unmanaged` with no ScriptContext in between.
//
// Rules for an entry:
//  - extern "C"-compatible signature, primitives and pointers only;
//  - never throws, never logs on the hot path: report failure through the return value and let
//    managed raise the exception (it falls back to the ScriptContext native for the message);
//  - game thread only, like the ScriptContext natives it shortcuts.
// Append new entries at the end and bump kFastNativeTableVersion; managed refuses a table smaller
// than its version-1 layout and checks `size` before using any later entry, so a newer API still
// gets the old entries on an older core.

#include <cstddef>
#include <cstdint>

#include "core/log.h"
#include "scripting/autonative.h"
#include "scripting/script_engine.h"
#include "core/function.h"
#include "core/globals.h"
#include "schema.h"

#include <public/entity2/entityinstance.h>

namespace counterstrikesharp {

namespace {

// Same lookup as GET_CONVAR_VALUE_ADDRESS, minus the error reporting: nullptr means "invalid access
// index or no data", and the managed caller re-asks through the ScriptContext native to get the error.
void* FastGetConvarValueAddress(uint16_t accessIndex)
{
    auto cvar = ConVarRefAbstract(accessIndex);
    if (!cvar.IsValidRef() || !cvar.IsConVarDataValid()) return nullptr;

    CSplitScreenSlot server(0);
    return cvar.GetConVarData()->ValueOrDefault(server);
}

void FastSetStateChanged(uintptr_t entity, uint32_t offset, uint32_t arrayIndex, uint32_t pathIndex)
{
    SetStateChanged(entity, offset, arrayIndex, pathIndex);
}

void FastNetworkStateChanged(uintptr_t chainEntity, uint32_t offset, uint32_t arrayIndex, uint32_t pathIndex)
{
    NetworkStateChanged(chainEntity, offset, arrayIndex, pathIndex);
}

// globals->curtime / tickcount. The ScriptContext natives dereference getGlobalVars() unchecked, which
// is null before the game server exists; these return 0 then.
float FastGetCurrentTime()
{
    auto* vars = globals::getGlobalVars();
    return vars ? vars->curtime : 0.0f;
}

int32_t FastGetTickCount()
{
    auto* vars = globals::getGlobalVars();
    return vars ? vars->tickcount : 0;
}

// GET_REF_FROM_ENTITY_POINTER without the ScriptContext. Every entity wrapper constructed from a
// pointer (As<T>, CreateEntityByName, GetPlayers...) asks for its handle once; at ~40 ns of
// marshalling per ask that was most of a wrapper's cost. Same reads as the ScriptContext native.
uint32_t FastGetRefFromEntityPointer(uintptr_t entity)
{
    auto* pEntity = reinterpret_cast<CEntityInstance*>(entity);
    if (pEntity == nullptr) return INVALID_EHANDLE_INDEX;

    auto hndl = pEntity->GetRefEHandle();
    if (hndl == INVALID_EHANDLE_INDEX) return INVALID_EHANDLE_INDEX;

    return hndl.ToInt();
}

// The target of a ValveFunction (MemoryFunction handle) for a managed direct call, or nullptr when
// ExecuteVirtualFunction would refuse it. Called once per binding, not per call.
void* FastGetFunctionDirectTarget(ValveFunction* function) { return function != nullptr ? function->DirectCallTarget() : nullptr; }

constexpr uint32_t kFastNativeTableVersion = 2;

// Layout is mirrored by FastNatives.Table in managed/CounterStrikeSharp.API/Core/FastNatives.cs.
struct FastNativeTable
{
    uint32_t version;
    uint32_t size; // sizeof(FastNativeTable), so managed can tell an older core apart
    void* (*GetConvarValueAddress)(uint16_t accessIndex);
    void (*SetStateChanged)(uintptr_t entity, uint32_t offset, uint32_t arrayIndex, uint32_t pathIndex);
    void (*NetworkStateChanged)(uintptr_t chainEntity, uint32_t offset, uint32_t arrayIndex, uint32_t pathIndex);
    float (*CurrentTime)();
    int32_t (*TickCount)();
    // version 2
    uint32_t (*GetRefFromEntityPointer)(uintptr_t entity);
    void* (*GetFunctionDirectTarget)(ValveFunction* function);
};

static_assert(offsetof(FastNativeTable, GetConvarValueAddress) == 8);
static_assert(offsetof(FastNativeTable, SetStateChanged) == 16);
static_assert(offsetof(FastNativeTable, NetworkStateChanged) == 24);
static_assert(offsetof(FastNativeTable, CurrentTime) == 32);
static_assert(offsetof(FastNativeTable, TickCount) == 40);
static_assert(offsetof(FastNativeTable, GetRefFromEntityPointer) == 48);
static_assert(offsetof(FastNativeTable, GetFunctionDirectTarget) == 56);

const FastNativeTable g_fastNativeTable{
    .version = kFastNativeTableVersion,
    .size = sizeof(FastNativeTable),
    .GetConvarValueAddress = &FastGetConvarValueAddress,
    .SetStateChanged = &FastSetStateChanged,
    .NetworkStateChanged = &FastNetworkStateChanged,
    .CurrentTime = &FastGetCurrentTime,
    .TickCount = &FastGetTickCount,
    .GetRefFromEntityPointer = &FastGetRefFromEntityPointer,
    .GetFunctionDirectTarget = &FastGetFunctionDirectTarget,
};

void GetFastNativeTable(ScriptContext& script_context) { script_context.SetResult(const_cast<FastNativeTable*>(&g_fastNativeTable)); }

} // namespace

REGISTER_NATIVES(fastpath, { ScriptEngine::RegisterNativeHandler("GET_FAST_NATIVE_TABLE", GetFastNativeTable); })
} // namespace counterstrikesharp
