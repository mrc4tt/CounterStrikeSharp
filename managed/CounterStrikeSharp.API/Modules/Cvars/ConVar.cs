using System.Runtime.CompilerServices;

namespace CounterStrikeSharp.API.Modules.Cvars;

[Obsolete("Use ConVar<T> instead for type-safe access to ConVars.")]
public class ConVar
{
    public ushort AccessIndex { get; protected set; }

    public ConVar(ushort accessIndex)
    {
        AccessIndex = accessIndex;
    }

    public string Name => NativeAPI.GetConvarName(AccessIndex);
    public string Description => NativeAPI.GetConvarHelpText(AccessIndex);

    /// <summary>
    /// The underlying data type of the ConVar.
    /// </summary>
    public ConVarType Type => (ConVarType)NativeAPI.GetConvarType(AccessIndex);

    /// <summary>
    /// The ConVar flags as defined by <see cref="ConVarFlags"/>.
    /// </summary>
    public ConVarFlags Flags
    {
        get => (ConVarFlags)NativeAPI.GetConvarFlags(AccessIndex);
        set => NativeAPI.SetConvarFlags(AccessIndex, (ulong)value);
    }

    /// <summary>
    /// Used to access primitive value types, i.e. <see langword="bool"/>, <see langword="float"/>, <see langword="int"/>, etc.
    /// </summary>
    /// <typeparam name="T">The type of value to retrieve</typeparam>
    // A ConVar's type never changes, so GetPrimitiveValue reads it once instead of asking native on every
    // read (that doubled the native calls per value read).
    private ConVarType? _cachedType;

    public unsafe ref T GetPrimitiveValue<T>()
    {
        var type = typeof(T);
        var convarType = _cachedType ??= Type;

        // Valve regularly retypes a bool ConVar as an integer (and back) between game updates, and
        // plugins built against the old type read it with the wrong T. That used to throw out of the
        // plugin's handler on every read. Bool and integer types are converted instead.
        if (IsBoolIntegerMismatch(convarType, type))
            return ref GetConvertedPrimitiveValue<T>(convarType);

        switch (convarType)
        {
            case ConVarType.Bool:
                if (type != typeof(bool))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.Float32:
            case ConVarType.Float64:
                if (type != typeof(float))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.UInt16:
                if (type != typeof(ushort))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.Int16:
                if (type != typeof(short))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.UInt32:
                if (type != typeof(uint))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.Int32:
                if (type != typeof(int))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.Int64:
                if (type != typeof(long))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.UInt64:
                if (type != typeof(ulong))
                    throw new InvalidOperationException(
                        $"ConVar is a {Type} but you are trying to get a {type} value.");
                break;
            case ConVarType.String:
            case ConVarType.Qangle:
            case ConVarType.Vector2:
            case ConVarType.Vector3:
            case ConVarType.Vector4:
            case ConVarType.Color:
                throw new InvalidOperationException("Reference types must be accessed using `GetReferenceValue`");
        }

        var address = GetValueAddress();
        if (address == IntPtr.Zero)
        {
            throw new InvalidOperationException($"ConVar {Name} is not initialized or does not have a value.");
        }

        return ref Unsafe.AsRef<T>((void*)address);
    }

    private static bool IsIntegerType(ConVarType type) => type is ConVarType.Int16 or ConVarType.UInt16
        or ConVarType.Int32 or ConVarType.UInt32 or ConVarType.Int64 or ConVarType.UInt64;

    private static bool IsIntegerType(Type type) => type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

    private static bool IsBoolIntegerMismatch(ConVarType convarType, Type requested) =>
        (requested == typeof(bool) && IsIntegerType(convarType)) ||
        (convarType == ConVarType.Bool && IsIntegerType(requested));

    private static int IntegerSize(ConVarType type) => type switch
    {
        ConVarType.Int16 or ConVarType.UInt16 => 2,
        ConVarType.Int32 or ConVarType.UInt32 => 4,
        _ => 8,
    };

    // Scratch storage for a converted value. The returned ref points here, so a write through it
    // does not reach the ConVar; use SetValue to change a ConVar whose type differs from T.
    private ulong _convertedValue;

    private unsafe ref T GetConvertedPrimitiveValue<T>(ConVarType convarType)
    {
        var address = GetValueAddress();
        if (address == IntPtr.Zero)
        {
            throw new InvalidOperationException($"ConVar {Name} is not initialized or does not have a value.");
        }

        ulong raw = convarType == ConVarType.Bool
            ? (*(byte*)address != 0 ? 1UL : 0UL)
            : IntegerSize(convarType) switch
            {
                2 => *(ushort*)address,
                4 => *(uint*)address,
                _ => *(ulong*)address,
            };

        _convertedValue = 0;
        fixed (ulong* scratch = &_convertedValue)
        {
            if (typeof(T) == typeof(bool))
                *(bool*)scratch = raw != 0;
            else
                *scratch = raw; // 0 or 1, little-endian: the low bytes hold it for any integer width
        }

        return ref Unsafe.As<ulong, T>(ref _convertedValue);
    }

    // The value address through the fast native table (~5 ns instead of a ~40 ns ScriptContext call).
    // The fast entry reports an invalid access index as null instead of throwing, so ask the
    // ScriptContext native again in that case: it throws the same NativeException as before.
    private IntPtr GetValueAddress()
    {
        var address = FastNatives.GetConvarValueAddress(AccessIndex);
        return address != IntPtr.Zero ? address : NativeAPI.GetConvarValueAddress(AccessIndex);
    }

    public void SetValue<T>(T value)
    {
        NativeAPI.SetConvarValue(AccessIndex, value);
    }

    /// <summary>
    /// Used to access reference value types, i.e. Vector, QAngle
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T GetNativeValue<T>() where T : NativeObject
    {
        return NativeAPI.GetConvarValue<T>(AccessIndex);
    }

    /// <summary>
    /// String value of the ConVar.
    /// </summary>
    /// <remarks>String is a special exception as we have to marshal the string to UTF8 on the send/receive to unmanaged code.
    /// </remarks>
    public string StringValue
    {
        get => GetStringValueCached();
        set => NativeAPI.SetConvarValueAsString(AccessIndex, value);
    }

    // StringValue formatted the value natively on every read (~200 ns plus a new string). Cache the
    // formatted string with the raw value it came from, and reuse it while the raw value is unchanged.
    // The raw value is read from the ConVar's own storage (CVValue_t): the text itself for string
    // ConVars (its buffer can be reused in place), the first 16 bytes of the value union otherwise.
    // One immutable snapshot so a read from another thread never pairs a new raw value with an old string.
    private sealed record StringValueSnapshot(byte[] Raw, string Value);
    private StringValueSnapshot? _stringValueSnapshot;
    private const int RawValueSize = 16; // largest CVValue_t members: Vector4D / QAngle

    private unsafe string GetStringValueCached()
    {
        var address = GetValueAddress();
        if (address == IntPtr.Zero)
            return NativeAPI.GetConvarValueAsString(AccessIndex);

        ReadOnlySpan<byte> raw;
        if ((_cachedType ??= Type) == ConVarType.String)
        {
            var text = *(byte**)address; // CUtlString::m_pString
            raw = text == null ? ReadOnlySpan<byte>.Empty : System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
        }
        else
        {
            raw = new ReadOnlySpan<byte>((void*)address, RawValueSize);
        }

        var snapshot = _stringValueSnapshot;
        if (snapshot != null && raw.SequenceEqual(snapshot.Raw))
            return snapshot.Value;

        var value = NativeAPI.GetConvarValueAsString(AccessIndex);
        _stringValueSnapshot = new StringValueSnapshot(raw.ToArray(), value);
        return value;
    }

    /// <summary>
    /// Shorthand for checking the <see cref="ConVarFlags.FCVAR_NOTIFY"/> flag.
    /// </summary>
    public bool Public
    {
        get => Flags.HasFlag(ConVarFlags.FCVAR_NOTIFY);
        set
        {
            if (value)
            {
                Flags |= ConVarFlags.FCVAR_NOTIFY;
            }
            else
            {
                Flags &= ~ConVarFlags.FCVAR_NOTIFY;
            }
        }
    }

    public override string ToString()
    {
        return $"ConVar [name={Name}, description={Description}, type={Type}, flags={Flags}]";
    }

    /// <summary>
    /// Finds a ConVar by name.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static ConVar? Find(string name)
    {
        var accessIndex = NativeAPI.GetConvarAccessIndexByName(name);
        if (accessIndex == 0) return null;

        return new ConVar(accessIndex);
    }
}