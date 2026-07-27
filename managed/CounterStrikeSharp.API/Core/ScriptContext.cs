/*
 * Copyright (c) 2014 Bas Timmer/NTAuthority et al.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 *
 * This file has been modified from its original form for use in this program
 * under GNU Lesser General Public License, version 2.
 */

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using CounterStrikeSharp.API.Modules.Utils;
using FastGenericNew;

namespace CounterStrikeSharp.API.Core
{
    public class NativeException : Exception
    {
        public NativeException(string message) : base(message)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    [Serializable]
    public unsafe struct fxScriptContext
    {
        public int numArguments;
        public int numResults;
        public int hasError;

        public ulong nativeIdentifier;
        public fixed byte functionData[8 * 32];
        public fixed byte result[8];
    }

    public class ScriptContext
    {
        [ThreadStatic] private static ScriptContext _globalScriptContext = null!;

        public static ScriptContext GlobalScriptContext
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return _globalScriptContext ?? InitGlobalScriptContext(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ScriptContext InitGlobalScriptContext()
        {
            _globalScriptContext = new ScriptContext();
            return _globalScriptContext;
        }

        public unsafe ScriptContext()
        {
        }

        public unsafe ScriptContext(fxScriptContext* context)
        {
            m_extContext = *context;
        }

        // Lazily allocated: only string pushes/results need finalizer tracking, but a
        // ScriptContext is constructed on EVERY native->managed dispatch (per tick, per
        // event, per listener). Allocating the queue eagerly cost one ConcurrentQueue +
        // its initial segment per dispatch even when no string was ever marshalled.
        private ConcurrentQueue<IntPtr>? ms_finalizers;

        private readonly object ms_lock = new object();

        internal object Lock => ms_lock;

        internal fxScriptContext m_extContext = new fxScriptContext();

        internal bool isCleanupLocked = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [SecuritySafeCritical]
        public void Reset()
        {
            InternalReset();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [SecurityCritical]
        private void InternalReset()
        {
            m_extContext.numArguments = 0;
            m_extContext.numResults = 0;
            m_extContext.hasError = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [SecuritySafeCritical]
        public void Invoke()
        {
            if (!isCleanupLocked)
            {
                isCleanupLocked = true;
                InvokeNativeInternal();
                GlobalCleanUp();
                isCleanupLocked = false;
                return;
            }

            InvokeNativeInternal();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [SecurityCritical]
        private void InvokeNativeInternal()
        {
            unsafe
            {
                fixed (fxScriptContext* cxt = &m_extContext)
                {
                    Helpers.InvokeNative(new IntPtr(cxt));
                }
            }
        }

        public unsafe byte[] GetBytes()
        {
            fixed (fxScriptContext* context = &m_extContext)
            {
                byte[] arr = new byte[8 * 32];
                Marshal.Copy((IntPtr)context->functionData, arr, 0, 8 * 32);

                return arr;
            }
        }

        public unsafe IntPtr GetContextUnderlyingAddress()
        {
            fixed (fxScriptContext* context = &m_extContext)
            {
                return (IntPtr)context;
            }
        }

        [SecuritySafeCritical]
        public void Push(object? arg)
        {
            PushInternal(arg);
        }

        [SecuritySafeCritical]
        public unsafe void SetResult(object arg, fxScriptContext* cxt)
        {
            SetResultInternal(cxt, arg);
        }

        [SecurityCritical]
        private unsafe void PushInternal(object? arg)
        {
            fixed (fxScriptContext* context = &m_extContext)
            {
                Push(context, arg);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [SecurityCritical]
        public unsafe void SetIdentifier(ulong arg)
        {
            fixed (fxScriptContext* context = &m_extContext)
            {
                context->nativeIdentifier = arg;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void CheckErrors()
        {
            if (m_extContext.hasError != 0)
            {
                string error = GetResult<string>();
                Reset();
                throw new NativeException(error);
            }
        }

        /// <summary>
        /// Cached per-Type reflection metadata used by the boxed Push/GetResult paths.
        /// These run on every native dispatch argument, so repeated Marshal.SizeOf /
        /// IsAssignableFrom / Activator.CreateInstance calls were pure per-tick overhead.
        /// </summary>
        private sealed class TypeMeta
        {
            public bool IsNativeObject;
            public bool IsEnum;
            public Type? EnumUnderlying;
            public int MarshalSize; // -1 => Marshal.SizeOf(type) throws for this type
            public Func<IntPtr, object>? NativeObjectFactory;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, TypeMeta> TypeMetaCache = new();

        private static TypeMeta GetTypeMeta(Type type)
        {
            return TypeMetaCache.GetOrAdd(type, static t =>
            {
                var meta = new TypeMeta
                {
                    IsNativeObject = typeof(NativeObject).IsAssignableFrom(t),
                    IsEnum = t.IsEnum,
                    EnumUnderlying = t.IsEnum ? t.GetEnumUnderlyingType() : null,
                };

                try
                {
                    meta.MarshalSize = Marshal.SizeOf(t);
                }
                catch
                {
                    meta.MarshalSize = -1;
                }

                if (meta.IsNativeObject)
                {
                    // Compiled (IntPtr) constructor — replaces Activator.CreateInstance
                    // on the argument-materialization path.
                    var ctor = t.GetConstructor(new[] { typeof(IntPtr) });
                    if (ctor != null)
                    {
                        var p = System.Linq.Expressions.Expression.Parameter(typeof(IntPtr), "ptr");
                        meta.NativeObjectFactory = System.Linq.Expressions.Expression
                            .Lambda<Func<IntPtr, object>>(System.Linq.Expressions.Expression.New(ctor, p), p)
                            .Compile();
                    }
                }

                return meta;
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void PushRaw<T>(fxScriptContext* cxt, T value) where T : unmanaged
        {
            byte* slot = &cxt->functionData[8 * cxt->numArguments];
            *(long*)slot = 0;
            *(T*)slot = value;
            cxt->numArguments++;
        }

        [SecurityCritical]
        internal unsafe void Push(fxScriptContext* context, object? arg)
        {
            if (arg == null)
            {
                arg = 0;
            }

            // Fast paths for boxed primitives — covers nearly all real native traffic
            // without Marshal.SizeOf/StructureToPtr reflection. Semantics match the
            // fallback: slot is zeroed, then the value's bytes are written.
            switch (arg)
            {
                case int v: PushRaw(context, v); return;
                case uint v: PushRaw(context, v); return;
                case long v: PushRaw(context, v); return;
                case ulong v: PushRaw(context, v); return;
                case IntPtr v: PushRaw(context, v); return;
                case UIntPtr v: PushRaw(context, v); return;
                case float v: PushRaw(context, v); return;
                case double v: PushRaw(context, v); return;
                case bool v: PushRaw(context, v ? (byte)1 : (byte)0); return;
                case byte v: PushRaw(context, v); return;
                case sbyte v: PushRaw(context, v); return;
                case short v: PushRaw(context, v); return;
                case ushort v: PushRaw(context, v); return;
                case string str:
                    PushString(context, str);
                    return;
                case InputArgument ia:
                    Push(context, ia.Value);
                    return;
                case IMarshalToNative marshalToNative:
                    foreach (var value in marshalToNative.GetNativeObject())
                    {
                        Push(context, value);
                    }

                    return;
                // Handle pushed directly — skips the InputArgument wrapper allocation
                // the implicit conversion used to create per call. Also covers
                // NativeEntity, which derives from NativeObject.
                case NativeObject nativeObject:
                    PushRaw(context, nativeObject.Handle);
                    return;
            }

            var meta = GetTypeMeta(arg.GetType());

            if (meta.IsEnum)
            {
                Push(context, Convert.ChangeType(arg, meta.EnumUnderlying!));
                return;
            }

            if (meta.MarshalSize == -1)
            {
                // Preserve the original behavior: Marshal.SizeOf throws for this type.
                Marshal.SizeOf(arg.GetType());
            }

            if (meta.MarshalSize <= 8)
            {
                PushUnsafe(context, arg);
            }

            context->numArguments++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SetResultRaw<T>(fxScriptContext* cxt, T value) where T : unmanaged
        {
            *(long*)(&cxt->result[0]) = 0;
            *(T*)(&cxt->result[0]) = value;
        }

        [SecurityCritical]
        internal unsafe void SetResultInternal(fxScriptContext* context, object arg)
        {
            if (arg == null)
            {
                arg = 0;
            }

            // Primitive fast paths — hook results (e.g. HookResult) land here on every
            // dispatch, so the Marshal.SizeOf/StructureToPtr fallback was per-tick cost.
            switch (arg)
            {
                case int v: SetResultRaw(context, v); return;
                case uint v: SetResultRaw(context, v); return;
                case long v: SetResultRaw(context, v); return;
                case ulong v: SetResultRaw(context, v); return;
                case IntPtr v: SetResultRaw(context, v); return;
                case UIntPtr v: SetResultRaw(context, v); return;
                case float v: SetResultRaw(context, v); return;
                case double v: SetResultRaw(context, v); return;
                case bool v: SetResultRaw(context, v ? (byte)1 : (byte)0); return;
                case byte v: SetResultRaw(context, v); return;
                case sbyte v: SetResultRaw(context, v); return;
                case short v: SetResultRaw(context, v); return;
                case ushort v: SetResultRaw(context, v); return;
                case string str:
                    SetResultString(context, str);
                    return;
                case InputArgument ia:
                    SetResultInternal(context, ia.Value);
                    return;
            }

            var meta = GetTypeMeta(arg.GetType());

            if (meta.IsEnum)
            {
                SetResultInternal(context, Convert.ChangeType(arg, meta.EnumUnderlying!));
                return;
            }

            if (meta.MarshalSize == -1)
            {
                // Preserve the original behavior: Marshal.SizeOf throws for this type.
                Marshal.SizeOf(arg.GetType());
            }

            if (meta.MarshalSize <= 8)
            {
                SetResultUnsafe(context, arg);
            }
        }

        [SecurityCritical]
        internal unsafe void PushUnsafe(fxScriptContext* cxt, object arg)
        {
            *(long*)(&cxt->functionData[8 * cxt->numArguments]) = 0;
            Marshal.StructureToPtr(arg, new IntPtr(cxt->functionData + (8 * cxt->numArguments)), true);
        }

        /// <summary>
        /// Pushes a primitive/unmanaged value directly into the context's function
        /// data buffer without boxing or Marshal.StructureToPtr overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void PushPrimitive<T>(T value) where T : unmanaged
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                *(long*)(&cxt->functionData[8 * cxt->numArguments]) = 0;
                *(T*)(&cxt->functionData[8 * cxt->numArguments]) = value;
                cxt->numArguments++;
            }
        }

        [SecurityCritical]
        internal unsafe void SetResultUnsafe(fxScriptContext* cxt, object arg)
        {
            *(long*)(&cxt->result[0]) = 0;
            Marshal.StructureToPtr(arg, new IntPtr(cxt->result), true);
        }

        [SecurityCritical]
        internal unsafe void PushString(string str)
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                PushString(cxt, str);
            }
        }

        [SecurityCritical]
        internal unsafe void PushString(fxScriptContext* cxt, string str)
        {
            if (str == null)
            {
                *(IntPtr*)(&cxt->functionData[8 * cxt->numArguments]) = IntPtr.Zero;
                cxt->numArguments++;
                return;
            }

            int maxBytes = Encoding.UTF8.GetMaxByteCount(str.Length);
            var ptr = Marshal.AllocHGlobal(maxBytes + 1);

            var dest = new Span<byte>((void*)ptr, maxBytes + 1);
            int written = Encoding.UTF8.GetBytes(str, dest);
            dest[written] = 0;

            (ms_finalizers ??= new ConcurrentQueue<IntPtr>()).Enqueue(ptr);

            *(IntPtr*)(&cxt->functionData[8 * cxt->numArguments]) = ptr;
            cxt->numArguments++;
        }

        [SecurityCritical]
        internal unsafe void SetResultString(fxScriptContext* cxt, string str)
        {
            if (str == null)
            {
                *(IntPtr*)(&cxt->result[0]) = IntPtr.Zero;
                return;
            }

            int maxBytes = Encoding.UTF8.GetMaxByteCount(str.Length);
            var ptr = Marshal.AllocHGlobal(maxBytes + 1);

            var dest = new Span<byte>((void*)ptr, maxBytes + 1);
            int written = Encoding.UTF8.GetBytes(str, dest);
            dest[written] = 0;

            (ms_finalizers ??= new ConcurrentQueue<IntPtr>()).Enqueue(ptr);
            *(IntPtr*)(&cxt->result[8]) = ptr;
        }

        [SecuritySafeCritical]
        public T GetArgument<T>(int index)
        {
            return (T)GetArgument(typeof(T), index);
        }

        [SecuritySafeCritical]
        public object GetArgument(Type type, int index)
        {
            return GetArgumentHelper(type, index);
        }

        [SecurityCritical]
        internal unsafe object GetArgument(fxScriptContext* cxt, Type type, int index)
        {
            return GetArgumentHelper(cxt, type, index);
        }

        [SecurityCritical]
        private unsafe object GetArgumentHelper(Type type, int index)
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                return GetArgumentHelper(cxt, type, index);
            }
        }

        [SecurityCritical]
        private unsafe object GetArgumentHelper(fxScriptContext* context, Type type, int index)
        {
            return GetResult(type, &context->functionData[index * 8]);
        }

        [SecuritySafeCritical]
        public T GetResult<T>()
        {
            return (T)GetResult(typeof(T));
        }

        [SecuritySafeCritical]
        public object GetResult(Type type)
        {
            return GetResultHelper(type);
        }

        [SecurityCritical]
        internal unsafe object GetResult(fxScriptContext* cxt, Type type)
        {
            return GetResultHelper(cxt, type);
        }

        [SecurityCritical]
        private unsafe object GetResultHelper(Type type)
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                return GetResultHelper(cxt, type);
            }
        }

        [SecurityCritical]
        private unsafe object GetResultHelper(fxScriptContext* context, Type type)
        {
            return GetResult(type, &context->result[0]);
        }

        [SecurityCritical]
        // Returns can legitimately be null (null string ptr, type==object, unhandled
        // size). The public GetResult/GetResult<T> contract is non-null, so the null
        // paths are asserted with null!/! rather than widening the signature.
        internal unsafe object GetResult(Type type, byte* ptr)
        {
            // Fast paths for the common primitive shapes — avoids the Marshal.SizeOf +
            // Marshal.PtrToStructure reflection this method used to pay per argument.
            // Boxing is inherent to the object-typed contract and unavoidable here.
            if (type == typeof(IntPtr)) return *(IntPtr*)ptr;
            if (type == typeof(int)) return *(int*)ptr;
            if (type == typeof(uint)) return *(uint*)ptr;
            if (type == typeof(long)) return *(long*)ptr;
            if (type == typeof(ulong)) return *(ulong*)ptr;
            if (type == typeof(float)) return *(float*)ptr;
            if (type == typeof(double)) return *(double*)ptr;
            if (type == typeof(bool)) return *(byte*)ptr != 0;
            if (type == typeof(byte)) return *ptr;
            if (type == typeof(sbyte)) return *(sbyte*)ptr;
            if (type == typeof(short)) return *(short*)ptr;
            if (type == typeof(ushort)) return *(ushort*)ptr;
            if (type == typeof(UIntPtr)) return *(UIntPtr*)ptr;

            if (type == typeof(string))
            {
                var nativeUtf8 = *(byte**)ptr;

                if (nativeUtf8 == null)
                {
                    return null!;
                }

                return Marshal.PtrToStringUTF8((IntPtr)nativeUtf8)!;
            }

            if (type == typeof(Color))
            {
                var pointer = *(IntPtr*)ptr;
                return Marshaling.ColorMarshaler.NativeToManaged(pointer);
            }

            // this one only works if the 'Raw'/uint is passed
            // maybe do this with a marshaler?!
            if (type == typeof(CEntityHandle))
            {
                return new CEntityHandle(*(uint*)ptr);
            }

            if (type == typeof(object))
            {
                return null!;
            }

            var meta = GetTypeMeta(type);

            if (meta.IsNativeObject)
            {
                var pointer = *(IntPtr*)ptr;
                return meta.NativeObjectFactory != null
                    ? meta.NativeObjectFactory(pointer)
                    : Activator.CreateInstance(type, pointer)!;
            }

            if (meta.IsEnum)
            {
                return Enum.ToObject(type, GetResult(meta.EnumUnderlying!, ptr));
            }

            if (meta.MarshalSize == -1)
            {
                // Preserve the original behavior: Marshal.SizeOf throws for this type.
                Marshal.SizeOf(type);
            }

            if (meta.MarshalSize <= 8)
            {
                return GetResultInternal(type, ptr);
            }

            return null!;
        }

        [SecurityCritical]
        private unsafe object GetResultInternal(Type type, byte* ptr)
        {
            var obj = Marshal.PtrToStructure(new IntPtr(ptr), type);
            return obj!;
        }

        [SecurityCritical]
        internal unsafe string GetResultString()
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                var nativeUtf8 = *(byte**)(&cxt->result[0]);

                if (nativeUtf8 == null)
                {
                    return null!;
                }

                return Marshal.PtrToStringUTF8((IntPtr)nativeUtf8)!;
            }
        }

        /// <summary>
        /// Reads a primitive/unmanaged result directly from the context's result
        /// buffer without Marshal.PtrToStructure or boxing overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe T GetResultPrimitive<T>() where T : unmanaged
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                return *(T*)(&cxt->result[0]);
            }
        }

        /// <summary>
        /// Reads a pointer result from the context and creates a NativeObject-derived
        /// instance using FastGenericNew. Avoids Activator.CreateInstance overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe T GetResultNativeObject<T>()
        {
            fixed (fxScriptContext* cxt = &m_extContext)
            {
                var pointer = *(IntPtr*)(&cxt->result[0]);
                return FastNew.CreateInstance<T, IntPtr>(pointer);
            }
        }


        [SecurityCritical]
        internal unsafe string ErrorHandler(byte* error)
        {
            if (error != null)
            {
                var errorStart = error;
                int length = 0;

                for (var p = errorStart; *p != 0; p++)
                {
                    length++;
                }

                return Encoding.UTF8.GetString(errorStart, length);
            }

            return "Native invocation failed.";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void GlobalCleanUp()
        {
            if (ms_finalizers is { IsEmpty: false })
            {
                GlobalCleanUpSlow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GlobalCleanUpSlow()
        {
            lock (ms_lock)
            {
                while (ms_finalizers!.TryDequeue(out var ptr))
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }

        public override string ToString()
        {
            return $"ScriptContext{{numArgs={m_extContext.numArguments}}}";
        }
    }
}
