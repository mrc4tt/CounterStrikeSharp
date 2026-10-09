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

using System.Text;
using System.Text.Unicode;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Managed writer for the native crash flight recorder (src/core/css_crash_context.h). Records which
    /// plugin callback is running and recent plugin activity, so a crash handler such as AcceleratorCS2 can
    /// name the plugin in its crash report. Everything here is best effort and must never throw into a
    /// plugin callback.
    /// </summary>
    internal static unsafe class CrashRecorder
    {
        // Layout of CssCrashContext / CssCrashEntry. Must match css_crash_context.h.
        private const ulong Magic = 0x31585443534353UL;
        private const uint Version = 1;
        private const int EntrySize = 256;
        private const int EntryPluginOffset = 8, EntryPluginLength = 48;
        private const int EntryKindOffset = 56, EntryKindLength = 16;
        private const int EntryDetailOffset = 72, EntryDetailLength = 184;

        private const int MaxDepth = 8, ActivitySize = 128, ExceptionSize = 8;
        private const int DepthOffset = 16;
        private const int CurrentOffset = 24;
        private const int ActivityNextOffset = CurrentOffset + MaxDepth * EntrySize;
        private const int ExceptionNextOffset = ActivityNextOffset + 8;
        private const int ActivityOffset = ActivityNextOffset + 16;
        private const int JournalOffset = ActivityOffset + ActivitySize * EntrySize;
        private const int ExceptionsOffset = JournalOffset + 32 * EntrySize;
        private const int ContextSize = ExceptionsOffset + ExceptionSize * EntrySize;

        // Per-frame or per-entity listeners. They still show up as the current callback, but are kept
        // out of the activity history, which they would otherwise fill within a second.
        private static readonly HashSet<string> HighFrequencyListeners = new(StringComparer.Ordinal)
        {
            "OnTick", "OnServerPreEntityThink", "OnServerPostEntityThink", "OnServerPreWorldUpdate", "CheckTransmit",
            "OnUpdateWhenNotInGame", "OnEntityCreated", "OnEntitySpawned", "OnEntityDeleted", "OnEntityParentChanged",
            "OnClientVoice", "OnPlayerButtonsChanged"
        };

        // Last activity entry written, so a callback that keeps firing only refreshes its time.
        private static string? _lastActivityOwner, _lastActivityKind, _lastActivityDetail;
        private static int _lastActivitySlot = -1;

        private static byte* _context;
        private static int _mainThreadId = -1;
        private static int _depth;

        /// <summary>
        /// Plugin that owns callbacks created right now: the plugin being loaded/unloaded, or the owner of the
        /// callback being dispatched. Used to attribute callbacks whose delegate lives in this assembly
        /// (wrappers built by BasePlugin), which would otherwise be blamed on the framework.
        /// </summary>
        internal static string? CurrentOwner { get; private set; }

        internal static void Initialize()
        {
            try
            {
                var context = (byte*)NativeAPI.GetCrashContext();
                if (context == null || *(ulong*)context != Magic || *(uint*)(context + 8) != Version ||
                    *(uint*)(context + 12) != ContextSize)
                    return;

                _mainThreadId = Environment.CurrentManagedThreadId;
                _context = context;
            }
            catch
            {
                // Older native core without GET_CRASH_CONTEXT: recording stays off.
            }
        }

        internal static bool IsHighFrequencyListener(string listenerName) => HighFrequencyListeners.Contains(listenerName);

        private static bool Enabled => _context != null && Environment.CurrentManagedThreadId == _mainThreadId;

        /// <summary>Marks a callback as running. Pair every call with <see cref="Exit"/>.</summary>
        internal static string? Enter(string owner, string kind, string detail)
        {
            var previousOwner = CurrentOwner;
            CurrentOwner = owner;
            if (!Enabled) return previousOwner;

            try
            {
                if (_depth < MaxDepth)
                    WriteEntry(_context + CurrentOffset + _depth * EntrySize, owner, kind, detail);
                _depth++;
                *(uint*)(_context + DepthOffset) = (uint)_depth;
            }
            catch
            {
                // Never let crash bookkeeping break a plugin callback.
            }

            return previousOwner;
        }

        internal static void Exit(string? previousOwner)
        {
            CurrentOwner = previousOwner;
            if (!Enabled || _depth == 0) return;

            _depth--;
            *(uint*)(_context + DepthOffset) = (uint)_depth;
        }

        /// <summary>Adds an entry to the activity history.</summary>
        internal static void Activity(string owner, string kind, string detail)
        {
            if (!Enabled) return;

            if (_lastActivitySlot >= 0 && owner == _lastActivityOwner && kind == _lastActivityKind && detail == _lastActivityDetail)
            {
                *(long*)(_context + ActivityOffset + _lastActivitySlot * EntrySize) = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return;
            }

            _lastActivitySlot = Append(ActivityNextOffset, ActivityOffset, ActivitySize, owner, kind, detail);
            _lastActivityOwner = owner;
            _lastActivityKind = kind;
            _lastActivityDetail = detail;
        }

        /// <summary>Records an exception a plugin callback threw (and the core caught).</summary>
        internal static void Exception(string owner, string kind, string detail) =>
            Append(ExceptionNextOffset, ExceptionsOffset, ExceptionSize, owner, kind, detail);

        /// <summary>Plugin lifecycle (load, unload) as activity, and as the owner of callbacks it registers meanwhile.</summary>
        internal static string? EnterPlugin(string plugin, string kind)
        {
            Activity(plugin, kind, plugin);
            return Enter(plugin, kind, plugin);
        }

        // Returns the slot written, -1 if nothing was.
        private static int Append(int nextOffset, int ringOffset, int size, string owner, string kind, string detail)
        {
            if (!Enabled) return -1;

            try
            {
                var next = (uint*)(_context + nextOffset);
                var slot = *next % (uint)size;
                WriteEntry(_context + ringOffset + slot * EntrySize, owner, kind, detail);
                // Publish only after the entry is complete.
                *next = (slot + 1) % (uint)size;
                return (int)slot;
            }
            catch
            {
                return -1;
            }
        }

        private static void WriteEntry(byte* entry, string owner, string kind, string detail)
        {
            *(long*)entry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteString(entry + EntryPluginOffset, EntryPluginLength, owner);
            WriteString(entry + EntryKindOffset, EntryKindLength, kind);
            WriteString(entry + EntryDetailOffset, EntryDetailLength, detail);
        }

        // UTF-8, truncated to fit, always NUL-terminated.
        private static void WriteString(byte* dest, int size, string? value)
        {
            var span = new Span<byte>(dest, size - 1);
            Utf8.FromUtf16((value ?? string.Empty).AsSpan(), span, out _, out var written, replaceInvalidSequences: true,
                isFinalBlock: true);
            dest[written] = 0;
        }
    }
}
