# ─── Stage 1: build the Angular frontend ────────────────────────────────────
FROM node:20-bookworm-slim AS frontend-build
WORKDIR /src
COPY annas-archive-app/package*.json ./
RUN npm ci
COPY annas-archive-app/ ./
# Forces this layer to re-run on every deploy (the "Latest Version" banner is
# stamped here via generate-version.js), regardless of whether frontend
# source actually changed — npm ci above still caches normally since this
# ARG is declared after it.
ARG BUILD_TIMESTAMP=unknown
RUN echo "Build: $BUILD_TIMESTAMP" && npm run build

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
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl nodejs npm ca-certificates \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp \
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
RUN npx --yes playwright@1.49.0 install --with-deps chromium

COPY --from=backend-build /app/publish ./
COPY --from=frontend-build /src/dist/annas-archive-app/browser ./wwwroot

# appsettings.json is never baked into the image (it's gitignored and holds
# secrets) — it must be bind-mounted into /app at runtime via docker-compose.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AnnasArchive.Api.dll"]
