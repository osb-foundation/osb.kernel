/* OSB kernel synchronization primitives (M0 / M-LOCK).
 *
 * These three primitives are the only assembly the M0 SpinLock needs. They are
 * kept in a dedicated helper file (not boot.S, which already exceeds the
 * 400-line rule) per AGENTS.md "keep assembly minimal and isolated". Each is
 * exposed to C# through NativeMethods ([DllImport("osbnative")]).
 *
 * System V AMD64 ABI: integer args in rdi, rsi, rdx; return in rax.
 *
 * The contracts are fixed by O8.10 Appendix B and must not be silently changed:
 *   - osb_save_and_cli is a NON-atomic sequence (pushfq; pop; cli). An interrupt
 *     window exists between pop and cli; this is correct for M0 because IRQ
 *     handlers never acquire M0 spinlocks.
 *   - osb_restore_interrupt_state restores ONLY the IF bit (bit 9) from the
 *     saved RFLAGS; it never executes an unconditional sti and never restores
 *     unrelated flags.
 *   - osb_spinlock_cas is a full-barrier atomic compare-and-swap (lock cmpxchg).
 */

.section .text

/* ulong osb_save_and_cli(void)
 *   Capture the caller's RFLAGS (atomic pushfq), then disable interrupts.
 *   Returns the old RFLAGS in rax (bit 9 = IF). On return IF = 0. */
.global osb_save_and_cli
.type osb_save_and_cli, @function
osb_save_and_cli:
    pushfq                  /* push RFLAGS onto the stack (atomic) */
    pop    %rax             /* rax = old RFLAGS */
    cli                     /* disable maskable interrupts (IF = 0) */
    ret                     /* return old RFLAGS in rax */

/* void osb_restore_interrupt_state(ulong rflags)
 *   rdi = saved RFLAGS (only bit 9 = IF is meaningful).
 *   Enable interrupts iff the saved IF bit was set; otherwise do nothing.
 *   Does not modify any other RFLAGS bit and never executes an unconditional
 *   sti. */
.global osb_restore_interrupt_state
.type osb_restore_interrupt_state, @function
osb_restore_interrupt_state:
    test   $0x200, %rdi      /* test IF bit (bit 9) */
    jz     1f                /* if IF was clear, return without sti */
    sti                      /* restore IF: enable interrupts */
1:
    ret

/* int osb_spinlock_cas(int* addr, int newVal, int oldVal)
 *   rdi = addr, esi = newVal, edx = oldVal (comparand).
 *   Atomically: if (*addr == oldVal) *addr = newVal.
 *   Returns the value previously at *addr in eax (== oldVal on success).
 *   lock cmpxchg is a full memory barrier (acquire + release). */
.global osb_spinlock_cas
.type osb_spinlock_cas, @function
osb_spinlock_cas:
    mov    %edx, %eax         /* eax = oldVal (comparand) */
    mov    %esi, %edx         /* edx = newVal */
    lock cmpxchg %edx, (%rdi) /* if (*addr == eax) *addr = edx; eax = *addr */
    ret                      /* eax = value found at *addr */

.section .note.GNU-stack, "", @progbits