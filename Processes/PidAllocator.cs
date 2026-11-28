// Authoritative process-identity allocator (Card 3).
//
// PID is an unsigned 64-bit integer (approved decision). Allocation is
// incremental, the first PID after boot is 1, and a PID is never reused during
// the same boot. The sequence resets only on a new boot: Scheduler.Init creates
// a fresh allocator (Reset). Exhaustion is explicit — once MaxValue has been
// issued the allocator is exhausted and refuses further allocation instead of
// wrapping around or reusing a PID (no silent wrap / no reuse).
//
// This type is deliberately free of any hardware or scheduler dependency so it
// can be unit-tested on the host (tests/unit/PidAllocatorTests.cs) as well as
// linked into the freestanding kernel.

namespace Osb.Kernel.Processes
{
    internal struct PidAllocator
    {
        public const ulong FirstPid = 1;
        public const ulong MaxPid = ulong.MaxValue;

        private ulong _next;       // next PID to hand out (1-based)
        private bool _exhausted;

        // Reset to the post-boot initial state: next PID is 1, not exhausted.
        public void Reset()
        {
            _next = FirstPid;
            _exhausted = false;
        }

        // Test-only: place the allocator at a specific next value so the
        // exhaustion boundary (MaxValue) can be exercised without looping 2^64
        // times. Mirrors ProgramRegistry.TestOnly_Reset. Not used by the kernel.
        internal void TestOnly_ResetTo(ulong next)
        {
            _next = next;
            _exhausted = false;
        }

        public bool Exhausted => _exhausted;

        // The PID that the next successful TryAllocate would return, or 0 when
        // exhausted. Does not consume anything.
        public ulong Peek()
        {
            return _exhausted ? 0 : _next;
        }

        // Allocate the next PID. Returns false and sets pid = 0 when the
        // allocator is exhausted. Never wraps and never reissues a PID.
        public bool TryAllocate(out ulong pid)
        {
            if (_exhausted || _next == 0)
            {
                pid = 0;
                _exhausted = true;
                return false;
            }
            pid = _next;
            if (_next == MaxPid) _exhausted = true;   // MaxPid was the last value
            else _next++;
            return true;
        }
    }
}