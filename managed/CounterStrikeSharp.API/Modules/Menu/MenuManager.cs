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

using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CounterStrikeSharp.API.Modules.Menu;

public static class MenuManager
{
    private static readonly Dictionary<IntPtr, IMenuInstance> ActiveMenus = new();

    public static Dictionary<IntPtr, IMenuInstance> GetActiveMenus()
    {
        return ActiveMenus;
    }
    
    public static IMenuInstance? GetActiveMenu(CCSPlayerController player)
    {
        return !ActiveMenus.TryGetValue(player.Handle, out var value) ? null : value;
    }
    
    public static void CloseActiveMenu(CCSPlayerController player)
    {
        if (ActiveMenus.TryGetValue(player.Handle, out var activeMenu))
        {
            activeMenu.Reset();
        }

        ActiveMenus.Remove(player.Handle);
    }
    
    // Drops an instance by value: its player may be gone, and then player.Handle no longer finds it.
    internal static void RemoveInstance(IMenuInstance instance)
    {
        foreach (var (key, value) in ActiveMenus)
        {
            if (!ReferenceEquals(value, instance)) continue;
            ActiveMenus.Remove(key);
            return;
        }
    }

    public static void OpenChatMenu(CCSPlayerController player, ChatMenu menu)
    {
        CloseActiveMenu(player);
        
        ActiveMenus[player.Handle] = new ChatMenuInstance(player, menu);
        ActiveMenus[player.Handle].Display();
    }
    
    public static void OpenCenterHtmlMenu(BasePlugin plugin, CCSPlayerController player, CenterHtmlMenu menu)
    {
        CloseActiveMenu(player);
        
        ActiveMenus[player.Handle] = new CenterHtmlMenuInstance(plugin, player, menu);
        ActiveMenus[player.Handle].Display();
    }
    
    public static void OpenConsoleMenu(CCSPlayerController player, ConsoleMenu menu)
    {
        CloseActiveMenu(player);
        
        ActiveMenus[player.Handle] = new ConsoleMenuInstance(player, menu);
        ActiveMenus[player.Handle].Display();
    }

    // ActiveMenus is static in the API, and its instances hold the owning plugin and the menu's
    // option delegates, which rooted the plugin's AssemblyLoadContext after unload. Called from
    // BasePlugin.Dispose.
    internal static void RemoveMenusForAssembly(BasePlugin plugin, Assembly assembly)
    {
        List<IntPtr>? stale = null;
        foreach (var (key, instance) in ActiveMenus)
        {
            if (instance is CenterHtmlMenuInstance html && html.Plugin == plugin
                || instance.GetType().Assembly == assembly
                || instance is BaseMenuInstance { Menu: var menu }
                   && (menu.GetType().Assembly == assembly
                       || menu.MenuOptions.Any(o => o.OnSelect?.Method.Module.Assembly == assembly)))
            {
                (stale ??= new()).Add(key);
            }
        }

        if (stale == null) return;
        foreach (var key in stale)
            ActiveMenus.Remove(key);
    }

    public static void OnKeyPress(CCSPlayerController player, int key)
    {
        GetActiveMenu(player)?.OnKeyPress(player, key);
    }
}
