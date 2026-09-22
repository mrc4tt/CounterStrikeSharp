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

using System.Collections;
using System.Runtime.InteropServices;

namespace CounterStrikeSharp.API.Core
{
    // Layout of the engine's CCheckTransmitInfo (584 bytes). hl2sdk only declares the first
    // pointer; the full struct is reversed in CS2Fixes (src/cs2_sdk/cchecktransmitinfo.h):
    //
    //   +0x00  CBitVec<MAX_EDICTS>* m_pTransmitEntity
    //   +0x08  CBitVec<MAX_EDICTS>* m_pTransmitNonPlayers
    //   +0x10  CBitVec<MAX_EDICTS>* m_pTransmitOutOfPVS
    //   +0x18  CBitVec<MAX_EDICTS>* m_pTransmitAlways
    //   +0x20  CUtlVector<CPlayerSlot> m_vecTargetSlots
    //   +0x38  vis_info_t m_VisInfo (520 bytes)
    //   +0x240 CPlayerSlot m_nPlayerSlot          (gamedata "CheckTransmitPlayerSlot" = 576)
    //   +0x244 bool m_bFullUpdate
    //
    // TransmitAlways used to sit at +0x8, which is really m_pTransmitNonPlayers. Clearing an
    // entity from TransmitEntities alone leaves the client with a stale copy of it — the
    // engine only emits the deletion delta for entities that are also set in
    // TransmitNonPlayers — and anything still referencing it client-side (particles, child
    // entities) logs "Missing client entity N". Use Hide() to do both.
    [StructLayout(LayoutKind.Explicit)]
    public struct CCheckTransmitInfo
    {
        /// <summary>
        /// Entity n is already marked for transmission
        /// </summary>
        [FieldOffset(0x0)]
        public CFixedBitVecBase TransmitEntities;

        /// <summary>
        /// Non-player entity n needs a deletion delta sent to the client.
        /// Set this together with clearing <see cref="TransmitEntities"/> when hiding an entity,
        /// otherwise the client keeps a stale copy and logs "Missing client entity".
        /// </summary>
        [FieldOffset(0x8)]
        public CFixedBitVecBase TransmitNonPlayers;

        /// <summary>
        /// Entity n left the PVS but still needs a delta update
        /// </summary>
        [FieldOffset(0x10)]
        public CFixedBitVecBase TransmitOutOfPVS;

        /// <summary>
        /// Entity n is always send even if not in PVS (HLTV and Replay only)
        /// </summary>
        [FieldOffset(0x18)]
        public CFixedBitVecBase TransmitAlways;

        /// <summary>
        /// Stop transmitting entity n to this client and make the engine send the deletion delta,
        /// so the client actually removes its copy instead of keeping a stale one.
        /// </summary>
        public void Hide(CEntityInstance entityInstance) => Hide((int)entityInstance.Index);

        /// <inheritdoc cref="Hide(CEntityInstance)"/>
        public void Hide(uint entityIndex) => Hide((int)entityIndex);

        /// <inheritdoc cref="Hide(CEntityInstance)"/>
        public void Hide(int entityIndex)
        {
            TransmitEntities.Remove(entityIndex);
            TransmitNonPlayers.Add(entityIndex);
        }
    };

    public sealed class CCheckTransmitInfoList : NativeObject, IReadOnlyList<(CCheckTransmitInfo info, CCSPlayerController? player)>
    {
        private int CheckTransmitPlayerSlotOffset = GameData.GetOffset("CheckTransmitPlayerSlot");

        private unsafe nint* Inner => (nint*)base.Handle;

        public unsafe int Count { get => (int)(*(this.Inner + 1)); }

        public unsafe CCheckTransmitInfoList(IntPtr pointer) : base(pointer)
            { }

        /// <summary>
        /// Get transmit info for the given index.
        /// </summary>
        /// <param name="index">Index of the info you want to retrieve from the list, should be between 0 and '<see cref="Count"/>' - 1</param>
        /// <returns></returns>
        public (CCheckTransmitInfo info, CCSPlayerController? player) this[int index]
        {
            get
            {
                var (transmit, slot) = this.Get(index);
                CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
                return (transmit, player);
            }
        }

        /// <summary>
        /// Get transmit info for the given index.
        /// </summary>
        /// <param name="index">Index of the info you want to retrieve from the list, should be between 0 and '<see cref="Count"/>' - 1</param>
        /// <returns></returns>
        private unsafe (CCheckTransmitInfo, int) Get(int index)
        {
            if (index < 0 || index >= this.Count)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            // 'base.Handle' holds the pointer for our 'CCheckTransmitInfoList' wrapper class

            // Get the pointer to the array of 'CCheckTransmitInfo'
            nint* infoListPtr = *(nint**)this.Inner; // Dereference 'Inner' to get the pointer to the array

            // Access the specific 'CCheckTransmitInfo*'
            nint infoPtr = *(infoListPtr + index);

            // Retrieve the 'CCheckTransmitInfo' from the pointer
            CCheckTransmitInfo info = Marshal.PtrToStructure<CCheckTransmitInfo>(infoPtr);

            // Get player slot from the 'infoPtr' using the 'CheckTransmitPlayerSlotOffset' offset
            int playerSlot = *(int*)((byte*)infoPtr + CheckTransmitPlayerSlotOffset);

            return (info, playerSlot);
        }

        public IEnumerator<(CCheckTransmitInfo, CCSPlayerController?)> GetEnumerator()
        {
            for (int i = 0; i < this.Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}
