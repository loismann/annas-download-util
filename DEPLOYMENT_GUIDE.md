# Anna's Archive Download Utility - Deployment Guide

## Table of Contents
1. [Local Development](#local-development)
2. [Killing Existing Processes](#killing-existing-processes)
3. [Building for Production](#building-for-production)
4. [Deploying to Synology](#deploying-to-synology)
5. [Auto-Start on Synology Reboot](#auto-start-on-synology-reboot)
6. [Troubleshooting](#troubleshooting)

---

## Local Development

### Prerequisites
- .NET SDK 8.0 or higher
- Node.js and npm
- Git

### Running the Application

You need **two separate terminal windows** to run the full application locally:

#### Terminal 1 - Backend API (runs on http://localhost:5001)

```bash
cd /Users/paulferrer/Documents/personal_dev/annas-download-util/annas-archive-api
dotnet run --project src/AnnasArchive.API/AnnasArchive.Api.csproj
```

The API will start and display:
```
Now listening on: http://localhost:5001
```

#### Terminal 2 - Frontend App (runs on http://localhost:4200)

```bash
cd /Users/paulferrer/Documents/personal_dev/annas-download-util/annas-archive-app
npm start
```

### Accessing the Application

1. Open your browser and navigate to: **http://localhost:4200**
2. Login with an access code:
   - **paul-7285** (Admin access)
   - **test-1234** (Test user access)

### Configuration Files

- API configuration: `annas-archive-api/src/AnnasArchive.API/appsettings.json`
- Frontend proxy config: `annas-archive-app/proxy.conf.json` (routes `/api` to `http://localhost:5001`)

---

## Killing Existing Processes

### Local Machine (macOS/Linux)

Kill the API process:
```bash
pkill -f "AnnasArchive.Api.dll" || true
```

Kill the frontend development server:
```bash
pkill -f "ng serve" || true
```

Or kill all Node and .NET processes (use with caution):
```bash
killall node
killall dotnet
```

### Synology NAS

SSH into your Synology and kill the API:
```bash
ssh pferrer@FS01pfBooks.synology.me
pkill -f "AnnasArchive.Api.dll" || true
```

Or find and kill specific processes:
```bash
# Find the process ID
ps aux | grep AnnasArchive.Api.dll

# Kill by PID
kill <PID>
```

---

## Building for Production

### 1. Build the Frontend

```bash
cd /Users/paulferrer/Documents/personal_dev/annas-download-util/annas-archive-app
npm run build
```

This creates a production build in `dist/annas-archive-app/browser/`.

### 2. Build and Publish the Backend

```bash
cd /Users/paulferrer/Documents/personal_dev/annas-download-util/annas-archive-api
dotnet publish src/AnnasArchive.API/AnnasArchive.Api.csproj \
    -c Release \
    -o publish
```

This creates a self-contained deployment in `annas-archive-api/publish/`.

**Important files to include:**
- `publish/AnnasArchive.Api.dll` (and all DLL dependencies)
- `publish/appsettings.json` (configuration)
- `publish/oauth-client.json` (Google OAuth credentials)
- `publish/oauth-token.json` (if previously authenticated)

---

## Deploying to Synology

### Deployment Directory Structure

Your Synology should have the following structure:
```
/volume1/web/annas-api/
└── api/
    ├── AnnasArchive.Api.dll
    ├── appsettings.json
    ├── oauth-client.json
    ├── oauth-token.json (created after OAuth)
    ├── (all other DLLs and dependencies)
    └── annas-api.log (created when app runs)
```

### Deployment Steps

1. **Build the API** (see [Building for Production](#building-for-production))

2. **Transfer files to Synology manually**:
   - Open Finder and connect to your Synology via SMB/AFP
   - Navigate to `/volume1/web/annas-api/api`
   - Copy all files from `annas-archive-api/publish/` to the Synology directory
   - Ensure `appsettings.json` and `oauth-client.json` are present

3. **Set proper permissions** (via SSH):
   ```bash
   ssh pferrer@FS01pfBooks.synology.me
   cd /volume1/web/annas-api/api
   chmod +x AnnasArchive.Api.dll
   ```

4. **Start the application**:
   ```bash
   cd /volume1/web/annas-api/api
   pkill -f "AnnasArchive.Api.dll" || true
   nohup /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll \
       --urls http://0.0.0.0:5050 \
       > annas-api.log 2>&1 &
   echo "Launched AnnasArchive.Api (PID $!) – logging to annas-api.log"
   ```

5. **Verify it's running**:
   ```bash
   tail -f annas-api.log
   # Or
   curl http://localhost:5050/api/anna/book?name=test
   ```

### One-Liner Deployment Command

```bash
cd /volume1/web/annas-api/api && \
pkill -f "AnnasArchive.Api.dll" || true && \
nohup /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll \
    --urls http://0.0.0.0:5050 \
    > annas-api.log 2>&1 & \
echo "Launched AnnasArchive.Api (PID $!) - logging to annas-api.log"
```

---

## Auto-Start on Synology Reboot

When your Synology NAS reboots, the app will stop. You need to set up an auto-start script.

### Option 1: Using Task Scheduler (Recommended)

1. **Log into Synology DSM Web Interface**
2. Open **Control Panel** → **Task Scheduler**
3. Click **Create** → **Triggered Task** → **User-defined script**
4. Configure the task:
   - **General Tab**:
     - Task name: `Start Anna's Archive API`
     - User: `pferrer` (or your user)
     - Event: `Boot-up`
   - **Task Settings Tab**:
     - Paste this script:
       ```bash
       sleep 30
       cd /volume1/web/annas-api/api
       /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll \
           --urls http://0.0.0.0:5050 \
           > annas-api.log 2>&1 &
       ```
5. Click **OK** to save

The `sleep 30` ensures the system is fully booted before starting the app.

### Option 2: Using rc.local Script (Advanced)

**Warning**: Synology strongly advises against running commands as root. Use with caution.

1. SSH into your Synology as root:
   ```bash
   ssh root@FS01pfBooks.synology.me
   ```

2. Edit the rc.local file:
   ```bash
   vi /usr/local/etc/rc.d/annas-api.sh
   ```

3. Add the following content:
   ```bash
   #!/bin/sh

   sleep 30
   cd /volume1/web/annas-api/api
   /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll \
       --urls http://0.0.0.0:5050 \
       > annas-api.log 2>&1 &
   ```

4. Make it executable:
   ```bash
   chmod +x /usr/local/etc/rc.d/annas-api.sh
   ```

**Note**: This script may be removed during DSM updates. Task Scheduler is more reliable.

---

## Troubleshooting

### Common Issues

#### 1. "Aborted (core dumped)" Error

This usually indicates a .NET runtime incompatibility. Ensure:
- You're using .NET 8.0 on the Synology
- The Synology dotnet8-runtime package is installed
- Your publish uses the correct runtime

Check runtime:
```bash
/volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet --version
```

#### 2. App Dies Immediately After Starting

Check the log file:
```bash
tail -50 /volume1/web/annas-api/api/annas-api.log
```

Common causes:
- Missing `appsettings.json`
- Invalid configuration values
- Port 5050 already in use

#### 3. Can't Find Process

List all .NET processes:
```bash
ps aux | grep dotnet
```

#### 4. Port Already in Use

Find what's using port 5050:
```bash
lsof -i :5050
# Or
netstat -tulpn | grep 5050
```

Kill the process:
```bash
kill -9 <PID>
```

#### 5. OAuth Token Issues

If Google Drive features aren't working:
1. Navigate to `http://FS01pfBooks.synology.me:5050/api/auth/google`
2. Complete the OAuth flow
3. The token will be saved to `oauth-token.json`

### Viewing Logs

Real-time log monitoring:
```bash
ssh pferrer@FS01pfBooks.synology.me
tail -f /volume1/web/annas-api/api/annas-api.log
```

### Health Check

Test if the API is responding:
```bash
curl http://FS01pfBooks.synology.me:5050/api/anna/book?name=dune
```

---

## Environment-Specific Settings

### Local Development
- API URL: `http://localhost:5001`
- Frontend URL: `http://localhost:4200`
- Login: Use access code `paul-7285` or `test-1234`

### Synology Production
- API URL: `http://FS01pfBooks.synology.me:5050`
- No frontend (API only)
- Access via direct API calls or configure your frontend to point to this URL

---

## Security Notes

1. **Never commit sensitive files**:
   - `appsettings.json` (contains JWT secrets, API keys, email credentials)
   - `oauth-client.json` (Google OAuth credentials)
   - `oauth-token.json` (Google OAuth tokens)

2. **Change default secrets** in production:
   - Update `Auth:JwtSecret` to a strong random value (at least 32 characters)
   - Update access codes regularly
   - Use environment-specific `appsettings.{Environment}.json` files

3. **Protect your Synology**:
   - Use strong SSH passwords
   - Enable firewall rules
   - Restrict port 5050 to trusted IPs if possible

---

## Quick Reference Commands

### Local Development
```bash
# Start API
cd annas-archive-api && dotnet run --project src/AnnasArchive.API/AnnasArchive.Api.csproj

# Start Frontend
cd annas-archive-app && npm start
```

### Build for Production
```bash
# Frontend
cd annas-archive-app && npm run build

# Backend
cd annas-archive-api && dotnet publish src/AnnasArchive.API/AnnasArchive.Api.csproj -c Release -o publish
```

### Synology Deployment
```bash
# One-liner to deploy and start
cd /volume1/web/annas-api/api && pkill -f "AnnasArchive.Api.dll" || true && nohup /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll --urls http://0.0.0.0:5050 > annas-api.log 2>&1 & echo "Launched AnnasArchive.Api (PID $!) - logging to annas-api.log"

# View logs
tail -f /volume1/web/annas-api/api/annas-api.log

# Check if running
ps aux | grep AnnasArchive
```


Paul Notes: 

Single Command Via SSH to turn on the app after a restart:
log in first:
ssh pferrer@FS01pfBooks.synology.me

then run:
cd /volume1/web/annas-api/api && pkill -f "AnnasArchive.Api.dll" || true && nohup /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll --urls http://0.0.0.0:5050 > annas-api.log 2>&1 & echo "Launched AnnasArchive.Api (PID $!)"

In order to see live logs use:
tail -f /volume1/web/annas-api/api/annas-api.log

Command to kill any existing processes, restart the api, wait 2 seconds, show last 30 lines of logs including startup messages:
cd /volume1/web/annas-api/api && pkill -f "AnnasArchive.Api.dll" || true && nohup /volume1/@appstore/dotnet8-runtime/share/dotnet/dotnet AnnasArchive.Api.dll --urls http://0.0.0.0:5050 > annas-api.log 2>&1 & echo "Launched AnnasArchive.Api (PID $!)" && sleep 2 && tail -30 annas-api.log

How to copy auth token from mac to server when needed:
cat /Users/paulferrer/Documents/personal_dev/annas-download-util/annas-archive-api/src/AnnasArchive.API/oauth-token.json | ssh pferrer@FS01pfBooks.synology.me "cat > /volume1/web/annas-api/api/oauth-token.json"