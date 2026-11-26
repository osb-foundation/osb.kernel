# Changelog

## [0.1.0] - Olorun (Generation 0)

### Added
- Initial release of osb.kernel under OSB generation 0 (Olorun).
- Bootable OSB kernel (Multiboot2, native AOT, no BCL/runtime): freestanding C# (Osb.Kernel.dll), ilc NativeAOT, lld linker, grub-mkrescue ISO.
- INT 21h syscall gate (`Osb.Kernel.Cpu.Syscalls.Handle`), interrupt/syscall interfaces, filesystem support (ext4 A:), scheduler, privilege levels, Ring 3 ABI v8.
- Memory layout: physical frame allocator floor at 0x200000; user virtual region at 0x2000000–0x2200000 (2 MiB per process, private physical block), task slots, context switch via lld.
- Environment variables (PATH, PROMPT, USER, HOME, TEMP, COMSPEC, OS, VER, LASTDRIVE) locked from login.
- Removed legacy shell command loop; userspace shell (SHELL.EXE) spawned via SPAWN/WAIT syscalls.
- Scheduler: preemptive multitasking without Unix process semantics.

### Stable
- Build succeeds (`./build.sh`); produces `osb.iso`, `osb.img`, `kernel.elf` (Multiboot2), `SHELL.EXE`, test programs (THello, TOslangEntry, TTlsProof, ...), passes host unit tests, ELF validates ABI version 8.

### Experimental / Incomplete
- Graphics/VGA desktop (gfxterm) not implemented; graphics baseline only.
- DHCP client minimal (DISCOVER → OFFER → REQUEST → ACK).
- Non-root sudoer account handling incomplete.
- Trash/file deletion commands basic.
