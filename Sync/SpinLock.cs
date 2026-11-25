// M0 kernel spinlock (O8.6 A-SYNC-2 / O8.10 final contract).
//
// A minimal, interrupt-safe, single-core spinlock. Acquisition saves the
// caller's RFLAGS and disables interrupts (osb_save_and_cli), then acquires the
// lock with an atomic compare-and-swap (osb_spinlock_cas). Release clears the
// lock with a plain store and restores the interrupt-enable state from the
// per-acquisition cookie (osb_restore_interrupt_state, conditional STI only).
//
// Contract (O8.10 §8):
//   - Lock state: 0 = free, 1 = held (one static word for M0).
//   - Per-acquisition cookie: the full RFLAGS captured at Acquire; only bit 9
//     (IF) is consumed by Release. No global saved-interrupt state.
//   - Release ordering: the lock word is cleared BEFORE interrupts are restored,
//     so the lock is available before IF can be re-enabled (x86-TSO + the
//     P/Invoke compiler barriers; see O8.10 §6 and the O8.11 experiment).
//   - Recursive acquisition of the same lock is prohibited (deadlock on one CPU).
//   - IRQ/NMI/SMI handlers must not acquire M0 locks.
//   - Nested acquisition of DISTINCT locks is supported (acquire A->B, release
//     B->A); each Acquire records its own cookie so IF is restored per boundary.
//   - Single-core only. The atomic CAS is retained for SMP-readiness but SMP
//     correctness is NOT claimed.
//
// AcquireOn/ReleaseOn operate on an explicit lock word so the identical code
// path can be exercised with more than one lock in tests; the public
// Acquire/Release bind the single M0 lock word (_locked).

using Osb.Kernel.Hardware;

namespace Osb.Kernel.Sync;

internal static unsafe partial class SpinLock
{
    // The single M0 lock word: 0 = free, 1 = held. Zero-initialized at type
    // construction (static field). Natural int alignment is sufficient for
    // lock cmpxchg.
    private static int _locked;

    // Acquire the M0 lock, saving the previous interrupt state in cookie.
    // Interrupts are disabled for the entire ownership period.
    public static void Acquire(out ulong cookie)
    {
        fixed (int* p = &_locked) AcquireOn(p, out cookie);
    }

    // Release the M0 lock and restore the interrupt state from cookie.
    public static void Release(ulong cookie)
    {
        fixed (int* p = &_locked) ReleaseOn(p, cookie);
    }

    // Core acquisition over an explicit lock word. Saves RFLAGS + disables
    // interrupts, then spins on the atomic CAS until the lock was free (0).
    internal static void AcquireOn(int* lockWord, out ulong cookie)
    {
        cookie = NativeMethods.osb_save_and_cli();          // save RFLAGS; IF -> 0
        while (NativeMethods.osb_spinlock_cas(lockWord, 1, 0) != 0)
        {
            // spin: lock held by another context (single-core: caller has IF=0)
        }
    }

    // Core release over an explicit lock word. Clears the lock first (plain
    // release store on x86-TSO), then conditionally restores IF from cookie.
    internal static void ReleaseOn(int* lockWord, ulong cookie)
    {
        *lockWord = 0;                                      // release: lock now free
        NativeMethods.osb_restore_interrupt_state(cookie);  // restore IF only
    }
}