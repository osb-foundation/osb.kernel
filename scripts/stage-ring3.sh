#!/usr/bin/env bash
# Ring-3 program staging for osb.kernel/build.sh (F3 + review item 7).
#
# Sourced by build.sh, which then calls stage_ring3. Keeping this out of
# build.sh keeps that file under the 400-line AGENTS limit. The caller must
# have set ROOT, OBJ, OUT_IMG, CC and defined the run() helper; WITH_TESTS=1
# additionally builds the ABI test programs (default: shell only).

stage_ring3() {
    local ILC_SHELL
    ILC_SHELL="$(ls -1d "$HOME"/.nuget/packages/runtime.linux-x64.microsoft.dotnet.ilcompiler/*/tools/ilc 2>/dev/null | sort -V | tail -n1)"
    if [[ -z "$ILC_SHELL" || ! -x "$ILC_SHELL" ]]; then
        echo "ERROR: ilc (NativeAOT compiler) not found in ~/.nuget/packages." >&2
        echo "Build the project once (dotnet build) or restore the SDK to fetch it." >&2
        return 1
    fi

    # Ring-3 entry / syscall shim / runtime helpers (shared osbkt crt0).
    "$CC" -c -nostdlib -ffreestanding -o "$OBJ/userspace.o" "$ROOT/../osbkt/crt0.S"

    # compile_rt3 <module> <csproj> [staged-name] -- build one ring-3 program,
    # link it at UserBase (0x2000000) via osbkt/user.ld (osb_startup = the ilc
    # entry symbol of the module), wrap it as OSBEXE and stage it at
    # /OSB/BIN/<staged-name or module>.EXE with mode 0100755 (F3: every shipped
    # .EXE must be executable).
    compile_rt3() {
        local module="$1" csproj="$2" staged="${3:-$1}"
        echo "  building $staged.EXE ..."
        dotnet build -c Release "$csproj" -v q -nologo
        local dll
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
    }

    compile_rt3 OsbShell "$ROOT/../osb.shell/Osb.Shell.Userspace.csproj" SHELL

    # Ring-3 ABI test programs (run from the shell; see osb.shell/tests/progs).
    # Gated behind WITH_TESTS=1 so the default image ships only SHELL.EXE.
    if [[ "${WITH_TESTS:-0}" == "1" ]]; then
        for prog in TSpawn TExit TArgs TPrint TWait TPtr TSpawnErr TQuiet TId TSpin TLogout TSudo TEnv; do
            compile_rt3 "$prog" "$ROOT/../osb.shell/tests/progs/$prog/$prog.csproj"
        done
        # 26-spawnerr EACCES proof: a valid OSBEXE with mode 0100644 (no x bit)
        # at /OSB/BIN/TNX.EXE. If SpawnPath were to create a task for it, TNX
        # (a copy of TPrint) would print; the EACCES check must refuse it first.
        dbg debugfs -w -R "dump /OSB/BIN/TPrint.EXE $OBJ/TNX.dump" "$OUT_IMG"
        dbg debugfs -w -R "write $OBJ/TNX.dump /OSB/BIN/TNX.EXE" "$OUT_IMG"
        dbg debugfs -w -R "sif /OSB/BIN/TNX.EXE mode 0100644" "$OUT_IMG"
        echo "  staged /OSB/BIN/TNX.EXE (mode 0100644, non-exec)"
        # 1c identity EACCES proof: a root-owned 0700 file (a silent TQuiet
        # copy). A non-root spawn must be refused with -3; a root (sudo) spawn
        # runs it silently.
        dbg debugfs -w -R "dump /OSB/BIN/TQuiet.EXE $OBJ/ROOTONLY.dump" "$OUT_IMG"
        dbg debugfs -w -R "write $OBJ/ROOTONLY.dump /OSB/BIN/ROOTONLY.EXE" "$OUT_IMG"
        dbg debugfs -w -R "sif /OSB/BIN/ROOTONLY.EXE mode 0100700" "$OUT_IMG"
        echo "  staged /OSB/BIN/ROOTONLY.EXE (mode 0100700, root-only exec)"
    fi

    run e2fsck -fy "$OUT_IMG"

    # Report the staged .EXE modes (F3: exec bit must be set).
    echo "  .EXE modes in /OSB/BIN:"
    debugfs -R "ls -l /OSB/BIN" "$OUT_IMG" 2>/dev/null | grep -i "\.EXE" || true
}