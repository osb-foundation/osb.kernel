using System;

namespace Osb.Kernel.Memory;

internal static class Layout
{
    // VGA
    public const uint VgaBase = 0xB8000;
    public const int VgaWidth = 80;
    public const int VgaHeight = 25;
    public const byte Attr = 0x07; // light grey on black

    // VGA graphics (mode 12h: 640x480, 16 colours, 4 bit planes)
    public const uint VgaGraphBase = 0xA0000;
    public const int GfxWidth = 640;
    public const int GfxHeight = 480;
    public const int GfxBytesPerRow = GfxWidth / 8;   // 80 bytes per scanline
    public const int GfxPlaneSize = GfxBytesPerRow * GfxHeight; // 38400
    public const int GfxPlaneCount = 4;
    // VGA BIOS keeps the 8x16 character generator at F000:FA6E.
    public const ulong BiosFont8x16 = 0xF000FA6E;

    // IDT + PIC
    public const int IdtSize = 256 * 16;          // 256 entries x 16 bytes
    public const ushort KernelSelector = 0x08;    // GDT: 64-bit code at offset 8
    public const byte IdtInterruptGate = 0x8E;    // present, DPL0, 64-bit gate
    public const byte IdtUserGate = 0xEE;         // present, DPL3 (callable via INT)
    // The PIC is remapped to vectors 0x30..0x3F so that vector 0x21 is free for
    // the DOS-style system-call entry point (INT 21h).
    public const int PicBase = 0x30;
    public const int KbdVector = PicBase + 1;     // IRQ1 -> vector 0x31
    public const int TimerVector = PicBase + 0;   // IRQ0 -> vector 0x30
    public const int SerVector = PicBase + 4;     // IRQ4 -> vector 0x34 (COM1 RX)
    public const int RtcVector = PicBase + 8;     // IRQ8 -> vector 0x38
    public const int Int21Vector = 0x21;          // DOS-style syscall gate
    public const int PitHz = 100;                 // PIT frequency in Hz

    // Ring 3 / user memory layout
    // Every process has the same user virtual layout (code at 0x400000, stack
    // just below 0x480000) but a *private* 2 MiB physical block behind it, so
    // one process cannot see another's memory. The block is mapped at pd[2].
    public const ulong UserBase = 0x2000000;      // user code/data region start (moved above all kernel state)
    public const ulong UserLimit = 0x2200000;     // end of the U/S=1 2 MiB page
    public const ulong UserStackTop = 0x2080000;  // user stack grows down from here (512 KiB image area)

    // Per-task TLS (M3.4.2). The NativeAOT helpers read FS:0 to get the thread
    // pointer (TP) and then access the 208-byte `tls_CurrentThread` object at
    // TP-0xD0. Each user task gets its own TP inside its private 2 MiB block:
    // [TlsTop-0x1000, TlsTop) is reserved, with the TLS object in the last
    // 0xD0 bytes and the self-pointer at TlsTop. Slots are 4 KiB apart, so a
    // task can never see another task's TLS. The region is above the stack
    // (0x2080000) and far below the key-event slot at UserLimit-16.
    public const ulong UserTlsBase = 0x2100000;   // TlsTop of slot 1
    public const int UserTlsStride = 0x1000;      // 4 KiB per task slot
    public const int TlsObjectSize = 0xD0;        // 208-byte tls_CurrentThread
    // End of the TLS area (8 slots). A helper that allocates without setting up
    // its own heap gets [UserTlsEnd, UserLimit-16): above every TLS slot and
    // below the 16-byte key-event slot at UserLimit-16.
    public const ulong UserTlsEnd = UserTlsBase + 8UL * (ulong)UserTlsStride;
    public const ulong UserTlsHeapEnd = UserLimit - 16;

    // Thread pointer (FS base value) for a task slot. Slot 0 is the kernel
    // console and never enters ring 3, so it has no TLS.
    public static ulong UserTlsTop(int slot)
    {
        return UserTlsBase + (ulong)slot * (ulong)UserTlsStride;
    }

    // Dynamic user area (P3R.4, VM-009): broad uniform user virtual-address
    // space shared by every process. Half-open range
    // [DynamicUserBase, DynamicUserLimit) = [64 GiB, 128 GiB), backed by
    // pml4[0] + pdpt[64..127], giving 64 GiB of virtual-address capacity.
    //
    // Corrected hexadecimal values (P3R.4 critical address correction):
    //   64 GiB = 0x0000001000000000
    //   128 GiB = 0x0000002000000000
    // The earlier report's 0x0000000010000000 / 0x0000000020000000 were
    // 256 MiB / 512 MiB respectively and must not be reused.
    //
    // Invariants (source-verified):
    //   DynamicUserBase == 64 GiB            (aligned to 4 KiB and 1 GiB)
    //   DynamicUserLimit == 128 GiB          (aligned to 4 KiB and 1 GiB)
    //   DynamicUserSize == 64 GiB            (derived, single source of truth)
    //   No overlap with legacy [0x02000000, 0x02200000)
    //   Within canonical lower half (< 0x0000800000000000, 128 TiB)
    //   PDPT entries 64..127 cover [64 GiB, 128 GiB)
    public const ulong DynamicUserBase = 0x0000001000000000UL;  // 64 GiB
    public const ulong DynamicUserLimit = 0x0000002000000000UL; // 128 GiB
    // Derived from the boundaries (do not duplicate the numeric value).
    public const ulong DynamicUserSize = DynamicUserLimit - DynamicUserBase; // 64 GiB
    // Minimum guard separation between dynamic regions (VM-010): 4 KiB.
    public const ulong DynamicUserMinGuard = 0x1000UL; // 4 KiB
    // Dynamic-region mapping granularity (VM-005): 4 KiB, one frame.
    public const int DynamicUserPageGranularity = FrameSize; // 4 KiB

    // Physical memory (frame allocator)
    public const ulong PhysFloorDefault = 0x200000;   // just above the kernel image
    public const ulong PhysTopDefault = 0x4000000;    // 64 MiB (QEMU -m 64M)
    // Ceiling of the boot-time identity map: boot.S builds one page directory
    // per GiB under pdpt[0..63], so the kernel addresses the first 64 GiB.
    public const ulong PhysMapLimit = 0x1000000000UL; // 64 GiB
    public const int FrameSize = 4096;
    public const int BlockFrames = 512;               // 2 MiB user block
    public const ulong BlockAlign = 0x200000;
    // Frames are never handed out from [0x400000,0x600000): that 2 MiB window is
    // pd[2] in every address space, so the kernel cannot address a fresh block
    // through its physical address there (VA != phys inside the user window).
    public const ulong ReservedLo = 0x2000000;
    public const ulong ReservedHi = 0x2200000;

    // GDT selectors (64-bit flat, base 0)
    public const ushort KernelDataSel = 0x10;
    public const ushort UserCodeSel = 0x18;       // RPL3 -> 0x1B in CS
    public const ushort UserDataSel = 0x20;       // RPL3 -> 0x23 in SS/DS
    public const ushort TssSel = 0x28;

    // RTC CMOS
    public const byte RtcIndex = 0x70;            // CMOS register select
    public const byte RtcData = 0x71;             // CMOS data port

    // ATA PIO (IDE)
    // Primary IDE: data 0x1F0, error/features 0x1F1, sector count 0x1F2,
    // LBA low/mid/high 0x1F3-0x1F5, drive/head 0x1F6, status/command 0x1F7
    public const ushort AtaPrimaryData = 0x1F0;
    public const ushort AtaPrimaryError = 0x1F1;
    public const ushort AtaPrimarySecCount = 0x1F2;
    public const ushort AtaPrimaryLbaLow = 0x1F3;
    public const ushort AtaPrimaryLbaMid = 0x1F4;
    public const ushort AtaPrimaryLbaHigh = 0x1F5;
    public const ushort AtaPrimaryDriveHead = 0x1F6;
    public const ushort AtaPrimaryStatus = 0x1F7;
    public const ushort AtaPrimaryCommand = 0x1F7;
    public const byte AtaCmdReadSectors = 0x20;
    public const byte AtaCmdWriteSectors = 0x30;
    public const byte AtaCmdIdentify = 0xEC;
    public const byte AtaStatusBusy = 0x80;
    public const byte AtaStatusDrq = 0x08;
    public const byte AtaStatusReady = 0x40;

    // ext4
    public const int Ext4BlockSize = 4096;

    // Inode field offsets (ext4)
    public const int InoMode = 0, InoUid = 2, InoSizeLo = 4, InoMtime = 16,
                     InoGid = 24, InoLinks = 26, InoBlocksLo = 28, InoFlags = 32,
                     InoBlock = 40, InoSizeHi = 108, InoBlocksHi = 116;

    // ext4 directory entry (ext4_dir_entry_2)
    public const int DirIno = 0, DirRecLen = 4, DirNameLen = 6, DirType = 7, DirName = 8;

    // Environment
    public const int MaxEnvVars = 32;
    public const int EnvNameBufSize = 512;
    public const int EnvValueBufSize = 4096;

    // Shell history (fixed ring, no managed arrays)
    public const int MaxHistory = 256;
    public const int HistoryLineCap = 128;

    // Scheduler
    public const int SlotConsole = 0;             // the shell / console (PID 1)
    public const int TaskCount = 8;               // console + 7 process slots
    public const int TaskFree = 0;                // slot not in use
    public const int TaskReady = 1;               // runnable (or running)
    public const int TaskZombie = 2;              // exited, not yet reaped
    public const int KindNone = 0;
    public const int KindShell = 1;
    public const int KindDemo = 2;
    public const int KindUser = 3;                // runs in ring 3
    public const int StackSize = 16384;
    public const int MaxName = 12;

    // Built-in ring-3 programs (no filesystem yet)
    public const int ProgHello = 0;
    public const int ProgBad = 1;
    public const int ProgSpawner = 2;             // exercises INT 21h AH=80h/81h

    // Physical frame bitmap: covers the mapped 64 GiB at 4 KiB granularity
    // (2 MiB of bitmap). One bit per frame, indexed from the allocator floor.
    public const int MaxFrames = 16777216;       // 64 GiB / 4 KiB
}