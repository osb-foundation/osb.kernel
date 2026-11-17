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

        Interrupts.PicInit();   // remap 8259A (IRQ0-15 -> vectors 0x30-0x3F)
        Pit.Init();             // 8254 channel 0: square wave @ 100 Hz
        Rtc.Init();             // CMOS/RTC: 24h + binary + update-ended IRQ (~1 Hz)
        Gdt.Init(Scheduler.KernelStackTop(1));   // user segments + TSS, then load GDT + ltr
        PhysicalMemory.Detect(multibootInfo);   // find how much RAM we can hand out
        PhysicalMemory.Init();   // physical frame allocator (bitmap)
        Interrupts.Init();      // fill + load the IDT (exception + IRQ stubs)
        Scheduler.Init();       // console (slot 0) + kernel address space
        Font.Init();            // embedded 8x8 font table
        Mouse.Init();           // PS/2 mouse on IRQ12

        Vga.WriteLine("OSB - Operating System Basic");
        Vga.WriteLine("Bootable kernel prototype (C# / NativeAOT, bare metal)");
        Vga.WriteLine("Keyboard: PS/2 IRQ1 + serial console IRQ4 (COM1).");
        Vga.WriteLine("Timer: PIT channel 0 @ 100 Hz (IRQ0 ticks).");
        Vga.WriteLine("Clock: RTC via IRQ8 (update-ended).");
        Vga.WriteLine("Scheduler: preemptive round-robin over a task table.");
        Vga.WriteLine("Processes: private address space per process (CR3).");
        Vga.WriteLine("Ring 3: user segments + TSS; syscalls via INT 21h (DOS-style).");
        Vga.WriteLine("Files: ATA PIO + ext4 on A:.");
        Vga.WriteLine("Shell: boot program (default A:\\OSB\\BIN\\SHELL.EXE, A:\\OSB\\CONF\\BOOT overrides)");
        Vga.WriteLine("Mouse: PS/2 IRQ12 (stream mode).");
        Vga.WriteLine("");

        Environment.Init();     // session environment (PATH, PROMPT, USER, ...)

        // Hand control to the userspace shell. The boot program is
        // A:\OSB\CONF\BOOT (first line) when present, else SHELL.EXE; the
        // golden harness points it at a test program (e.g. TWait.EXE) to
        // exercise the syscall gate without patching the kernel.
        char* boot = stackalloc char[256];
        int bootLen = ReadBootProgram(boot, 256);
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
        var r = Scheduler.SpawnFile(boot, bootLen, Scheduler.Console, out pid);
        if (r != SpawnResult.Ok)
        {
            // If we cannot launch the boot program, halt the system.
            Vga.WriteLine("Failed to launch boot program. Halting.");
            while (true) { NativeMethods.hlt_cpu(); }
        }
        Scheduler.WaitFor(pid);
        // If the boot program exits, halt the system.
        Vga.WriteLine("Boot program exited. Halting.");
        while (true) { NativeMethods.hlt_cpu(); }
    }

    // Reads A:\OSB\CONF\BOOT (first line, trailing spaces trimmed) into path.
    // Returns the length, or 0 when the file is missing/empty/unreadable.
    private static int ReadBootProgram(char* path, int cap)
    {
        if (!Storage.Ext4.Mount()) return 0;
        uint ino;
        fixed (char* boot = "A:\\OSB\\CONF\\BOOT")
        {
            int bl = 0; while (boot[bl] != '\0') bl++;
            if (!Storage.Ext4.Resolve(boot, bl, out ino)) return 0;
        }
        byte* inode = stackalloc byte[256];
        if (!Storage.Ext4.ReadInode(ino, inode)) return 0;
        if (!Storage.Ext4.IsReg(Storage.Ext4.InodeGetMode(inode))) return 0;
        byte* buf = stackalloc byte[256];
        int got;
        if (!Storage.Ext4.ReadFileAll(ino, buf, 256, out got)) return 0;
        int n = 0;
        for (int i = 0; i < got && n < cap - 1; i++)
        {
            byte b = buf[i];
            if (b == (byte)'\r' || b == (byte)'\n') break;   // first line only
            path[n++] = (char)b;
        }
        while (n > 0 && path[n - 1] == ' ') n--;              // trim trailing spaces
        return n;
    }
}
}