# Local Development Setup with Docker Compose

This guide explains how to run the Bee Logistics backend locally with hot-reload using Docker Compose.

## Prerequisites

- Docker Desktop (Windows/Mac) or Docker Engine + Docker Compose (Linux)
- .NET SDK 10.0 (optional, for local development without Docker)

## Quick Start

1. **Start all services:**
   ```bash
   docker compose -f compose.dev.yml up
   ```

2. **Start in detached mode (background):**
   ```bash
   docker compose -f compose.dev.yml up -d
   ```

3. **View logs:**
   ```bash
   docker compose -f compose.dev.yml logs -f backend
   ```

4. **Stop all services:**
   ```bash
   docker compose -f compose.dev.yml down
   ```

5. **Stop and remove volumes (clean slate):**
   ```bash
   docker compose -f compose.dev.yml down -v
   ```

## Services Included

| Service | Port | Description | Access URL |
|---------|------|-------------|------------|
| **Backend API** | 5248 | ASP.NET Core API with hot reload | http://localhost:5248 |
| **PostgreSQL** | - | Database (external - not included) | Configure separately |
| **Redis** | 6379 | Cache | localhost:6379 |
| **RabbitMQ** | 5672 | Message broker | localhost:5672 |
| **RabbitMQ UI** | 15672 | Management dashboard | http://localhost:15672 (guest/guest) |
| **RedisInsight** | 5540 | Redis dashboard | http://localhost:5540 |
| **MailHog** | 8025 | Email testing UI | http://localhost:8025 |

**Note:** PostgreSQL is **not included** in this compose file. You need to configure an external PostgreSQL instance separately.

## Hot Reload / Auto-Refresh

The backend service uses `dotnet watch` which automatically:
- ✅ Detects changes to `.cs` files
- ✅ Rebuilds the project
- ✅ Restarts the application
- ✅ Preserves application state (no data loss)

**How it works:**
- Source code is mounted as a volume (`./src:/app/src`)
- `dotnet watch` monitors file changes
- Changes trigger automatic rebuild and restart

**Note:** First build may take 2-3 minutes. Subsequent changes are much faster.

## Environment Variables

Default development settings are configured in `compose.dev.yml`. To override:

1. Create a `.env` file in the project root:
   ```env
   # PostgreSQL Configuration (required)
   POSTGRES_HOST=localhost
   POSTGRES_PORT=5432
   POSTGRES_DB=bee_logistics_dev
   POSTGRES_USER=postgres
   POSTGRES_PASSWORD=postgres
   
   # Email Configuration (optional)
   EMAIL_HOST=localhost
   EMAIL_PORT=1025
   EMAIL_FROM=dev@beelogistics.local
   ```

2. Or modify `compose.dev.yml` directly.

**Important:** You must configure PostgreSQL connection details either via `.env` file or environment variables before starting the backend.

## Database Migrations

### Run migrations manually:

```bash
# Enter the backend container
docker compose -f compose.dev.yml exec backend bash

# Run migrations (if you have dotnet-ef installed)
dotnet ef database update --project src/BeeLogistics.Api/BeeLogistics.Api.csproj
```

### Or use the seed scripts:

```bash
# Windows
.\seed-database.bat

# Linux/Mac
./seed-database.ps1
```

## Development Workflow

1. **Start services:**
   ```bash
   docker compose -f compose.dev.yml up
   ```

2. **Make code changes** in your IDE (VS Code, Visual Studio, Rider, etc.)

3. **Watch the logs** - you'll see:
   ```
   backend  | File changed: /app/src/Modules/BeeLogistics.Modules.Sales/Application/Handlers/DriverOfferHandlers.cs
   backend  | Building...
   backend  | Build succeeded.
   backend  | Application started.
   ```

4. **Test your changes** - API automatically restarts with new code

## Troubleshooting

### Port Already in Use

If you get port conflicts:
```bash
# Check what's using the port
netstat -ano | findstr :5248  # Windows
lsof -i :5248                  # Mac/Linux

# Or change ports in compose.dev.yml
ports:
  - "5249:8080"  # Use different host port
```

### PostgreSQL Connection (Windows/Mac Docker Desktop)

If PostgreSQL is running on your host machine (not in Docker), use `host.docker.internal`:

```yaml
ConnectionStrings__DefaultConnection: Host=host.docker.internal;Port=5432;Database=bee_logistics_dev;Username=postgres;Password=postgres
```

On Linux, use your host IP address or ensure PostgreSQL is accessible from Docker network.

### Database Connection Issues

Since PostgreSQL is external, check your PostgreSQL instance:

```bash
# Test PostgreSQL connection
psql -h localhost -U postgres -d bee_logistics_dev

# Or if using Docker for PostgreSQL separately
docker ps | grep postgres

# Check backend logs for connection errors
docker compose -f compose.dev.yml logs backend | grep -i postgres
```

**Common Issues:**
- PostgreSQL not running: Start your PostgreSQL instance
- Wrong connection string: Check environment variables in `compose.dev.yml`
- Network issues: Ensure backend can reach PostgreSQL (use `host.docker.internal` on Windows/Mac if PostgreSQL is on host)

### Hot Reload Not Working

1. **Check file permissions** - ensure source files are readable
2. **Check volume mounts** - verify `./src` is mounted correctly
3. **Check logs** - look for file watching errors:
   ```bash
   docker compose -f compose.dev.yml logs backend
   ```

### Build Errors

```bash
# Rebuild the backend image
docker compose -f compose.dev.yml build --no-cache backend

# Or rebuild everything
docker compose -f compose.dev.yml build --no-cache
```

## Performance Tips

1. **Use volume mounts** - Source code is already mounted for hot reload
2. **NuGet cache** - Packages are cached in `nuget_cache` volume
3. **Database data** - Persisted in `postgres_dev_data` volume
4. **Redis data** - Persisted in `redis_dev_data` volume

## Cleanup

### Remove all containers and volumes:
```bash
docker compose -f compose.dev.yml down -v
```

### Remove only containers (keep data):
```bash
docker compose -f compose.dev.yml down
```

### Remove unused images:
```bash
docker image prune -a
```

## VS Code Integration

If using VS Code, you can attach the debugger:

1. Install "C# Dev Kit" extension
2. Create `.vscode/launch.json`:
   ```json
   {
     "version": "0.2.0",
     "configurations": [
       {
         "name": "Docker: Attach to .NET",
         "type": "coreclr",
         "request": "attach",
         "processId": "${command:pickProcess}"
       }
     ]
   }
   ```

3. Find the dotnet process in the container and attach

## Production vs Development

| Feature | Development (`compose.dev.yml`) | Production (`docker-compose.yml`) |
|--------|-------------------------------|-----------------------------------|
| Hot Reload | ✅ Yes (`dotnet watch`) | ❌ No (pre-built image) |
| Source Mount | ✅ Yes (volume mount) | ❌ No |
| Environment | `Development` | `Production` |
| Debugging | ✅ Full debugging | ❌ Limited |
| Performance | Slower (rebuilds) | Faster (pre-built) |

## Next Steps

- Read [DEVELOPMENT.md](docs/DEVELOPMENT.md) for coding guidelines
- Check [API_REFERENCE.md](docs/API_REFERENCE.md) for API documentation
- Review [DRIVER_OFFER_FLOW.md](docs/DRIVER_OFFER_FLOW.md) for business logic

## Support

If you encounter issues:
1. Check the logs: `docker compose -f compose.dev.yml logs`
2. Verify all services are healthy: `docker compose -f compose.dev.yml ps`
3. Check network connectivity: `docker compose -f compose.dev.yml exec backend ping postgres`

