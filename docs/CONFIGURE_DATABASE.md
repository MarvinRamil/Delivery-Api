# Configuring PostgreSQL Connection String

This guide explains how to configure the PostgreSQL connection string for local development with Docker Compose.

## Quick Setup

### Option 1: Using `.env` File (Recommended)

1. **Create a `.env` file** in the project root (same directory as `compose.dev.yml`):

```env
POSTGRES_HOST=localhost
POSTGRES_PORT=5432
POSTGRES_DB=bee_logistics_dev
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
```

2. **Start the services:**
```bash
docker compose -f compose.dev.yml up
```

The `.env` file is automatically loaded by Docker Compose, and the connection string will be built from these variables.

### Option 2: Direct Environment Variables

Set environment variables in your shell before running Docker Compose:

**Windows (PowerShell):**
```powershell
$env:POSTGRES_HOST="localhost"
$env:POSTGRES_PORT="5432"
$env:POSTGRES_DB="bee_logistics_dev"
$env:POSTGRES_USER="postgres"
$env:POSTGRES_PASSWORD="postgres"

docker compose -f compose.dev.yml up
```

**Windows (CMD):**
```cmd
set POSTGRES_HOST=localhost
set POSTGRES_PORT=5432
set POSTGRES_DB=bee_logistics_dev
set POSTGRES_USER=postgres
set POSTGRES_PASSWORD=postgres

docker compose -f compose.dev.yml up
```

**Linux/Mac:**
```bash
export POSTGRES_HOST=localhost
export POSTGRES_PORT=5432
export POSTGRES_DB=bee_logistics_dev
export POSTGRES_USER=postgres
export POSTGRES_PASSWORD=postgres

docker compose -f compose.dev.yml up
```

### Option 3: Edit `compose.dev.yml` Directly

Edit the `ConnectionStrings__DefaultConnection` line in `compose.dev.yml`:

```yaml
environment:
  # ... other variables ...
  ConnectionStrings__DefaultConnection: Host=your-host;Port=5432;Database=your-db;Username=your-user;Password=your-password
```

## Connection String Format

The connection string follows PostgreSQL Npgsql format:

```
Host=HOSTNAME;Port=PORT;Database=DATABASE_NAME;Username=USERNAME;Password=PASSWORD
```

### Examples

**Local PostgreSQL (default port):**
```
Host=localhost;Port=5432;Database=bee_logistics_dev;Username=postgres;Password=postgres
```

**Remote PostgreSQL:**
```
Host=192.168.1.100;Port=5432;Database=bee_logistics_dev;Username=postgres;Password=secure_password
```

**PostgreSQL with SSL:**
```
Host=db.example.com;Port=5432;Database=bee_logistics_dev;Username=postgres;Password=password;SslMode=Require
```

**Docker Desktop (PostgreSQL on host machine):**
```
Host=host.docker.internal;Port=5432;Database=bee_logistics_dev;Username=postgres;Password=postgres
```

## Common Scenarios

### Scenario 1: PostgreSQL Running on Host Machine

If PostgreSQL is installed directly on your computer (not in Docker):

**Windows/Mac Docker Desktop:**
```env
POSTGRES_HOST=host.docker.internal
POSTGRES_PORT=5432
POSTGRES_DB=bee_logistics_dev
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
```

**Linux:**
```env
POSTGRES_HOST=172.17.0.1  # Docker bridge IP, or use your host IP
POSTGRES_PORT=5432
POSTGRES_DB=bee_logistics_dev
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
```

### Scenario 2: PostgreSQL in Separate Docker Container

If you have PostgreSQL running in another Docker Compose file or container:

1. **Find the container name:**
```bash
docker ps | grep postgres
```

2. **Use the container name as host:**
```env
POSTGRES_HOST=your-postgres-container-name
POSTGRES_PORT=5432
POSTGRES_DB=bee_logistics_dev
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
```

3. **Or use Docker network:**
   - Ensure both containers are on the same network
   - Use the service name from the other compose file

### Scenario 3: Remote PostgreSQL Server

For a PostgreSQL server on another machine or cloud:

```env
POSTGRES_HOST=your-server-ip-or-domain
POSTGRES_PORT=5432
POSTGRES_DB=bee_logistics_dev
POSTGRES_USER=postgres
POSTGRES_PASSWORD=your-secure-password
```

**Note:** Ensure the PostgreSQL server allows connections from your Docker network.

### Scenario 4: PostgreSQL with Custom Port

If PostgreSQL is running on a non-standard port:

```env
POSTGRES_HOST=localhost
POSTGRES_PORT=5433  # Custom port
POSTGRES_DB=bee_logistics_dev
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
```

## Verifying Connection

### Test Connection from Backend Container

```bash
# Enter the backend container
docker compose -f compose.dev.yml exec backend bash

# Test PostgreSQL connection (if psql is installed)
psql -h $POSTGRES_HOST -p $POSTGRES_PORT -U $POSTGRES_USER -d $POSTGRES_DB

# Or check connection string
echo $ConnectionStrings__DefaultConnection
```

### Check Backend Logs

```bash
# View backend logs for connection errors
docker compose -f compose.dev.yml logs backend | grep -i postgres

# Or view all logs
docker compose -f compose.dev.yml logs backend
```

### Test from Host Machine

```bash
# Using psql (if installed)
psql -h localhost -p 5432 -U postgres -d bee_logistics_dev

# Or using Docker
docker run -it --rm postgres:16-alpine psql -h host.docker.internal -U postgres -d bee_logistics_dev
```

## Troubleshooting

### Connection Refused

**Error:** `Connection refused` or `No route to host`

**Solutions:**
1. Verify PostgreSQL is running:
   ```bash
   # Windows
   Get-Service postgresql*
   
   # Linux/Mac
   sudo systemctl status postgresql
   ```

2. Check PostgreSQL is listening on the correct port:
   ```bash
   # Windows
   netstat -ano | findstr :5432
   
   # Linux/Mac
   lsof -i :5432
   ```

3. Verify firewall rules allow connections

### Authentication Failed

**Error:** `password authentication failed`

**Solutions:**
1. Verify username and password are correct
2. Check `pg_hba.conf` allows connections from Docker network
3. For Docker Desktop, ensure `host.docker.internal` is used

### Host Not Found

**Error:** `Name or service not known`

**Solutions:**
1. Use `host.docker.internal` on Windows/Mac Docker Desktop
2. Use container name if PostgreSQL is in Docker
3. Use IP address instead of hostname
4. Ensure containers are on the same Docker network

### Database Does Not Exist

**Error:** `database "bee_logistics_dev" does not exist`

**Solutions:**
1. Create the database:
   ```sql
   CREATE DATABASE bee_logistics_dev;
   ```

2. Or run migrations:
   ```bash
   docker compose -f compose.dev.yml exec backend dotnet ef database update --project src/BeeLogistics.Api/BeeLogistics.Api.csproj
   ```

## Security Notes

⚠️ **Never commit `.env` files to Git!**

The `.env` file contains sensitive credentials. Always:
- Add `.env` to `.gitignore`
- Use `.env.example` as a template (without real passwords)
- Use different credentials for development/production
- Use strong passwords in production

## Next Steps

After configuring the connection string:

1. **Start services:**
   ```bash
   docker compose -f compose.dev.yml up
   ```

2. **Run database migrations:**
   ```bash
   docker compose -f compose.dev.yml exec backend dotnet ef database update --project src/BeeLogistics.Api/BeeLogistics.Api.csproj
   ```

3. **Seed database (if needed):**
   ```bash
   docker compose -f compose.dev.yml exec backend dotnet run --project src/BeeLogistics.Api/BeeLogistics.Api.csproj -- seed
   ```

4. **Verify API is running:**
   ```bash
   curl http://localhost:5248/health
   ```

