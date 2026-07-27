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
    [StructLayout(LayoutKind.Explicit)]
    public struct CCheckTransmitInfo
    {
        /// <summary>
        /// Entity n is already marked for transmission
        /// </summary>
        [FieldOffset(0x0)]
        public CFixedBitVecBase TransmitEntities;

        /// <summary>
        /// Entity n is always send even if not in PVS (HLTV and Replay only)
        /// </summary>
        [FieldOffset(0x8)]
        public CFixedBitVecBase TransmitAlways;
    };

    public sealed class CCheckTransmitInfoList : NativeObject, IReadOnlyList<(CCheckTransmitInfo info, CCSPlayerController? player)>
    {
        // Resolved once per process, not per construction: this class is constructed on
        // EVERY CheckTransmit dispatch (once per tick per listener), and the old
        // per-instance field initializer paid two dictionary lookups + an OS-platform
        // check each time. Lazy so the first touch happens after gamedata is loaded.
        private static int _checkTransmitPlayerSlotOffset = -1;

        private static int CheckTransmitPlayerSlotOffset
        {
            get
            {
                var offset = _checkTransmitPlayerSlotOffset;
                if (offset < 0)
                {
                    _checkTransmitPlayerSlotOffset = offset = GameData.GetOffset("CheckTransmitPlayerSlot");
                }

                return offset;
            }
        }

        private unsafe nint* Inner => (nint*)base.Handle;

        /// <summary>
        /// Number of infos in the list. Cached at construction — the native list does
        /// not change size during the CheckTransmit callback this instance lives in.
        /// </summary>
        public int Count { get; }

        // Player wrappers cached per instance so repeated indexing / multiple
        // enumerations of the same tick's list don't re-allocate a
        // CCSPlayerController per access.
        private CCSPlayerController?[]? _players;

        public unsafe CCheckTransmitInfoList(IntPtr pointer) : base(pointer)
        {
            Count = (int)(*((nint*)pointer + 1));
        }

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

                var players = _players ??= new CCSPlayerController?[this.Count];
                CCSPlayerController? player = players[index] ??= Utilities.GetPlayerFromSlot(slot);

                return (transmit, player);
            }
        }

        /// <summary>
        /// Get transmit info for the given index without materializing a
        /// <see cref="CCSPlayerController"/> wrapper. Zero-allocation accessor for
        /// per-tick hot paths; combine with <see cref="GetPlayerSlot"/>.
        /// </summary>
        /// <param name="index">Index of the info you want to retrieve from the list, should be between 0 and '<see cref="Count"/>' - 1</param>
        public CCheckTransmitInfo GetInfo(int index) => this.Get(index).Item1;

        /// <summary>
        /// Get the player slot for the given index without materializing a
        /// <see cref="CCSPlayerController"/> wrapper. Zero-allocation accessor for
        /// per-tick hot paths.
        /// </summary>
        /// <param name="index">Index of the info you want to retrieve from the list, should be between 0 and '<see cref="Count"/>' - 1</param>
        public unsafe int GetPlayerSlot(int index)
        {
            if (index < 0 || index >= this.Count)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            nint infoPtr = *(*(nint**)this.Inner + index);
            return *(int*)((byte*)infoPtr + CheckTransmitPlayerSlotOffset);
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

            // Blittable struct (two native pointers) — direct dereference instead of
            // the reflection-based Marshal.PtrToStructure the old code used per player.
            CCheckTransmitInfo info = *(CCheckTransmitInfo*)infoPtr;

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
