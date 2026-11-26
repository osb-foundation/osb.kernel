#!/usr/bin/env bash
# OSB release preparation and publication (Olorun, OSB 1.1).
#
# Two modes:
#
#   DRY_RUN=1 ./scripts/release.sh   (default in this task)
#       Runs the full release *preparation* with no side effects:
#         - validates the authoritative version.json (Cake validator),
#         - validates the release branch / ref,
#         - resolves every sibling component at an explicit deterministic ref
#           (no implicit default-branch resolution, no "unknown"),
#         - generates and validates the release manifest,
#         - checks required files and the release layout,
#         - checks the built artifacts when present.
#       It NEVER creates tags, pushes, creates GitHub Releases, uploads assets,
#       publishes the ISO, merges into master or touches any remote.
#
#   ./scripts/release.sh   (publication; NOT authorized in this task)
#       Performs the same preparation and then, only on master, creates the
#       tag, pushes it and creates the GitHub Release. Publication is gated
#       behind explicit guards so a dry run can never reach it.
#
# The script is deterministic: sibling refs are recorded explicitly and the
# manifest records the exact commit of every component.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

DRY_RUN="${DRY_RUN:-0}"
VERSION_JSON="$ROOT/version.json"
COMPONENT_REPOS=(osb.kernel osb.shell osb.core osbkt osb.oslang)

log()  { printf '%s\n' "$*"; }
err()  { printf 'ERROR: %s\n' "$*" >&2; }
die()  { err "$*"; exit 1; }

# Validate a release manifest: schema, required top-level fields, exactly the
# five expected components, each with a concrete version/generation/codename/
# commit. Any "unknown"/empty field or missing component is a hard failure.
validate_manifest() {
    local mf="$1"
    [[ -f "$mf" ]] || { err "manifest not found: $mf"; return 1; }
    jq -e . "$mf" >/dev/null 2>&1 || { err "manifest is not valid JSON: $mf"; return 1; }

    local schema
    schema="$(jq -r '.schema // ""' "$mf")"
    [[ "$schema" == "osb.release.v1" ]] || { err "manifest schema '$schema' != osb.release.v1"; return 1; }

    local ver gen code commit
    ver="$(jq -r '.version // ""' "$mf")"
    gen="$(jq -r '.generation // empty' "$mf")"
    code="$(jq -r '.codename // ""' "$mf")"
    commit="$(jq -r '.commit // ""' "$mf")"
    [[ -n "$ver" ]]   || { err "manifest missing version"; return 1; }
    [[ -n "$gen" ]]   || { err "manifest missing generation"; return 1; }
    [[ -n "$commit" ]] || { err "manifest missing commit"; return 1; }

    local n
    n="$(jq '.components | length' "$mf")"
    [[ "$n" == "5" ]] || { err "manifest has $n components, expected 5"; return 1; }

    local comp
    for comp in osb.kernel osb.shell osb.core osbkt osb.oslang; do
        local entry
        entry="$(jq -c --arg c "$comp" '.components[] | select(.component==$c)' "$mf")"
        [[ -n "$entry" ]] || { err "manifest missing component '$comp'"; return 1; }
        local ev eg ec ecode ecommit
        ev="$(jq -r '.version // ""' <<<"$entry")"
        eg="$(jq -r '.generation // empty' <<<"$entry")"
        ecode="$(jq -r '.codename // ""' <<<"$entry")"
        ecommit="$(jq -r '.commit // ""' <<<"$entry")"
        [[ -n "$ev" && "$ev" != "unknown" ]]      || { err "component '$comp' has unknown/empty version"; return 1; }
        [[ -n "$eg" ]]                            || { err "component '$comp' has no generation"; return 1; }
        [[ -n "$ecode" && "$ecode" != "unknown" ]] || { err "component '$comp' has unknown/empty codename"; return 1; }
        [[ -n "$ecommit" && "$ecommit" != "unknown" ]] || { err "component '$comp' has unknown/empty commit"; return 1; }
    done
    return 0
}

# ===========================================================================
# 0. Tooling
# ===========================================================================
command -v dotnet >/dev/null 2>&1 || die "dotnet SDK required"
command -v git    >/dev/null 2>&1 || die "git required"
command -v jq     >/dev/null 2>&1 || die "jq required to build/validate the manifest"
dotnet tool install --global Cake.Tool >/dev/null 2>&1 || true

# ===========================================================================
# 1. Validate the authoritative version metadata (Cake validator).
# ===========================================================================
log "== [1/7] Validating version.json =="
dotnet cake "$ROOT/scripts/validate-version.cake" --versionJson="$VERSION_JSON" --expectCodename=Olorun \
    || die "version.json validation failed"

VERSION="$(jq -r '.version' "$VERSION_JSON")"
CODENAME="$(jq -r '.codename // ""' "$VERSION_JSON")"
GENERATION="$(jq -r '.generation' "$VERSION_JSON")"
RELEASE_FLAG="$(jq -r '.release // false' "$VERSION_JSON")"
TAG="v$VERSION"
[[ -n "$VERSION" && "$VERSION" != "null" ]] || die "could not read version from version.json"
log "   version=$VERSION generation=$GENERATION codename=${CODENAME:-<none>} release=$RELEASE_FLAG"

# ===========================================================================
# 2. Release branch / ref validation (approved Git Flow).
# ===========================================================================
log "== [2/7] Validating release ref =="
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
HEAD_COMMIT="$(git rev-parse HEAD)"
log "   branch=$BRANCH head=$HEAD_COMMIT"

# Publication is only ever allowed from master. Preparation (dry run) is
# allowed from develop, a release/<version> branch, or master, but the
# release/<version> branch name must match the version.
if [[ "$BRANCH" == release/* ]]; then
    BR_VER="${BRANCH#release/}"
    [[ "$BR_VER" == "$VERSION" ]] \
        || die "release branch '$BRANCH' does not match version '$VERSION'"
fi

# ===========================================================================
# 3. Deterministic sibling resolution (explicit refs, no "unknown").
# ===========================================================================
log "== [3/7] Resolving sibling components =="
SIB_ROOT="$ROOT/.."
declare -A SIB_COMMIT
declare -A SIB_VERSION
declare -A SIB_GENERATION
declare -A SIB_CODENAME
MANIFEST_REFS=()

resolve_sibling() {
    local comp="$1" dir="$SIB_ROOT/$1"
    [[ -d "$dir" ]] || die "sibling component '$comp' not found at $dir"
    local commit
    commit="$(git -C "$dir" rev-parse HEAD 2>/dev/null)" \
        || die "could not resolve commit for '$comp'"
    [[ -n "$commit" ]] || die "empty commit for '$comp'"

    # Verify clean working tree (no uncommitted changes)
    if ! git -C "$dir" diff --quiet 2>/dev/null || ! git -C "$dir" diff --cached --quiet 2>/dev/null; then
        die "'$comp' has uncommitted changes; release requires clean tree"
    fi

    # Verify branch: must be on release/<version> or master
    local sib_branch
    sib_branch="$(git -C "$dir" rev-parse --abbrev-ref HEAD 2>/dev/null)" \
        || die "could not resolve branch for '$comp'"
    if [[ "$sib_branch" != "master" && "$sib_branch" != "release/$VERSION" ]]; then
        die "'$comp' is on branch '$sib_branch', expected 'master' or 'release/$VERSION'"
    fi

    local vj="$dir/version.json"
    [[ -f "$vj" ]] || die "'$comp' is missing version.json at $vj"

    # Validate the sibling's metadata with the same Cake validator, requiring Olorun codename.
    dotnet cake "$ROOT/scripts/validate-version.cake" --versionJson="$vj" --expectCodename=Olorun \
        || die "'$comp' version.json failed validation (expectCodename=Olorun)"

    local cver cgen ccode ccomp
    cver="$(jq -r '.version' "$vj")"
    cgen="$(jq -r '.generation' "$vj")"
    ccode="$(jq -r '.codename // ""' "$vj")"
    ccomp="$(jq -r '.component // ""' "$vj")"
    [[ -n "$cver" && "$cver" != "null" ]] || die "'$comp' has no version"
    [[ -n "$cgen" ]] || die "'$comp' has no generation"
    [[ -n "$ccode" && "$ccode" != "unknown" && "$ccode" != "" ]] || die "'$comp' has no codename (refusing 'unknown')"
    [[ -n "$ccomp" && "$ccomp" == "$comp" ]] || die "'$comp' component field '$ccomp' does not match expected '$comp'"

    SIB_COMMIT[$comp]="$commit"
    SIB_VERSION[$comp]="$cver"
    SIB_GENERATION[$comp]="$cgen"
    SIB_CODENAME[$comp]="$ccode"
    MANIFEST_REFS+=("$comp")
    log "   $comp @ $commit  version=$cver generation=$cgen codename=$ccode branch=$sib_branch"
}

for comp in "${COMPONENT_REPOS[@]}"; do
    resolve_sibling "$comp"
done

# ===========================================================================
# 4. Manifest generation + validation.
# ===========================================================================
log "== [4/7] Generating release manifest =="
MANIFEST="$ROOT/release-manifest-$VERSION.json"

# Build the components array deterministically in COMPONENT_REPOS order.
COMPONENTS_JSON="[]"
for comp in "${COMPONENT_REPOS[@]}"; do
    ENTRY="$(jq -n \
        --arg c "$comp" \
        --arg v "${SIB_VERSION[$comp]}" \
        --argjson g "${SIB_GENERATION[$comp]}" \
        --arg code "${SIB_CODENAME[$comp]}" \
        --arg commit "${SIB_COMMIT[$comp]}" \
        '{component:$c, version:$v, generation:$g, codename:$code, commit:$commit}')"
    COMPONENTS_JSON="$(jq -n --argjson acc "$COMPONENTS_JSON" --argjson e "$ENTRY" '$acc + [$e]')"
done

jq -n \
    --arg schema "osb.release.v1" \
    --arg version "$VERSION" \
    --argjson generation "$GENERATION" \
    --arg codename "$CODENAME" \
    --arg commit "$HEAD_COMMIT" \
    --arg branch "$BRANCH" \
    --argjson components "$COMPONENTS_JSON" \
    '{schema:$schema, version:$version, generation:$generation, codename:$codename,
      commit:$commit, branch:$branch, components:$components}' \
    > "$MANIFEST"

log "   wrote $MANIFEST"
validate_manifest "$MANIFEST" || die "generated manifest failed validation"

# ===========================================================================
# 5. Required files + release layout.
# ===========================================================================
log "== [5/7] Checking required files and release layout =="
for f in version.json scripts/generate-metadata.cake scripts/validate-version.cake; do
    [[ -f "$ROOT/$f" ]] || die "required file missing: $f"
done
mkdir -p "$ROOT/obj/release"
log "   release layout prepared at $ROOT/obj/release"

# ===========================================================================
# 6. Artifact checks (only when present; a dry run does not build).
# ===========================================================================
log "== [6/7] Checking release artifacts =="
ARTIFACTS=()
for a in osb.iso osb.img; do
    if [[ -f "$ROOT/$a" ]]; then
        ARTIFACTS+=("$a")
        log "   present: $a ($(stat -c%s "$ROOT/$a") bytes)"
    else
        log "   absent (will be produced by the publication build): $a"
    fi
done

# ===========================================================================
# 7. Publication (STRUCTURALLY GATED — unreachable in a dry run).
# ===========================================================================
if [[ "$DRY_RUN" == "1" ]]; then
    log "== [7/7] DRY_RUN: skipping all publication =="
    log "DRY_RUN OK: prepared release $TAG ($VERSION, ${CODENAME:-no codename}); no side effects."
    exit 0
fi

# ---- Everything below this line performs irreversible publication. ----
log "== [7/7] Publishing release $TAG =="

# Re-assert the publication preconditions: master only, release metadata.
[[ "$BRANCH" == "master" ]] \
    || die "publication requires master (current: $BRANCH)"
[[ "$RELEASE_FLAG" == "true" ]] \
    || die "publication requires release=true in version.json"

# Idempotency: sync tags, refuse to overwrite a different target.
git fetch origin --tags --quiet 2>/dev/null || true
REMOTE_TARGET=""
if git rev-parse --verify --quiet "refs/tags/$TAG" >/dev/null 2>&1; then
    REMOTE_TARGET="$(git rev-parse "refs/tags/$TAG^{commit}" 2>/dev/null || true)"
fi
if [[ -n "$REMOTE_TARGET" && "$REMOTE_TARGET" != "$HEAD_COMMIT" ]]; then
    die "tag $TAG exists at $REMOTE_TARGET, not HEAD $HEAD_COMMIT; refusing to overwrite"
fi

# Build the official ISO + checksums (publication build).
log "Building official OSB $VERSION ISO ..."
./build.sh
sha256sum osb.iso osb.img > osb.iso.sha256
ARTIFACTS+=(osb.iso osb.img osb.iso.sha256 "$MANIFEST")

if [[ -z "$REMOTE_TARGET" ]]; then
    log "Creating tag $TAG ..."
    git tag -a "$TAG" -m "OSB $VERSION (generation $GENERATION)${CODENAME:+, codename $CODENAME}"
    git push origin "$TAG"
else
    log "tag $TAG already present (idempotent)."
fi

if command -v gh >/dev/null 2>&1; then
    if gh release view "$TAG" --repo "osb-foundation/osb.kernel" >/dev/null 2>&1; then
        log "GitHub release $TAG already exists (idempotent)."
    else
        gh release create "$TAG" --repo "osb-foundation/osb.kernel" \
            --title "OSB $VERSION — ${CODENAME:-pending}" \
            --notes "OSB $VERSION (generation $GENERATION). Source commit $HEAD_COMMIT. See release-manifest-$VERSION.json." \
            "${ARTIFACTS[@]}"
    fi
else
    err "gh CLI not available; tag created but GitHub Release skipped"
fi

log "RELEASE OK: $TAG"