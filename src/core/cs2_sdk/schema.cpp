/**
 * =============================================================================
 * CS2Fixes
 * Copyright (C) 2023 Source2ZE
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

#include "schema.h"

#include "interfaces/cs2_interfaces.h"
#include "core/globals.h"
#include "core/memory.h"
#include "core/log.h"

#include "tier1/utlmap.h"
#include <schemasystem.h>
#include <entity2/entitysystem.h>
#include <entity2/entityclass.h>
#include <networksystem/inetworkserializer.h>

// memdbgon must be the last include file in a .cpp file!!!
#include "tier0/memdbgon.h"

using SchemaKeyValueMap_t = CUtlOrderedMap<uint32_t, SchemaKey>;
using SchemaTableMap_t = CUtlOrderedMap<uint32_t, SchemaKeyValueMap_t*>;

static CNetworkSerializerCodeGenDatabase* GetNetworkSerializerDatabase()
{
    if (!GameEntitySystem()) return nullptr;

    CEntityClass* pEntityClass = GameEntitySystem()->FindClassByName("CBaseEntity");
    if (!pEntityClass || !pEntityClass->m_NetworkSerializerInfo) return nullptr;

    return pEntityClass->m_NetworkSerializerInfo->m_pDatabase;
}

static CNetworkSerializerClassInfo* FindNetworkSerializerClassInfo(const char* className)
{
    CNetworkSerializerCodeGenDatabase* pDatabase = GetNetworkSerializerDatabase();
    if (!pDatabase) return nullptr;

    auto index = pDatabase->m_ClassInfos.Find(className);
    if (index == pDatabase->m_ClassInfos.InvalidIndex()) return nullptr;

    return pDatabase->m_ClassInfos[index];
}

static bool IsFieldNetworked(CNetworkSerializerClassInfo* pNetworkClassInfo, SchemaClassFieldData_t& field)
{
    if (!pNetworkClassInfo) return false;
    return pNetworkClassInfo->FindField(field.m_pszName) != nullptr;
}

static bool InitSchemaFieldsForClass(SchemaTableMap_t* tableMap, const char* className, uint32_t classKey)
{
    CSchemaSystemTypeScope* pType = counterstrikesharp::globals::schemaSystem->FindTypeScopeForModule(MODULE_PREFIX "server" MODULE_EXT);

    if (!pType) return false;

    SchemaClassInfoData_t* pClassInfo = pType->FindDeclaredClass(className).Get();

    if (!pClassInfo)
    {
        SchemaKeyValueMap_t* map = new SchemaKeyValueMap_t(0, 0);
        tableMap->Insert(classKey, map);

        Warning("InitSchemaFieldsForClass(): '%s' was not found!\n", className);
        return false;
    }

    CNetworkSerializerClassInfo* pNetworkClassInfo = FindNetworkSerializerClassInfo(className);

    SchemaKeyValueMap_t* keyValueMap = new SchemaKeyValueMap_t(0, 0);
    keyValueMap->EnsureCapacity(pClassInfo->m_nFieldCount);
    tableMap->Insert(classKey, keyValueMap);

    // Walk the class and then its base classes, so a field is found through any class that
    // inherits it: `SetStateChanged(pawn, "CCSPlayer_WeaponServices", "m_hActiveWeapon")`
    // names a field declared on CPlayer_WeaponServices, and used to resolve to offset 0,
    // silently (it then read as "not networked", so the call did nothing). A field the
    // derived class redeclares wins, because it is inserted first.
    //
    // __m_pChainEntity is deliberately NOT inherited. Utilities.SetStateChanged(entity,
    // className, ...) probes it on className and, when found, notifies through
    // `entity + chainOffset`: that pointer is only meaningful for the object that owns the
    // chain, never for the entity a component-class name is usually paired with. Inheriting
    // it would turn those calls from a no-op into a notify through a garbage pointer.
    uint32_t baseOffset = 0;
    for (SchemaClassInfoData_t* pCurrent = pClassInfo; pCurrent != nullptr;)
    {
        const bool isDeclaringClass = pCurrent == pClassInfo;
        CNetworkSerializerClassInfo* pCurrentNetworkInfo =
            isDeclaringClass ? pNetworkClassInfo : FindNetworkSerializerClassInfo(pCurrent->m_pszName);

        for (int i = 0; i < pCurrent->m_nFieldCount; ++i)
        {
            SchemaClassFieldData_t& field = pCurrent->m_pFields[i];

            if (!isDeclaringClass && V_strcmp(field.m_pszName, "__m_pChainEntity") == 0) continue;

            auto fieldKey = hash_32_fnv1a_const(field.m_pszName);
            if (keyValueMap->IsValidIndex(keyValueMap->Find(fieldKey))) continue;

            auto offset = static_cast<int32_t>(baseOffset + field.m_nSingleInheritanceOffset);
            bool networked = IsFieldNetworked(pNetworkClassInfo, field) || IsFieldNetworked(pCurrentNetworkInfo, field);

            if (field.m_pType->m_eTypeCategory == SCHEMA_TYPE_ATOMIC && field.m_pType->m_eAtomicCategory == SCHEMA_ATOMIC_COLLECTION_OF_T)
                keyValueMap->Insert(fieldKey,
                                    { offset, networked, static_cast<CSchemaType_Atomic_CollectionOfT*>(field.m_pType)->m_pfnManipulator });
            else
                keyValueMap->Insert(fieldKey, { offset, networked });
        }

        if (pCurrent->m_nBaseClassCount == 0 || !pCurrent->m_pBaseClasses) break;
        baseOffset += pCurrent->m_pBaseClasses[0].m_nOffset;
        pCurrent = pCurrent->m_pBaseClasses[0].m_pClass;
    }

    return true;
}

int16_t schema::FindChainOffset(const char* className)
{
    CSchemaSystemTypeScope* pType = counterstrikesharp::globals::schemaSystem->FindTypeScopeForModule(MODULE_PREFIX "server" MODULE_EXT);

    if (!pType) return false;

    auto* pClassInfo = pType->FindDeclaredClass(className).Get();

    do
    {
        SchemaClassFieldData_t* pFields = pClassInfo->m_pFields;
        short fieldsSize = pClassInfo->m_nFieldCount;
        for (int i = 0; i < fieldsSize; ++i)
        {
            SchemaClassFieldData_t& field = pFields[i];

            if (V_strcmp(field.m_pszName, "__m_pChainEntity") == 0)
            {
                return field.m_nSingleInheritanceOffset;
            }
        }
    } while ((pClassInfo = pClassInfo->m_pBaseClasses->m_pClass) != nullptr);

    return 0;
}

SchemaKey schema::GetOffset(const char* className, uint32_t classKey, const char* memberName, uint32_t memberKey)
{
    static SchemaTableMap_t schemaTableMap(0, 0);
    auto tableMapIndex = schemaTableMap.Find(classKey);
    if (!schemaTableMap.IsValidIndex(tableMapIndex))
    {
        if (InitSchemaFieldsForClass(&schemaTableMap, className, classKey)) return GetOffset(className, classKey, memberName, memberKey);

        return { 0, 0 };
    }

    SchemaKeyValueMap_t* tableMap = schemaTableMap[tableMapIndex];
    auto memberIndex = tableMap->Find(memberKey);
    if (!tableMap->IsValidIndex(memberIndex))
    {
        // An unknown name still resolves to offset 0 (callers depend on getting a key back),
        // but no longer silently: a typo or a field Valve removed reads/writes the start of
        // the object. Warn once, then cache the miss so the warning does not repeat.
        // __m_pChainEntity is probed on every class by SetStateChanged; its absence is normal.
        if (V_strcmp(memberName, "__m_pChainEntity") != 0)
        {
            CSSHARP_CORE_WARN("Schema field '{}::{}' not found (not declared on the class or its bases); using offset 0", className,
                              memberName);
        }
        tableMap->Insert(memberKey, { 0, false });
        return { 0, 0 };
    }

    return tableMap->Element(memberIndex);
}

void NetworkStateChanged(uintptr_t chainEntity, uint32_t offset, uint32_t nArrayIndex, uint32_t nPathIndex)
{
    CNetworkStateChangedInfo info(offset, nArrayIndex, nPathIndex);

    if (counterstrikesharp::globals::NetworkStateChanged)
        counterstrikesharp::globals::NetworkStateChanged(reinterpret_cast<void*>(chainEntity), info);
}

void SetStateChanged(uintptr_t pEntity, uint32_t offset, uint32_t nArrayIndex, uint32_t nPathIndex)
{
    CNetworkStateChangedInfo info(offset, nArrayIndex, nPathIndex);

    static auto fnOffset = counterstrikesharp::globals::gameConfig->GetOffset("SetStateChanged");

    if (fnOffset < 0)
    {
        static bool bWarned = false;

        if (!bWarned)
        {
            bWarned = true;
            CSSHARP_CORE_WARN("gamedata: offset 'SetStateChanged' is missing, network state updates are disabled");
        }

        return;
    }

    CALL_VIRTUAL(void, fnOffset, (void*)pEntity, &info);
}
