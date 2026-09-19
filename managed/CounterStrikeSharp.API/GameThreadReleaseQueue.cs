using System.Collections.Concurrent;
using System.Threading;

namespace CounterStrikeSharp.API
{
    /// <summary>
    /// Hands engine-owned pointers from the finalizer thread to the game thread for release.
    ///
    /// Finalizers arrive in bursts (a whole GC's worth at once), and the engine objects behind them may only
    /// be touched from the game thread. Scheduling one <c>Server.NextFrame</c> closure per object releases
    /// the entire burst inside a single frame; this queues the pointers instead and drains a bounded number
    /// per frame, so a large backlog is spread out rather than spent as one long frame.
    /// </summary>
    internal sealed class GameThreadReleaseQueue
    {
        private readonly ConcurrentQueue<IntPtr> _pending = new();
        private readonly Action<IntPtr> _release;
        private readonly Action _drain;
        private readonly int _maxPerFrame;
        private int _drainScheduled;

        public GameThreadReleaseQueue(Action<IntPtr> release, int maxPerFrame)
        {
            _release = release;
            _maxPerFrame = maxPerFrame;
            _drain = Drain;
        }

        internal int PendingCount => _pending.Count;

        public void Enqueue(IntPtr pointer)
        {
            _pending.Enqueue(pointer);
            ScheduleDrain();
        }

        private void ScheduleDrain()
        {
            if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
            {
                Server.NextFrame(_drain);
            }
        }

        private void Drain()
        {
            for (var i = 0; i < _maxPerFrame && _pending.TryDequeue(out var pointer); i++)
            {
                try
                {
                    _release(pointer);
                }
                catch (Exception)
                {
                    // A pointer the engine no longer accepts must not stop the rest from being released.
                }
            }

            Volatile.Write(ref _drainScheduled, 0);

            // Also covers an Enqueue that lost the race against the flag being cleared above.
            if (!_pending.IsEmpty)
            {
                ScheduleDrain();
            }
        }
    }
}
