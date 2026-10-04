# Troubleshooting Liveness Service

## curl Gets Stuck / Timeout Issue

If `curl http://localhost:8000/v1/health` hangs or times out, this is likely an IPv6/IPv4 issue.

### Solution 1: Use 127.0.0.1 instead of localhost
```bash
curl http://127.0.0.1:8000/v1/health
```

### Solution 2: Force IPv4
```bash
curl -4 http://localhost:8000/v1/health
```

### Solution 3: Use PowerShell instead
```powershell
Invoke-RestMethod -Uri "http://localhost:8000/v1/health"
```

## Why This Happens

- `localhost` resolves to both IPv6 (`::1`) and IPv4 (`127.0.0.1`)
- curl prefers IPv6 by default
- Docker Desktop on Windows may have IPv6 port binding issues
- Using `127.0.0.1` forces IPv4, which works reliably

## Verify Service is Running

```bash
# Check container status
docker ps --filter "name=beeapp-liveness"

# Check logs
docker logs beeapp-liveness

# Test from inside container (always works)
docker exec beeapp-liveness curl http://localhost:8000/v1/health
```

## Service is Accessible

The service IS accessible from your host machine. Use `127.0.0.1` instead of `localhost` to avoid IPv6 issues.
