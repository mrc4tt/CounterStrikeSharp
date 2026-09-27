using System;
using System.Runtime.CompilerServices;
using System.Threading;
using CounterStrikeSharp.API.Core;

namespace CounterStrikeSharp.API.Modules.Memory;

/// <summary>
/// A handle to one schema member (<c>Class::m_field</c>) whose offset is resolved once and
/// then cached on the instance, so each access is a pointer add instead of a cache lookup
/// keyed by the two names.
/// <para>
/// Intended to be declared once and reused, typically as a <c>static readonly</c> field:
/// <code>
/// private static readonly SchemaField&lt;int&gt; Health = new("CBaseEntity", "m_iHealth");
///
/// var hp = Health.Get(pawn);
/// Health.Set(pawn, 150); // writes directly, then marks the networked field changed
/// </code>
/// </para>
/// <para>
/// Nothing is resolved in the constructor: the offset (and, separately, whether the field is
/// networked) is looked up on first use, so declaring one in a static initializer is safe even
/// before the schema system is ready. Resolution honours the <c>FollowCS2ServerGuidelines</c>
/// blocklist exactly like <see cref="Schema.GetSchemaOffset"/>: every access to a blocked field
/// throws while the option is enabled.
/// </para>
/// <para>
/// Unmanaged <typeparamref name="T"/> (primitives, enums, blittable structs, <see cref="IntPtr"/>)
/// is read and written directly at the cached offset. Any other <typeparamref name="T"/>
/// (e.g. <see cref="string"/>) falls back to <see cref="Schema.GetSchemaValue{T}"/> /
/// <see cref="Schema.SetSchemaValue{T}"/>.
/// </para>
/// </summary>
/// <typeparam name="T">The managed type of the field.</typeparam>
public sealed class SchemaField<T>
{
    // Sentinel for "not resolved yet". Real offsets come back from native as int16, so this
    // can never collide with one.
    private const int Unresolved = int.MinValue;

    private const int NetworkUnresolved = 0;
    private const int NetworkNo = 1;
    private const int NetworkYes = 2;

    // Resolution is a benign race: two threads may both resolve, and both will write the same
    // values. _blocked / _chainOffset are written BEFORE the Volatile.Write that publishes
    // _offset / _networkState, and read AFTER the Volatile.Read that observes them, so a reader
    // that sees a resolved state never sees a stale companion value.
    private int _offset = Unresolved;
    private bool _blocked;
    private int _networkState = NetworkUnresolved;
    private int _chainOffset;

    /// <summary>
    /// Creates a handle to <paramref name="className"/>::<paramref name="fieldName"/>.
    /// Nothing is looked up until the first access.
    /// </summary>
    /// <param name="className" example="CBaseEntity">Schema class that declares the field</param>
    /// <param name="fieldName" example="m_iHealth">Schema field name</param>
    public SchemaField(string className, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(className);
        ArgumentNullException.ThrowIfNull(fieldName);

        ClassName = className;
        FieldName = fieldName;
    }

    /// <summary>
    /// Pre-resolved constructor: a test seam that lets the read/write paths be exercised
    /// off-server against a managed buffer, with no native schema lookup.
    /// </summary>
    internal SchemaField(string className, string fieldName, int offset, bool blocked = false, bool networked = false,
        int chainOffset = 0)
        : this(className, fieldName)
    {
        _blocked = blocked;
        _offset = offset;
        _chainOffset = chainOffset;
        _networkState = networked ? NetworkYes : NetworkNo;
    }

    /// <summary>
    /// Schema class name this field belongs to.
    /// </summary>
    public string ClassName { get; }

    /// <summary>
    /// Schema field name.
    /// </summary>
    public string FieldName { get; }

    /// <summary>
    /// Byte offset of the field inside <see cref="ClassName"/>. Resolved and cached on first access.
    /// </summary>
    /// <exception cref="Exception">The field is blocked by <c>FollowCS2ServerGuidelines</c>.</exception>
    public int Offset
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var offset = Volatile.Read(ref _offset);
            if (offset == Unresolved)
            {
                offset = ResolveOffset();
            }

            if (_blocked && CoreConfig.FollowCS2ServerGuidelines)
            {
                throw Schema.BlockedFieldException(ClassName, FieldName);
            }

            return offset;
        }
    }

    /// <summary>
    /// Whether the field is send-table networked. Resolved and cached on first access.
    /// </summary>
    public bool IsNetworked
    {
        get
        {
            var state = Volatile.Read(ref _networkState);
            if (state == NetworkUnresolved)
            {
                state = ResolveNetworkState();
            }

            return state == NetworkYes;
        }
    }

    /// <summary>
    /// Reads the field from the object at <paramref name="handle"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is <see cref="IntPtr.Zero"/></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe T Get(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentNullException(nameof(handle), "Schema target points to null.");

        var offset = Offset;

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return Unsafe.Read<T>((void*)(handle + offset));
        }

        return Schema.GetSchemaValue<T>(handle, ClassName, FieldName);
    }

    /// <summary>
    /// Reads the field from <paramref name="obj"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> is null or has a null handle</exception>
    public T Get(NativeObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return Get(obj.Handle);
    }

    /// <summary>
    /// Returns a reference to the field in the object at <paramref name="handle"/>.
    /// Only valid for unmanaged <typeparamref name="T"/>.
    /// <para>
    /// Writing through the reference does NOT notify the network layer. For a networked field,
    /// call <see cref="SetStateChanged(NativeObject)"/> afterwards, or use <see cref="Set(NativeObject, T)"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is <see cref="IntPtr.Zero"/></exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is or contains a reference type</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe ref T GetRef(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new ArgumentNullException(nameof(handle), "Schema target points to null.");

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new NotSupportedException($"{nameof(GetRef)} requires an unmanaged type; '{typeof(T)}' is or contains a reference.");
        }

        return ref Unsafe.AsRef<T>((void*)(handle + Offset));
    }

    /// <summary>
    /// Returns a reference to the field in <paramref name="obj"/>. See <see cref="GetRef(IntPtr)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> is null or has a null handle</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is or contains a reference type</exception>
    public ref T GetRef(NativeObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return ref GetRef(obj.Handle);
    }

    /// <summary>
    /// Writes the field on the object at <paramref name="handle"/>.
    /// <para>
    /// A raw pointer does not say whether it is an entity, so the only state-changed
    /// notification sent from here is the chain-entity one (for classes that carry a
    /// <c>__m_pChainEntity</c>, e.g. entity components), which is safe for any pointer to
    /// <see cref="ClassName"/>. To write a networked field directly on an entity, use
    /// <see cref="Set(NativeObject, T)"/> with the entity instead.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is <see cref="IntPtr.Zero"/></exception>
    public void Set(IntPtr handle, T value)
    {
        Write(handle, value);

        if (IsNetworked && _chainOffset != 0)
        {
            NativeAPI.SchemaNetworkStateChanged(handle + _chainOffset, (uint)Offset, 0xFFFFFFFF, 0xFFFFFFFF);
        }
    }

    /// <summary>
    /// Writes the field on <paramref name="obj"/> and, if the field is networked, marks it
    /// changed exactly as <see cref="Utilities.SetStateChanged"/> would (chain entity if the
    /// class has one, otherwise the entity's own state-changed path), so the new value is
    /// transmitted to clients. A networked field on a non-entity object whose class has no
    /// chain entity is written but cannot be notified.
    /// <para>
    /// Like the generated schema properties, this does not validate the entity; the caller is
    /// responsible for <paramref name="obj"/> still being alive.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> is null or has a null handle</exception>
    public void Set(NativeObject obj, T value)
    {
        ArgumentNullException.ThrowIfNull(obj);

        Write(obj.Handle, value);

        if (IsNetworked)
        {
            NotifyChanged(obj);
        }
    }

    /// <summary>
    /// Marks this field changed on <paramref name="obj"/> for network transmission, without
    /// writing it. Use after mutating the field through <see cref="GetRef(NativeObject)"/>.
    /// Does nothing when the field is not networked (the engine cannot resolve a
    /// non-networked offset and would log an error per call).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> is null or has a null handle</exception>
    public void SetStateChanged(NativeObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj.Handle == IntPtr.Zero) throw new ArgumentNullException(nameof(obj), "Schema target points to null.");

        if (IsNetworked)
        {
            NotifyChanged(obj);
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"{ClassName}::{FieldName}";

    private unsafe void Write(IntPtr handle, T value)
    {
        if (handle == IntPtr.Zero) throw new ArgumentNullException(nameof(handle), "Schema target points to null.");

        var offset = Offset;

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Unsafe.Write((void*)(handle + offset), value);
            return;
        }

        Schema.SetSchemaValue(handle, ClassName, FieldName, value);
    }

    private void NotifyChanged(NativeObject obj)
    {
        var offset = Offset;

        if (_chainOffset != 0)
        {
            NativeAPI.SchemaNetworkStateChanged(obj.Handle + _chainOffset, (uint)offset, 0xFFFFFFFF, 0xFFFFFFFF);
            return;
        }

        // No chain: the field lives on the entity itself. Anything that is not an entity
        // wrapper cannot be notified safely (SchemaSetStateChanged calls a virtual on it).
        switch (obj)
        {
            case CBaseEntity entity:
                Utilities.NotifyStateChanged(entity, offset, 0);
                break;
            case CEntityInstance instance:
                Utilities.NotifyStateChanged(new CBaseEntity(instance.Handle), offset, 0);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ResolveOffset()
    {
        var (offset, blocked) = Schema.ResolveEntry(ClassName, FieldName);

        _blocked = blocked;
        Volatile.Write(ref _offset, offset);

        return offset;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ResolveNetworkState()
    {
        var networked = Schema.IsSchemaFieldNetworked(ClassName, FieldName);

        // Same chain lookup as Utilities.SetStateChanged (FindSchemaChain). Only needed, and
        // only resolved, for networked fields.
        _chainOffset = networked ? Schema.ResolveEntry(ClassName, "__m_pChainEntity").Offset : 0;

        var state = networked ? NetworkYes : NetworkNo;
        Volatile.Write(ref _networkState, state);

        return state;
    }
}
