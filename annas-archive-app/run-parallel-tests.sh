#!/bin/bash

# Parallel E2E Test Runner with Error Reporting
#
# This script runs E2E tests in parallel with visible browser windows
# and provides detailed error output in the terminal.
#
# Usage:
#   ./run-parallel-tests.sh [workers]
#
# Examples:
#   ./run-parallel-tests.sh        # Run with 6 workers (default)
#   ./run-parallel-tests.sh 4      # Run with 4 workers
#   ./run-parallel-tests.sh 8      # Run with 8 workers

set -e

# Configuration
WORKERS=${1:-6}
TIMESTAMP=$(date +%Y%m%d-%H%M%S)
LOG_FILE="parallel-e2e-${TIMESTAMP}.log"
BACKEND_PORT=5050
FRONTEND_PORT=4200
ACCESS_CODE="***REMOVED***"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color
BOLD='\033[1m'

# Track PIDs for cleanup
BACKEND_PID=""
FRONTEND_PID=""
STARTED_BACKEND=false
STARTED_FRONTEND=false

# Cleanup function
cleanup() {
    echo ""
    echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${CYAN}Cleaning up...${NC}"
    echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"

    if [ "$STARTED_FRONTEND" = true ] && [ -n "$FRONTEND_PID" ]; then
        echo -e "${YELLOW}Stopping frontend server (PID: $FRONTEND_PID)...${NC}"
        kill $FRONTEND_PID 2>/dev/null || true
        pkill -P $FRONTEND_PID 2>/dev/null || true
        sleep 1
        lsof -ti:$FRONTEND_PORT | xargs kill -9 2>/dev/null || true
    fi

    if [ "$STARTED_BACKEND" = true ] && [ -n "$BACKEND_PID" ]; then
        echo -e "${YELLOW}Stopping backend API (PID: $BACKEND_PID)...${NC}"
        kill $BACKEND_PID 2>/dev/null || true
        pkill -P $BACKEND_PID 2>/dev/null || true
        sleep 1
        lsof -ti:$BACKEND_PORT | xargs kill -9 2>/dev/null || true
    fi

    echo -e "${GREEN}Cleanup complete${NC}"
}

# Set up trap for cleanup
trap cleanup EXIT INT TERM

# Helper function to check if port is in use
is_port_in_use() {
    lsof -i:$1 >/dev/null 2>&1
}

# Helper function to wait for server
wait_for_server() {
    local url=$1
    local name=$2
    local max_wait=60

    echo -e "${YELLOW}Waiting for $name to be ready...${NC}"
    for i in $(seq 1 $max_wait); do
        if curl -s "$url" >/dev/null 2>&1; then
            echo -e "${GREEN}✓ $name is ready${NC}"
            return 0
        fi
        sleep 1
    done

    echo -e "${RED}✗ $name failed to start within ${max_wait}s${NC}"
    return 1
}

# Print header
clear
echo -e "${BOLD}${CYAN}"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "       Parallel E2E Test Runner with Visual Monitoring       "
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo -e "${NC}"
echo -e "${CYAN}Configuration:${NC}"
echo -e "  • Workers:      ${BOLD}$WORKERS${NC} parallel tests"
echo -e "  • Backend:      http://localhost:$BACKEND_PORT"
echo -e "  • Frontend:     http://localhost:$FRONTEND_PORT"
echo -e "  • Access Code:  $ACCESS_CODE"
echo -e "  • Log File:     $LOG_FILE"
echo ""
echo -e "${YELLOW}Browser windows will open - use Rectangle or Mission Control to tile them${NC}"
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

# Check and start backend API
echo -e "${BOLD}[1/3] Backend API${NC}"
if is_port_in_use $BACKEND_PORT; then
    echo -e "${GREEN}✓ Backend already running on port $BACKEND_PORT${NC}"
else
    echo -e "${YELLOW}Starting backend API...${NC}"
    cd ../annas-archive-api
    EPUB_CACHE_ROOT=../test-cache/epubs \
    AI_CACHE_ROOT=../test-cache/ai \
    Auth__AccessCodes__0__Code="$ACCESS_CODE" \
    Auth__AccessCodes__0__Name="E2E Test" \
    Auth__AccessCodes__0__IsAdmin=true \
    Auth__JwtSecret="E2ETestSecretKeyForPlaywrightTestsOnly123456789" \
    Auth__TokenExpirationDays=1 \
    E2E_LOGIN_RATE_LIMIT=100 \
    E2E_API_RATE_LIMIT=200 \
    dotnet run --project src/AnnasArchive.API/AnnasArchive.Api.csproj --urls http://localhost:$BACKEND_PORT >>"$LOG_FILE" 2>&1 &
    BACKEND_PID=$!
    STARTED_BACKEND=true
    cd ../annas-archive-app

    wait_for_server "http://localhost:$BACKEND_PORT/health" "Backend API" || exit 1
fi
echo ""

# Check and start frontend
echo -e "${BOLD}[2/3] Frontend Application${NC}"
if is_port_in_use $FRONTEND_PORT; then
    echo -e "${GREEN}✓ Frontend already running on port $FRONTEND_PORT${NC}"
else
    echo -e "${YELLOW}Starting frontend dev server...${NC}"
    npm run start >>"$LOG_FILE" 2>&1 &
    FRONTEND_PID=$!
    STARTED_FRONTEND=true

    wait_for_server "http://localhost:$FRONTEND_PORT" "Frontend" || exit 1
fi
echo ""

# Run tests
echo -e "${BOLD}[3/3] Running E2E Tests${NC}"
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""
echo -e "${YELLOW}Watch the browser windows - tests running in parallel!${NC}"
echo ""

# Run tests and capture output
TEST_OUTPUT=$(mktemp)
E2E_ACCESS_CODE="$ACCESS_CODE" PARALLEL_TESTS=$WORKERS npm run e2e 2>&1 | tee "$TEST_OUTPUT"
TEST_EXIT_CODE=${PIPESTATUS[0]}

echo ""
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"

# Check results
if [ $TEST_EXIT_CODE -eq 0 ]; then
    echo -e "${GREEN}${BOLD}"
    echo "  ✓ ALL TESTS PASSED! "
    echo -e "${NC}"
    PASSED_COUNT=$(grep -c "✓" "$TEST_OUTPUT" || echo "0")
    echo -e "${GREEN}Passed: $PASSED_COUNT tests${NC}"
else
    echo -e "${RED}${BOLD}"
    echo "  ✗ TESTS FAILED "
    echo -e "${NC}"

    # Extract failure information
    FAILED_COUNT=$(grep -c "✘" "$TEST_OUTPUT" || echo "0")
    PASSED_COUNT=$(grep -c "✓" "$TEST_OUTPUT" || echo "0")

    echo -e "${GREEN}Passed: $PASSED_COUNT tests${NC}"
    echo -e "${RED}Failed: $FAILED_COUNT tests${NC}"
    echo ""

    # Show failed test names
    echo -e "${RED}${BOLD}Failed Tests:${NC}"
    echo -e "${RED}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    grep "✘" "$TEST_OUTPUT" | sed 's/^/  /' || echo "  (Could not extract test names)"
    echo ""

    # Find and display error details
    echo -e "${YELLOW}${BOLD}Error Details:${NC}"
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"

    # Look for error context files
    ERROR_CONTEXTS=$(find test-results -name "error-context.md" 2>/dev/null || true)

    if [ -n "$ERROR_CONTEXTS" ]; then
        while IFS= read -r error_file; do
            echo ""
            echo -e "${CYAN}Error from: $(dirname "$error_file")${NC}"
            echo -e "${CYAN}────────────────────────────────────────────────────────────${NC}"
            cat "$error_file" | head -50
            echo -e "${CYAN}────────────────────────────────────────────────────────────${NC}"
        done <<< "$ERROR_CONTEXTS"
    else
        # Fallback: show error sections from test output
        awk '/Error:|Error Context:|Call log:/{flag=1} flag{print} /^[[:space:]]*$/{if(flag)flag=0}' "$TEST_OUTPUT" | head -100
    fi
    echo ""

    # Show traces if available
    TRACE_FILES=$(find test-results -name "trace.zip" 2>/dev/null | head -3 || true)
    if [ -n "$TRACE_FILES" ]; then
        echo -e "${YELLOW}${BOLD}View Detailed Traces:${NC}"
        echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
        while IFS= read -r trace_file; do
            echo -e "  ${BLUE}npx playwright show-trace $trace_file${NC}"
        done <<< "$TRACE_FILES"
        echo ""
    fi

    # Show screenshots if available
    SCREENSHOT_FILES=$(find test-results -name "test-failed-*.png" 2>/dev/null | head -3 || true)
    if [ -n "$SCREENSHOT_FILES" ]; then
        echo -e "${YELLOW}${BOLD}View Screenshots:${NC}"
        echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
        while IFS= read -r screenshot; do
            echo -e "  ${BLUE}open $screenshot${NC}"
        done <<< "$SCREENSHOT_FILES"
        echo ""
    fi
fi

echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo -e "${BOLD}Full Log:${NC} $LOG_FILE"
echo -e "${CYAN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
echo ""

# Clean up temp file
rm -f "$TEST_OUTPUT"

# Exit with test exit code
exit $TEST_EXIT_CODE
