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

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Core.Hosting;
using CounterStrikeSharp.API.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Serializable instance of the CoreConfig
    /// </summary>
    internal sealed partial class CoreConfigData
    {
        [JsonPropertyName("PublicChatTrigger")]
        public IEnumerable<string> PublicChatTrigger { get; set; } = new HashSet<string>() { "!" };

        [JsonPropertyName("SilentChatTrigger")]
        public IEnumerable<string> SilentChatTrigger { get; set; } = new HashSet<string>() { "/" };

        [JsonPropertyName("LogVerbosity")]
        public string LogVerbosity { get; set; } = "information";

        [JsonPropertyName("FollowCS2ServerGuidelines")]
        public bool FollowCS2ServerGuidelines { get; set; } = true;

        [JsonPropertyName("PluginHotReloadEnabled")]
        public bool PluginHotReloadEnabled { get; set; } = true;

        [JsonPropertyName("PluginAutoLoadEnabled")]
        public bool PluginAutoLoadEnabled { get; set; } = true;

        [JsonPropertyName("PluginResolveNugetPackages")]
        public bool PluginResolveNugetPackages { get; set; }

        [JsonPropertyName("ServerLanguage")] public string ServerLanguage { get; set; } = "en";

        [JsonPropertyName("UnlockConCommands")]
        public bool UnlockConCommands { get; set; } = true;

        [JsonPropertyName("UnlockConVars")] public bool UnlockConVars { get; set; } = true;

        [JsonPropertyName("AutoUpdateEnabled")]
        public bool AutoUpdateEnabled { get; set; } = true;

        [JsonPropertyName("AutoUpdateURL")] public string AutoUpdateURL { get; set; } = "http://gamedata.cssharp.dev";

        [JsonPropertyName("MaximumFrameTasksExecutedPerTick")]
        public int MaximumFrameTasksExecutedPerTick { get; set; } = 1024;

        [JsonPropertyName("UpdateWatcherEnabled")]
        public bool UpdateWatcherEnabled { get; set; } = true;

        [JsonPropertyName("UpdateWatcherAutoRestart")]
        public bool UpdateWatcherAutoRestart { get; set; } = false;

        [JsonPropertyName("UpdateWatcherRestartOnMapChange")]
        public bool UpdateWatcherRestartOnMapChange { get; set; } = true;

        [JsonPropertyName("UpdateWatcherRestartWhenEmpty")]
        public bool UpdateWatcherRestartWhenEmpty { get; set; } = true;

        [JsonPropertyName("UpdateWatcherDebounceSeconds")]
        public int UpdateWatcherDebounceSeconds { get; set; } = 10;

        [JsonPropertyName("UpdateWatcherRestartCommand")]
        public string UpdateWatcherRestartCommand { get; set; } = "quit";

        [JsonPropertyName("SlowFrameDetectionEnabled")]
        public bool SlowFrameDetectionEnabled { get; set; } = true;

        // 0 => auto: 2x the engine tick interval (~31ms at 64 tick).
        [JsonPropertyName("SlowFrameBudgetMs")]
        public double SlowFrameBudgetMs { get; set; } = 0;

        [JsonPropertyName("SlowFrameLogFile")]
        public string SlowFrameLogFile { get; set; } = "logs/slowframes.log";

        [JsonPropertyName("SlowFrameTopN")]
        public int SlowFrameTopN { get; set; } = 5;

        // Leave empty in the committed default (private fork: set it in the server's
        // own core.json, or via the CSSHARP_SLOWFRAME_WEBHOOK env var which overrides
        // this). The env var is preferred so the secret never lives in a tracked file.
        [JsonPropertyName("SlowFrameDiscordWebhookUrl")]
        public string SlowFrameDiscordWebhookUrl { get; set; } = "";

        [JsonPropertyName("SlowFrameDiscordCooldownSeconds")]
        public int SlowFrameDiscordCooldownSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Configuration related to the Core API.
    /// </summary>
    public partial class CoreConfig
    {
        /// <summary>
        /// List of characters to use for public chat triggers.
        /// </summary>
        public static IEnumerable<string> PublicChatTrigger => _coreConfig.PublicChatTrigger;

        /// <summary>
        /// List of characters to use for silent chat triggers.
        /// </summary>
        public static IEnumerable<string> SilentChatTrigger => _coreConfig.SilentChatTrigger;

        /// <summary>
        /// Minimum log level for CounterStrikeSharp's own output. One of:
        /// verbose/trace, debug, information, warning, error, fatal. Default "information".
        /// Lower to "debug" to see the per-step boot/init lines hidden at the default level.
        /// </summary>
        public static string LogVerbosity => _coreConfig.LogVerbosity;

        /// <summary>
        /// <para>
        /// Per <see href="http://blog.counter-strike.net/index.php/server_guidelines/"/>, certain plugin
        /// functionality will trigger all of the game server owner's Game Server Login Tokens
        /// (GSLTs) to get banned when executed on a Counter-Strike 2 game server.
        /// </para>
        ///
        /// <para>
        /// Enabling this option will block plugins from using functionality that is known to cause this.
        ///
        /// Note that this does NOT guarantee that you cannot
        ///
        /// receive a ban.
        /// </para>
        ///
        /// <para>
        /// Disable this option at your own risk.
        /// </para>
        /// </summary>
        public static bool FollowCS2ServerGuidelines => _coreConfig.FollowCS2ServerGuidelines;

        /// <summary>
        /// When enabled, plugins are automatically reloaded when their .dll file is updated.
        /// </summary>
        public static bool PluginHotReloadEnabled => _coreConfig.PluginHotReloadEnabled;

        /// <summary>
        /// When enabled, plugins are automatically loaded from the plugins directory on server start.
        /// </summary>
        public static bool PluginAutoLoadEnabled => _coreConfig.PluginAutoLoadEnabled;

        public static bool PluginResolveNugetPackages => _coreConfig.PluginResolveNugetPackages;

        public static string ServerLanguage => _coreConfig.ServerLanguage;

        public static bool UnlockConCommands => _coreConfig.UnlockConCommands;

        public static bool UnlockConVars => _coreConfig.UnlockConVars;

        public static int MaximumFrameTasksExecutedPerTick => _coreConfig.MaximumFrameTasksExecutedPerTick;

        /// <summary>
        /// When enabled, CounterStrikeSharp watches its own native/managed binaries for
        /// on-disk changes (i.e. a framework update applied while the server is live) and
        /// schedules a clean server restart at a safe point instead of risking a mid-match
        /// crash. Requires the update to be applied atomically (write + rename) so the live
        /// process keeps running its old binary until the restart fires.
        /// </summary>
        public static bool UpdateWatcherEnabled => _coreConfig.UpdateWatcherEnabled;

        /// <summary>
        /// <para>
        /// When <c>false</c> (default), a detected update is only announced in the log and the
        /// running server is left alone — safe on every host, because the live process keeps
        /// running the old binary and the operator restarts when convenient.
        /// </para>
        /// <para>
        /// When <c>true</c>, the watcher additionally issues <see cref="UpdateWatcherRestartCommand"/>
        /// at the next safe point (map change / empty server). Enable this ONLY if you know your
        /// supervisor relaunches the process after it exits (systemd <c>Restart=always</c>, docker
        /// <c>--restart unless-stopped</c>, a panel with crash-restart, a bash loop). On a plain
        /// Docker/Pterodactyl setup a clean exit leaves the server OFFLINE, so leave this off there.
        /// </para>
        /// </summary>
        public static bool UpdateWatcherAutoRestart => _coreConfig.UpdateWatcherAutoRestart;

        /// <summary>
        /// When an update is pending and <see cref="UpdateWatcherAutoRestart"/> is enabled,
        /// restart the server on the next map change (OnMapEnd).
        /// </summary>
        public static bool UpdateWatcherRestartOnMapChange => _coreConfig.UpdateWatcherRestartOnMapChange;

        /// <summary>
        /// When an update is pending, restart the server as soon as it is empty (0 players),
        /// without waiting for the map to end.
        /// </summary>
        public static bool UpdateWatcherRestartWhenEmpty => _coreConfig.UpdateWatcherRestartWhenEmpty;

        /// <summary>
        /// Seconds to wait after the last detected file change before treating an update as
        /// complete. An update touches many files; this debounce avoids restarting halfway
        /// through the copy.
        /// </summary>
        public static int UpdateWatcherDebounceSeconds => _coreConfig.UpdateWatcherDebounceSeconds;

        /// <summary>
        /// Server command issued to perform the restart. Defaults to <c>quit</c>: CS2 has no
        /// in-process binary reload, so the process must exit and an external supervisor
        /// (systemd <c>Restart=always</c> / docker <c>--restart</c> / a bash <c>while</c> loop)
        /// relaunches it with the new binary. Override only if your supervisor expects a
        /// different shutdown command.
        /// </summary>
        public static string UpdateWatcherRestartCommand => _coreConfig.UpdateWatcherRestartCommand;

        /// <summary>
        /// When enabled, CounterStrikeSharp times each plugin's per-tick handlers and,
        /// when a frame exceeds <see cref="SlowFrameBudgetMs"/>, writes a report naming
        /// the dominant plugin(s) to <see cref="SlowFrameLogFile"/> and optionally a
        /// Discord webhook. Designed to answer "which plugin is lagging my server".
        /// </summary>
        public static bool SlowFrameDetectionEnabled => _coreConfig.SlowFrameDetectionEnabled;

        /// <summary>Per-frame budget in ms. 0 = auto (2x the engine tick interval).</summary>
        public static double SlowFrameBudgetMs => _coreConfig.SlowFrameBudgetMs;

        /// <summary>Report log file. Relative paths resolve against the addons root.</summary>
        public static string SlowFrameLogFile => _coreConfig.SlowFrameLogFile;

        /// <summary>How many plugins to list per report.</summary>
        public static int SlowFrameTopN => _coreConfig.SlowFrameTopN;

        /// <summary>
        /// Discord webhook for slow-frame reports. Empty = file only. Overridden by the
        /// CSSHARP_SLOWFRAME_WEBHOOK environment variable when that is set.
        /// </summary>
        public static string SlowFrameDiscordWebhookUrl => _coreConfig.SlowFrameDiscordWebhookUrl;

        /// <summary>Minimum seconds between Discord posts (anti-spam / rate-limit guard).</summary>
        public static int SlowFrameDiscordCooldownSeconds => _coreConfig.SlowFrameDiscordCooldownSeconds;
    }

    public partial class CoreConfig : IStartupService
    {
        private static CoreConfigData _coreConfig = new CoreConfigData();

        private readonly ICommandManager _commandManager;
        private readonly ILogger<CoreConfig> _logger;

        private readonly string _coreConfigPath;
        private readonly string _rootPath;
        private bool _commandsRegistered = false;

        public CoreConfig(IScriptHostConfiguration scriptHostConfiguration, ICommandManager commandManager, ILogger<CoreConfig> logger)
        {
            _commandManager = commandManager;
            _logger = logger;
            _coreConfigPath = Path.Join(scriptHostConfiguration.ConfigsPath, "core.json");
            _rootPath = scriptHostConfiguration.RootPath;
        }

        [RequiresPermissions("@css/config")]
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        private void ReloadCoreConfigCommand(CCSPlayerController? player, CommandInfo command)
        {
            Load();
        }

        public void Load()
        {
            if (!_commandsRegistered)
            {
                _commandManager.RegisterCommand(new CommandDefinition("css_core_reload",
                    "Reloads the core configuration file.",
                    ReloadCoreConfigCommand));
                _commandsRegistered = true;
            }

            if (File.Exists(_coreConfigPath))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<CoreConfigData>(File.ReadAllText(_coreConfigPath, Encoding.UTF8),
                        new JsonSerializerOptions() { ReadCommentHandling = JsonCommentHandling.Skip });

                    if (data != null)
                    {
                        _coreConfig = data;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load core configuration, fallback values will be used");
                }
            }
            else
            {
                _logger.LogWarning(
                    "Core configuration could not be found at path \"{CoreConfigPath}\", fallback values will be used.",
                    _coreConfigPath);
            }

            var serverCulture = CultureInfo.GetCultures(CultureTypes.AllCultures)
                .FirstOrDefault(x => x.Name == ServerLanguage);
            if (serverCulture == null)
            {
                try
                {
                    _logger.LogWarning("Server Language \"{ServerLanguage}\" is not supported, falling back to \"en\"",
                        ServerLanguage);
                    _coreConfig.ServerLanguage = "en";
                    serverCulture = new CultureInfo("en");
                }
                catch (Exception)
                {
                    _logger.LogWarning("Server is running in invariant mode, translations will not be available.");
                    serverCulture = CultureInfo.InvariantCulture;
                }
            }

            CultureInfo.DefaultThreadCurrentUICulture = serverCulture;
            CultureInfo.DefaultThreadCurrentCulture = serverCulture;
            CultureInfo.CurrentUICulture = serverCulture;
            CultureInfo.CurrentCulture = serverCulture;

            // Apply configured verbosity to the live Serilog level switch. Hot-reloadable
            // via css_core_reload — lowering to "debug" surfaces the demoted boot lines.
            CoreLogging.SetVerbosity(LogVerbosity);

            ConfigureSlowFrameDetection();

            _logger.LogInformation("Successfully loaded core configuration");
        }

        private void ConfigureSlowFrameDetection()
        {
            // Webhook precedence: env var (preferred — keeps the secret out of any
            // tracked file) overrides the core.json value. Empty => file-only.
            string webhook = Environment.GetEnvironmentVariable("CSSHARP_SLOWFRAME_WEBHOOK");
            if (string.IsNullOrEmpty(webhook)) webhook = SlowFrameDiscordWebhookUrl;

            // 0 => auto: 2x the engine tick interval. CS2 runs a fixed 64-tick sim
            // (0.015625s), matching the native side's engine_fixed_tick_interval.
            double budget = SlowFrameBudgetMs > 0 ? SlowFrameBudgetMs : (1000.0 / 64.0) * 2.0;

            Profiling.PluginProfiler.Configure(SlowFrameDetectionEnabled, budget);
            Profiling.SlowFrameReporter.Configure(SlowFrameDetectionEnabled, _rootPath, SlowFrameLogFile, webhook,
                SlowFrameTopN, SlowFrameDiscordCooldownSeconds);

            if (SlowFrameDetectionEnabled)
            {
                _logger.LogInformation(
                    "Slow-frame detection on (budget {Budget:F1}ms, report -> {Log}{Discord}).",
                    budget, SlowFrameLogFile,
                    string.IsNullOrEmpty(webhook) ? "" : " + Discord");
            }
        }
    }
}
