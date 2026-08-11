using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API.Core;
using FastGenericNew;

namespace CounterStrikeSharp.API.Modules.Memory;

public class Schema
{
    /// <summary>
    /// Everything resolved once per (class, property) pair: the byte offset, plus
    /// whether the property is on the CS2 server-guidelines blocklist. Folding the
    /// blocklist answer in here removes a second string hash (the
    /// <c>_cs2BadList.Contains</c> probe) from every schema property read.
    /// </summary>
    private readonly struct SchemaEntry
    {
        public readonly short Offset;
        public readonly bool Blocked;

        public SchemaEntry(short offset, bool blocked)
        {
            Offset = offset;
            Blocked = blocked;
        }
    }

    /// <summary>
    /// Compares (class, property) keys by REFERENCE, not by content. The generated
    /// schema accessors (and virtually all plugin code) pass string literals, which
    /// the runtime interns — so the same call site hands us the same two string
    /// objects forever. Hashing their object identity is a couple of loads; hashing
    /// their contents was two randomized Marvin passes over ~20 characters plus an
    /// ordinal compare, paid on EVERY property read (entity.Health, .AbsOrigin, ...).
    /// </summary>
    private sealed class RefPairComparer : IEqualityComparer<(string ClassName, string PropertyName)>
    {
        public static readonly RefPairComparer Instance = new();

        public bool Equals((string ClassName, string PropertyName) x, (string ClassName, string PropertyName) y)
            => ReferenceEquals(x.ClassName, y.ClassName) && ReferenceEquals(x.PropertyName, y.PropertyName);

        public int GetHashCode((string ClassName, string PropertyName) key)
            => (RuntimeHelpers.GetHashCode(key.ClassName) * 397) ^ RuntimeHelpers.GetHashCode(key.PropertyName);
    }

    // Fast path. Only interned (literal) keys are admitted, so its key space is
    // bounded by the string literal pool rather than growing with every runtime-built
    // name a plugin passes in.
    private static readonly ConcurrentDictionary<(string ClassName, string PropertyName), SchemaEntry> _offsetsByRef
        = new(RefPairComparer.Instance);

    // Correctness path: content-keyed, so a runtime-built string still resolves (and
    // still only costs one native lookup ever).
    //
    // Both are ConcurrentDictionary rather than Dictionary: the old cache was a plain
    // Dictionary mutated with no synchronisation, so a schema read from a worker
    // thread racing the game thread could observe a half-resized table.
    private static readonly ConcurrentDictionary<(string ClassName, string PropertyName), SchemaEntry> _offsetsByValue
        = new();

    private static HashSet<string> _cs2BadList = new HashSet<string>()
    {
        "m_bIsValveDS",
        "m_bIsQuestEligible",
        // "m_iItemDefinitionIndex", // as of 2023.11.11 this is currently not blocked
        "m_iEntityLevel",
        "m_iItemIDHigh",
        "m_iItemIDLow",
        "m_iAccountID",
        "m_iEntityQuality",

        "m_bInitialized",
        "m_szCustomName",
        "m_iAttributeDefinitionIndex",
        "m_iRawValue32",
        "m_iRawInitialValue32",
        "m_flValue", // MNetworkAlias "m_iRawValue32"
        "m_flInitialValue", // MNetworkAlias "m_iRawInitialValue32"
        "m_bSetBonus",
        "m_nRefundableCurrency",

        "m_OriginalOwnerXuidLow",
        "m_OriginalOwnerXuidHigh",

        "m_nFallbackPaintKit",
        "m_nFallbackSeed",
        "m_flFallbackWear",
        "m_nFallbackStatTrak",

        "m_iCompetitiveWins",
        "m_iCompetitiveRanking",
        "m_iCompetitiveRankType",
        "m_iCompetitiveRankingPredicted_Win",
        "m_iCompetitiveRankingPredicted_Loss",
        "m_iCompetitiveRankingPredicted_Tie",

        "m_nActiveCoinRank",
        "m_nMusicID",
    };

    public static int GetClassSize(string className) => NativeAPI.GetSchemaClassSize(className);

    public static short GetSchemaOffset(string className, string propertyName)
    {
        var entry = Resolve(className, propertyName);

        if (entry.Blocked && CoreConfig.FollowCS2ServerGuidelines)
        {
            throw new Exception($"Cannot set or get '{className}::{propertyName}' with \"FollowCS2ServerGuidelines\" option enabled.");
        }

        return entry.Offset;
    }

    private static short GetSchemaOffsetCore(string className, string propertyName) => Resolve(className, propertyName).Offset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SchemaEntry Resolve(string className, string propertyName)
    {
        return _offsetsByRef.TryGetValue((className, propertyName), out var entry)
            ? entry
            : ResolveSlow(className, propertyName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SchemaEntry ResolveSlow(string className, string propertyName)
    {
        var key = (className, propertyName);

        if (!_offsetsByValue.TryGetValue(key, out var entry))
        {
            entry = new SchemaEntry(NativeAPI.GetSchemaOffset(className, propertyName), _cs2BadList.Contains(propertyName));
            _offsetsByValue[key] = entry;
        }

        // Admit to the reference-keyed cache only when both halves ARE the interned
        // instance. A literal always is; a runtime-built name (string.Concat, config
        // value, ...) generally is not, and admitting those would let the fast cache
        // grow without bound while never producing a hit.
        if (ReferenceEquals(string.IsInterned(className), className) &&
            ReferenceEquals(string.IsInterned(propertyName), propertyName))
        {
            _offsetsByRef[key] = entry;
        }

        return entry;
    }

    public static bool IsSchemaFieldNetworked(string className, string propertyName)
    {
        return NativeAPI.IsSchemaFieldNetworked(className, propertyName);
    }

    public static unsafe T GetSchemaValue<T>(IntPtr handle, string className, string propertyName)
    {
        if (handle == IntPtr.Zero) throw new ArgumentNullException(nameof(handle), "Schema target points to null.");

        // Fast path: blittable value types (int/float/bool/enums/blittable
        // structs/IntPtr) are read straight from the cached offset, skipping the
        // per-call string marshal + native schema lookup. Mirrors how the
        // generated .g.cs accessors read primitives via GetRef<T>. Reference
        // types (string, ...) still need the native marshalling path.
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            var offset = GetSchemaOffsetCore(className, propertyName);
            return Unsafe.Read<T>((void*)(handle + offset));
        }

        return NativeAPI.GetSchemaValueByName<T>(handle, (int)typeof(T).ToDataType()!, className, propertyName);
    }

    public static void SetSchemaValue<T>(IntPtr handle, string className, string propertyName, T value)
    {
        if (handle == IntPtr.Zero) throw new ArgumentNullException(nameof(handle), "Schema target points to null.");

        if (CoreConfig.FollowCS2ServerGuidelines && _cs2BadList.Contains(propertyName))
        {
            throw new Exception($"Cannot set or get '{className}::{propertyName}' with \"FollowCS2ServerGuidelines\" option enabled.");
        }

        NativeAPI.SetSchemaValueByName<T>(handle, (int)typeof(T).ToDataType()!, className, propertyName, value);
    }

    public static T GetDeclaredClass<T>(IntPtr pointer, string className, string memberName)
    {
        if (pointer == IntPtr.Zero) throw new ArgumentNullException(nameof(pointer), "Schema target points to null.");

        return FastNew.CreateInstance<T, IntPtr>(pointer + GetSchemaOffset(className, memberName));
    }

    public static unsafe ref T GetRef<T>(IntPtr pointer, string className, string memberName)
    {
        if (pointer == IntPtr.Zero) throw new ArgumentNullException(nameof(pointer), "Schema target points to null.");

        return ref Unsafe.AsRef<T>((void*)(pointer + GetSchemaOffset(className, memberName)));
    }

    public static T GetPointer<T>(IntPtr pointer)
    {
        var pointerTo = Marshal.ReadIntPtr(pointer);
        if (pointerTo == IntPtr.Zero)
        {
            return default;
        }

        return FastNew.CreateInstance<T, IntPtr>(pointerTo);
    }

    public static T GetPointer<T>(IntPtr pointer, string className, string memberName)
    {
        if (pointer == IntPtr.Zero) throw new ArgumentNullException(nameof(pointer), "Schema target points to null.");

        var pointerTo = Marshal.ReadIntPtr(pointer + GetSchemaOffset(className, memberName));
        if (pointerTo == IntPtr.Zero)
        {
            return default;
        }

        return FastNew.CreateInstance<T, IntPtr>(pointerTo);
    }

    public static unsafe Span<T> GetFixedArray<T>(IntPtr pointer, string className, string memberName, int count)
    {
        if (pointer == IntPtr.Zero) throw new ArgumentNullException(nameof(pointer), "Schema target points to null.");

        Span<T> span = new((void*)(pointer + GetSchemaOffset(className, memberName)), count);
        return span;
    }

    /// <summary>
    /// Reads a string from the specified pointer, class name, and member name.
    /// These are for non-networked strings, which are just stored as raw char bytes on the server.
    /// </summary>
    /// <returns></returns>
    public static string GetString(IntPtr pointer, string className, string memberName)
    {
        return GetSchemaValue<string>(pointer, className, memberName);
    }

    /// <summary>
    /// Reads a UTF8 encoded string from the specified pointer, class name, and member name.
    /// These are for networked strings, which need to be read differently.
    /// </summary>
    /// <param name="pointer"></param>
    /// <param name="className"></param>
    /// <param name="memberName"></param>
    /// <returns></returns>
    public static string GetUtf8String(IntPtr pointer, string className, string memberName)
    {
        return Utilities.ReadStringUtf8(pointer + GetSchemaOffset(className, memberName));
    }

    // Used to write to `string_t` and `char*` pointer type strings
    public unsafe static void SetString(IntPtr pointer, string className, string memberName, string value)
    {
        SetSchemaValue(pointer, className, memberName, value);
    }

    // Used to write to the char[] specified at the schema location, i.e. char m_iszPlayerName[128]; 
    internal unsafe static void SetStringBytes(IntPtr pointer, string className, string memberName, string value, int maxLength)
    {
        // Inline char[] buffer: the field IS the storage, so we need its address
        // (pointer + offset), not a deref. GetSchemaValue<IntPtr> hits the blittable
        // fast path which Unsafe.Read's the field (deref) and returns the zero'd
        // buffer bytes => null handle => NRE on write. Compute the address directly.
        var handle = pointer + GetSchemaOffset(className, memberName);

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maxLength)
        {
            throw new ArgumentException($"String length exceeds maximum length of {maxLength}");
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            Unsafe.Write((void*)(handle.ToInt64() + i), bytes[i]);
        }

        Unsafe.Write((void*)(handle.ToInt64() + bytes.Length), 0);
    }

    public static T GetCustomMarshalledType<T>(IntPtr pointer, string className, string memberName)
    {
        var type = typeof(T);
        object result = type switch
        {
            _ when type == typeof(Color) => Marshaling.ColorMarshaler.NativeToManaged(pointer + GetSchemaOffset(className, memberName)),
            _ => throw new NotSupportedException(),
        };

        return (T)result;
    }

    public static void SetCustomMarshalledType<T>(IntPtr pointer, string className, string memberName, T value)
    {
        var type = typeof(T);
        switch (type)
        {
            case var _ when value is Color c:
                Marshaling.ColorMarshaler.ManagedToNative(pointer + GetSchemaOffset(className, memberName), c);
                break;
            default:
                throw new NotSupportedException();
        }
    }
}
