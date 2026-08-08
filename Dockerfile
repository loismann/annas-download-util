# ─── Stage 1: build the Angular frontend ────────────────────────────────────
FROM node:20-bookworm-slim AS frontend-build
WORKDIR /src
COPY annas-archive-app/package*.json ./
RUN npm ci
COPY annas-archive-app/ ./
RUN npm run build

# ─── Stage 2: build the .NET backend ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src/annas-archive-api
# Project files only, first — NuGet restore is the slow, network-bound part
# and this way it's only re-run when a .csproj actually changes, not on
# every source edit.
COPY annas-archive-api/src/AnnasArchive.API/AnnasArchive.Api.csproj src/AnnasArchive.API/
COPY annas-archive-api/src/AnnasArchive.Core/AnnasArchive.Core.csproj src/AnnasArchive.Core/
RUN dotnet restore src/AnnasArchive.API/AnnasArchive.Api.csproj
COPY annas-archive-api/ ./
RUN dotnet publish src/AnnasArchive.API/AnnasArchive.Api.csproj -c Release -o /app/publish --no-restore

# ─── Stage 3: runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# yt-dlp (standalone binary, no Python required) + Node.js (yt-dlp's JS runtime
# for signature extraction) + curl for the download itself.
#
# Node/npm previously came from `apt-get install nodejs npm`, which is slow
# (Debian's packages pull in a huge tree of separately-packaged JS libraries)
# but reliable. An attempt to speed this up by copying Node's binaries from
# the frontend-build stage instead broke `npx` at runtime ("Cannot find
# module '../lib/cli.js'") — copying just the bin/ symlinks and
# node_modules/ isn't enough to reproduce a fully working npm install, likely
# due to how npm's own internals resolve paths relative to its real install
# location. Reverted to plain apt-get: a slow-but-working build beats a fast
# one that silently fails and leaves the old container running untouched.
#
# yt-dlp is pinned rather than tracking `releases/latest`. It ships very often and
# occasionally breaks extraction for a site; with `latest`, an unrelated rebuild
# silently changes which yt-dlp you are running, so a download that stops working
# has no obvious cause and no way back. Bump this deliberately.
ARG YTDLP_VERSION=2026.07.04
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl nodejs npm ca-certificates \
    && curl -fL "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp" -o /usr/local/bin/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*

# Playwright + Chromium for Cloudflare-bypass HTML fetching. Version must match
# the Microsoft.Playwright PackageReference in AnnasArchive.Api.csproj.
# Installed via the Node CLI (not `dotnet tool`) because this runtime image
# only has the ASP.NET runtime, not the full SDK that `dotnet tool install`
# requires — the npm-based installer downloads the same browser binaries to
# the same shared cache path (~/.cache/ms-playwright) that the .NET package
# looks up at runtime, so this satisfies both regardless of which language
# triggered the install.
#
# Browsers go to a shared path rather than root's home cache. The install needs
# root (it apt-gets Chromium's system libraries), but the app does not run as
# root — and ~/.cache/ms-playwright would then be a directory the app user
# cannot read. Setting this as ENV means both the installer and the .NET runtime
# resolve the same location.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN npx --yes playwright@1.49.0 install --with-deps chromium \
    && chmod -R a+rX /ms-playwright

COPY --from=backend-build /app/publish ./
COPY --from=frontend-build /src/dist/annas-archive-app/browser ./wwwroot

# Stamps the "Latest Version" banner's timestamp as a static asset fetched at
# runtime (see app.component.ts), instead of baking it into the compiled JS
# bundle — that used to force the whole Angular build to re-run on every
# deploy just to refresh this string. Cache-busting it here instead is nearly
# free (a single RUN, not a ~60-90s `ng build`), since it sits after the two
# expensive COPY --from stages above.
ARG BUILD_TIMESTAMP=unknown
RUN echo "Deploy: $BUILD_TIMESTAMP" \
    && BUILD_TIME=$(TZ='America/Chicago' date +"%A, %B %-d, %Y at %-I:%M %p") \
    && mkdir -p ./wwwroot/assets \
    && printf '{"buildTime":"%s CST","timezone":"America/Chicago"}' "$BUILD_TIME" > ./wwwroot/assets/version.json

# appsettings.json is never baked into the image (it's gitignored and holds
# secrets) — it must be bind-mounted into /app at runtime via docker-compose.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Runs as a normal user, not root. 1000:1000 deliberately matches the PUID/PGID
# every other service in docker-compose.yml uses and the NAS account that owns
# the bind-mounted directories — a container writing to /app/state as root is
# how those directories end up owned by root and unreadable by everything else.
#
# The port is 8080, above 1024, so no privileged bind is needed.
#
# NOTE for the first deploy after this change. Files this container created while
# it ran as root are still root-owned and need handing over once. Do NOT do that
# with a blanket `chown -R ../data`: that directory also holds immich/, plex/ and
# audiobookshelf/, which belong to other containers that run as root and would
# break. Only this service's own mounts, and only the entries actually owned by
# root:
#
#   for p in library epub-cache ai-cache youtube logs state; do
#     sudo find ../data/$p \( -user 0 -o -group 0 \) -exec chown 1000:1000 {} +
#   done
#   sudo find ~/Media/PhotoPrints ~/Media/_audiobook_staging \
#     \( -user 0 -o -group 0 \) -exec chown 1000:1000 {} +
#
# The two Media paths matter most: unlike ../data/* (which is 0777 throughout and
# would survive regardless) they are 0755 root:root, so a non-root container
# cannot write photo-print renders or rename audiobooks at all.
# Named "annas", not "app": the .NET 8 runtime images already ship a non-root
# `app` user, at UID 1654 — which does not match the bind mounts, and whose
# existence would make `groupadd app` fail the build outright.
#
# The home directory is created deliberately. Without one, Chromium reports
# "Fontconfig error: No writable cache directories" and falls back to unhinted
# font rendering — which matters here, because the CVS checkout leg screenshots
# the order review page for a human to approve. Anything else that expects a
# writable HOME (caches, profile scratch) gets one too, at no real cost.
RUN groupadd --gid 1000 annas \
    && useradd --uid 1000 --gid 1000 --create-home --shell /usr/sbin/nologin annas \
    && chown -R annas:annas /app
USER annas

ENTRYPOINT ["dotnet", "AnnasArchive.Api.dll"]
