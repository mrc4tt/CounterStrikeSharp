/**
 * =============================================================================
 * CS2Fixes
 * Copyright (C) 2023-2024 Source2ZE
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 */

#include "core/log.h"
#include "core/globals.h"
#include "core/gameconfig.h"
#include "core/game_system.h"
#include "core/managers/server_manager.h"
#include "core/managers/player_manager.h"
#include "scripting/callback_manager.h"
#include <tier0/vprof.h>

CBaseGameSystemFactory** CBaseGameSystemFactory::sm_pFirst = nullptr;

CGameSystem g_GameSystem;
IGameSystemFactory* CGameSystem::sm_Factory = nullptr;
bool CGameSystem::sm_Active = false;

IEntityResourceManifest* m_exportResourceManifest = nullptr;

// This mess is needed to get the pointer to sm_pFirst so we can insert game systems
bool InitGameSystems()
{
    // This signature directly points to the instruction referencing sm_pFirst, and the opcode is 3
    // bytes so we skip those
    auto* pAnchor = (uint8*)counterstrikesharp::globals::gameConfig->ResolveSignature("IGameSystem_InitAllSystems_pFirst");

    // Check the raw result: `nullptr + 3` is never null, so the old check never fired.
    if (!pAnchor)
    {
        CSSHARP_CORE_ERROR("Failed to InitGameSystems, see warnings above.");
        return false;
    }

    uint8* ptr = pAnchor + 3;

    // Grab the offset as 4 bytes
    uint32 offset = *(uint32*)ptr;

    // Go to the next instruction, which is the starting point of the relative jump
    ptr += 4;

    // Now grab our pointer
    CBaseGameSystemFactory::sm_pFirst = (CBaseGameSystemFactory**)(ptr + offset);

    // And insert the game system(s). On `meta unload` + `meta load` the .so is still the same
    // mapping (see sm_Active), so the factory from the first load is still linked into the
    // engine's list and g_GameSystem is still registered: reuse it rather than link a second
    // factory for the same object.
    if (!CGameSystem::sm_Factory)
    {
        CGameSystem::sm_Factory = new CGameSystemStaticFactory<CGameSystem>("CSSharp_GameSystem", &g_GameSystem);
    }

    CGameSystem::sm_Active = true;
    return true;
}

void StopGameSystems() { CGameSystem::sm_Active = false; }

GS_EVENT_MEMBER(CGameSystem, BuildGameSessionManifest)
{
    if (!sm_Active) return;

    IEntityResourceManifest* pResourceManifest = msg->m_pResourceManifest;

    CSSHARP_CORE_INFO("CGameSystem::BuildGameSessionManifest");

    m_exportResourceManifest = pResourceManifest;

    counterstrikesharp::globals::serverManager.OnPrecacheResources(pResourceManifest);
}

GS_EVENT_MEMBER(CGameSystem, ServerPreEntityThink)
{
    if (!sm_Active) return;

    // VPROF_BUDGET("CS#::CGameSystem::ServerPreEntityThink", "CS# On Frame");
    auto callback = counterstrikesharp::globals::serverManager.on_server_pre_entity_think;

    if (callback && callback->GetFunctionCount())
    {
        callback->ScriptContext().Reset();
        callback->Execute();
    }

    auto globals = counterstrikesharp::globals::getGlobalVars();
    if (globals && globals->m_bInSimulation)
    {
        counterstrikesharp::globals::playerManager.RunThink();
    }
}

GS_EVENT_MEMBER(CGameSystem, ServerPostEntityThink)
{
    if (!sm_Active) return;

    // VPROF_BUDGET("CS#::CGameSystem::ServerPostEntityThink", "CS# On Frame");
    auto callback = counterstrikesharp::globals::serverManager.on_server_post_entity_think;

    if (callback && callback->GetFunctionCount())
    {
        callback->ScriptContext().Reset();
        callback->Execute();
    }
}
