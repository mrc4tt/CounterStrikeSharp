using FastGenericNew;
﻿/*
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

using System.Numerics;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Core;

public partial class CCSPlayer_ItemServices
{
    /// <summary>
    /// Drops the active player weapon on the ground.
    /// </summary>
    /// <exception cref="InvalidOperationException">ItemServices points to null</exception>
    public void DropActivePlayerWeapon(CBasePlayerWeapon activeWeapon)
    {
        if(Handle == IntPtr.Zero)
            throw new InvalidOperationException("ItemServices points to null.");

        Guard.IsValidEntity(activeWeapon);

        // The native signature is DropActivePlayerWeapon(CBasePlayerWeapon*, const Vector& vecDropMomentum).
        // Omitting the second argument leaves vecDropMomentum reading uninitialised stack and can produce
        // NaN drop velocity, so pass a pointer to a zeroed Vector. Stack-allocated, no native handle leak.
        unsafe
        {
            var dropMomentum = new Vector3();
            VirtualFunction.CreateVoid<nint, nint, nint>(Handle, GameData.GetOffset("CCSPlayer_ItemServices_DropActivePlayerWeapon"))(Handle, activeWeapon.Handle, (nint)(&dropMomentum));
        }
    }

    /// <summary>
    /// Removes every weapon from the player, and the defuser. Armor and helmet are kept.
    /// </summary>
    /// <exception cref="InvalidOperationException">ItemServices points to null</exception>
    public void RemoveWeapons() => RemoveWeapons(false);

    /// <summary>
    /// Removes every weapon from the player, and the defuser.
    /// </summary>
    /// <param name="removeArmor">Also take the helmet off and set the armor to 0.</param>
    /// <exception cref="InvalidOperationException">ItemServices points to null</exception>
    public void RemoveWeapons(bool removeArmor)
    {
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("ItemServices points to null.");

        // The game's RemoveWeapons takes a second argument on both platforms: when set it
        // also clears m_bHasHelmet and the pawn's m_ArmorValue (m_bHasDefuser is cleared
        // either way). Binding it with only `this` left that flag to whatever the previous
        // native call had left in the register (rsi / rdx), so armor was removed at random.
        VirtualFunction.CreateVoid<nint, bool>(Handle, GameData.GetOffset("CCSPlayer_ItemServices_RemoveWeapons"))(Handle, removeArmor);
    }

    public T? GiveNamedItem<T>(string item) where T : CEntityInstance
    {
        var pointer = VirtualFunction.Create<nint, string, nint>(Handle, GameData.GetOffset("CCSPlayer_ItemServices_GiveNamedItem"))(Handle, item);
        if (pointer == IntPtr.Zero)
            return null;

        return FastNew.CreateInstance<T, IntPtr>(pointer);
    }

    public AcquireResult CanAcquire(CEconItemView itemView, AcquireMethod method, IntPtr unknown = 0)
    {
        return VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Invoke(this, itemView, method, unknown);
    }
}
