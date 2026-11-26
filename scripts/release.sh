#!/usr/bin/env bash
# OSB release publication on master.
#
# Triggered by the merge of a release/<version> branch into master.
# Produces the official ISO, checksums, a release manifest, a Git tag and a
# GitHub Release. Fully idempotent: re-running never creates duplicate releases
# and never overwrites an existing published release.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION_JSON="$ROOT/version.json"

# ---------------------------------------------------------------------------
# 0. Validate the authoritative version metadata.
# ---------------------------------------------------------------------------
dotnet tool install --global Cake.Tool >/dev/null 2>&1 || true
dotnet cake "$ROOT/scripts/validate-version.cake" --versionJson="$VERSION_JSON"

VERSION=$(python3 -c "import json;print(json.load(open('$VERSION_JSON'))['version'])")
CODENAME=$(python3 -c "import json;d=json.load(open('$VERSION_JSON'));print(d.get('codename') or '')")
GENERATION=$(python3 -c "import json;print(json.load(open('$VERSION_JSON'))['generation'])")
TAG="v$VERSION"
if [[ -n "$CODENAME" ]]; then
    RELEASE_TITLE="OSB $VERSION — $CODENAME"
else
    RELEASE_TITLE="OSB $VERSION (generation $GENERATION, codename pending)"
fi

# ---------------------------------------------------------------------------
# 1. Must run on master (the publication branch).
# ---------------------------------------------------------------------------
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
if [[ "$BRANCH" != "master" ]]; then
    echo "ERROR: release publication must run on master (current branch: $BRANCH)" >&2
    exit 1
fi
HEAD_COMMIT="$(git rev-parse HEAD)"

# ---------------------------------------------------------------------------
# 2. Idempotency: sync remote tags, then decide publish vs no-op vs fail.
# ---------------------------------------------------------------------------
echo "Syncing remote tags ..."
git fetch origin --tags --quiet 2>/dev/null || true

REMOTE_TARGET=""
if git rev-parse --verify --quiet "refs/tags/$TAG" >/dev/null 2>&1; then
    REMOTE_TARGET="$(git rev-parse "refs/tags/$TAG^{commit}" 2>/dev/null || true)"
fi

if [[ -n "$REMOTE_TARGET" ]]; then
    if [[ "$REMOTE_TARGET" == "$HEAD_COMMIT" ]]; then
        echo "NOTE: tag $TAG already targets HEAD $HEAD_COMMIT — tag is up to date."
        TAG_ACTION="exists"
    else
        echo "ERROR: tag $TAG exists but targets $REMOTE_TARGET, not HEAD $HEAD_COMMIT." >&2
        echo "Refusing to overwrite an existing tag/release target." >&2
        exit 1
    fi
else
    TAG_ACTION="create"
fi

# ---------------------------------------------------------------------------
# 3. Ensure sibling checkouts side by side (build.sh requirement).
# ---------------------------------------------------------------------------
for sib in osb.shell osbkt osb.core osb.oslang; do
    if [[ ! -d "$ROOT/../$sib" ]]; then
        echo "  cloning sibling $sib ..."
        git clone --depth 1 "https://github.com/osb-foundation/$sib.git" "$ROOT/../$sib"
    fi
done

# ---------------------------------------------------------------------------
# 4. Build the official ISO (this is the release build, not develop CI).
# ---------------------------------------------------------------------------
echo "Building official OSB $VERSION ISO ..."
./build.sh   # produces osb.iso (+ osb.img)

# ---------------------------------------------------------------------------
# 5. Checksums + release manifest (cross-repo version coordination).
# ---------------------------------------------------------------------------
echo "Generating checksums and release manifest ..."
sha256sum osb.iso osb.img > osb.iso.sha256

MANIFEST="release-manifest-$VERSION.json"
SIB_ROOTS="$ROOT $ROOT/../osb.shell $ROOT/../osbkt $ROOT/../osb.core $ROOT/../osb.oslang"
dotnet cake "$ROOT/scripts/generate-metadata.cake" --versionJson="$VERSION_JSON" -o "$ROOT/osb/generated-metadata.json"

if [[ -n "$REMOTE_TARGET" ]]; then
    echo "Creating tag $TAG ..."
    git tag -a "$TAG" -m "OSB $VERSION (generation $GENERATION)${CODENAME:+, codename $CODENAME}

Commit: $HEAD_COMMIT
Branch: master
Component versions: see release-manifest-$VERSION.json"
    git push origin "$TAG"
    echo "  pushed tag $TAG"
else
    echo "  tag $TAG already present (idempotent)."
fi

# ---------------------------------------------------------------------------
# 7. Create the GitHub Release (never overwrite an existing one).
# ---------------------------------------------------------------------------
if command -v gh >/dev/null 2>&1; then
    if gh release view "$TAG" --repo "osb-foundation/osb.kernel" >/dev/null 2>&1; then
        echo "NOTE: GitHub release $TAG already exists — idempotent, not overwriting."
    else
        NOTES="OSB $VERSION (generation $GENERATION)."
        if [[ -n "$CODENAME" ]]; then
            NOTES="OSB $VERSION — $CODENAME (generation $GENERATION)."
        else
            NOTES="OSB $VERSION (generation $GENERATION). Codename pending final decision."
        fi
        NOTES="$NOTES

Source commit: $HEAD_COMMIT
Tag: $TAG
Branch: master
Components: see release-manifest-$VERSION.json"
        echo "Creating GitHub Release $TAG ..."
        gh release create "$TAG" --repo "osb-foundation/osb.kernel" \
            --title "$RELEASE_TITLE" --notes "$NOTES" \
            osb.iso osb.img osb.iso.sha256 "$MANIFEST"
        echo "  GitHub Release created."
    fi
else
    echo "WARN: gh CLI not available — skipping GitHub Release creation (run in GitHub Actions)."
fi

echo "RELEASE OK: $TAG ($RELEASE_TITLE)"
