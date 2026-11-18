// ATA PIO (IDE) driver: primary channel, LBA28, one 512-byte sector per call.
// Ext4.cs is the only user; it turns its 4 KiB blocks into the eight sectors
// this driver transfers. The data phase uses rep insw/outsw so one string
// instruction moves a whole sector (one VM exit instead of 256 under KVM).
// Status waits are bounded so a dead drive fails instead of hanging the kernel.

using Osb.Kernel.Hardware;
using Osb.Kernel.Memory;

namespace Osb.Kernel.Storage;

internal static unsafe class Ata
{
    private const int SectorWords = 512 / 2;      // 256 x 16-bit words
    private const int MaxTimeout = 1_000_000;     // bounded status polls

    // Spin while the controller reports BSY; false if it never clears.
    public static bool WaitBusy()
    {
        for (int i = 0; i < MaxTimeout; i++)
            if ((NativeMethods.inb(Layout.AtaPrimaryStatus) & Layout.AtaStatusBusy) == 0) return true;
        return false;
    }

    // Spin until the drive raises DRQ (data ready to transfer).
    public static bool WaitDrq()
    {
        for (int i = 0; i < MaxTimeout; i++)
            if ((NativeMethods.inb(Layout.AtaPrimaryStatus) & Layout.AtaStatusDrq) != 0) return true;
        return false;
    }

    // Point the primary master at `lba` and issue `command`, then wait for the
    // data phase to open.
    private static bool Select(ulong lba, byte command)
    {
        if (!WaitBusy()) return false;
        NativeMethods.outb(Layout.AtaPrimaryDriveHead, (byte)(0xE0 | ((lba >> 24) & 0x0F)));
        NativeMethods.outb(Layout.AtaPrimarySecCount, 1);
        NativeMethods.outb(Layout.AtaPrimaryLbaLow, (byte)(lba & 0xFF));
        NativeMethods.outb(Layout.AtaPrimaryLbaMid, (byte)((lba >> 8) & 0xFF));
        NativeMethods.outb(Layout.AtaPrimaryLbaHigh, (byte)((lba >> 16) & 0xFF));
        NativeMethods.outb(Layout.AtaPrimaryCommand, command);
        return WaitBusy();
    }

    public static bool ReadSector(ulong lba, byte* buffer)
    {
        if (!Select(lba, Layout.AtaCmdReadSectors)) return false;
        if (!WaitDrq()) return false;
        NativeMethods.insw(Layout.AtaPrimaryData, (ushort*)buffer, SectorWords);
        return true;
    }

    public static bool WriteSector(ulong lba, byte* buffer)
    {
        if (!Select(lba, Layout.AtaCmdWriteSectors)) return false;
        if (!WaitDrq()) return false;
        NativeMethods.outsw(Layout.AtaPrimaryData, (ushort*)buffer, SectorWords);
        return WaitBusy();
    }
}