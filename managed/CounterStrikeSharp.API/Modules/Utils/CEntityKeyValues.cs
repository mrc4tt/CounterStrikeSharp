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

using System.Drawing;
using System.Numerics;
using System.Threading;

namespace CounterStrikeSharp.API.Modules.Utils
{
    /// <summary>
    /// <b>WARNING: This is intended to be only used with <see cref="CEntityKeyValues"/> for now!</b>
    /// </summary>
    public enum KeyValuesType : uint
    {
        TYPE_BOOL,
        TYPE_INT,
        TYPE_UINT,
        TYPE_INT64,
        TYPE_UINT64,
        TYPE_FLOAT,
        TYPE_DOUBLE,
        TYPE_STRING,
        TYPE_POINTER,
        TYPE_STRING_TOKEN,
        TYPE_EHANDLE,
        TYPE_COLOR,
        TYPE_VECTOR,
        TYPE_VECTOR2D,
        TYPE_VECTOR4D,
        TYPE_QUATERNION,
        TYPE_QANGLE,
        TYPE_MATRIX3X4
    }

    /// <summary>
    /// <b>WARNING: This is intended to be only used with <see cref="CBaseEntity.DispatchSpawn"/> for now!</b>
    /// </summary>
    public class CEntityKeyValues : NativeObject, IDisposable
    {
        // Set for instances created through EntityKeyValuesNew, which hands us one reference. The engine
        // takes its own reference while an entity spawns from these values, so ours is still outstanding
        // after DispatchSpawn and the native object lives until it is released.
        private readonly bool _ownsReference;
        private int _released;

        public CEntityKeyValues() : base(NativeAPI.EntityKeyValuesNew())
        {
            _ownsReference = true;
        }

        public CEntityKeyValues(nint pointer) : base(pointer)
        {
            // Wraps a pointer somebody else owns: nothing to release, so skip the finalizer queue.
            GC.SuppressFinalize(this);
        }

        public object? this[string key, KeyValuesType type]
        {
            get
            {
                return type switch
                {
                    KeyValuesType.TYPE_BOOL => GetBool(key),
                    KeyValuesType.TYPE_INT => GetInt(key),
                    KeyValuesType.TYPE_UINT => GetUInt(key),
                    KeyValuesType.TYPE_INT64 => GetInt64(key),
                    KeyValuesType.TYPE_UINT64 => GetUInt64(key),
                    KeyValuesType.TYPE_FLOAT => GetFloat(key),
                    KeyValuesType.TYPE_DOUBLE => GetDouble(key),
                    KeyValuesType.TYPE_STRING => GetString(key),
                    KeyValuesType.TYPE_POINTER => GetPointer(key),
                    KeyValuesType.TYPE_STRING_TOKEN => GetStringToken(key),
                    KeyValuesType.TYPE_EHANDLE => GetEHandle(key),
                    KeyValuesType.TYPE_COLOR => GetColor(key),
                    KeyValuesType.TYPE_VECTOR => GetVector(key),
                    KeyValuesType.TYPE_VECTOR2D => GetVector2D(key),
                    KeyValuesType.TYPE_VECTOR4D => GetVector4D(key),
                    KeyValuesType.TYPE_QUATERNION => GetQuaternion(key),
                    KeyValuesType.TYPE_QANGLE => GetAngle(key),
                    KeyValuesType.TYPE_MATRIX3X4 => GetMatrix3x4(key),
                    _ => null
                };
            }

            set
            {
#pragma warning disable CS8605 // Unboxing a possibly null value.
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8604 // Possible null reference argument.
                switch (type)
                {
                    case KeyValuesType.TYPE_BOOL:
                        SetBool(key, (bool)value);
                        break;
                    case KeyValuesType.TYPE_INT:
                        SetInt(key, (int)value);
                        break;
                    case KeyValuesType.TYPE_UINT:
                        SetUInt(key, (uint)value);
                        break;
                    case KeyValuesType.TYPE_INT64:
                        SetInt64(key, (long)value);
                        break;
                    case KeyValuesType.TYPE_UINT64:
                        SetUInt64(key, (ulong)value);
                        break;
                    case KeyValuesType.TYPE_FLOAT:
                        SetFloat(key, (float)value);
                        break;
                    case KeyValuesType.TYPE_DOUBLE:
                        SetDouble(key, (double)value);
                        break;
                    case KeyValuesType.TYPE_STRING:
                        SetString(key, (string)value);
                        break;
                    case KeyValuesType.TYPE_POINTER:
                        SetPointer(key, (nint)value);
                        break;
                    case KeyValuesType.TYPE_STRING_TOKEN:
                        // TODO: use 'CUtlStringToken' once we have it
                        SetStringToken(key, (uint)value);
                        break;
                    case KeyValuesType.TYPE_EHANDLE:
                        SetEHandle(key, (CEntityHandle)value);
                        break;
                    case KeyValuesType.TYPE_COLOR:
                        SetColor(key, (Color)value);
                        break;
                    case KeyValuesType.TYPE_VECTOR:
                        SetVector(key, (Vector)value);
                        break;
                    case KeyValuesType.TYPE_VECTOR2D:
                        SetVector2D(key, (Vector2D)value);
                        break;
                    case KeyValuesType.TYPE_VECTOR4D:
                        SetVector4D(key, (Vector4D)value);
                        break;
                    case KeyValuesType.TYPE_QUATERNION:
                        SetQuaternion(key, (Quaternion)value);
                        break;
                    case KeyValuesType.TYPE_QANGLE:
                        SetAngle(key, (QAngle)value);
                        break;
                    case KeyValuesType.TYPE_MATRIX3X4:
                        SetMatrix3x4(key, (matrix3x4_t)value);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported KeyValuesType.");
                }
#pragma warning restore CS8605 // Unboxing a possibly null value.
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning restore CS8604 // Possible null reference argument.
            }
        }

        #region GETTER

        public bool GetBool(string key, bool defaultValue = false) => GetValue(key, KeyValuesType.TYPE_BOOL, defaultValue);

        public int GetInt(string key, int defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_INT, defaultValue);

        public uint GetUInt(string key, uint defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_UINT, defaultValue);

        public long GetInt64(string key, long defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_INT64, defaultValue);

        public ulong GetUInt64(string key, ulong defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_UINT64, defaultValue);

        public float GetFloat(string key, float defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_FLOAT, defaultValue);

        public double GetDouble(string key, double defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_DOUBLE, defaultValue);

        public string? GetString(string key, string defaultValue = "") => GetValue(key, KeyValuesType.TYPE_STRING, defaultValue);

        public nint GetPointer(string key, nint defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_POINTER, defaultValue);

        // TODO: use 'CUtlStringToken' once we have it
        public uint GetStringToken(string key, uint defaultValue = 0) => GetValue(key, KeyValuesType.TYPE_STRING_TOKEN, defaultValue);

        public CEntityHandle? GetEHandle(string key, CEntityHandle? defaultValue = null) =>
            GetValue<CEntityHandle?>(key, KeyValuesType.TYPE_EHANDLE, defaultValue);

        public Color GetColor(string key) => GetValue(key, KeyValuesType.TYPE_COLOR, Color.Empty);

        public Vector? GetVector(string key, Vector? defaultValue = null) =>
            GetValue<Vector?>(key, KeyValuesType.TYPE_VECTOR, defaultValue);

        public Vector2D? GetVector2D(string key, Vector2D? defaultValue = null) =>
            GetValue(key, KeyValuesType.TYPE_VECTOR2D, defaultValue);

        public Vector4D? GetVector4D(string key, Vector4D? defaultValue = null) =>
            GetValue<Vector4D?>(key, KeyValuesType.TYPE_VECTOR4D, defaultValue);

        public Quaternion? GetQuaternion(string key, Quaternion? defaultValue = null) =>
            GetValue<Quaternion?>(key, KeyValuesType.TYPE_QUATERNION, defaultValue);

        public QAngle? GetAngle(string key, QAngle? defaultValue = null) => GetValue<QAngle?>(key, KeyValuesType.TYPE_QANGLE, defaultValue);

        public matrix3x4_t? GetMatrix3x4(string key, matrix3x4_t? defaultValue = null) =>
            GetValue<matrix3x4_t?>(key, KeyValuesType.TYPE_MATRIX3X4, defaultValue);

        #endregion

        #region SETTER

        public void SetBool(string key, bool value) => SetValue(key, KeyValuesType.TYPE_BOOL, value);

        public void SetInt(string key, int value) => SetValue(key, KeyValuesType.TYPE_INT, value);

        public void SetUInt(string key, uint value) => SetValue(key, KeyValuesType.TYPE_UINT, value);

        public void SetInt64(string key, long value) => SetValue(key, KeyValuesType.TYPE_INT64, value);

        public void SetUInt64(string key, ulong value) => SetValue(key, KeyValuesType.TYPE_UINT64, value);

        public void SetFloat(string key, float value) => SetValue(key, KeyValuesType.TYPE_FLOAT, value);

        public void SetDouble(string key, double value) => SetValue(key, KeyValuesType.TYPE_DOUBLE, value);

        public void SetString(string key, string value) => SetValue(key, KeyValuesType.TYPE_STRING, value);

        public void SetPointer(string key, nint value) => SetValue(key, KeyValuesType.TYPE_POINTER, value);

        // TODO: use 'CUtlStringToken' once we have it
        public void SetStringToken(string key, uint value) => SetValue(key, KeyValuesType.TYPE_STRING_TOKEN, value);

        public void SetEHandle(string key, CEntityHandle value) => SetValue(key, KeyValuesType.TYPE_EHANDLE, value);

        public void SetColor(string key, Color value) => SetValue(key, KeyValuesType.TYPE_COLOR, value);

        public void SetVector(string key, float x, float y, float z) =>
            SetValue(key, KeyValuesType.TYPE_VECTOR, new Vector(x, y, z));

        public void SetVector(string key, Vector vector) => SetValue(key, KeyValuesType.TYPE_VECTOR, vector);

        public void SetVector2D(string key, float x, float y) => SetValue(key, KeyValuesType.TYPE_VECTOR2D, new Vector2D(x, y));

        public void SetVector2D(string key, Vector2D value) => SetValue(key, KeyValuesType.TYPE_VECTOR2D, value);

        public void SetVector4D(string key, float x, float y, float z, float w) =>
            SetValue(key, KeyValuesType.TYPE_VECTOR4D, new Vector4D(x, y, z, w));

        public void SetVector4D(string key, Vector4D value) => SetValue(key, KeyValuesType.TYPE_VECTOR4D, value);

        public void SetQuaternion(string key, float x, float y, float z, float w) =>
            SetValue(key, KeyValuesType.TYPE_QUATERNION, new Quaternion(x, y, z, w));

        public void SetQuaternion(string key, Quaternion value) => SetValue(key, KeyValuesType.TYPE_QUATERNION, value);

        public void SetAngle(string key, float pitch, float yaw, float roll) =>
            SetValue(key, KeyValuesType.TYPE_QANGLE, new QAngle(pitch, yaw, roll));

        public void SetAngle(string key, QAngle angle) => SetValue(key, KeyValuesType.TYPE_QANGLE, angle);

        public void SetMatrix3x4(string key, matrix3x4_t value) => SetValue(key, KeyValuesType.TYPE_MATRIX3X4, value);

        #endregion

        public bool HasValue(string key)
        {
            return NativeAPI.EntityKeyValuesHasValue(Handle, key);
        }

        internal void SetValue<T>(string key, KeyValuesType type, T value)
        {
            List<object> arguments = new List<object>();

            switch (type)
            {
                case KeyValuesType.TYPE_EHANDLE:
                {
                    if (value is CEntityHandle entityHandle)
                    {
                        arguments.Add(entityHandle.Raw);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_COLOR:
                {
                    if (value is Color color)
                    {
                        arguments.Add(color.R);
                        arguments.Add(color.G);
                        arguments.Add(color.B);
                        arguments.Add(color.A);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_VECTOR:
                {
                    if (value is Vector vector)
                    {
                        arguments.Add(vector.X);
                        arguments.Add(vector.Y);
                        arguments.Add(vector.Z);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_VECTOR2D:
                {
                    if (value is Vector2D vector)
                    {
                        arguments.Add(vector.X);
                        arguments.Add(vector.Y);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_VECTOR4D:
                {
                    if (value is Vector4D vector)
                    {
                        arguments.Add(vector.X);
                        arguments.Add(vector.Y);
                        arguments.Add(vector.Z);
                        arguments.Add(vector.W);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_QUATERNION:
                {
                    if (value is Quaternion quaternion)
                    {
                        arguments.Add(quaternion.X);
                        arguments.Add(quaternion.Y);
                        arguments.Add(quaternion.Z);
                        arguments.Add(quaternion.W);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_QANGLE:
                {
                    if (value is QAngle angle)
                    {
                        arguments.Add(angle.X);
                        arguments.Add(angle.Y);
                        arguments.Add(angle.Z);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                case KeyValuesType.TYPE_MATRIX3X4:
                {
                    if (value is matrix3x4_t matrix)
                    {
                        arguments.Add(matrix.M00);
                        arguments.Add(matrix.M01);
                        arguments.Add(matrix.M02);
                        arguments.Add(matrix.M03);

                        arguments.Add(matrix.M10);
                        arguments.Add(matrix.M11);
                        arguments.Add(matrix.M12);
                        arguments.Add(matrix.M13);

                        arguments.Add(matrix.M20);
                        arguments.Add(matrix.M21);
                        arguments.Add(matrix.M22);
                        arguments.Add(matrix.M23);
                    }
                    else
                    {
                        BadTypeHandler(key, type, value);
                    }
                }
                    break;

                default:
                    arguments.Add((object)value!);
                    break;
            }

            NativeAPI.EntityKeyValuesSetValue(Handle, key, (uint)type, arguments.ToArray());
        }

        internal T? GetValue<T>(string key, KeyValuesType type, T? defaultValue)
        {
            var value = NativeAPI.EntityKeyValuesGetValue<T>(Handle, key, (uint)type);

            if (!typeof(T).IsValueType)
            {
                value = (T?)DetachFromNativeScratch(value);
            }

            return value ?? defaultValue;
        }

        // Aggregate results come back as a pointer into per-type scratch storage on the native side, which
        // the next read of the same type overwrites. Copy the values out so the caller gets an independent,
        // managed-backed instance instead of a view over that scratch (the native side used to heap-allocate
        // one object per read and never free it).
        private static object? DetachFromNativeScratch(object? value)
        {
            switch (value)
            {
                case Vector v:
                    return new Vector(v.X, v.Y, v.Z);
                case QAngle a:
                    return new QAngle(a.X, a.Y, a.Z);
                case Vector2D v2:
                    return new Vector2D(v2.X, v2.Y);
                case Vector4D v4:
                    return new Vector4D(v4.X, v4.Y, v4.Z, v4.W);
                case Quaternion q:
                    return new Quaternion(q.X, q.Y, q.Z, q.W);
                case matrix3x4_t m:
                    return new matrix3x4_t(
                        m[0, 0], m[0, 1], m[0, 2], m[0, 3],
                        m[1, 0], m[1, 1], m[1, 2], m[1, 3],
                        m[2, 0], m[2, 1], m[2, 2], m[2, 3]);
                default:
                    return value;
            }
        }

        internal void BadTypeHandler<T>(string key, KeyValuesType type, T value)
        {
            throw new ArgumentException($"Bad type for EntityKeyValues: got '{typeof(T)}' expected: '{type}'");
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            NativeAPI.EntityKeyValuesRelease(Handle);
        }

        ~CEntityKeyValues()
        {
            if (!_ownsReference || Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            // The native refcount is a plain int and releasing it can run the engine's destructor, so hop
            // to the game thread instead of releasing from the finalizer thread.
            var handle = RawHandle;
            Server.NextFrame(() => NativeAPI.EntityKeyValuesRelease(handle));
        }
    }
}