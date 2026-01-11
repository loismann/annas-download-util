#!/bin/bash

# Parallel E2E Test Runner (single frontend + per-worker API isolation)
#
# Usage:
#   ./run-parallel-tests.sh [workers]
#
# Environment:
#   E2E_ACCESS_CODE         Access code for login
#   E2E_AI_LIVE             Set to true to run live AI tests
#   API_WORKER_BASE_PORT    Base port for API workers (default: 5101)
#   AUTH_WORKER_BASE_PORT   Base port for auth workers (default: 5150)
#   FRONTEND_PORT           Frontend port (default: 4200)
#   API_PROXY_PORT          API proxy port (default: 5001)
#   AUTH_PROXY_PORT         Auth proxy port (default: 5050)

set -e
set -o pipefail

WORKERS=${1:-6}
ACCESS_CODE=${E2E_ACCESS_CODE:-}
if [ -z "$ACCESS_CODE" ]; then
  echo "[parallel-e2e] ERROR: E2E_ACCESS_CODE is required" >&2
  exit 1
fi

API_WORKER_BASE_PORT=${API_WORKER_BASE_PORT:-5101}
AUTH_WORKER_BASE_PORT=${AUTH_WORKER_BASE_PORT:-5150}
FRONTEND_PORT=${FRONTEND_PORT:-4200}
API_PROXY_PORT=${API_PROXY_PORT:-5001}
AUTH_PROXY_PORT=${AUTH_PROXY_PORT:-5050}
AI_LIVE=${E2E_AI_LIVE:-}

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DIR="$ROOT_DIR/annas-archive-app"
API_DIR="$ROOT_DIR/annas-archive-api"
LOG_DIR="$ROOT_DIR/deployment-logs/parallel-e2e-$(date +%Y%m%d-%H%M%S)"

mkdir -p "$LOG_DIR"

BACKEND_PIDS=()
FRONTEND_PID=""
PROXY_PID=""
TEST_PIDS=()

cleanup() {
  echo ""
  echo "[parallel-e2e] Cleaning up..."

  for pid in "${TEST_PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
  done

  if [ -n "$FRONTEND_PID" ]; then
    kill "$FRONTEND_PID" 2>/dev/null || true
    pkill -P "$FRONTEND_PID" 2>/dev/null || true
  fi

  if [ -n "$PROXY_PID" ]; then
    kill "$PROXY_PID" 2>/dev/null || true
  fi

  for pid in "${BACKEND_PIDS[@]}"; do
    kill "$pid" 2>/dev/null || true
    pkill -P "$pid" 2>/dev/null || true
  done

  for i in $(seq 0 $((WORKERS-1))); do
    lsof -ti:$((API_WORKER_BASE_PORT+i)) | xargs kill -9 2>/dev/null || true
    lsof -ti:$((AUTH_WORKER_BASE_PORT+i)) | xargs kill -9 2>/dev/null || true
  done
  lsof -ti:$FRONTEND_PORT | xargs kill -9 2>/dev/null || true
  lsof -ti:$API_PROXY_PORT | xargs kill -9 2>/dev/null || true
  lsof -ti:$AUTH_PROXY_PORT | xargs kill -9 2>/dev/null || true

  echo "[parallel-e2e] Cleanup complete."
}

trap cleanup EXIT INT TERM

wait_for_url() {
  local url=$1
  local name=$2
  local max_wait=${3:-60}

  for i in $(seq 1 $max_wait); do
    if curl -s "$url" >/dev/null 2>&1; then
      echo "[parallel-e2e] $name ready: $url"
      return 0
    fi
    sleep 1
  done

  echo "[parallel-e2e] ERROR: $name failed to start: $url"
  return 1
}

echo "[parallel-e2e] Starting $WORKERS worker(s)..."

# Ensure proxy/front-end ports are free
lsof -ti:$API_PROXY_PORT | xargs kill -9 2>/dev/null || true
lsof -ti:$AUTH_PROXY_PORT | xargs kill -9 2>/dev/null || true
lsof -ti:$FRONTEND_PORT | xargs kill -9 2>/dev/null || true

# Start API workers
for i in $(seq 0 $((WORKERS-1))); do
  API_PORT=$((API_WORKER_BASE_PORT+i))
  AUTH_PORT=$((AUTH_WORKER_BASE_PORT+i))

  echo "[parallel-e2e] Worker $i ports: API=$API_PORT AUTH=$AUTH_PORT"

  lsof -ti:$API_PORT | xargs kill -9 2>/dev/null || true
  lsof -ti:$AUTH_PORT | xargs kill -9 2>/dev/null || true

  API_LOG="$LOG_DIR/api-worker-$i.log"
  EPUB_CACHE_ROOT="$ROOT_DIR/test-cache/epubs/worker-$i"
  AI_CACHE_ROOT="$ROOT_DIR/test-cache/ai/worker-$i"
  LIBRARY_ROOT="$ROOT_DIR/test-cache/library/worker-$i"
  QUIZ_STORAGE_ROOT="$ROOT_DIR/test-cache/quiz/worker-$i"
  DOWNLOAD_TRACKING_PATH="$ROOT_DIR/test-cache/download-tracking/worker-$i.json"
  mkdir -p "$EPUB_CACHE_ROOT" "$AI_CACHE_ROOT" "$LIBRARY_ROOT" "$QUIZ_STORAGE_ROOT" "$(dirname "$DOWNLOAD_TRACKING_PATH")"

  (
    cd "$API_DIR"
    EPUB_CACHE_ROOT="$EPUB_CACHE_ROOT" \
    AI_CACHE_ROOT="$AI_CACHE_ROOT" \
    LIBRARY_ROOT="$LIBRARY_ROOT" \
    Quiz__StoragePath="$QUIZ_STORAGE_ROOT" \
    DownloadTracking__StoragePath="$DOWNLOAD_TRACKING_PATH" \
    Auth__AccessCodes__0__Code="$ACCESS_CODE" \
    Auth__AccessCodes__0__Name="E2E Test" \
    Auth__AccessCodes__0__IsAdmin=true \
    Auth__JwtSecret="E2ETestSecretKeyForPlaywrightTestsOnly123456789" \
    Auth__TokenExpirationDays=1 \
    E2E_LOGIN_RATE_LIMIT=100 \
    E2E_API_RATE_LIMIT=200 \
    dotnet run --project src/AnnasArchive.API/AnnasArchive.Api.csproj --urls "http://localhost:$AUTH_PORT;http://localhost:$API_PORT" >>"$API_LOG" 2>&1 &
    echo $! > "$LOG_DIR/api-worker-$i.pid"
  )
  BACKEND_PIDS+=($(cat "$LOG_DIR/api-worker-$i.pid"))

  wait_for_url "http://localhost:$API_PORT/swagger" "API worker $i" 60

done

# Start proxy (single instance)
PROXY_LOG="$LOG_DIR/e2e-proxy.log"
(
  cd "$APP_DIR"
  E2E_API_PROXY_PORT="$API_PROXY_PORT" \
  E2E_AUTH_PROXY_PORT="$AUTH_PROXY_PORT" \
  E2E_API_TARGET_BASE_PORT="$API_WORKER_BASE_PORT" \
  E2E_AUTH_TARGET_BASE_PORT="$AUTH_WORKER_BASE_PORT" \
  node scripts/e2e-proxy-server.js >>"$PROXY_LOG" 2>&1 &
  echo $! > "$LOG_DIR/e2e-proxy.pid"
)
PROXY_PID=$(cat "$LOG_DIR/e2e-proxy.pid")

wait_for_url "http://localhost:$API_PROXY_PORT/swagger" "API proxy" 30

# Start frontend (single instance)
FRONTEND_LOG="$LOG_DIR/frontend.log"
(
  cd "$APP_DIR"
  npm run start >>"$FRONTEND_LOG" 2>&1 &
  echo $! > "$LOG_DIR/frontend.pid"
)
FRONTEND_PID=$(cat "$LOG_DIR/frontend.pid")

wait_for_url "http://localhost:$FRONTEND_PORT" "Frontend" 90

echo "[parallel-e2e] Launching Playwright shards..."

for i in $(seq 0 $((WORKERS-1))); do
  SHARD="$((i+1))/$WORKERS"
  TEST_LOG="$LOG_DIR/tests-worker-$i.log"

  echo "[parallel-e2e] Worker $i test shard $SHARD -> baseURL=http://localhost:$FRONTEND_PORT (x-e2e-worker: $i)"

  (
    cd "$APP_DIR"
    E2E_BASE_URL="http://localhost:$FRONTEND_PORT" \
    E2E_ACCESS_CODE="$ACCESS_CODE" \
    E2E_WORKER_INDEX="$i" \
    E2E_AI_LIVE="$AI_LIVE" \
    PARALLEL_TESTS="$WORKERS" \
    npx playwright test --workers=1 --shard="$SHARD" --reporter=list >>"$TEST_LOG" 2>&1
  ) &
  TEST_PIDS+=($!)

done

EXIT_CODE=0
for pid in "${TEST_PIDS[@]}"; do
  if ! wait "$pid"; then
    EXIT_CODE=1
  fi
done

if [ $EXIT_CODE -eq 0 ]; then
  echo "[parallel-e2e] All shards passed. Logs: $LOG_DIR"
else
  echo "[parallel-e2e] One or more shards failed. Logs: $LOG_DIR"
fi

exit $EXIT_CODE
