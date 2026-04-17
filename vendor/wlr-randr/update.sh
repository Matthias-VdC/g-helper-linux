#!/usr/bin/env bash
set -euo pipefail

# Update vendored wlr-randr source to the latest upstream release.
# Usage: ./update.sh [version]
#   ./update.sh          — fetches latest tag automatically
#   ./update.sh v0.6.0   — fetches a specific version

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UPSTREAM="https://gitlab.freedesktop.org/emersion/wlr-randr.git"
CURRENT=$(cat "$SCRIPT_DIR/VERSION" 2>/dev/null || echo "unknown")

echo "wlr-randr updater"
echo "  Upstream: $UPSTREAM"
echo "  Current:  v$CURRENT"
echo ""

# Determine target version
if [[ -n "${1:-}" ]]; then
    TARGET_TAG="$1"
else
    echo "Fetching tags..."
    TARGET_TAG=$(git ls-remote --tags --sort=-v:refname "$UPSTREAM" 'refs/tags/v*' 2>/dev/null \
        | grep -v '\^{}' \
        | head -1 \
        | sed 's|.*refs/tags/||')

    if [[ -z "$TARGET_TAG" ]]; then
        echo "ERROR: Could not fetch tags from upstream."
        exit 1
    fi
fi

TARGET_VERSION="${TARGET_TAG#v}"
echo "  Latest:   $TARGET_TAG"

if [[ "$TARGET_VERSION" == "$CURRENT" ]]; then
    echo ""
    echo "Already up to date."
    exit 0
fi

echo ""
echo "Updating v$CURRENT → $TARGET_TAG ..."

# Clone the target tag
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

git clone --depth 1 --branch "$TARGET_TAG" "$UPSTREAM" "$TMP_DIR" 2>&1 | tail -1

# Copy source files
cp "$TMP_DIR/main.c" "$SCRIPT_DIR/main.c"
cp "$TMP_DIR/protocol/wlr-output-management-unstable-v1.xml" "$SCRIPT_DIR/protocol/"
cp "$TMP_DIR/LICENSE" "$SCRIPT_DIR/LICENSE"

# Update version
echo "$TARGET_VERSION" > "$SCRIPT_DIR/VERSION"

echo ""
echo "Done. Updated to $TARGET_TAG"
echo "  main.c:    $(wc -l < "$SCRIPT_DIR/main.c") lines"
echo "  protocol:  $(wc -l < "$SCRIPT_DIR/protocol/wlr-output-management-unstable-v1.xml") lines"
echo ""
echo "Next: run 'bash build.sh' to rebuild."
