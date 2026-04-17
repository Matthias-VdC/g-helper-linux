#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/src/GHelper.Linux.csproj"

echo "=== Format C# source files ==="
echo ""

dotnet format "$PROJECT" --verbosity normal

echo ""
echo "Done."
