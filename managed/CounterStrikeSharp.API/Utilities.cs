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
            var needle = NeedleBytes(designerName);
            var designerNameOffset = s_identityDesignerNameField.Offset;
            var nextOffset = s_identityNextField.Offset;
            var walk = ++t_designerNameWalk;

            for (var identity = EntitySystem.FirstActiveEntity; identity != IntPtr.Zero; identity = Marshal.ReadIntPtr(identity + nextOffset))
            {
                if (!DesignerNameMatches(Marshal.ReadIntPtr(identity + designerNameOffset), needle, walk)) continue;
                yield return new PointerTo<T>(identity).Value;
            }
        }

        // The needle of the last call on this thread, by reference: callers pass the same literal
        // every time (per tick, per event), so its UTF-8 bytes are encoded once instead of per call.
        [ThreadStatic] private static string? t_lastNeedleText;
        [ThreadStatic] private static byte[]? t_lastNeedle;

        private static byte[] NeedleBytes(string designerName)
        {
            if (ReferenceEquals(designerName, t_lastNeedleText) && t_lastNeedle != null)
                return t_lastNeedle;

            var bytes = Encoding.UTF8.GetBytes(designerName);
            t_lastNeedleText = designerName;
            t_lastNeedle = bytes;
            return bytes;
        }

        // Entities of one class share their designer name string (same char*), so within one walk
        // each distinct name is compared once and remembered by its pointer. Entries are stamped
        // with the walk that wrote them and only trusted by that walk, so nothing carries over
        // between calls (a freed name's address reused for another name cannot give a stale
        // answer) and a nested walk with another needle only costs the outer walk a recompare.
        private const int DesignerNameMemoSize = 64;
        [ThreadStatic] private static DesignerNameMemoEntry[]? t_designerNameMemo;
        [ThreadStatic] private static int t_designerNameWalk;

        private struct DesignerNameMemoEntry
        {
            public IntPtr Name;
            public int Walk;
            public bool Matches;
        }

        private static unsafe bool DesignerNameMatches(IntPtr name, byte[] needle, int walk)
        {
            if (name == IntPtr.Zero)
                return false;

            var memo = t_designerNameMemo ??= new DesignerNameMemoEntry[DesignerNameMemoSize];
            ref var entry = ref memo[(int)((ulong)name >> 3) & (DesignerNameMemoSize - 1)];
            if (entry.Name == name && entry.Walk == walk)
                return entry.Matches;

            var matches = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)name).IndexOf(needle) >= 0;
            entry.Name = name;
            entry.Walk = walk;
            entry.Matches = matches;
            return matches;
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
            // NativeEntity.Handle resolves through the same raw handle, so comparing the two would be
            // Zero == Zero once the proxy is deleted and the stale proxy would be returned forever.
            if (cached != null && EntitySystem.GetEntityPointerByRawHandle(s_gameRulesProxyHandle) != IntPtr.Zero)
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
            // Plugins call this every tick. Sized exactly: a default-capacity List grows 0 -> 4 -> ... -> 64
            // discarding every intermediate array, and sizing it to MaxPlayers (64) allocated a 528-byte
            // backing array per call even with a handful of players. The mask pass is ~64 pointer reads.
            //
            // Slots are checked on the raw entity pointer and a wrapper is built only for connected
            // players. Building one for every slot cost a native handle lookup plus two more handle
            // resolutions (IsValid, Connected) per slot, every tick, for every plugin calling this.
            var maxPlayers = Server.MaxPlayers;
            if (maxPlayers <= 64)
            {
                var mask = GetConnectedPlayersMask();
                var players = new List<CCSPlayerController>(System.Numerics.BitOperations.PopCount(mask));
                while (mask != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;

                    var pointer = GetConnectedControllerPointer(slot);
                    if (pointer != IntPtr.Zero)
                        players.Add(new CCSPlayerController(pointer));
                }

                return players;
            }

            var all = new List<CCSPlayerController>(maxPlayers);
            for (int i = 0; i < maxPlayers; i++)
            {
                var pointer = GetConnectedControllerPointer(i);
                if (pointer != IntPtr.Zero)
                    all.Add(new CCSPlayerController(pointer));
            }

            return all;
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

        // GetConnectedPlayersMask, remembered for the current tick: ~64 slot reads per call otherwise, and
        // RecipientFilter.AddAllPlayers runs it per message. Within a tick a player who disconnected is
        // still filtered out by AddAllPlayers' per-slot pointer check; one who connected mid-tick is
        // picked up on the next tick.
        private static int s_connectedMaskTick = int.MinValue;
        private static ulong s_connectedMask;

        internal static ulong GetConnectedPlayersMaskThisTick()
        {
            var tick = Server.TickCount;
            if (tick == System.Threading.Volatile.Read(ref s_connectedMaskTick))
                return s_connectedMask;

            var mask = GetConnectedPlayersMask();
            s_connectedMask = mask;
            System.Threading.Volatile.Write(ref s_connectedMaskTick, tick);
            return mask;
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
            // One handle resolve: Handle is Zero for an entity that is gone or an invalid handle
            // (what Guard.IsValidEntity checked, with a second resolve).
            var pointer = entity.Handle;
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("Entity is not valid");

            // Skip non-networked fields BEFORE forwarding on either path (matches upstream).
            // A field with no send-table entry cannot be resolved by the engine; forwarding it
            // — direct OR via the chain entity — makes the engine spam "Couldn't resolve offset
            // <N> in <Class>" on EVERY call (seen with plugins that SetStateChanged inventory/
            // weapon-slot sub-fields on CCSPlayerController, e.g. offset 3920). The previous fork
            // behaviour forwarded anyway (commit 727c6f98, no rationale) which caused the spam.
            // Mark the correct networked parent (the pointer/handle field) instead.
            if (!Schema.TryGetStateChangedTarget(className, fieldName, out var offset, out var chainOffset))
            {
                Application.Instance.Logger.LogDebug(
                    "Field {ClassName}:{FieldName} is not send-table networked; skipping SetStateChanged "
                    + "(the engine cannot resolve a non-networked offset). Mark the networked parent instead.",
                    className, fieldName);
                return;
            }

            NotifyStateChanged(pointer, offset + extraOffset, chainOffset);
        }

        /// <summary>
        /// The notification half of <see cref="SetStateChanged"/> with every lookup already
        /// resolved, shared with <see cref="Modules.Memory.SchemaField{T}"/> so both mark a
        /// networked field changed the same way. The caller has already established that
        /// the field is networked.
        /// </summary>
        internal static void NotifyStateChanged(CBaseEntity entity, int offset, int chainOffset)
        {
            NotifyStateChanged(entity.Handle, offset, chainOffset);
        }

        // m_lastNetworkChange / m_isSteadyState are touched on every notification: offsets cached
        // once, and the entity pointer resolved once by the caller, instead of the generated
        // properties (a handle resolve each, and a name lookup for the IsSteadyState span).
        private static readonly SchemaField<float> s_lastNetworkChange = new("CBaseEntity", "m_lastNetworkChange");
        private static readonly SchemaField<ulong> s_isSteadyState = new("CBaseEntity", "m_isSteadyState");

        internal static unsafe void NotifyStateChanged(IntPtr entity, int offset, int chainOffset)
        {
            if (chainOffset != 0)
            {
                FastNatives.NetworkStateChanged(entity + chainOffset, (uint)offset, 0xFFFFFFFF, 0xFFFFFFFF);
                return;
            }

            FastNatives.SetStateChanged(entity, (uint)offset, 0xFFFFFFFF, 0xFFFFFFFF);

            *(float*)(entity + s_lastNetworkChange.Offset) = FastNatives.CurrentTime();
            *(ulong*)(entity + s_isSteadyState.Offset) = 0; // CBitVec<64>: 8 bytes, all cleared
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
