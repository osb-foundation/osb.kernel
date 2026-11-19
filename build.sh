#!/usr/bin/env bash
# Builds the bootable OSB kernel ISO (osb.iso) from src/Osb.Kernel.
#
# Pipeline:
#   1. csc (via the .NET SDK) compiles the freestanding C# against our own
#      mini-CoreLib (MiniCoreLib.cs) -> OsbKernel.dll (managed IL only).
#   2. ilc (NativeAOT) turns that IL into a bare x86-64 object file, with no
#      BCL and no runtime.
#   3. $CC assembles the Multiboot2 boot stub (boot.S) and the .S helpers.
#   4. ld.lld links boot.o + OsbKernel.o with linker.ld into a Multiboot2 ELF.
#   5. grub-mkrescue wraps it in a bootable ISO (GRUB BIOS).
#
# Requires: dotnet (10.x SDK), clang or gcc, lld (ld.lld), xorriso,
# grub-mkrescue.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OBJ="$ROOT/obj/kernel"
ISO_DIR="$ROOT/iso"
OUT_ISO="$ROOT/osb.iso"
OUT_IMG="$ROOT/osb.img"

# Assembler for the .S files (GNU syntax; clang preferred, gcc works too).
if [[ -z "${CC:-}" ]]; then
    if command -v clang >/dev/null 2>&1; then CC=clang
    elif command -v gcc >/dev/null 2>&1; then CC=gcc
    else echo "ERROR: neither clang nor gcc found in PATH." >&2; exit 1; fi
fi

# Run a build command quietly; abort with a clear message on failure. The old
# code discarded debugfs/e2fsck output with `>/dev/null 2>&1`, so a failed
# write/sif/mkdir aborted the build with no explanation (review item 5).
run() {
    if ! "$@" >/dev/null 2>&1; then
        echo "ERROR: $* failed" >&2
        exit 1
    fi
}

# debugfs is lenient: it prints "No such file..." / "File not found" but still
# exits 0, so `run` would not catch a missing write/sif source. Run debugfs,
# keep its output, and abort on a non-zero exit OR an error phrase (review item
# 5). "already exists" is benign (idempotent mkdir on a re-run).
dbg() {
    local out rc
    out="$("$@" 2>&1)"; rc=$?
    if [[ $rc -ne 0 ]] || grep -qiE 'No such file|File not found' <<<"$out"; then
        echo "ERROR: $* failed (rc=$rc)" >&2
        printf '%s\n' "$out" >&2
        exit 1
    fi
}

# Sibling checkouts (A10): the split repos live side by side. osb.shell and
# osbkt provide SHELL.EXE's sources/tooling; osb.core provides the canonical
# MiniCoreLib. osb.xwin is optional (best-effort staging below).
for sib in osb.shell osbkt osb.core; do
    if [[ ! -d "$ROOT/../$sib" ]]; then
        echo "ERROR: sibling repo ../$sib not found next to osb.kernel." >&2
        echo "       Clone the OSB repos side by side (see README.md)." >&2
        exit 1
    fi
done

mkdir -p "$OBJ"

# Generate the ext4 disk image (drive A:) if it does not exist or is older than
# the population script markers (for now, always regenerate small dev image).
echo "[0/6] Building ext4 disk image (A:)"
mkdir -p "$OBJ/fs"
rm -f "$OUT_IMG"
dd if=/dev/zero of="$OUT_IMG" bs=1M count=16 >/dev/null 2>&1
mke2fs -t ext4 -b 4096 \
  -O ^has_journal,^metadata_csum,^flex_bg,^dir_index \
  -L "OSB" "$OUT_IMG" >/dev/null 2>&1
cat > "$OBJ/fs/populate.txt" <<'EOF'
mkdir /OSB
mkdir /OSB/BIN
mkdir /OSB/CONF
mkdir /OSB/HELP
mkdir /OSB/HOME
mkdir /OSB/TEMP
mkdir /OSB/APPS
mkdir /OSB/ROOT
mkdir /OSB/ROOT/TRASH
EOF
printf 'OSB File System test.\nCase-sensitive filenames: ON\nHome is A:\\OSB\\ROOT.\n' > "$OBJ/fs/welcome.txt"

# Login system seeds: PASSWD (root, passwordless, uid 0, home A:\OSB\ROOT) and
# SUDOERS. WITH_TESTS=1 additionally seeds a non-root non-sudoer (tester, uid 1)
# and a non-root sudoer (sudoer, uid 2) for the 1c security tests (I5); the
# default image ships root only.
if [[ "${WITH_TESTS:-0}" == "1" ]]; then
    printf 'root::0:A:\\OSB\\ROOT\ntester:pw:1:A:\\OSB\\HOME\\tester\nsudoer:pw:2:A:\\OSB\\HOME\\sudoer\nabcdefghijklmno:pw:3:A:\\OSB\\HOME\\abcdefghijklmno\n' > "$OBJ/fs/passwd.txt"
    printf 'root\nsudoer\n' > "$OBJ/fs/sudoers.txt"
    printf 'mkdir /OSB/HOME/tester\nmkdir /OSB/HOME/sudoer\nmkdir /OSB/HOME/abcdefghijklmno\n' >> "$OBJ/fs/populate.txt"
else
    printf 'root::0:A:\\OSB\\ROOT\n' > "$OBJ/fs/passwd.txt"
    printf 'root\n' > "$OBJ/fs/sudoers.txt"
fi

# Boot program: A:\OSB\CONF\BOOT names the program the kernel launches (one
# line). Defaults to SHELL.EXE; OSB_BOOT_PROGRAM overrides the seed at build
# time (the golden harness instead patches CONF/BOOT per case on a throwaway
# image copy — see osb.shell/tests/golden/golden.sh patch_boot).
printf '%s\n' "${OSB_BOOT_PROGRAM:-A:\\OSB\\BIN\\SHELL.EXE}" > "$OBJ/fs/boot.txt"

{ cat "$OBJ/fs/populate.txt"; printf 'write "%s" /OSB/README.TXT\n' "$OBJ/fs/welcome.txt"; printf 'write "%s" /OSB/CONF/PASSWD\n' "$OBJ/fs/passwd.txt"; printf 'write "%s" /OSB/CONF/SUDOERS\n' "$OBJ/fs/sudoers.txt"; printf 'sif /OSB/TEMP mode 040777\n'; printf 'sif /OSB/CONF/PASSWD mode 0100600\n'; printf 'sif /OSB/CONF/SUDOERS mode 0100600\n'; printf 'write "%s" /OSB/CONF/BOOT\n' "$OBJ/fs/boot.txt"; } > "$OBJ/fs/populate2.txt"
dbg debugfs -w -f "$OBJ/fs/populate2.txt" "$OUT_IMG"
# debugfs "write" does not update the inode/block bitmaps: reconcile them or the
# kernel's allocators would hand out the seeded files' inodes (e.g. SUDOERS).
run e2fsck -fy "$OUT_IMG"

# ----- stage the external XWIN.EXE (optional, best-effort) -----
XWIN_ROOT=""
if [[ -d "$ROOT/../osb.xwin" ]]; then
    XWIN_ROOT="$(cd "$ROOT/../osb.xwin" && pwd)"
fi
if [[ -n "$XWIN_ROOT" && -x "$XWIN_ROOT/build.sh" ]]; then
    mkdir -p "$OBJ/fs/xwin_stage"
    if STAGE_BIN="$OBJ/fs/xwin_stage" "$XWIN_ROOT/build.sh" > "$OBJ/xwin_stage.log" 2>&1; then
        if [[ -f "$OBJ/fs/xwin_stage/XWIN.EXE" ]]; then
            dbg debugfs -w -R "write \"$OBJ/fs/xwin_stage/XWIN.EXE\" /OSB/BIN/XWIN.EXE" "$OUT_IMG"
            run e2fsck -fy "$OUT_IMG"
            echo "  staged /OSB/BIN/XWIN.EXE"
        fi
    else
        echo "  WARN: XWIN build failed (see $OBJ/xwin_stage.log), continuing" >&2
    fi
fi
echo "OK -> $OUT_IMG"

# ----- compile the external BIN programs (MEM.EXE & DU.EXE) -----
echo "[6/6] Compiling external BIN programs ..."
# MEM/DU are C# programs compiled to AOT native binaries and wrapped as OSBEXE.
# They are prepared separately (mem_exe.bin/du_exe.bin) and written into /OSB/BIN.

# Locate the native AOT compiler shipped with the SDK (NuGet cache).
ILC="$(ls -1d "$HOME"/.nuget/packages/runtime.linux-x64.microsoft.dotnet.ilcompiler/*/tools/ilc 2>/dev/null | sort -V | tail -n1)"
if [[ -z "$ILC" || ! -x "$ILC" ]]; then
    echo "ERROR: ilc (NativeAOT compiler) not found in ~/.nuget/packages." >&2
    echo "Build the project once (dotnet build) or restore the SDK to fetch it." >&2
    exit 1
fi

echo "[1/6] Compiling C# (managed, mini-CoreLib) ..."
# WITH_TESTS=1 (H-B): build the kernel so it honors A:\OSB\CONF/BOOT, letting
# the golden harness boot a test program directly. The default build always
# boots SHELL.EXE regardless of CONF/BOOT.
BOOT_CONF_ARG=""
if [[ "${WITH_TESTS:-0}" == "1" ]]; then BOOT_CONF_ARG="-p:BootFromConf=true"; fi
dotnet build -c Release "$ROOT/Osb.Kernel.csproj" -v q -nologo $BOOT_CONF_ARG

DLL="$ROOT/bin/Release/net10.0/OsbKernel.dll"
test -f "$DLL" || { echo "ERROR: $DLL not produced"; exit 1; }

echo "[2/6] NativeAOT (ilc) ..."
"$ILC" "$DLL" -o "$OBJ/OsbKernel.o" \
    --systemmodule OsbKernel \
    --targetarch x64 --targetos linux \
    -O -g --directpinvoke:osbnative \
    --map "$OBJ/kernel.map"

echo "[3/6] Generating ISR stubs + assembling ..."
# Generate interrupts.S: one 2-instruction stub per IDT vector plus a common
# frame save/restore entry. Vectors with a CPU error code (8,10-14,17) and a
# uniform [vector][dummy] pushed-frame layout for the C# handler.
ISR="$OBJ/interrupts.S"
: > "$ISR"
printf '.section .text\n\n' >> "$ISR"
ERRVECS=" 8 10 11 12 13 14 17 "
for i in $(seq 0 255); do
  printf '.global isr%s\nisr%s:\n' "$i" "$i" >> "$ISR"
  if [[ "$ERRVECS" == *" $i "* ]]; then
    printf '    addq $8, %%rsp    /* discard CPU error code */\n' >> "$ISR"
  fi
  printf '    pushq $0\n    pushq $%s\n    jmp isr_common\n\n' "$i" >> "$ISR"
done
printf '.global osb_isr_stubs\nosb_isr_stubs:\n' >> "$ISR"
for i in $(seq 0 255); do
  printf '    .quad isr%s\n' "$i" >> "$ISR"
done
cat >> "$ISR" <<'ASM'

.global osb_get_stub_table
.type osb_get_stub_table, @function
osb_get_stub_table:
    leaq osb_isr_stubs(%rip), %rax
    ret

/* Common ISR entry: save all GPR + XMM regs (the C# handler is a SysV
 * function and may clobber caller-saved state, including XMM0-15 that the
 * interrupted C# frame may hold live), realign to 16 bytes, call the
 * dispatcher with a pointer to the saved frame, then restore + iretq. */
isr_common:
    pushq %rax
    pushq %rbx
    pushq %rcx
    pushq %rdx
    pushq %rsi
    pushq %rdi
    pushq %rbp
    pushq %r8
    pushq %r9
    pushq %r10
    pushq %r11
    pushq %r12
    pushq %r13
    pushq %r14
    pushq %r15
    subq $256, %rsp
    movdqu %xmm0,   0(%rsp)
    movdqu %xmm1,  16(%rsp)
    movdqu %xmm2,  32(%rsp)
    movdqu %xmm3,  48(%rsp)
    movdqu %xmm4,  64(%rsp)
    movdqu %xmm5,  80(%rsp)
    movdqu %xmm6,  96(%rsp)
    movdqu %xmm7, 112(%rsp)
    movdqu %xmm8, 128(%rsp)
    movdqu %xmm9, 144(%rsp)
    movdqu %xmm10, 160(%rsp)
    movdqu %xmm11, 176(%rsp)
    movdqu %xmm12, 192(%rsp)
    movdqu %xmm13, 208(%rsp)
    movdqu %xmm14, 224(%rsp)
    movdqu %xmm15, 240(%rsp)
    movq %rsp, %rbp          /* frame base: XMM save area (below the GPRs) */
    andq $-16, %rsp          /* SysV: rsp%16==0 at the call site */
    leaq 256(%rbp), %rdi     /* arg0: pointer to the GPR frame (vector at +120) */
    call osb_handle_interrupt
    movq %rbp, %rsp
    movdqu 240(%rsp), %xmm15
    movdqu 224(%rsp), %xmm14
    movdqu 208(%rsp), %xmm13
    movdqu 192(%rsp), %xmm12
    movdqu 176(%rsp), %xmm11
    movdqu 160(%rsp), %xmm10
    movdqu 144(%rsp), %xmm9
    movdqu 128(%rsp), %xmm8
    movdqu 112(%rsp), %xmm7
    movdqu  96(%rsp), %xmm6
    movdqu  80(%rsp), %xmm5
    movdqu  64(%rsp), %xmm4
    movdqu  48(%rsp), %xmm3
    movdqu  32(%rsp), %xmm2
    movdqu  16(%rsp), %xmm1
    movdqu   0(%rsp), %xmm0
    addq $256, %rsp
    popq %r15
    popq %r14
    popq %r13
    popq %r12
    popq %r11
    popq %r10
    popq %r9
    popq %r8
    popq %rbp
    popq %rdi
    popq %rsi
    popq %rdx
    popq %rcx
    popq %rbx
    popq %rax
    addq $16, %rsp           /* drop [vector][dummy error code] */
    iretq
ASM
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/interrupts.o" "$ISR"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/boot.o" "$ROOT/boot.S"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/sched.o" "$ROOT/sched.S"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/user.o" "$ROOT/user.S"

echo "[4/6] Linking kernel.elf ..."
ld.lld -T "$ROOT/linker.ld" -o "$OBJ/kernel.elf" \
    "$OBJ/boot.o" "$OBJ/interrupts.o" "$OBJ/sched.o" "$OBJ/user.o" "$OBJ/OsbKernel.o" --no-relax

# ----- compile ring-3 programs (SHELL.EXE + tests) -----
echo "[x/6] Compiling ring-3 programs ..."
# shellcheck source=scripts/stage-ring3.sh
source "$ROOT/scripts/stage-ring3.sh"
stage_ring3

# ----- stage KISS editor (OSLANG script) at /OSB/BIN/KISS/ -----
KISS_DIR="$ROOT/../osb.oslang/samples/kiss"
if [[ -d "$KISS_DIR" ]]; then
    echo "  staging KISS editor ..."
    # mkdir is benign if the directory already exists (rebuild)
    debugfs -w -R "mkdir /OSB/BIN/KISS" "$OUT_IMG" 2>/dev/null || true
    for f in "$KISS_DIR"/*; do
        [[ -f "$f" ]] || continue
        name="$(basename "$f")"
        dbg debugfs -w -R "write \"$f\" /OSB/BIN/KISS/$name" "$OUT_IMG"
        echo "    staged /OSB/BIN/KISS/$name"
    done
fi

echo "[5/6] Building ISO ..."
rm -rf "$ISO_DIR"
mkdir -p "$ISO_DIR/boot/grub"
cp "$OBJ/kernel.elf" "$ISO_DIR/boot/kernel.elf"
cp "$ROOT/grub.cfg" "$ISO_DIR/boot/grub/grub.cfg"
cp "$OUT_IMG" "$ISO_DIR/osb.img" 2>/dev/null || true
grub-mkrescue -o "$OUT_ISO" "$ISO_DIR" >/dev/null

echo "OK -> $OUT_ISO"

# Helpers
case "${1:-}" in
  run)
    # Headless: serial straight to the terminal, no GUI window to grab the
    # host keyboard/mouse. The kernel mirrors VGA -> COM1, so you still see
    # everything. Quit: `POWEROFF` at the OSB prompt (ACPI S5 -> QEMU exits).
    # The default std VGA carries the Bochs/DISPI registers the GFX desktop
    # uses for its linear framebuffer (PCI 0x1234:0x1111, 32bpp BGRX).
    echo "Booting in QEMU (serial on stdio) ..."
    # OSB_MEM overrides the guest RAM size (default 64M); the kernel maps up
    # to 64 GiB, so values like 2G exercise the multi-directory identity map.
    exec qemu-system-x86_64 -cdrom "$OUT_ISO" -m "${OSB_MEM:-64M}" \
      -drive file="$OUT_IMG",if=ide,index=0 \
      -netdev user,id=net0 -device e1000,netdev=net0 \
      -vga std -display none -serial stdio -no-reboot
    ;;
  run-gui)
    # Graphical window with QEMU's std VGA. The GFX desktop drives its Bochs/
    # DISPI linear framebuffer, which scans out to this window.
    # The window grabs the mouse on click: press Ctrl+Alt+G to release,
    # Ctrl+Alt+2 for the QEMU monitor, or use POWEROFF at the OSB prompt
    # to shut the emulator down cleanly.
    echo "Booting in QEMU (GUI window + serial on stdio) ..."
    echo "  Grab escape: Ctrl+Alt+G  |  monitor: Ctrl+Alt+2 then 'quit'"
    exec qemu-system-x86_64 -cdrom "$OUT_ISO" -m "${OSB_MEM:-64M}" -serial stdio -no-reboot \
      -drive file="$OUT_IMG",if=ide,index=0 \
      -vga std
    ;;
  check)
    grub-file --is-x86-multiboot2 "$OBJ/kernel.elf" && echo "kernel.elf: Multiboot2 OK"
    # ABI version consistency check: verify AbiVersion constant in source
    EXPECTED_ABI=8
    if grep -q "public const int AbiVersion = $EXPECTED_ABI;" "$ROOT/Cpu/Syscalls.cs"; then
        echo "ABI version: $EXPECTED_ABI OK (source constant verified)"
    else
        echo "ERROR: ABI version mismatch in source!" >&2
        exit 1
    fi
    ;;
esac