using FastGenericNew;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace CounterStrikeSharp.API.Core;

public partial class CBaseEntity
{
    /// <exception cref="InvalidOperationException">Entity is not valid</exception>
    /// <exception cref="ArgumentNullException">At least one parameter must be specified</exception>
    public void Teleport(Vector? position = null, QAngle? angles = null, Vector? velocity = null)
    {
        Teleport(position == null ? null : (Vector3)position, angles == null ? null : (Vector3)angles,
            velocity == null ? null : (Vector3)velocity);
    }

    /// <summary>
    /// Teleports the entity to the specified position, angles, and velocity using Vector3 parameters.
    /// This overload is optimized for memory efficiency by directly working with a Vector3 struct.
    /// </summary>
    /// <exception cref="InvalidOperationException">Entity is not valid</exception>
    /// <exception cref="ArgumentException">At least one parameter must be specified</exception>
    public void Teleport(Vector3? position = null, Vector3? angles = null, Vector3? velocity = null)
    {
        Guard.IsValidEntity(this);

        if (position == null && angles == null && velocity == null)
            throw new ArgumentException("At least one parameter must be specified");

        unsafe
        {
            void* positionPtr = null, anglePtr = null, velocityPtr = null;

            if (position.HasValue)
            {
                var pos = position.Value;
                positionPtr = &pos;
            }

            if (angles.HasValue)
            {
                var ang = angles.Value;
                anglePtr = &ang;
            }

            if (velocity.HasValue)
            {
                var vel = velocity.Value;
                velocityPtr = &vel;
            }

            // Call the vtable entry directly: the old path looked up the gamedata offset, built a new
            // VirtualFunction (LINQ, boxing, a native CreateVirtualFunction) and dynamically invoked it
            // on every teleport. The target is the same vtable slot, so hooks on it still run.
            var entity = Handle;
            var function = (delegate* unmanaged<nint, nint, nint, nint, void>)
                ((nint*)*(nint*)entity)[TeleportOffset];
            function(entity, (nint)positionPtr, (nint)anglePtr, (nint)velocityPtr);
        }
    }

    // Cached per loaded gamedata set: GameData.Load builds a new Methods dictionary, so a reload
    // (e.g. after a gamedata update) is picked up.
    private static Dictionary<string, LoadedGameData>? s_teleportOffsetSource;
    private static int s_teleportOffset;

    private static int TeleportOffset
    {
        get
        {
            var methods = GameData.GameDataProvider.Methods;
            if (!ReferenceEquals(methods, s_teleportOffsetSource))
            {
                s_teleportOffset = GameData.GetOffset("CBaseEntity_Teleport");
                s_teleportOffsetSource = methods;
            }
            return s_teleportOffset;
        }
    }

    /// <exception cref="InvalidOperationException">Entity is not valid</exception>
    public void DispatchSpawn(CEntityKeyValues? keyValues)
    {
        // A freshly created entity (CreateEntityByName) has no valid EHandle until it
        // is spawned, so the handle-resolved Handle/IsValid path reports it invalid.
        // Spawn from the raw construction pointer, which is the entity we just created.
        if (RawHandle == IntPtr.Zero)
            throw new InvalidOperationException("Entity is not valid");

        NativeAPI.DispatchSpawn(RawHandle, keyValues?.Handle ?? IntPtr.Zero);
    }

    public void DispatchSpawn()
    {
        if (RawHandle == IntPtr.Zero)
            throw new InvalidOperationException("Entity is not valid");

        NativeAPI.DispatchSpawn(RawHandle, IntPtr.Zero);
    }

    /// <summary>
    /// Shorthand for accessing an entity's CBodyComponent?.SceneNode?.AbsOrigin;
    /// </summary>
    public Vector? AbsOrigin => CBodyComponent?.SceneNode?.AbsOrigin;

    /// <summary>
    /// Shorthand for accessing an entity's CBodyComponent?.SceneNode?.AbsRotation;
    /// </summary>
    /// <exception cref="InvalidOperationException">Entity is not valid</exception>
    public QAngle? AbsRotation => CBodyComponent?.SceneNode?.AbsRotation;

    public T? GetVData<T>() where T : CEntitySubclassVDataBase
    {
        Guard.IsValidEntity(this);

        return FastNew.CreateInstance<T, IntPtr>(Marshal.ReadIntPtr(SubclassID.Handle + 4));
    }

    /// <summary>
    /// Emit a soundevent to all players.
    /// </summary>
    /// <param name="soundEventName">The name of the soundevent to emit.</param>
    /// <param name="recipients">The recipients of the soundevent.</param>
    /// <param name="volume">The volume of the soundevent.</param>
    /// <param name="pitch">The pitch of the soundevent.</param>
    /// <returns>The sound event guid.</returns>
    public uint EmitSound(string soundEventName, RecipientFilter? recipients = null, float volume = 1f, float pitch = 0)
    {
        Guard.IsValidEntity(this);

        if (recipients == null)
        {
            recipients = new RecipientFilter();
            recipients.AddAllPlayers();
        }

        return NativeAPI.EmitSoundFilter(recipients.GetRecipientMask(), this.Index, soundEventName, volume, pitch);
    }

    /// <summary>
    /// Returns true if the entity is a player pawn.
    /// </summary>
    public bool IsPlayerPawn()
    {
        Guard.IsValidEntity(this);

        return VirtualFunction.Create<IntPtr, bool>(Handle, GameData.GetOffset("CBaseEntity_IsPlayerPawn"))(Handle);
    }
}