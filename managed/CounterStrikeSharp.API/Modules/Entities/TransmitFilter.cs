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

using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Modules.Entities;

/// <summary>
/// Native-side entity transmit filtering — the fast path for hiding entities from
/// specific players.
///
/// Rules registered here are stored in native code and applied to the engine's
/// transmit bit vectors inside the <c>CheckTransmit</c> hook every tick with
/// <b>zero</b> native ↔ managed transitions. Call these methods only when a rule
/// actually changes (e.g. on spawn, on team switch), NOT per tick.
///
/// Prefer this over a managed <see cref="Core.Listeners.CheckTransmit"/> listener
/// whenever your logic is rule-based ("hide entity X from player Y until further
/// notice"). A managed listener runs C# every single tick; this table does not.
///
/// Lifetimes handled automatically by the native side:
/// <list type="bullet">
/// <item>Entity deleted → its rules are cleared (indices recycle).</item>
/// <item>Player disconnects → that slot's rules are cleared (slots recycle).</item>
/// </list>
///
/// Caution: hiding an entity that a client is currently observing/spectating can
/// crash that client. Guard observer targets in your plugin logic.
/// </summary>
public static class TransmitFilter
{
    /// <summary>
    /// Hide (or un-hide) an entity from a single player slot.
    /// </summary>
    /// <param name="entityIndex">Entity index (1..16383).</param>
    /// <param name="playerSlot">Player slot (0..63).</param>
    /// <param name="hidden">True to hide, false to remove the rule.</param>
    public static void SetHidden(int entityIndex, int playerSlot, bool hidden) =>
        NativeAPI.TransmitSetHidden(entityIndex, playerSlot, hidden);

    /// <inheritdoc cref="SetHidden(int,int,bool)"/>
    public static void SetHidden(CEntityInstance entity, CCSPlayerController player, bool hidden) =>
        NativeAPI.TransmitSetHidden((int)entity.Index, player.Slot, hidden);

    /// <summary>
    /// Hide (or un-hide) an entity from all player slots.
    /// </summary>
    public static void SetHiddenAll(int entityIndex, bool hidden) =>
        NativeAPI.TransmitSetHiddenAll(entityIndex, hidden);

    /// <inheritdoc cref="SetHiddenAll(int,bool)"/>
    public static void SetHiddenAll(CEntityInstance entity, bool hidden) =>
        NativeAPI.TransmitSetHiddenAll((int)entity.Index, hidden);

    /// <summary>
    /// Remove all rules for an entity across every player slot.
    /// (Also happens automatically when the entity is deleted.)
    /// </summary>
    public static void ClearEntity(int entityIndex) => NativeAPI.TransmitClearEntity(entityIndex);

    /// <summary>
    /// Remove all rules for a player slot.
    /// (Also happens automatically when the player disconnects.)
    /// </summary>
    public static void ClearPlayer(int playerSlot) => NativeAPI.TransmitClearPlayer(playerSlot);

    /// <summary>
    /// Remove every rule in the table.
    /// </summary>
    public static void ClearAll() => NativeAPI.TransmitClearAll();

    /// <summary>
    /// Whether a hide rule exists for the given entity/player pair.
    /// </summary>
    public static bool IsHidden(int entityIndex, int playerSlot) =>
        NativeAPI.TransmitIsHidden(entityIndex, playerSlot);
}
