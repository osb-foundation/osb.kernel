#!/usr/bin/env python3
"""OSB SemVer + codename validation.

Validates an OSB component version.json against the OSB versioning policy
(Trello card 1.1). This is the single source of truth for version parsing,
SemVer rules and generation codename lookup used by CI and release workflows.

Exit codes:
  0 - ok
  1 - invalid input / policy violation
  2 - usage error
"""

import argparse
import json
import re
import sys

SEMVER_RE = re.compile(
    r"^(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)\.(?P<patch>0|[1-9]\d*)"
    r"(?:-(?P<prerelease>[0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*))?"
    r"(?:\+(?P<build>[0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*))?$"
)

# Generation codename registry. Generation 0 is intentionally unresolved:
# the codename is null until the project owner records an explicit final
# decision. Candidates are documented but NOT hardcoded as definitive.
GENERATION_CODENAMES = {
    0: {
        "codename": None,
        "candidates": ["Olorun", "Ptah"],
        "note": "Generation 0 codename pending explicit final decision.",
    },
}


def fail(message):
    print(f"ERROR: {message}", file=sys.stderr)
    return 1


def parse_version(version):
    m = SEMVER_RE.match(version or "")
    if not m:
        return None
    return {
        "major": int(m.group("major")),
        "minor": int(m.group("minor")),
        "patch": int(m.group("patch")),
        "prerelease": m.group("prerelease"),
        "build": m.group("build"),
    }


def prerelease_parts(prerelease):
    if not prerelease:
        return []
    return prerelease.split(".")


def compare_prerelease(a, b):
    """Compare two prerelease identifiers lists per SemVer 11.2/11.3."""
    for xa, xb in zip(a, b):
        a_num = xa.isdigit()
        b_num = xb.isdigit()
        if a_num and b_num:
            ia, ib = int(xa), int(xb)
            if ia != ib:
                return -1 if ia < ib else 1
        elif a_num:
            return -1  # numeric < alphanumeric
        elif b_num:
            return 1
        else:
            if xa != xb:
                return -1 if xa < xb else 1
    if len(a) != len(b):
        return -1 if len(a) < len(b) else 1
    return 0


def compare(v1, v2):
    """Compare two parsed versions per SemVer 11. Returns -1/0/1."""
    for field in ("major", "minor", "patch"):
        if v1[field] != v2[field]:
            return -1 if v1[field] < v2[field] else 1
    p1, p2 = v1["prerelease"], v2["prerelease"]
    if p1 is None and p2 is None:
        return 0
    if p1 is None:
        return 1  # release > prerelease
    if p2 is None:
        return -1
    return compare_prerelease(prerelease_parts(p1), prerelease_parts(p2))


def validate_semver(version):
    parsed = parse_version(version)
    if parsed is None:
        return fail(f"'{version}' is not valid SemVer (MAJOR.MINOR.PATCH).")
    if parsed["major"] == 0:
        # 0.y.z: API unstable; MINOR increments may be incompatible. Informational.
        pass
    return 0, parsed


def codename_for_generation(generation):
    entry = GENERATION_CODENAMES.get(generation)
    if entry is None:
        return fail(f"no codename registry entry for generation {generation}"), None
    return 0, entry


def main(argv=None):
    parser = argparse.ArgumentParser(description="Validate OSB version.json metadata.")
    parser.add_argument("version_json", help="Path to a version.json file.")
    parser.add_argument(
        "--expect-generation",
        type=int,
        help="Expected generation number (validated against the file).",
    )
    parser.add_argument(
        "--expect-codename",
        help="Expected codename for the generation (null accepted if pending).",
    )
    parser.add_argument(
        "--on-branch",
        help="Current branch name; used to validate release/<version> consistency.",
    )
    parser.add_argument(
        "--strict-release",
        action="store_true",
        help="Require release=true and a resolved (non-null) codename.",
    )
    args = parser.parse_args(argv)

    try:
        with open(args.version_json, "r", encoding="utf-8") as f:
            data = json.load(f)
    except FileNotFoundError:
        return fail(f"version.json not found: {args.version_json}")
    except json.JSONDecodeError as e:
        return fail(f"invalid JSON in {args.version_json}: {e}")

    component = data.get("component")
    version = data.get("version")
    generation = data.get("generation")
    codename = data.get("codename")
    release = data.get("release", False)

    if not component or not isinstance(component, str):
        return fail("version.json missing 'component'.")
    if not version:
        return fail("version.json missing 'version'.")

    rc, parsed = validate_semver(version)
    if rc != 0:
        return rc

    if args.expect_generation is not None and generation != args.expect_generation:
        return fail(
            f"generation mismatch: file={generation}, expected={args.expect_generation}"
        )

    if args.expect_codename is not None:
        if codename != args.expect_codename:
            return fail(
                f"codename mismatch: file={codename!r}, expected={args.expect_codename!r}"
            )

    if args.strict_release:
        if release is not True:
            return fail("strict-release requires release=true.")
        if codename is None:
            return fail("strict-release requires a resolved codename (not null).")

    if args.on_branch:
        prefix = "release/"
        if args.on_branch.startswith(prefix):
            branch_version = args.on_branch[len(prefix):]
            if branch_version != version:
                return fail(
                    f"release branch {args.on_branch} version {branch_version} "
                    f"does not match version.json {version}"
                )

    # Codename lookup by generation (registry).
    rc, entry = codename_for_generation(generation)
    if rc != 0:
        return rc
    registry_codename = entry["codename"]
    if codename is not None and codename != registry_codename:
        return fail(
            f"codename {codename!r} does not match registry for generation {generation} "
            f"({registry_codename!r})"
        )

    print(
        f"OK: {component} {version} (generation {generation}, "
        f"codename={codename!r}, release={release})"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
