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

namespace CounterStrikeSharp.API.Core;

public partial class CCSPlayerController_InventoryServices
{
    /// <summary>
    /// Raw view of the <c>m_rank</c> fixed array as <see cref="uint"/> item definition indices.
    /// </summary>
    /// <remarks>
    /// The generated <see cref="Rank"/> accessor types this array as <see cref="MedalRank_t"/>, but in live
    /// CS2 gameplay the slots hold cosmetic display-flair <em>item definition indices</em> (e.g. 874, 4974,
    /// 5313, 6132), not medal-rank enum values 0-4. Write the item defidx here and mark the field state-changed
    /// to display scoreboard flair. Valid defidx values are sparse in the ~874-6134 range; validating them
    /// against <c>items_game.txt</c> is the plugin author's responsibility. See
    /// <see href="https://github.com/roflmuffin/CounterStrikeSharp/issues/1340">issue #1340</see>.
    /// </remarks>
    public Span<uint> RankRaw => Schema.GetFixedArray<uint>(this.Handle, "CCSPlayerController_InventoryServices", "m_rank", 6);
}
