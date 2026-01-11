#!/bin/bash

# E2E Test Visual Watching Script
#
# This script runs Playwright tests in parallel with visible browser windows
# and attempts to tile them across your screen for easy viewing.
#
# Usage:
#   ./e2e-watch.sh [number_of_workers]
#
# Example:
#   ./e2e-watch.sh 6    # Run 6 tests in parallel

set -e

# Number of parallel workers (default: 6)
WORKERS=${1:-6}

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "  E2E Visual Test Watcher"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "Running $WORKERS tests in parallel..."
echo "Browser windows will open momentarily."
echo ""
echo "NOTE: Window tiling on macOS is limited."
echo "The OS will position windows, but they may overlap."
echo ""
echo "Tips for better viewing:"
echo "  • Use macOS Mission Control to arrange windows"
echo "  • Use Rectangle app (free) for better window tiling"
echo "  • Reduce workers to 4 for larger windows"
echo ""
echo "Press Ctrl+C to stop the tests"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""

# Run tests with parallel workers
PARALLEL_TESTS=$WORKERS npm run e2e:watch

# Note: Automatic window tiling via AppleScript is unreliable because:
# 1. Playwright spawns browser windows asynchronously
# 2. Window titles/IDs are not predictable
# 3. Timing issues make it hard to catch windows as they open
#
# For best results, use a third-party window manager like:
# - Rectangle (free): https://rectangleapp.com/
# - Magnet (paid): https://magnet.crowdcafe.com/
# - BetterSnapTool (paid): https://folivora.ai/bettersnaptool
