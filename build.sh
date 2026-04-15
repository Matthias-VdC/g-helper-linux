#!/usr/bin/env bash
set -uo pipefail

# G-Helper Linux — Build Script
# Compiles the project as a native AOT binary and copies output to dist/

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
DIST_DIR="$SCRIPT_DIR/dist"
PUBLISH_DIR="$SRC_DIR/bin/Release/net10.0/linux-x64/publish"

echo "=== G-Helper Linux Build ==="
echo ""

# Check .NET SDK
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK not found."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install dotnet-sdk-10.0"
    echo "  Fedora:         sudo dnf install dotnet-sdk-10.0"
    echo "  Arch:           sudo pacman -S dotnet-sdk"
    exit 1
fi

SDK_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
echo "Using .NET SDK: $SDK_VERSION"

# Check for clang (required for AOT)
if ! command -v clang &>/dev/null; then
    echo ""
    echo "WARNING: clang not found. Native AOT requires clang."
    echo "Install it with:"
    echo "  Ubuntu/Debian:  sudo apt install clang zlib1g-dev"
    echo "  Fedora:         sudo dnf install clang zlib-devel"
    echo "  Arch:           sudo pacman -S clang"
    echo ""
    read -rp "Try building anyway? [y/N] " ans
    [[ "$ans" =~ ^[Yy] ]] || exit 1
fi

# Build wlr-randr (Wayland display tool — vendored v0.5.0, MIT license)
WLR_RANDR_DIR="$SCRIPT_DIR/vendor/wlr-randr"
WLR_RANDR_BIN=""

if command -v wayland-scanner &>/dev/null && command -v cc &>/dev/null; then
    WLR_VERSION=$(cat "$WLR_RANDR_DIR/VERSION" 2>/dev/null || echo "unknown")
    echo ""
    echo "Building wlr-randr v${WLR_VERSION}..."
    (
        cd "$WLR_RANDR_DIR"
        wayland-scanner client-header \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-client-protocol.h
        wayland-scanner private-code \
            protocol/wlr-output-management-unstable-v1.xml \
            wlr-output-management-unstable-v1-protocol.c
        cc -O2 -o wlr-randr main.c wlr-output-management-unstable-v1-protocol.c \
            -I. -lwayland-client -lm
        strip wlr-randr
    )
    if [[ -f "$WLR_RANDR_DIR/wlr-randr" ]]; then
        WLR_RANDR_BIN="$WLR_RANDR_DIR/wlr-randr"
        echo "  wlr-randr built: $(du -sh "$WLR_RANDR_BIN" | cut -f1)"
    else
        echo "WARNING: wlr-randr build failed (Wayland refresh rate switching unavailable)"
    fi
else
    echo ""
    echo "NOTE: wayland-scanner not found, skipping wlr-randr build."
    echo "  Install with: sudo apt install libwayland-dev"
fi

# Clean previous build artifacts
echo ""
echo "[1/4] Cleaning previous build..."
rm -rf "$SRC_DIR/bin/Release" 2>/dev/null || true

# Restore packages
echo "[2/4] Restoring packages..."
if ! dotnet restore "$SRC_DIR" --runtime linux-x64 -q; then
    echo "ERROR: Package restore failed."
    exit 1
fi

# Publish as native AOT
echo "[3/4] Compiling native AOT binary (this may take a minute)..."
dotnet publish "$SRC_DIR" -c Release --no-restore 2>&1 | grep -v "^.*error : Deleting file" || true

# Verify the binary was produced
if [[ ! -f "$PUBLISH_DIR/ghelper" ]]; then
    echo ""
    echo "ERROR: Build failed — binary not found at $PUBLISH_DIR/ghelper"
    echo "Run 'dotnet publish src/ -c Release' manually to see full errors."
    exit 1
fi

# Copy to dist/
echo "[4/4] Copying to dist/..."
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"

cp "$PUBLISH_DIR/ghelper" "$DIST_DIR/"
chmod +x "$DIST_DIR/ghelper"

# Copy native .so libraries (required at runtime, loaded from same directory)
for lib in libSkiaSharp.so libHarfBuzzSharp.so; do
    if [[ -f "$PUBLISH_DIR/$lib" ]]; then
        cp "$PUBLISH_DIR/$lib" "$DIST_DIR/"
    fi
done

# Clean wlr-randr build artifacts from vendor dir (binary is embedded in ghelper)
if [[ -n "$WLR_RANDR_BIN" ]]; then
    rm -f "$WLR_RANDR_DIR/wlr-randr" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-client-protocol.h" \
          "$WLR_RANDR_DIR/wlr-output-management-unstable-v1-protocol.c"
fi

# Summary
BINARY_SIZE=$(du -sh "$DIST_DIR/ghelper" | cut -f1)
TOTAL_SIZE=$(du -sh "$DIST_DIR" | cut -f1)
FILE_COUNT=$(ls -1 "$DIST_DIR" | wc -l)

echo ""
echo "=== Build Complete ==="
echo "  Binary:  $BINARY_SIZE  (ghelper)"
echo "  Total:   $TOTAL_SIZE  ($FILE_COUNT files)"
echo "  Output:  $DIST_DIR/"
echo ""
echo "Run it:"
echo "  $DIST_DIR/ghelper"
