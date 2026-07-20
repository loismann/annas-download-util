# Anna's Archive Download Utility

Full-stack application for managing Anna's Archive downloads with automated testing and deployment.

## Quick Start

### One-Time Setup

Add to your shell profile (`~/.zshrc` or `~/.bash_profile`):

```bash
export E2E_ACCESS_CODE=your-access-code
export SYNOLOGY_USER=your-user
export SYNOLOGY_HOST=your-host
export SYNOLOGY_PASS=your-password
```

Reload: `source ~/.zshrc`

### Commands

Run from **any directory**:

```bash
# Run E2E tests (headless, with file selection)
./scripts/test-e2e.sh

# Run unit tests only (backend + frontend)
./scripts/test-unit.sh

# Deploy with unit tests only (faster)
./scripts/deploy-unit-only.sh

# Deploy with all tests (unit + E2E)
./scripts/deploy-full.sh
```

## What These Commands Do

**test-e2e.sh** - E2E testing with file selection
- Interactive file selection - choose specific tests or run all
- Starts API (ports 5050, 5001) with test rate limits
- Starts frontend (port 4200)
- Runs selected E2E tests (including GPT-4 live tests)
- Cleans up automatically (even on Ctrl+C)

**test-unit.sh** - Unit tests only
- Runs backend unit tests (87+ tests)
- Runs frontend unit tests (103+ tests)
- Fast execution, no external dependencies
- No API/frontend startup required

**deploy-unit-only.sh** - Deploy with unit tests only
1. Runs backend unit tests (87+ tests)
2. Runs frontend unit tests (103+ tests)
3. Builds backend + deploys to Synology
4. Restarts API with production rate limits
5. Builds frontend + deploys to Synology
- Faster than full deploy, good for quick iterations

**deploy-full.sh** - Full deployment with all tests (Recommended)
1. Runs backend unit tests (87+ tests)
2. Runs frontend unit tests (103+ tests)
3. Runs E2E tests with file selection (including GPT-4 live tests)
4. Builds backend + deploys to Synology
5. Restarts API with production rate limits
6. Builds frontend + deploys to Synology
- Comprehensive validation before deployment

## Features

- **Automatic Setup**: Starts API, frontend, manages ports, creates cache directories
- **Graceful Shutdown**: Ctrl+C cleans up all processes
- **Higher Rate Limits for Testing**: API: 200/min, Login: 100/min (vs production 300/30)
- **Interactive Test Selection**: Choose specific E2E test files or run all tests
- **AI Tests**: GPT-4 live tests included with automatic retry logic for rate limiting
- **Comprehensive Logging**: All output saved to `test-logs/` or `deployment-logs/`

## Documentation

See [DOCS/DEPLOYMENT_GUIDE.md](DOCS/DEPLOYMENT_GUIDE.md) for detailed information including:
- Manual deployment steps
- Security configuration
- Synology infrastructure setup
- Troubleshooting guide

## Architecture

**Production:**
- Frontend: Angular 19 (SSR) → https://fs01pfbooks.synology.me
- API: .NET 8 → https://fs01pfbooks.synology.me:5051
- Reverse Proxy: Synology Nginx (SSL termination)
- SSL Certificate: Let's Encrypt (auto-renewed)

**Local Development:**
- Frontend: http://localhost:4200
- API: http://localhost:5050 (auth), http://localhost:5001 (API)

**Testing:**
- E2E Tests: Playwright with Chrome (5 live GPT-4 tests with retry logic)
- Unit Tests: Backend (.NET 8 xUnit) + Frontend (Karma/Jasmine)
- Total: 195+ tests (87+ backend unit, 103+ frontend unit, 5+ E2E)
