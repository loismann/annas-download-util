# Anna's Archive Download Utility

A self-hosted media stack for a single household: ebook search and download,
an in-app EPUB/PDF reader with AI summaries, audiobooks, TV/movie management,
and a weekly "Date Night" movie picker.

Angular 19 frontend + .NET 8 API, deployed as a Docker Compose stack on a
UGREEN NAS and reachable only over a private Tailscale network.

## Architecture

Everything runs as one Compose stack. The app container publishes to
**loopback only** (`127.0.0.1:8080`) and is never exposed on the LAN —
`tailscale serve` proxies from the tailnet to that port, so the only way in
is from a device on your own tailnet.

| Service | Role |
| --- | --- |
| `annas-archive` | The app itself (Angular frontend + .NET 8 API in one image) |
| `gluetun` | VPN gateway. Only the Anna's Archive HTTP client and the Playwright browser route through it |
| `gluetun-torrent` | Separate VPN gateway for the torrent path, with its own region |
| `qbittorrent`, `sabnzbd` | Download clients, behind `gluetun-torrent` |
| `prowlarr`, `sonarr`, `radarr` | Indexer + TV/movie automation |
| `jellyfin`, `jellyfin-proxy` | Media server and the nginx proxy the embedded player's iframe points at |
| `plex` | Second media server (optional) |
| `audiobookshelf` | Audiobook library and streaming |
| `seq` | Structured log ingestion, internal network only |
| `autoheal` | Restarts containers that report unhealthy |

## Setup

### 1. Configuration

Two files hold all secrets. Both are gitignored — never commit a filled-in copy.

```bash
# Stack-level config (VPN, *arr API keys, Tailscale hostname)
cp .env.example .env

# App-level config (OpenAI, Dropbox, access codes, SMTP)
cp annas-archive-api/src/AnnasArchive.API/appsettings.Template.json \
   annas-archive-api/src/AnnasArchive.API/appsettings.json
```

Both templates document every field inline, including which ones are optional
and which can only be filled in *after* a first deploy (Sonarr, Radarr,
Jellyfin and Audiobookshelf each generate their own API key on first boot —
deploy once, collect the keys from their web UIs, fill them in, redeploy).

Access codes must be BCrypt hashes at cost 12. Plaintext codes are not
supported.

### 2. Tailscale

Set `TAILSCALE_HOSTNAME` in `.env` to this machine's MagicDNS name (`tailscale
status` will show it). It is used to build the Jellyfin embed URL and to scope
the CSP header that permits that iframe, so the embedded player will not work
until it is set.

Then, on the NAS:

```bash
tailscale serve --bg 8080
```

### 3. Deploy

```bash
npm run deploy:docker
```

This rsyncs the source tree to the NAS over SSH and builds there — a native
x86_64 build, avoiding cross-architecture emulation from an Apple Silicon Mac.
Logs land in `deployment-logs/` (gitignored).

The script uses `set -eo pipefail` deliberately: every remote command is piped
through `tee`, and without `pipefail` a failed remote build would be masked by
`tee`'s successful exit and reported as a successful deploy.

## Local development

```bash
# Frontend — http://localhost:4200
cd annas-archive-app && npm start

# API — http://localhost:5050
cd annas-archive-api/src/AnnasArchive.API && dotnet run
```

The frontend proxies API calls in development, so both must be running.

## Tests

```bash
npm run test:unit    # backend (xUnit) + frontend (Karma/Jasmine)
npm run test:e2e     # Playwright, interactive test selection
```

`test:e2e` starts its own API and frontend with relaxed rate limits, then
cleans up on exit (including on Ctrl+C). It needs `E2E_ACCESS_CODE` exported
in your shell.

## Documentation

- [DOCS/features/DATE_NIGHT.md](DOCS/features/DATE_NIGHT.md) — the weekly movie-picker feature
- [DOCS/reference/PROJECT_AUDIT.md](DOCS/reference/PROJECT_AUDIT.md) — repo audit; counts are unreliable, verify against source
- [DOCS/REFACTORING_TODO.md](DOCS/REFACTORING_TODO.md) — open work only; empty means everything is done
- [DOCS/ASSERTIONS_AND_ASSUMPTIONS.md](DOCS/ASSERTIONS_AND_ASSUMPTIONS.md) — what we learned and must not re-litigate
