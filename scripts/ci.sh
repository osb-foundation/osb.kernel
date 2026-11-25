#!/usr/bin/env bash
# OSB kernel CI on develop.
#
# Runs build validation + unit tests on every push/PR targeting develop.
# This workflow must NOT publish a stable release or an official ISO.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "=== OSB kernel CI (develop) ==="

# 1. Validate the authoritative version metadata.
dotnet tool install --global Cake.Tool >/dev/null 2>&1 || true
dotnet cake "$ROOT/scripts/validate-version.cake" --versionJson="$ROOT/version.json"

# 2. Build the freestanding kernel (managed C# + mini-CoreLib).
echo "[1/4] dotnet build Osb.Kernel.csproj ..."
dotnet build -c Release "$ROOT/Osb.Kernel.csproj" -v q -nologo

# 3. Host-side unit tests.
echo "[2/4] dotnet test ..."
if [[ -f "$ROOT/tests/unit/Osb.Kernel.Unit.csproj" ]]; then
    dotnet test -c Release "$ROOT/tests/unit/Osb.Kernel.Unit.csproj" -v q -nologo
else
    echo "  WARN: no unit test project found; skipping"
fi

# 4. Link the kernel ELF (NativeAOT + boot stubs) and validate it is a
#    Multiboot2 image with the expected ABI version constant.
echo "[3/4] link kernel.elf ..."
ILC="$(ls -1d "$HOME"/.nuget/packages/runtime.linux-x64.microsoft.dotnet.ilcompiler/*/tools/ilc 2>/dev/null | sort -V | tail -n1)"
if [[ -z "$ILC" || ! -x "$ILC" ]]; then
    echo "ERROR: ilc (NativeAOT compiler) not found." >&2; exit 1
fi
OBJ="$ROOT/obj/kernel"
mkdir -p "$OBJ"
DLL="$ROOT/bin/Release/net10.0/OsbKernel.dll"
[[ -f "$DLL" ]] || { echo "ERROR: $DLL not produced"; exit 1; }
"$ILC" "$DLL" -o "$OBJ/OsbKernel.o" \
    --systemmodule OsbKernel --targetarch x64 --targetos linux \
    -O -g --directpinvoke:osbnative --map "$OBJ/kernel.map" >/dev/null 2>&1

if [[ -z "${CC:-}" ]]; then
    if command -v clang >/dev/null 2>&1; then CC=clang
    elif command -v gcc >/dev/null 2>&1; then CC=gcc
    else echo "ERROR: neither clang nor gcc found in PATH." >&2; exit 1; fi
fi

# Generate ISR stubs (same as build.sh) and assemble all .S files.
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
    movq %rsp, %rbp
    andq $-16, %rsp
    leaq 256(%rbp), %rdi
    call osb_handle_interrupt
    movq %rbp, %rsp
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
for s in boot sched sync user probe; do
    "$CC" -c -nostdlib -ffreestanding -o "$OBJ/$s.o" "$ROOT/$s.S"
done

ld.lld -T "$ROOT/linker.ld" -o "$OBJ/kernel.elf" \
    "$OBJ/boot.o" "$OBJ/interrupts.o" "$OBJ/sched.o" "$OBJ/sync.o" "$OBJ/user.o" "$OBJ/probe.o" "$OBJ/OsbKernel.o" --no-relax >/dev/null 2>&1

echo "[4/4] validate kernel.elf (Multiboot2 + ABI version) ..."
grub-file --is-x86-multiboot2 "$OBJ/kernel.elf" >/dev/null 2>&1 \
    || { echo "ERROR: kernel.elf is not Multiboot2"; exit 1; }
EXPECTED_ABI=8
if grep -q "public const int AbiVersion = $EXPECTED_ABI;" "$ROOT/Cpu/Syscalls.cs"; then
    echo "ABI version: $EXPECTED_ABI OK (source constant verified)"
else
    echo "ERROR: ABI version mismatch in source!" >&2; exit 1
fi
echo "CI OK"
