// OSB kernel entry point.
//
// Freestanding C# (NativeAOT, no runtime): this file is only the boot
// sequence - it brings up the machine in order and hands control to the shell.
// Every subsystem lives in its own component:
//
//   Hardware/    Vga, Serial, Keyboard, ConsoleControl, NativeMethods (port I/O)
//   Memory/      Layout (constants), PhysicalMemory (frame allocator)
//   Cpu/         Pit, Rtc, Gdt (ring 3 + TSS), Interrupts (PIC/IDT), Syscalls
//   Processes/   Scheduler (task table + round-robin), AddressSpace (per-process CR3)
//   Storage/     Ata (PIO), Ext4 (reader/writer), Cwd (current drive/directory)
//   Shell/       Shell (loop + dispatch), Environment, Prompt, JobControl,
//                ShellText, Commands/*

using System.Runtime;
using Osb.Kernel.Cpu;
using Osb.Kernel.Graphics;
using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;
using Osb.Kernel.Processes;
using Osb.Kernel.Shell;
using Osb.Kernel.Storage;

namespace Osb.Kernel
{
    internal static unsafe partial class Kernel
{
    // Entry point (named "Main" so the assembly has a managed entry point; the
    // real entry is kmain, called by _start in boot.S).
    private static void Main() { }

    [RuntimeExport("kmain")]
    private static void KmMain(ulong multibootInfo)
    {
        Serial.Init();
        Vga.Clear();

        // M0 SpinLock validation: exercise the real assembly primitives in ring 0
        // at the IF=0 boot default (no IDT/timer yet, so the sti branch is not
        // taken here — see SpinLock.SelfTest). Silent on success; halts on failure.
        if (!Sync.SpinLock.SelfTest())
        {
            Vga.WriteLine("SPINLOCK SELFTEST FAILED. Halting.");
            while (true) { NativeMethods.hlt_cpu(); }
        }

        Interrupts.PicInit();   // remap 8259A (IRQ0-15 -> vectors 0x30-0x3F)
        Pit.Init();             // 8254 channel 0: square wave @ 100 Hz
        Rtc.Init();             // CMOS/RTC: 24h + binary + update-ended IRQ (~1 Hz)
        Gdt.Init(Scheduler.KernelStackTop(1));   // user segments + TSS, then load GDT + ltr
        PhysicalMemory.Detect(multibootInfo);   // find how much RAM we can hand out
        PhysicalMemory.Init();   // physical frame allocator (bitmap)
        Interrupts.Init();      // fill + load the IDT (exception + IRQ stubs)
        Scheduler.Init();       // console (slot 0) + kernel address space
        if (!ProgramRegistry.Initialize())
        {
            Vga.WriteLine("Failed to initialize embedded program registry. Halting.");
            while (true) { NativeMethods.hlt_cpu(); }
        }
        Font.Init();            // embedded 8x8 font table
        Mouse.Init();           // PS/2 mouse on IRQ12
        Keyboard.Init();        // PS/2 keyboard on IRQ1

        Vga.WriteLine("OSB - Operating System Basic");
        Vga.WriteLine("Bootable kernel prototype (C# / NativeAOT, bare metal)");
        Vga.WriteLine("Keyboard: PS/2 IRQ1 + serial console IRQ4 (COM1).");
        Vga.WriteLine("Timer: PIT channel 0 @ 100 Hz (IRQ0 ticks).");
        Vga.WriteLine("Clock: RTC via IRQ8 (update-ended).");
        Vga.WriteLine("Scheduler: preemptive round-robin over a task table.");
        Vga.WriteLine("Processes: private address space per process (CR3).");
        Vga.WriteLine("Ring 3: user segments + TSS; syscalls via INT 21h (DOS-style).");
        Vga.WriteLine("Files: ATA PIO + ext4 on A:.");
        Vga.WriteLine("Shell: boot program (default A:\\OSB\\BIN\\SHELL.EXE; CONF/BOOT only in WITH_TESTS builds)");
        Vga.WriteLine("Mouse: PS/2 IRQ12 (stream mode).");
        Vga.FlushCursor();
        Vga.WriteLine("");

        Environment.Init();     // session environment (PATH, PROMPT, USER, ...)

        // Load the user database (PASSWD/SUDOERS) kernel-side at boot (1c, I1;
        // MIGRATION A1 fixes X13: this used to live in the unreachable Shell.Run).
        Users.Init();

        // Hand control to the userspace shell. The boot program is
        // SHELL.EXE by default (H-B). Only WITH_TESTS=1 builds (BOOT_FROM_CONF)
        // honor A:\OSB\CONF/BOOT so golden cases can boot a test program.
        char* boot = stackalloc char[256];
        int bootLen;
#if BOOT_FROM_CONF
        bootLen = ReadBootProgram(boot, 256);
#if DIAGNOSTICS
        Vga.Write("BOOT_FROM_CONF: ReadBootProgram returned bootLen="); Vga.WriteDec((ulong)bootLen); Vga.WriteLine("");
        Vga.Write("BOOT_FROM_CONF: buffer[0..5]=");
        for (int i = 0; i < 6 && i < bootLen; i++) { Vga.Write(boot[i].ToString()); Vga.Write(" "); }
        Vga.WriteLine("");
#endif
#else
        // Default build: always SHELL.EXE, regardless of any CONF/BOOT file.
        fixed (char* def = "A:\\OSB\\BIN\\SHELL.EXE")
        {
            bootLen = 0;
            while (def[bootLen] != '\0') bootLen++;
            for (int i = 0; i < bootLen; i++) boot[i] = def[i];
        }
#if DIAGNOSTICS
        Vga.WriteLine("NO_BOOT_FROM_CONF: using default SHELL.EXE bootLen="); Vga.WriteDec((ulong)bootLen); Vga.WriteLine("");
#endif
#endif
if (bootLen <= 0)
        {
            fixed (char* def = "A:\\OSB\\BIN\\SHELL.EXE")
            {
                bootLen = 0;
                while (def[bootLen] != '\0') bootLen++;
                for (int i = 0; i < bootLen; i++) boot[i] = def[i];
            }
        }
        int pid;
#if DIAGNOSTICS
        // DEBUG: Print boot program path
        Vga.Write("Boot program: ");
        for (int i = 0; i < bootLen; i++) Vga.Write(boot[i].ToString());
        Vga.WriteLine("");
#endif
        // Parse boot command line: split by whitespace, first token = program path
        char* prog = stackalloc char[260];
        char* args = stackalloc char[260];
        int progLen = 0, argsLen = 0;
        int idx = 0;
        while (idx < bootLen && (boot[idx] == ' ' || boot[idx] == '\t')) idx++; // skip leading spaces
        while (idx < bootLen && boot[idx] != ' ' && boot[idx] != '\t' && progLen < 259)
        {
            prog[progLen++] = boot[idx++];
        }
        while (idx < bootLen && (boot[idx] == ' ' || boot[idx] == '\t')) idx++; // skip spaces before args
        while (idx < bootLen && argsLen < 259)
        {
            args[argsLen++] = boot[idx++];
        }
        // Check if program is OSLANG.EXE (case-insensitive match on "OSLANG.EXE")
        bool isOslang = false;
        const int OsLangLen = 10;
        if (progLen >= OsLangLen)
        {
            int cmp = 0;
            while (cmp < OsLangLen)
            {
                char a = prog[progLen - OsLangLen + cmp];
                char b = "OSLANG.EXE"[cmp];
                if (a >= 'a' && a <= 'z') a = (char)(a - 'a' + 'A');
                if (b >= 'a' && b <= 'z') b = (char)(b - 'a' + 'A');
                if (a != b) break;
                cmp++;
            }
            isOslang = (cmp == OsLangLen);
        }
        SpawnResult r;
        if (isOslang)
        {
            // For OSLANG.EXE, use parsed args if present, otherwise default to HELLO.OSL
            if (argsLen > 0)
            {
                r = Scheduler.SpawnFileWithArgs(prog, progLen, Scheduler.Console, out pid, args, argsLen);
            }
            else
            {
                char* helloPath = stackalloc char[32];
                fixed (char* def = "A:\\OSB\\BIN\\HELLO.OSL")
                {
                    int len = 0;
                    while (def[len] != '\0') len++;
                    for (int j = 0; j < len; j++) helloPath[j] = def[j];
                    r = Scheduler.SpawnFileWithArgs(prog, progLen, Scheduler.Console, out pid, helloPath, len);
                }
            }
        }
        else
        {
            r = Scheduler.SpawnFile(prog, progLen, Scheduler.Console, out pid, boot: true);
        }
        if (r != SpawnResult.Ok)
        {
            // If we cannot launch the boot program, halt the system.
            Vga.WriteLine("Failed to launch boot program. Halting.");
            while (true) { NativeMethods.hlt_cpu(); }
        }
        // The boot program is the session leader of a fresh session that starts
        // with no uid; it must LOGIN before doing anything privileged (1c, I3).
        Scheduler.BeginFirstSession((ulong)pid);
        Scheduler.WaitFor((ulong)pid);
        // If the boot program exits, halt the system.
        Vga.WriteLine("Boot program exited. Halting.");
        while (true) { NativeMethods.hlt_cpu(); }
    }

    // Allocate n byte-aligned frames from the physical frame allocator and
    // return them as a pointer (the kernel is identity-mapped, so the physical
    // address doubles as the virtual address). Returns null on failure.
    public static unsafe void* OsAlloc(int bytes)
    {
        int frames = (bytes + Layout.FrameSize - 1) / Layout.FrameSize;
        if (frames == 0) return null;
        ulong phys = PhysicalMemory.Alloc(frames, Layout.FrameSize);
        if (phys == 0) return null;
        return (void*)phys;
    }

    // Release bytes previously allocated with OsAlloc, rounding up to whole
    // frames and returning them to the physical frame allocator. A null
    // pointer or zero byte count is a no-op. There is no managed heap; this
    // is the release half of the kernel allocator used by dynamic kernel
    // storage (e.g. the growable Program Registry descriptor array).
    public static unsafe void OsFree(void* ptr, int bytes)
    {
        if (ptr == null || bytes <= 0) return;
        int frames = (bytes + Layout.FrameSize - 1) / Layout.FrameSize;
        PhysicalMemory.Free((ulong)ptr, frames);
    }

    // Reads A:\OSB\CONF\BOOT (first line, trailing spaces trimmed) into path.
    // Returns the length, or 0 when the file is missing/empty/unreadable.
    private static int ReadBootProgram(char* path, int cap)
    {
#if DIAGNOSTICS
        Vga.WriteLine("ReadBootProgram: Mount...");
        if (!Storage.Ext4.Mount()) { Vga.WriteLine("ReadBootProgram: Mount FAILED"); return 0; }
        Vga.WriteLine("ReadBootProgram: Mount OK");
#else
        if (!Storage.Ext4.Mount()) return 0;
#endif
        uint ino;
        fixed (char* boot = "A:\\OSB\\CONF\\BOOT")
        {
            int bl = 0; while (boot[bl] != '\0') bl++;
#if DIAGNOSTICS
            Vga.Write("ReadBootProgram: Resolve... ");
            if (!Storage.Ext4.Resolve(boot, bl, out ino)) { Vga.WriteLine("FAILED"); return 0; }
            Vga.WriteLine("OK");
#else
            if (!Storage.Ext4.Resolve(boot, bl, out ino)) return 0;
#endif
        }
        byte* inode = stackalloc byte[256];
#if DIAGNOSTICS
        Vga.Write("ReadBootProgram: ReadInode... ");
        if (!Storage.Ext4.ReadInode(ino, inode)) { Vga.WriteLine("FAILED"); return 0; }
        Vga.WriteLine("OK");
        Vga.Write("ReadBootProgram: IsReg... ");
        if (!Storage.Ext4.IsReg(Storage.Ext4.InodeGetMode(inode))) { Vga.WriteLine("FAILED"); return 0; }
        Vga.WriteLine("OK");
#else
        if (!Storage.Ext4.ReadInode(ino, inode)) return 0;
        if (!Storage.Ext4.IsReg(Storage.Ext4.InodeGetMode(inode))) return 0;
#endif
        byte* buf = stackalloc byte[256];
        int got;
#if DIAGNOSTICS
        Vga.Write("ReadBootProgram: ReadFileAll... ino="); Vga.WriteDec((ulong)ino); Vga.WriteLine("");
        if (!Storage.Ext4.ReadFileAll(ino, buf, 256, out got)) { Vga.WriteLine("FAILED"); return 0; }
        Vga.Write("ReadBootProgram: ReadFileAll returned, got="); Vga.WriteDec((ulong)got); Vga.WriteLine("");
#else
        if (!Storage.Ext4.ReadFileAll(ino, buf, 256, out got)) return 0;
#endif
        int n = 0;
        for (int i = 0; i < got && n < cap - 1; i++)
        {
            byte b = buf[i];
            if (b == (byte)'\r' || b == (byte)'\n') break;   // first line only
            path[n++] = (char)b;
        }
        while (n > 0 && path[n - 1] == ' ') n--;              // trim trailing spaces
#if DIAGNOSTICS
        Vga.Write("ReadBootProgram: returning n="); Vga.WriteDec((ulong)n); Vga.WriteLine("");
#endif
        return n;
    }
}
}