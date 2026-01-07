#!/bin/bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$ROOT_DIR/annas-archive-api/src/AnnasArchive.API"
APP_DIR="$ROOT_DIR/annas-archive-app"

API_PORT=5001
APP_PORT=4200

LOG_DIR="$ROOT_DIR/local-logs"
API_LOG="$LOG_DIR/api-local.log"

API_PID=""
TAIL_PID=""
NG_PID=""

cleanup() {
    echo ""
    echo "Shutting down local services..."

    # Kill Angular dev server
    if [ -n "$NG_PID" ] && kill -0 "$NG_PID" 2>/dev/null; then
        echo "Stopping Angular (PID: $NG_PID)..."
        kill "$NG_PID" 2>/dev/null || true
        sleep 1
    fi

    # Kill tail process
    if [ -n "$TAIL_PID" ] && kill -0 "$TAIL_PID" 2>/dev/null; then
        kill "$TAIL_PID" 2>/dev/null || true
    fi

    # Kill API
    if [ -n "$API_PID" ] && kill -0 "$API_PID" 2>/dev/null; then
        echo "Stopping API (PID: $API_PID)..."
        kill "$API_PID" 2>/dev/null || true
        sleep 1
    fi

    # Force kill any remaining dotnet processes on the API port
    lsof -ti:$API_PORT | xargs kill -9 2>/dev/null || true

    # Force kill any remaining node processes on port 4200
    lsof -ti:$APP_PORT | xargs kill -9 2>/dev/null || true

    echo "All services stopped. Ports freed."
}

trap cleanup EXIT INT TERM

# Kill any existing processes on the ports before starting
echo "Checking for existing processes on ports ${API_PORT} and ${APP_PORT}..."
lsof -ti:$API_PORT | xargs kill -9 2>/dev/null && echo "Killed existing process on port ${API_PORT}" || echo "No process found on port ${API_PORT}"
lsof -ti:$APP_PORT | xargs kill -9 2>/dev/null && echo "Killed existing process on port ${APP_PORT}" || echo "No process found on port ${APP_PORT}"
sleep 1

mkdir -p "$LOG_DIR"
echo "" > "$API_LOG"

echo "========================================"
echo "Anna's Archive - Local Runner"
echo "========================================"
echo "API:       http://localhost:${API_PORT}"
echo "Frontend:  http://localhost:${APP_PORT}"
echo "API log:   ${API_LOG}"
echo ""

echo "Starting API..."
cd "$API_DIR"
ASPNETCORE_URLS="http://0.0.0.0:${API_PORT}" dotnet watch run >>"$API_LOG" 2>&1 &
API_PID=$!
cd "$ROOT_DIR"

echo "API PID:   ${API_PID}"
echo "Tailing API log..."
tail -n 50 -f "$API_LOG" | sed 's/^/[api] /' &
TAIL_PID=$!

echo ""
echo "Starting frontend..."
cd "$APP_DIR"
npx ng serve --port "$APP_PORT" &
NG_PID=$!
echo "Angular PID: ${NG_PID}"

# Wait for Angular process to finish (or be interrupted)
wait $NG_PID
