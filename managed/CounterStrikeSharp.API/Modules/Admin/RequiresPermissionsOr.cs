using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API.Modules.Entities;

namespace CounterStrikeSharp.API.Modules.Admin
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public class RequiresPermissionsOr : BaseRequiresPermissions
    {
        public RequiresPermissionsOr(params string[] permissions) : base(permissions) { }

        public override bool CanExecuteCommand(CCSPlayerController? caller)
        {
            if (caller == null) return true;
            if (AdminManager.PlayerHasCommandOverride(caller, Command))
            {
                return AdminManager.GetPlayerCommandOverrideState(caller, Command);
            }
            if (!base.CanExecuteCommand(caller)) return false;

            var adminData = AdminManager.GetPlayerAdminData(caller.AuthorizedSteamID);
            if (adminData == null) return false;
            
            return HasAnyPermission(caller.AuthorizedSteamID!, adminData);
        }

        // Any one requirement is enough. User flags go through PlayerHasPermissions one at a time,
        // which honours @domain/root (and /*) and permission aliases the same way RequiresPermissions
        // does. This used to strip root-covered flags out of Permissions itself: that only ever
        // looked at #group entries, so @css/root never matched a list of @css/ flags, and it
        // permanently mutated the attribute's shared set for every later caller.
        internal bool HasAnyPermission(SteamID steamId, AdminData adminData)
        {
            if (Permissions.Any(perm => perm.StartsWith(PermissionCharacters.GroupPermissionChar) && adminData.Groups.Contains(perm)))
            {
                return true;
            }

            return Permissions.Any(perm => perm.StartsWith(PermissionCharacters.UserPermissionChar)
                && AdminManager.PlayerHasPermissions(steamId, perm));
        }
    }
}
