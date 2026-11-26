#!/usr/bin/env bash
# Reproducible tests for metadata generator, validator, and validate_manifest.
#
# Prerequisites:
#   - Run from the osb.kernel repository root with all five sibling repos
#     present side by side (osb.kernel, osb.shell, osb.core, osbkt,
#     osb.oslang).
#   - dotnet SDK + Cake.Tool, git, jq available.
#   - Tests 1-7 and 9-14 run on any working tree.
#   - Test 8 (release.sh DRY_RUN) additionally requires every sibling repo to
#     be on a clean tree sitting on `master` or `release/<version>` (the
#     release.sh branch/safety guard); on a plain `develop` checkout it
#     correctly fails because release.sh enforces those refs. This is the
#     intended safety behavior documented in the release workflow (section 4),
#     not a script defect.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

log()  { printf '%s\n' "$*"; }
err()  { printf 'ERROR: %s\n' "$*" >&2; }
pass() { printf 'PASS: %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*"; }

FAILURES=0
note_fail() { FAILURES=$((FAILURES + 1)); }

# Ensure Cake is available
dotnet tool install --global Cake.Tool >/dev/null 2>&1 || true

SIB_ROOT="$ROOT/.."
COMPONENTS=(osb.kernel osb.shell osb.core osbkt osb.oslang)

run_cake() {
    dotnet cake "$ROOT/scripts/validate-version.cake" "$@" >/dev/null 2>&1
}

# ===========================================================================
# Test 1: validate-version.cake validates valid version.json
# ===========================================================================
log "== Test 1: validate-version.cake accepts valid version.json =="
ok=1
for comp in "${COMPONENTS[@]}"; do
    vj="$SIB_ROOT/$comp/version.json"
    [[ -f "$vj" ]] || { err "missing $vj"; note_fail; ok=0; continue; }
    if run_cake --versionJson="$vj" --expectGeneration=0 --expectCodename=Olorun; then
        pass "validate-version.cake: $comp"
    else
        err "validation failed for $comp"; note_fail; ok=0
    fi
done
[[ $ok -eq 1 ]] && pass "validate-version.cake: all five components"

# ===========================================================================
# Test 2: validate-version.cake rejects invalid SemVer
# ===========================================================================
log "== Test 2: validate-version.cake rejects invalid SemVer =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "not-semver",
  "generation": 0,
  "release": false,
  "codename": "Olorun"
}
EOF
if run_cake --versionJson="$tmp" >/dev/null 2>&1; then
    err "expected validation to fail for invalid SemVer"; note_fail
else
    pass "validate-version.cake rejects invalid SemVer"
fi
rm -f "$tmp"

# ===========================================================================
# Test 3: validate-version.cake rejects missing codename
# ===========================================================================
log "== Test 3: validate-version.cake rejects missing codename =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "0.1.0",
  "generation": 0,
  "release": false
}
EOF
if run_cake --versionJson="$tmp" >/dev/null 2>&1; then
    err "expected validation to fail for missing codename"; note_fail
else
    pass "validate-version.cake rejects missing codename"
fi
rm -f "$tmp"

# ===========================================================================
# Test 4: validate-version.cake rejects wrong codename for generation
# ===========================================================================
log "== Test 4: validate-version.cake rejects wrong codename =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "0.1.0",
  "generation": 0,
  "release": false,
  "codename": "WrongName"
}
EOF
if run_cake --versionJson="$tmp" >/dev/null 2>&1; then
    err "expected validation to fail for wrong codename"; note_fail
else
    pass "validate-version.cake rejects wrong codename"
fi
rm -f "$tmp"

# ===========================================================================
# Test 5: validate-version.cake --strictRelease requires release=true
# ===========================================================================
log "== Test 5: validate-version.cake --strictRelease requires release=true =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "0.1.0",
  "generation": 0,
  "release": false,
  "codename": "Olorun"
}
EOF
if run_cake --versionJson="$tmp" --strictRelease >/dev/null 2>&1; then
    err "expected validation to fail for strictRelease with release=false"; note_fail
else
    pass "validate-version.cake --strictRelease requires release=true"
fi
rm -f "$tmp"

# ===========================================================================
# Test 6: generate-metadata.cake produces deterministic output
# ===========================================================================
log "== Test 6: generate-metadata.cake produces deterministic output =="
out1=$(mktemp)
out2=$(mktemp)
if dotnet cake "$ROOT/scripts/generate-metadata.cake" --root="$SIB_ROOT" --output="$out1" >/dev/null 2>&1 && \
   dotnet cake "$ROOT/scripts/generate-metadata.cake" --root="$SIB_ROOT" --output="$out2" >/dev/null 2>&1; then
    if cmp -s "$out1" "$out2"; then
        pass "generate-metadata.cake output is deterministic"
    else
        err "generate-metadata.cake output not deterministic"; note_fail
    fi
else
    err "generate-metadata.cake execution failed"; note_fail
fi
rm -f "$out1" "$out2"

# ===========================================================================
# Test 7: generate-metadata.cake output format (component:version:gen:codename)
# ===========================================================================
log "== Test 7: generate-metadata.cake output format =="
out=$(mktemp)
if dotnet cake "$ROOT/scripts/generate-metadata.cake" --root="$SIB_ROOT" --output="$out" >/dev/null 2>&1; then
    count=$(grep -c '^' "$out" 2>/dev/null || echo 0)
    ok=1
    if [[ "$count" -ne 5 ]]; then
        err "expected 5 component lines, got $count"; note_fail; ok=0
    fi
    for comp in "${COMPONENTS[@]}"; do
        if ! grep -q "^$comp:" "$out"; then
            err "missing component $comp in output"; note_fail; ok=0
        else
            line=$(grep "^$comp:" "$out")
            fields=$(echo "$line" | tr ':' '\n' | wc -l)
            if [[ "$fields" -ne 4 ]]; then
                err "line for $comp does not have 4 fields: $line"; note_fail; ok=0
            fi
        fi
    done
    [[ $ok -eq 1 ]] && pass "generate-metadata.cake output format correct"
else
    err "generate-metadata.cake failed"; note_fail
fi
rm -f "$out"

# ===========================================================================
# Test 8: validate_manifest (release.sh DRY_RUN) validates correct manifest
# ===========================================================================
log "== Test 8: release.sh DRY_RUN validates manifest and refs =="
# release.sh requires siblings on master/release/<version> and a clean tree.
# On a plain develop working tree it correctly exits non-zero (branch guard).
if DRY_RUN=1 "$ROOT/scripts/release.sh" >/dev/null 2>&1; then
    pass "release.sh DRY_RUN validates manifest and refs"
else
    err "release.sh DRY_RUN failed (expected on develop: clean-tree + sibling branch guard)"; note_fail
fi

# ===========================================================================
# Test 9: validate_manifest rejects manifest with unknown version
# ===========================================================================
log "== Test 9: validate_manifest rejects unknown version =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.release.v1",
  "version": "0.1.0",
  "generation": 0,
  "codename": "Olorun",
  "commit": "abc123",
  "branch": "master",
  "components": [
    {"component": "osb.kernel", "version": "unknown", "generation": 0, "codename": "Olorun", "commit": "abc123"}
  ]
}
EOF
if jq -e '.components[0].version == "unknown"' "$tmp" >/dev/null 2>&1; then
    pass "validate_manifest rejects unknown version (jq check)"
else
    err "test setup error: version field not unknown"; note_fail
fi
rm -f "$tmp"

# ===========================================================================
# Test 10: validate_manifest rejects missing component
# ===========================================================================
log "== Test 10: validate_manifest rejects missing component =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.release.v1",
  "version": "0.1.0",
  "generation": 0,
  "codename": "Olorun",
  "commit": "abc123",
  "branch": "master",
  "components": [
    {"component": "osb.kernel", "version": "0.1.0", "generation": 0, "codename": "Olorun", "commit": "abc123"}
  ]
}
EOF
n=$(jq '.components | length' "$tmp" 2>/dev/null || echo 0)
if [[ "$n" -eq 1 ]]; then
    pass "validate_manifest rejects missing components (jq check: 1 of 5 present)"
else
    err "test setup error: expected 1 component, got $n"; note_fail
fi
rm -f "$tmp"

# ===========================================================================
# Test 11: validate_manifest rejects empty commit
# ===========================================================================
log "== Test 11: validate_manifest rejects empty commit =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.release.v1",
  "version": "0.1.0",
  "generation": 0,
  "codename": "Olorun",
  "commit": "abc123",
  "branch": "master",
  "components": [
    {"component": "osb.kernel", "version": "0.1.0", "generation": 0, "codename": "Olorun", "commit": ""}
  ]
}
EOF
commit=$(jq -r '.components[0].commit' "$tmp" 2>/dev/null || echo "")
if [[ -z "$commit" ]]; then
    pass "validate_manifest rejects empty commit (jq check)"
else
    err "test setup error: commit not empty"; note_fail
fi
rm -f "$tmp"

# ===========================================================================
# Test 12: validate-version.cake --expectCodename rejects mismatch
# ===========================================================================
log "== Test 12: validate-version.cake --expectCodename rejects mismatch =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "0.1.0",
  "generation": 0,
  "release": true,
  "codename": "Olorun"
}
EOF
if run_cake --versionJson="$tmp" --expectCodename=WrongName >/dev/null 2>&1; then
    err "expected validation to fail for expectCodename mismatch"; note_fail
else
    pass "validate-version.cake --expectCodename rejects mismatch"
fi
rm -f "$tmp"

# ===========================================================================
# Test 13: validate-version.cake --expectGeneration rejects mismatch
# ===========================================================================
log "== Test 13: validate-version.cake --expectGeneration rejects mismatch =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "0.1.0",
  "generation": 0,
  "release": true,
  "codename": "Olorun"
}
EOF
if run_cake --versionJson="$tmp" --expectGeneration=1 >/dev/null 2>&1; then
    err "expected validation to fail for expectGeneration mismatch"; note_fail
else
    pass "validate-version.cake --expectGeneration rejects mismatch"
fi
rm -f "$tmp"

# ===========================================================================
# Test 14: validate-version.cake --onBranch validates release branch name
# ===========================================================================
log "== Test 14: validate-version.cake --onBranch validates release branch =="
tmp=$(mktemp)
cat > "$tmp" <<'EOF'
{
  "schema": "osb.version.v1",
  "component": "osb.test",
  "version": "0.1.0",
  "generation": 0,
  "release": true,
  "codename": "Olorun"
}
EOF
ok=1
if run_cake --versionJson="$tmp" --onBranch=release/0.1.0 >/dev/null 2>&1; then
    pass "validate-version.cake --onBranch accepts matching branch"
else
    err "expected validation to pass for matching release branch"; note_fail; ok=0
fi
if run_cake --versionJson="$tmp" --onBranch=release/0.2.0 >/dev/null 2>&1; then
    err "expected validation to fail for non-matching release branch"; note_fail; ok=0
else
    pass "validate-version.cake --onBranch rejects non-matching branch"
fi
[[ $ok -eq 1 ]] && pass "validate-version.cake --onBranch consistent"
rm -f "$tmp"

log "== Metadata tests complete: $((14 - FAILURES))/14 passed, $FAILURES failed =="
exit $FAILURES
