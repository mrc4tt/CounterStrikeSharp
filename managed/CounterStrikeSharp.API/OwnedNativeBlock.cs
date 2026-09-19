using System.Runtime.InteropServices;
using System.Threading;

namespace CounterStrikeSharp.API
{
    /// <summary>
    /// Owns the unmanaged buffer behind a lazily materialised value type (<c>Vector</c>, <c>QAngle</c>, ...).
    ///
    /// The wrapper holds the only reference to its block, so the block becomes unreachable in the same GC
    /// as the wrapper and its finalizer releases the buffer. This replaced <c>NativeHandleTracker</c>, which
    /// kept a locked list of weak references and swept it from a timer: every tracked handle cost an entry
    /// plus a <see cref="WeakReference{T}"/> (itself finalizable, plus a GC handle), and frees lagged the GC
    /// by however long the round-robin sweep took to come back around.
    ///
    /// Nothing here runs on the game thread except the allocation itself. <c>Marshal.FreeHGlobal</c> is
    /// libc <c>free</c>, not the engine allocator, so it is safe from the finalizer thread.
    /// </summary>
    internal sealed class OwnedNativeBlock
    {
        // A wrapper can become unreachable while the native call it handed its pointer to is still running
        // (the JIT considers `this` dead after the last managed use, and a GC triggered by another thread
        // can run while the game thread sits in native code). Freeing straight from the finalizer would pull
        // the buffer out from under that call, so dead blocks wait out a grace period no native call spans.
        private const long GraceMilliseconds = 1000;

        private static readonly Queue<(IntPtr Pointer, long DeadAt)> _pending = new();
        private static readonly object _pendingLock = new();
        private static long _liveCount;

        private IntPtr _pointer;

        /// <summary>
        /// Number of buffers allocated and not yet released, including those waiting out the grace period.
        /// </summary>
        internal static long LiveCount => Interlocked.Read(ref _liveCount);

        /// <summary>
        /// Test hook, raised with the pointer of every buffer as it is released. <see cref="LiveCount"/> is
        /// process-wide, so a test sharing the server with other code can only follow its own buffer this way.
        /// </summary>
        internal static Action<IntPtr>? Released;

        public IntPtr Pointer => _pointer;

        private OwnedNativeBlock(int size)
        {
            _pointer = Marshal.AllocHGlobal(size);
            Interlocked.Increment(ref _liveCount);
        }

        /// <summary>
        /// Returns the native pointer for <paramref name="slot"/>, allocating and seeding it from
        /// <paramref name="values"/> on first use. Safe against concurrent first use: the loser's buffer was
        /// never published, so it is released immediately.
        /// </summary>
        public static unsafe IntPtr Materialize(ref OwnedNativeBlock? slot, ReadOnlySpan<float> values)
        {
            var current = Volatile.Read(ref slot);
            if (current != null)
            {
                return current._pointer;
            }

            var block = new OwnedNativeBlock(values.Length * sizeof(float));
            values.CopyTo(new Span<float>((void*)block._pointer, values.Length));

            var existing = Interlocked.CompareExchange(ref slot, block, null);
            if (existing != null)
            {
                block.ReleaseUnpublished();
                return existing._pointer;
            }

            return block._pointer;
        }

        private void ReleaseUnpublished()
        {
            Release(_pointer);
            _pointer = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        private static void Release(IntPtr pointer)
        {
            Marshal.FreeHGlobal(pointer);
            Interlocked.Decrement(ref _liveCount);
            Released?.Invoke(pointer);
        }

        /// <summary>
        /// Releases every dead buffer whose grace period has elapsed. Runs from each finalizer; exposed so
        /// tests can drain the tail without waiting for another block to die.
        /// </summary>
        internal static void FlushPending()
        {
            var now = Environment.TickCount64;

            lock (_pendingLock)
            {
                FlushPendingLocked(now);
            }
        }

        private static void FlushPendingLocked(long now)
        {
            while (_pending.TryPeek(out var head) && now - head.DeadAt >= GraceMilliseconds)
            {
                _pending.Dequeue();
                Release(head.Pointer);
            }
        }

        ~OwnedNativeBlock()
        {
            if (_pointer == IntPtr.Zero)
            {
                return;
            }

            var now = Environment.TickCount64;

            lock (_pendingLock)
            {
                _pending.Enqueue((_pointer, now));
                FlushPendingLocked(now);
            }
        }
    }
}
