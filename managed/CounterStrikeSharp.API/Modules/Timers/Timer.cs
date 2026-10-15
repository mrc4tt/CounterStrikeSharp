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
using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Modules.Timers
{
    [Flags]
    public enum TimerFlags
    {
        REPEAT = (1 << 0), // Timer will repeat until stopped
        STOP_ON_MAPCHANGE = (1 << 1)

    }

    public class Timer : NativeObject
    {
        // Identifier of the callback's FunctionReference. KillTimer stops native from
        // calling the pointer again, but the managed FunctionReference (which holds the
        // plugin callback delegate) must also be removed or it pins the plugin's
        // AssemblyLoadContext forever and leaks one keep-alive stub per timer across reloads.
        private readonly int _functionReferenceIdentifier;
        private bool _killed;
        private bool _finished;

        /// <summary>
        /// Raised once, when the timer can never run again: a one-shot fired, <see cref="Kill"/>
        /// was called, or a STOP_ON_MAPCHANGE timer was cut by the map change.
        /// </summary>
        internal event Action<Timer>? Finished;

        // Live STOP_ON_MAPCHANGE timers. Native deletes them at level end without telling managed
        // code, so their references (pinning the plugin) and Timers entries stayed forever; they
        // are killed here on OnMapEnd instead. Game thread only.
        private static readonly System.Collections.Generic.HashSet<Timer> MapChangeTimers = new();
        private static bool _mapEndListenerAdded;

        public Timer(float interval, Action callback, TimerFlags? flags = null) : base(IntPtr.Zero)
        {
            var repeat = ((flags ?? 0) & TimerFlags.REPEAT) != 0;

            // Own reference per timer (CreateUnique): the shared one from Create() meant two
            // timers on the same method shared an id, and killing one stopped the other.
            // A one-shot is SingleUse so the reference is reclaimed once it fires, instead of
            // living (and pinning the plugin's ALC) until the plugin unloads.
            var functionReference = FunctionReference.CreateUnique(callback,
                repeat ? FunctionLifetime.Permanent : FunctionLifetime.SingleUse);
            functionReference.SetCrashLabel("timer",
                (callback.Method.DeclaringType?.Name ?? "?") + "." + callback.Method.Name + " (" + interval.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s)");
            if (!repeat)
            {
                functionReference.SingleUseCompleted = () =>
                {
                    _killed = true;
                    Finish();
                };
            }

            _functionReferenceIdentifier = functionReference.Identifier;
            // Handle is the native timer's serial, not a pointer (see natives_timers.cpp).
            Handle = NativeAPI.CreateTimer(interval, functionReference, (int)(flags ?? 0));

            if (((flags ?? 0) & TimerFlags.STOP_ON_MAPCHANGE) != 0)
            {
                EnsureMapEndListener();
                MapChangeTimers.Add(this);
            }
        }

        public void Kill()
        {
            if (_killed) return;
            _killed = true;

            // Native first: once KillTimer returns it never calls this reference again (a timer
            // killed from its own callback is flagged and dropped after it returns), so the
            // stub can be released rather than rooted forever.
            NativeAPI.KillTimer(Handle);
            FunctionReference.RemoveReleased(_functionReferenceIdentifier);
            Finish();
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            MapChangeTimers.Remove(this);
            Finished?.Invoke(this);
        }

        private static void EnsureMapEndListener()
        {
            if (_mapEndListenerAdded) return;
            _mapEndListenerAdded = true;

            // Core-owned and never removed: lives for the process, like the timers it serves.
            var listener = FunctionReference.Create(new Action(() =>
            {
                foreach (var timer in new System.Collections.Generic.List<Timer>(MapChangeTimers))
                    timer.Kill();
            }));
            NativeAPI.AddListener("OnMapEnd", listener);
        }
    }
}