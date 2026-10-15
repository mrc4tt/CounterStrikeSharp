using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Core.Logging;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Modules.Admin
{
    public static partial class AdminManager
    {
        public static ICommandManager CommandManagerProvider { get; internal set; } = null!;

        public static void AddCommands()
        {
            CommandManagerProvider.RegisterCommand(new CommandDefinition("css_admins_reload", "Reloads the admin file.",
                ReloadAdminsCommand));
            CommandManagerProvider.RegisterCommand(new CommandDefinition("css_admins_list",
                "List admins and their flags.", ListAdminsCommand));
            CommandManagerProvider.RegisterCommand(new CommandDefinition("css_groups_reload",
                "Reloads the admin groups file.", ReloadAdminGroupsCommand));
            CommandManagerProvider.RegisterCommand(new CommandDefinition("css_groups_list",
                "List admin groups and their flags.", ListAdminGroupsCommand));
            CommandManagerProvider.RegisterCommand(new CommandDefinition("css_overrides_reload",
                "Reloads the admin command overrides file.", ReloadAdminOverridesCommand));
            CommandManagerProvider.RegisterCommand(new CommandDefinition("css_overrides_list",
                "List admin command overrides and their flags.", ListAdminOverridesCommand));
        }

        public static void MergeGroupPermsIntoAdmins()
        {
            foreach (var (steamID, adminDef) in Admins)
            {
                AddPlayerToGroup(steamID, adminDef.Groups.ToArray());
            }
        }

        [RequiresPermissions(permissions: "@css/generic")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private static void ReloadAdminsCommand(CCSPlayerController? player, CommandInfo command)
        {
            var rootDir = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.Parent!;
            var path = Path.Combine(rootDir.FullName, "configs", "admins.json");
            if (!CanReload<Dictionary<string, AdminData>>(path, command)) return;

            Admins.Clear();
            LoadAdminData(path);
            MergeGroupPermsIntoAdmins();
            command.ReplyToCommand($"Reloaded admins ({Admins.Count}).");
        }

        // Reload commands clear the live table and then load the file. A typo in the JSON used to
        // leave the table empty until restart (nobody had admin), so the file is parsed first and
        // the reload is refused, keeping the current data, when it would not load.
        private static bool CanReload<T>(string path, CommandInfo command) where T : class
        {
            string? error = null;
            try
            {
                if (!File.Exists(path))
                    error = "file not found";
                else if (JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8),
                             new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip }) == null)
                    error = "file is empty";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error == null) return true;

            _logger.LogError("Not reloading {Path}, keeping the current data: {Error}", path, error);
            command.ReplyToCommand($"Not reloaded, current data kept: {Path.GetFileName(path)}: {error}");
            return false;
        }

        [RequiresPermissions(permissions: "@css/generic")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private static void ListAdminsCommand(CCSPlayerController? player, CommandInfo command)
        {
            foreach (var (steamId, data) in Admins)
            {
                command.ReplyToCommand($"{steamId.SteamId64}, {steamId.SteamId2} - FLAGS: ");
                foreach (var domain in data.Flags.Keys)
                {
                    command.ReplyToCommand($"   Domain {domain}: {string.Join(", ", data.Flags[domain])}");
                }
            }
        }

        [RequiresPermissions(permissions: "@css/generic")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private static void ReloadAdminGroupsCommand(CCSPlayerController? player, CommandInfo command)
        {
            var rootDir = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.Parent!;
            var path = Path.Combine(rootDir.FullName, "configs", "admin_groups.json");
            if (!CanReload<Dictionary<string, AdminGroupData>>(path, command)) return;

            Groups.Clear();
            LoadAdminGroups(path);
            MergeGroupPermsIntoAdmins();
            command.ReplyToCommand($"Reloaded admin groups ({Groups.Count}).");
        }

        [RequiresPermissions(permissions: "@css/generic")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private static void ListAdminGroupsCommand(CCSPlayerController? player, CommandInfo command)
        {
            foreach (var (groupName, groupDef) in Groups)
            {
                command.ReplyToCommand($"{groupName} - {string.Join(", ", groupDef.Flags)}");
            }
        }

        [RequiresPermissions(permissions: "@css/generic")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private static void ReloadAdminOverridesCommand(CCSPlayerController? player, CommandInfo command)
        {
            var rootDir = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory!.Parent!;
            var path = Path.Combine(rootDir.FullName, "configs", "admin_overrides.json");
            if (!CanReload<Dictionary<string, CommandData>>(path, command)) return;

            CommandOverrides.Clear();
            LoadCommandOverrides(path);
            command.ReplyToCommand($"Reloaded admin command overrides ({CommandOverrides.Count}).");
        }

        [RequiresPermissions(permissions: "@css/generic")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private static void ListAdminOverridesCommand(CCSPlayerController? player, CommandInfo command)
        {
            foreach (var (commandName, commandDef) in CommandOverrides)
            {
                command.ReplyToCommand(
                    $"{commandName} (enabled: {commandDef.Enabled.ToString()}) - {string.Join(", ", commandDef.Flags)}");
            }
        }
    }
}