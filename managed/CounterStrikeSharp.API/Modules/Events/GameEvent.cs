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
using System.Threading;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;

namespace CounterStrikeSharp.API.Modules.Events
{
    public class EventAttribute : Attribute
    {
        public EventAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; set; }
    }

    public class GameEvent : NativeObject
    {
        // Set only for manually created events, which we have to free unless the engine does it for us
        // (FireEvent). Kept as a separate object so the wrappers handed to event hooks, which are created
        // for every fired event and own nothing, stay non-finalizable.
        private readonly OwnedEvent? _owned;

        private sealed class OwnedEvent
        {
            private readonly IntPtr _handle;
            private int _released;

            public OwnedEvent(IntPtr handle)
            {
                _handle = handle;
            }

            public bool TryMarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;

            // A created event that was never fired (or only fired with FireEventToClient, which does not
            // consume it) and never freed would otherwise stay allocated in the engine forever. Event
            // memory belongs to the game thread, so defer instead of freeing from the finalizer thread.
            ~OwnedEvent()
            {
                if (!TryMarkReleased())
                {
                    return;
                }

                var handle = _handle;
                Server.NextFrame(() => NativeAPI.FreeEvent(handle));
            }
        }

        public GameEvent(IntPtr pointer) : base(pointer)
        {
        }

        public GameEvent(string name, bool force) : this(NativeAPI.CreateEvent(name, force))
        {
            if (RawHandle != IntPtr.Zero)
            {
                _owned = new OwnedEvent(RawHandle);
            }
        }

        public string EventName => NativeAPI.GetEventName(Handle);

        public T Get<T>(string name)
        {
            var type = typeof(T);
            object result = type switch
            {
                _ when type == typeof(float) => GetFloat(name),
                _ when type == typeof(short) => GetInt(name),
                _ when type == typeof(int) => GetInt(name),
                _ when type == typeof(string) => GetString(name),
                _ when type == typeof(bool) => GetBool(name),
                _ when type == typeof(ulong) => GetUint64(name),
                _ when type == typeof(long) => (long)GetUint64(name),
                // This is a special case for player controllers as this method previously did not allow for nullable returns.
                // So we return an invalid player controller if the player is not found.
                _ when type == typeof(CCSPlayerController) => GetPlayer(name) ?? new CCSPlayerController(0),
                _ => throw new NotSupportedException(),
            };

            return (T)result;
        }

        public void Set<T>(string name, T value)
        {
            var type = typeof(T);
            switch (type)
            {
                case var _ when value is float f:
                    SetFloat(name, f);
                    break;
                case var _ when value is short s:
                    SetInt(name, s);
                    break;
                case var _ when value is int i:
                    SetInt(name, i);
                    break;
                case var _ when value is CCSPlayerController player:
                    SetPlayer(name, player);
                    break;
                case var _ when value is string s:
                    SetString(name, s);
                    break;
                case var _ when value is bool b:
                    SetBool(name, b);
                    break;
                case var _ when value is ulong ul:
                    SetUint64(name, ul);
                    break;
                case var _ when value is long l:
                    SetUint64(name, (ulong)l);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        protected bool GetBool(string name) => NativeAPI.GetEventBool(Handle, name);
        protected float GetFloat(string name) => NativeAPI.GetEventFloat(Handle, name);
        protected string GetString(string name) => NativeAPI.GetEventString(Handle, name);
        protected int GetInt(string name) => NativeAPI.GetEventInt(Handle, name);

        protected CCSPlayerController? GetPlayer(string name)
        {
            var ptr = NativeAPI.GetEventPlayerController(Handle, name);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            return new CCSPlayerController(ptr);
        }

        protected ulong GetUint64(string name) => NativeAPI.GetEventUint64(Handle, name);

        protected void SetUint64(string name, ulong value) => NativeAPI.SetEventUint64(Handle, name, value);

        // public Player GetPlayer(string name) => Player.FromUserId(GetInt(name));

        protected void SetBool(string name, bool value) => NativeAPI.SetEventBool(Handle, name, value);
        protected void SetFloat(string name, float value) => NativeAPI.SetEventFloat(Handle, name, value);
        protected void SetString(string name, string value) => NativeAPI.SetEventString(Handle, name, value);
        protected void SetInt(string name, int value) => NativeAPI.SetEventInt(Handle, name, value);
        protected void SetInt(string name, long value) => SetInt(name, (int)value);

        protected void SetEntity(string name, IntPtr value) => NativeAPI.SetEventEntity(Handle, name, value);

        protected void SetEntityIndex(string name, int value) => NativeAPI.SetEventEntityIndex(Handle, name, value);

        protected void SetPlayer(string name, CCSPlayerController? player) => NativeAPI.SetEventPlayerController(Handle, name, player?.Handle ?? IntPtr.Zero);

        public void FireEvent(bool dontBroadcast)
        {
            NativeAPI.FireEvent(Handle, dontBroadcast);
            _owned?.TryMarkReleased();
        }

        public void FireEventToClient(CCSPlayerController player) => NativeAPI.FireEventToClient(Handle, (int)player.Index);

        /// <summary>
        /// Used to manually free the event.
        /// <remarks>If <see cref="FireEvent"/> is called, Free will be called automatically.</remarks>
        /// </summary>
        public void Free()
        {
            if (_owned == null || !_owned.TryMarkReleased())
            {
                throw new InvalidOperationException("Event is not able to be freed.");
            }

            NativeAPI.FreeEvent(Handle);
        }
    }
}
