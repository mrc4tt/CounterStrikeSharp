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

namespace CounterStrikeSharp.API
{
    /// <summary>
    /// Provides access to server command line parameters.
    /// </summary>
    public static class CommandLine
    {
        /// <summary>
        /// Gets the full command line string used to start the server.
        /// </summary>
        public static string GetCommandLineString() => NativeAPI.GetCommandLineString();

        /// <summary>
        /// Checks if a command line parameter exists.
        /// Unlike GetCommandParamValue, this can distinguish between 
        /// "parameter not set" and "parameter set to empty string".
        /// </summary>
        /// <param name="param">The parameter to check (e.g., "+sv_setsteamaccount", "-dedicated")</param>
        /// <returns>True if the parameter exists on the command line</returns>
        /// <example>
        /// <code>
        /// if (CommandLine.HasParam("+sv_setsteamaccount"))
        /// {
        ///     // Parameter exists (could be empty or have a value)
        /// }
        /// </code>
        /// </example>
        public static bool HasParam(string param) => NativeAPI.FindCommandLineParam(param);

        /// <summary>
        /// Gets a command line parameter value as a string.
        /// </summary>
        /// <param name="param">The parameter to get (e.g., "+sv_setsteamaccount")</param>
        /// <param name="defaultValue">Value to return if parameter is not found (default: "")</param>
        /// <returns>The parameter value, or defaultValue if not found</returns>
        public static string GetString(string param, string defaultValue = "") 
            => NativeAPI.GetCommandLineParam(param, defaultValue);

        /// <summary>
        /// Gets a command line parameter value as an integer.
        /// </summary>
        /// <param name="param">The parameter to get</param>
        /// <param name="defaultValue">Value to return if parameter is not found or invalid</param>
        /// <returns>The parameter value as int, or defaultValue if not found/invalid</returns>
        public static int GetInt(string param, int defaultValue = 0)
        {
            var value = GetString(param);
            return int.TryParse(value, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Gets a command line parameter value as a float.
        /// </summary>
        /// <param name="param">The parameter to get</param>
        /// <param name="defaultValue">Value to return if parameter is not found or invalid</param>
        /// <returns>The parameter value as float, or defaultValue if not found/invalid</returns>
        public static float GetFloat(string param, float defaultValue = 0f)
        {
            var value = GetString(param);
            return float.TryParse(value, System.Globalization.NumberStyles.Float, 
                System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        /// <summary>
        /// Tries to get a command line parameter value.
        /// This is the recommended way to check if a parameter exists and get its value.
        /// </summary>
        /// <param name="param">The parameter to get</param>
        /// <param name="value">The output value if the parameter exists</param>
        /// <returns>True if the parameter exists, false otherwise</returns>
        /// <example>
        /// <code>
        /// if (CommandLine.TryGetString("+sv_setsteamaccount", out var gslt))
        /// {
        ///     // gslt contains the value (could be empty string)
        ///     Logger.LogInformation($"GSLT is configured: {!string.IsNullOrEmpty(gslt)}");
        /// }
        /// else
        /// {
        ///     // Parameter not on command line at all
        ///     Logger.LogWarning("No +sv_setsteamaccount parameter found");
        /// }
        /// </code>
        /// </example>
        public static bool TryGetString(string param, out string value)
        {
            if (!HasParam(param))
            {
                value = string.Empty;
                return false;
            }
            value = GetString(param);
            return true;
        }

        /// <summary>
        /// Tries to get a command line parameter value as an integer.
        /// </summary>
        /// <param name="param">The parameter to get</param>
        /// <param name="value">The output value if the parameter exists and is valid</param>
        /// <returns>True if the parameter exists and is a valid integer, false otherwise</returns>
        public static bool TryGetInt(string param, out int value)
        {
            value = 0;
            if (!HasParam(param))
                return false;
            
            return int.TryParse(GetString(param), out value);
        }

        /// <summary>
        /// Tries to get a command line parameter value as a float.
        /// </summary>
        /// <param name="param">The parameter to get</param>
        /// <param name="value">The output value if the parameter exists and is valid</param>
        /// <returns>True if the parameter exists and is a valid float, false otherwise</returns>
        public static bool TryGetFloat(string param, out float value)
        {
            value = 0f;
            if (!HasParam(param))
                return false;
            
            return float.TryParse(GetString(param), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
