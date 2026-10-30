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

using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API.Modules.Commands.Targeting;
using CounterStrikeSharp.API.Modules.Entities;
using FastGenericNew;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API
{
    public static class Utilities
    {
        // Only for networked entities, which are edicts. There can be more server-only entities that aren't edicts, so this is not the max number of entities in the game, but the max number of edicts.
        public const int MaxEdictBits = 14;
        public const int MaxEdicts = 1 << MaxEdictBits;

        // Counter-Strike 2 - max 16384 edicts and 16384 server-only entities, total 32768 entities, which is 15 bits for the entity index.
        public const int MaxEntityBits = 15;
        public const int MaxEntities = 1 << MaxEntityBits;

        public const int NumEHandleSerialNumberBits = 17;
        public const uint InvalidEHandleIndex = 0xFFFFFFFF;

        public static IEnumerable<T> FlagsToList<T>(this T flags) where T : Enum
        {
            return Enum.GetValues(typeof(T)).Cast<T>()
                .Where(x => flags.HasFlag(x)).AsEnumerable();
        }

        public static T? GetEntityFromIndex<T>(int index) where T : CEntityInstance
        {
            var entityPtr = EntitySystem.GetEntityByIndex((uint)index);
            if (entityPtr is null || entityPtr == IntPtr.Zero)
            {
                return null;
            }

            return FastNew.CreateInstance<T, IntPtr>(entityPtr.Value);
        }

        public static T? CreateEntityByName<T>(string name) where T : CBaseEntity
        {
            return FastNew.CreateInstance<T, IntPtr>(VirtualFunctions.UTIL_CreateEntityByNameFunc.Invoke(name, -1))!;
        }

        public static CCSPlayerController? GetPlayerFromIndex(int index)
        {
            var entityPtr = EntitySystem.GetEntityByIndex((uint)index);
            if (entityPtr is null || entityPtr == IntPtr.Zero)
            {
                return null;
            }

            return new CCSPlayerController(entityPtr.Value);
        }

        public static CCSPlayerController? GetPlayerFromSlot(int slot)
        {
            return GetPlayerFromIndex(slot + 1);
        }

        public static CCSPlayerController? GetPlayerFromUserid(int userid)
        {
            return GetPlayerFromIndex((userid & 0xFF) + 1);
        }

        public static CCSPlayerController? GetPlayerFromSteamId(ulong steamId)
        {
            return GetPlayers().FirstOrDefault(player => player.AuthorizedSteamID == (SteamID)steamId);
        }

        public static CCSPlayerController? GetPlayerFromSteamId64(ulong steamId)
        {
            return GetPlayers().FirstOrDefault(player => player.SteamID == steamId);
        }

        public static TargetResult ProcessTargetString(string pattern, CCSPlayerController player)
        {
            return new Target(pattern).GetTarget(player);
        }

        public static bool RemoveItemByDesignerName(this CCSPlayerController player, string designerName)
        {
            var weapon = player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                .Select(w => w.Value)
                .Where(w => w?.IsValid is true && w.DesignerName == designerName)
                .FirstOrDefault();

            if (weapon == null)
                return false;

            weapon.AddEntityIOEvent("Kill", weapon, delay: 0.1f);
            return true;
        }

        public static bool RemoveItemByDesignerName(this CCSPlayerController player, string designerName, bool _)
        {
            return RemoveItemByDesignerName(player, designerName);
        }

        public static bool RemoveItemBySlot(this CCSPlayerController player, gear_slot_t slot)
        {
            var weapon = player.PlayerPawn.Value?.WeaponServices?.MyWeapons
                .Select(w => w.Value)
                .Where(w => w?.As<CCSWeaponBase>().VData?.GearSlot == slot)
                .FirstOrDefault();

            if (weapon == null)
                return false;

            weapon.AddEntityIOEvent("Kill", weapon, delay: 0.1f);
            return true;
        }

        public static IEnumerable<T> FindAllEntitiesByDesignerName<T>(string designerName) where T : CEntityInstance
        {
            // Walks the identity list on raw pointers and matches the designer name as UTF-8 bytes. Before, each
            // entity got a CEntityIdentity wrapper, and its designer name was decoded into a new string twice
            // (null check + Contains), byte by byte: hundreds of allocations per call on a full server.
            // Same rule as before: the designer name contains designerName (ordinal), null names never match.
            var needle = Encoding.UTF8.GetBytes(designerName);
            var designerNameOffset = s_identityDesignerNameField.Offset;
            var nextOffset = s_identityNextField.Offset;

            for (var identity = EntitySystem.FirstActiveEntity; identity != IntPtr.Zero; identity = Marshal.ReadIntPtr(identity + nextOffset))
            {
                if (!DesignerNameContains(identity + designerNameOffset, needle)) continue;
                yield return new PointerTo<T>(identity).Value;
            }
        }

        // The usual idiom, FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules,
        // walks the whole entity list (~2us on a live server) and plugins run it per tick or per event.
        // The proxy is cached by its entity handle: the serial check in GetEntityPointerByRawHandle fails
        // once the proxy is deleted (map change, round restart re-creating it), and then we walk again.
        private static uint s_gameRulesProxyHandle = 0xFFFFFFFF;
        private static CCSGameRulesProxy? s_gameRulesProxy;
        private static CCSGameRules? s_gameRules;

        /// <summary>
        /// Returns the <c>cs_gamerules</c> proxy entity, or null when there is none (no map loaded, or the entity system does not exist yet).
        /// Cached; the entity list is only walked again once the cached proxy no longer exists.
        /// </summary>
        public static CCSGameRulesProxy? GetGameRulesProxy()
        {
            var cached = s_gameRulesProxy;
            if (cached != null && EntitySystem.GetEntityPointerByRawHandle(s_gameRulesProxyHandle) == cached.Handle)
                return cached;

            CCSGameRulesProxy? proxy;
            try
            {
                proxy = FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            }
            catch (NativeException)
            {
                // Entity system not created yet (e.g. called from Load() on a cold start). Nothing is
                // cached, so the next call after the map is up walks the entity list again.
                proxy = null;
            }

            s_gameRulesProxy = proxy;
            s_gameRulesProxyHandle = proxy?.EntityHandle.Raw ?? 0xFFFFFFFF;
            s_gameRules = null;
            return proxy;
        }

        /// <summary>
        /// Returns the current <see cref="CCSGameRules"/>, or null when there is no gamerules proxy.
        /// Equivalent to <c>FindAllEntitiesByDesignerName&lt;CCSGameRulesProxy&gt;("cs_gamerules").First().GameRules</c>
        /// without walking the entity list on every call.
        /// </summary>
        public static CCSGameRules? GetGameRules()
        {
            var proxy = GetGameRulesProxy();
            if (proxy == null)
                return null;

            // m_pGameRules is re-read each call; only the wrapper is reused while it points at the same object.
            var pointer = s_gameRulesPointerField.Get(proxy.Handle);
            if (pointer == IntPtr.Zero)
                return null;

            var rules = s_gameRules;
            if (rules == null || rules.Handle != pointer)
                s_gameRules = rules = new CCSGameRules(pointer);

            return rules;
        }

        private static readonly SchemaField<IntPtr> s_gameRulesPointerField = new("CCSGameRulesProxy", "m_pGameRules");

        private static readonly SchemaField<IntPtr> s_identityDesignerNameField = new("CEntityIdentity", "m_designerName");
        private static readonly SchemaField<IntPtr> s_identityNextField = new("CEntityIdentity", "m_pNext");

        private static unsafe bool DesignerNameContains(IntPtr designerNameField, byte[] needle)
        {
            var name = *(byte**)designerNameField;
            if (name == null)
                return false;

            var length = 0;
            while (name[length] != 0)
                length++;

            return new ReadOnlySpan<byte>(name, length).IndexOf(needle) >= 0;
        }

        public static IEnumerable<CEntityInstance> GetAllEntities()
        {
            var pEntity = new CEntityIdentity(EntitySystem.FirstActiveEntity);
            for (; pEntity != null && pEntity.Handle != IntPtr.Zero; pEntity = pEntity.Next)
            {
                yield return new PointerTo<CEntityInstance>(pEntity.Handle).Value;
            }
        }

        /// <summary>
        /// Returns a list of <see cref="CCSPlayerController"/> that are valid and have a valid <see cref="CCSPlayerController.UserId"/> >= 0
        /// </summary>
        public static List<CCSPlayerController> GetPlayers()
        {
            // Pre-sized: plugins call this every tick, and a default-capacity List grows
            // 0 -> 4 -> 8 -> 16 -> 32 -> 64, discarding every intermediate array as Gen0
            // garbage on each call.
            var maxPlayers = Server.MaxPlayers;
            List<CCSPlayerController> players = new(maxPlayers);

            for (int i = 0; i < maxPlayers; i++)
            {
                // Check the slot on the raw entity pointer first and only build a wrapper for connected
                // players. Building one for every slot cost a native handle lookup plus two more handle
                // resolutions (IsValid, Connected) per slot, every tick, for every plugin calling this.
                var pointer = GetConnectedControllerPointer(i);
                if (pointer == IntPtr.Zero)
                    continue;

                players.Add(new CCSPlayerController(pointer));
            }

            return players;
        }

        private static readonly SchemaField<PlayerConnectedState> s_connectedField = new("CBasePlayerController", "m_iConnected");

        /// <summary>
        /// The controller pointer for a slot when the player is connected, otherwise IntPtr.Zero. Same
        /// conditions as GetPlayers used: entity present at index slot+1 (its handle matches, so it is
        /// valid) and m_iConnected == Connected.
        /// </summary>
        internal static IntPtr GetConnectedControllerPointer(int slot)
        {
            var pointer = EntitySystem.GetEntityByIndex((uint)(slot + 1));
            if (pointer is not { } entity || entity == IntPtr.Zero)
                return IntPtr.Zero;

            return s_connectedField.GetRef(entity) == PlayerConnectedState.Connected ? entity : IntPtr.Zero;
        }

        /// <summary>Bit mask (bit = slot) of the players GetPlayers would return, without building wrappers.</summary>
        internal static ulong GetConnectedPlayersMask()
        {
            ulong mask = 0;
            var maxPlayers = Math.Min(Server.MaxPlayers, 64);
            for (int i = 0; i < maxPlayers; i++)
            {
                if (GetConnectedControllerPointer(i) != IntPtr.Zero)
                    mask |= 1UL << i;
            }

            return mask;
        }

        [Obsolete]
        public static void ReplyToCommand(CCSPlayerController? player, string msg, bool console = false)
        {
            if (player != null)
            {
                if (console) player.PrintToConsole(msg);
                else player.PrintToChat(msg);
            }
            else
            {
                Server.PrintToConsole(msg);
            }
        }

        public static unsafe string ReadStringUtf8(IntPtr ptr)
        {
            unsafe
            {
                var nativeUtf8 = Unsafe.Read<IntPtr>((void*)ptr);

                if (nativeUtf8 == IntPtr.Zero)
                {
                    // A null native string stays null rather than becoming "": callers
                    // test for it. The return type stays non-nullable because every
                    // generated schema string property is built on this.
                    return null!;
                }

                // Same decoding as before (UTF-8 up to the terminator), without a per-byte
                // Marshal.ReadByte call and an intermediate byte[].
                return Marshal.PtrToStringUTF8(nativeUtf8)!;
            }
        }

        public static T? GetPointer<T>(IntPtr pointer) where T : NativeObject
        {
            var pointerTo = Marshal.ReadIntPtr(pointer);
            if (pointerTo == IntPtr.Zero)
            {
                return null;
            }

            return FastNew.CreateInstance<T, IntPtr>(pointerTo);
        }

        private static int FindSchemaChain(string className) => Schema.GetSchemaOffset(className, "__m_pChainEntity");

        /// <summary>
        /// Marks a field as changed for network transmission.
        /// Not all schema fields are network enabled, so please check the schema before using this.
        /// </summary>
        /// <param name="entity">Entity to update</param>
        /// <param name="className" example="CBaseEntity">Schema field class name</param>
        /// <param name="fieldName" example="m_iHealth">Schema field name</param>
        /// <param name="extraOffset">Any additional offset to the schema field</param>
        /// <exception cref="InvalidOperationException">Entity is not valid</exception>
        public static void SetStateChanged(CBaseEntity entity, string className, string fieldName, int extraOffset = 0)
        {
            Guard.IsValidEntity(entity);

            // Skip non-networked fields BEFORE forwarding on either path (matches upstream).
            // A field with no send-table entry cannot be resolved by the engine; forwarding it
            // — direct OR via the chain entity — makes the engine spam "Couldn't resolve offset
            // <N> in <Class>" on EVERY call (seen with plugins that SetStateChanged inventory/
            // weapon-slot sub-fields on CCSPlayerController, e.g. offset 3920). The previous fork
            // behaviour forwarded anyway (commit 727c6f98, no rationale) which caused the spam.
            // Mark the correct networked parent (the pointer/handle field) instead.
            if (!Schema.IsSchemaFieldNetworked(className, fieldName))
            {
                Application.Instance.Logger.LogDebug(
                    "Field {ClassName}:{FieldName} is not send-table networked; skipping SetStateChanged "
                    + "(the engine cannot resolve a non-networked offset). Mark the networked parent instead.",
                    className, fieldName);
                return;
            }

            int offset = Schema.GetSchemaOffset(className, fieldName);
            int chainOffset = FindSchemaChain(className);

            NotifyStateChanged(entity, offset + extraOffset, chainOffset);
        }

        /// <summary>
        /// The notification half of <see cref="SetStateChanged"/> with every lookup already
        /// resolved, shared with <see cref="Modules.Memory.SchemaField{T}"/> so both mark a
        /// networked field changed the same way. The caller has already established that
        /// the field is networked.
        /// </summary>
        internal static void NotifyStateChanged(CBaseEntity entity, int offset, int chainOffset)
        {
            if (chainOffset != 0)
            {
                NativeAPI.SchemaNetworkStateChanged(entity.Handle + chainOffset, (uint)offset, 0xFFFFFFFF, 0xFFFFFFFF);
                return;
            }

            NativeAPI.SchemaSetStateChanged(entity.Handle, (uint)offset, 0xFFFFFFFF, 0xFFFFFFFF);

            entity.LastNetworkChange = Server.CurrentTime;
            entity.IsSteadyState.Clear();
        }

        /// <summary>
        /// metamod method 'MetaFactory' to get the pointer of api interface exposed by metamod plugins.
        /// Returns null when the interface cannot be found.
        /// </summary>
        /// <param name="interfaceName">The interface name of metamod api, can be found in their api header file</param>
        public static IntPtr? MetaFactory(string interfaceName)
        {
            IntPtr ptr = NativeAPI.MetaFactory(interfaceName);
            if (ptr == 0)
            {
                return null;
            }

            return ptr;
        }
    }
}
