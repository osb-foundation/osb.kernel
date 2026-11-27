#!/usr/bin/env bash
# Minimal build for THello proof - builds kernel + THello only
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OBJ="$ROOT/obj/kernel"
ISO_DIR="$ROOT/iso"
OUT_ISO="$ROOT/osb.iso"
OUT_IMG="$ROOT/osb.img"

# Assembler
if [[ -z "${CC:-}" ]]; then
    if command -v clang >/dev/null 2>&1; then CC=clang
    elif command -v gcc >/dev/null 2>&1; then CC=gcc
    else echo "ERROR: neither clang nor gcc found in PATH." >&2; exit 1; fi
fi

run() {
    if ! "$@" >/dev/null 2>&1; then
        echo "ERROR: $* failed" >&2
        exit 1
    fi
}

dbg() {
    local out rc
    out="$("$@" 2>&1)"; rc=$?
    if [[ $rc -ne 0 ]] || grep -qiE 'No such file|File not found' <<<"$out"; then
        echo "ERROR: $* failed (rc=$rc)" >&2
        printf '%s\n' "$out" >&2
        exit 1
    fi
}

# Sibling checkouts
for sib in osb.shell osbkt osb.core; do
    if [[ ! -d "$ROOT/../$sib" ]]; then
        echo "ERROR: sibling repo ../$sib not found next to osb.kernel." >&2
        exit 1
    fi
done

mkdir -p "$OBJ"

# Build ext4 disk image
echo "[0/5] Building ext4 disk image (A:)"
mkdir -p "$OBJ/fs"
rm -f "$OUT_IMG"
dd if=/dev/zero of="$OUT_IMG" bs=1M count=16 >/dev/null 2>&1
mke2fs -t ext4 -b 4096 \
  -O ^has_journal,^metadata_csum,^flex_bg,^dir_index \
  -L "OSB" "$OUT_IMG" >/dev/null 2>&1
cat > "$OBJ/fs/populate.txt" <<'POPEOF'
mkdir /OSB
mkdir /OSB/BIN
mkdir /OSB/CONF
mkdir /OSB/HELP
mkdir /OSB/HOME
mkdir /OSB/TEMP
mkdir /OSB/APPS
mkdir /OSB/ROOT
mkdir /OSB/ROOT/TRASH
POPEOF
printf 'OSB File System test.\nCase-sensitive filenames: ON\nHome is A:\\OSB\\ROOT.\n' > "$OBJ/fs/welcome.txt"
printf 'root::0:A:\\OSB\\ROOT\n' > "$OBJ/fs/passwd.txt"
printf 'root\n' > "$OBJ/fs/sudoers.txt"
printf 'A:\\OSB\\BIN\\SHELL.EXE\n' > "$OBJ/fs/boot.txt"
{ cat "$OBJ/fs/populate.txt"; printf 'write "%s" /OSB/README.TXT\n' "$OBJ/fs/welcome.txt"; printf 'write "%s" /OSB/CONF/PASSWD\n' "$OBJ/fs/passwd.txt"; printf 'write "%s" /OSB/CONF/SUDOERS\n' "$OBJ/fs/sudoers.txt"; printf 'sif /OSB/TEMP mode 040777\n'; printf 'sif /OSB/CONF/PASSWD mode 0100600\n'; printf 'sif /OSB/CONF/SUDOERS mode 0100600\n'; printf 'write "%s" /OSB/CONF/BOOT\n' "$OBJ/fs/boot.txt"; } > "$OBJ/fs/populate2.txt"
dbg debugfs -w -f "$OBJ/fs/populate2.txt" "$OUT_IMG"
run e2fsck -fy "$OUT_IMG"
echo "OK -> $OUT_IMG"

# Compile kernel
echo "[1/5] Compiling C# (managed, mini-CoreLib) ..."
dotnet build -c Release "$ROOT/Osb.Kernel.csproj" -v q -nologo

DLL="$ROOT/bin/Release/net10.0/OsbKernel.dll"
test -f "$DLL" || { echo "ERROR: $DLL not produced"; exit 1; }

echo "[2/5] NativeAOT (ilc) ..."
ILC="$(ls -1d "$HOME"/.nuget/packages/runtime.linux-x64.microsoft.dotnet.ilcompiler/*/tools/ilc 2>/dev/null | sort -V | tail -n1)"
test -x "$ILC" || { echo "ERROR: ilc not found"; exit 1; }
"$ILC" "$DLL" -o "$OBJ/OsbKernel.o" \
    --systemmodule OsbKernel \
    --targetarch x64 --targetos linux \
    -O -g --directpinvoke:osbnative \
    --map "$OBJ/kernel.map"

echo "[3/5] Generating ISR stubs + assembling ..."
ISR="$OBJ/interrupts.S"
: > "$ISR"
printf '.section .text\n\n' >> "$ISR"
ERRVECS=" 8 10 11 12 13 14 17 "
for i in $(seq 0 255); do
  printf '.global isr%s\nisr%s:\n' "$i" "$i" >> "$ISR"
  if [[ "$ERRVECS" == *" $i "* ]]; then
    printf '    addq $8, %%rsp    /* discard CPU error code */\n' >> "$ISR"
  fi
  printf '    pushq $0\n    pushq $%s\n    jmp isr_common\n\n' "$i" "$i" >> "$ISR"
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

/* Common ISR entry */
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
    movq %rsp, %rbp
    andq $-16, %rsp
    leaq 256(%rbp), %rdi
    call osb_handle_interrupt
    movq %rbp, %rsp
    movdqu 240(%rsp), %xmm15
    movdqu 224(%rsp), %xmm14
    movdqu 208(%rsp), %xmm13
    movdqu 192(%rsp), %xmm12
    movdqu 176(%rsp), %xmm11
    movdqu 160(%rsp), %xmm10
    movdqu 144(%rsp), %xmm9
    movdqu 112(%rsp), %xmm6
    movdqu  96(%rsp), %xmm5
    movdqu  80(%rsp), %xmm4
    movdqu  64(%rsp), %xmm3
    movdqu  48(%rsp), %xmm2
    movdqu  32(%rsp), %xmm1
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
    addq $16, %rsp
    iretq
ASM
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/interrupts.o" "$ISR"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/boot.o" "$ROOT/boot.S"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/sched.o" "$ROOT/sched.S"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/sync.o" "$ROOT/sync.S"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/user.o" "$ROOT/user.S"
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/probe.o" "$ROOT/probe.S"

echo "[4/5] Linking kernel.elf ..."
ld.lld -T "$ROOT/linker.ld" -o "$OBJ/kernel.elf" \
    "$OBJ/boot.o" "$OBJ/interrupts.o" "$OBJ/sched.o" "$OBJ/sync.o" "$OBJ/user.o" "$OBJ/probe.o" "$OBJ/OsbKernel.o" --no-relax

# Compile THello only
echo "[5/5] Compiling THello (Ring-3 Hello World) ..."
"$CC" -c -nostdlib -ffreestanding -o "$OBJ/userspace.o" "$ROOT/../osbkt/crt0.S"

ILC_SHELL="$ILC"
module=THello
csproj="$ROOT/../osb.shell/tests/progs/THello/THello.csproj"
staged=HELLO

echo "  building $staged.EXE ..."
dotnet build -c Release "$csproj" -v q -nologo
dll="$(dirname "$csproj")/bin/Release/net10.0/$module.dll"
test -f "$dll" || { echo "ERROR: $dll not produced"; exit 1; }
"$ILC_SHELL" "$dll" -o "$OBJ/$module.o" \
    --systemmodule "$module" \
    --targetarch x64 --targetos linux \
    -O -g --directpinvoke:osbnative \
    --map "$OBJ/$module.map"

ld.lld -T "$ROOT/../osbkt/user.ld" -o "$OBJ/$module.elf" \
    "$OBJ/userspace.o" "$OBJ/$module.o" --no-relax \
    --defsym="osb_startup=${module}__Module___StartupCodeMain"
objcopy -O binary "$OBJ/$module.elf" "$OBJ/$module.bin"
python3 "$ROOT/../osbkt/mkosbx.py" "$OBJ/$module.bin" "$OBJ/$module.EXE" 0 >/dev/null
dbg debugfs -w -R "write \"$OBJ/$module.EXE\" /OSB/BIN/$staged.EXE" "$OUT_IMG"
dbg debugfs -w -R "sif /OSB/BIN/$staged.EXE mode 0100755" "$OUT_IMG"
echo "  staged /OSB/BIN/$staged.EXE (mode 0100755)"

# Also stage SHELL.EXE so we can run THello from the shell
echo "  building SHELL.EXE ..."
dotnet build -c Release "$ROOT/../osb.shell/Osb.Shell.Userspace.csproj" -v q -nologo
dll="$ROOT/../osb.shell/bin/Release/net10.0/OsbShell.dll"
test -f "$dll" || { echo "ERROR: $dll not produced"; exit 1; }
"$ILC_SHELL" "$dll" -o "$OBJ/OsbShell.o" \
    --systemmodule OsbShell \
    --targetarch x64 --targetos linux \
    -O -g --directpinvoke:osbnative \
    --map "$OBJ/OsbShell.map"
ld.lld -T "$ROOT/../osbkt/user.ld" -o "$OBJ/OsbShell.elf" \
    "$OBJ/userspace.o" "$OBJ/OsbShell.o" --no-relax \
    --defsym="osb_startup=OsbShell__Module___StartupCodeMain"
objcopy -O binary "$OBJ/OsbShell.elf" "$OBJ/OsbShell.bin"
python3 "$ROOT/../osbkt/mkosbx.py" "$OBJ/OsbShell.bin" "$OBJ/OsbShell.EXE" 0 >/dev/null
dbg debugfs -w -R "write \"$OBJ/OsbShell.EXE\" /OSB/BIN/SHELL.EXE" "$OUT_IMG"
dbg debugfs -w -R "sif /OSB/BIN/SHELL.EXE mode 0100755" "$OUT_IMG"
echo "  staged /OSB/BIN/SHELL.EXE (mode 0100755)"

run e2fsck -fy "$OUT_IMG"

# Build ISO
echo "[6/5] Building ISO ..."
rm -rf "$ISO_DIR"
mkdir -p "$ISO_DIR/boot/grub"
cp "$OBJ/kernel.elf" "$ISO_DIR/boot/kernel.elf"
cp "$ROOT/grub.cfg" "$ISO_DIR/boot/grub/grub.cfg"
cp "$OUT_IMG" "$ISO_DIR/osb.img" 2>/dev/null || true
grub-mkrescue -o "$OUT_ISO" "$ISO_DIR" >/dev/null

echo "OK -> $OUT_ISO"
