# Docker Environment Variables Setup

## Using .env Files with Docker

Docker Compose automatically loads environment variables from a `.env` file in the same directory as `docker-compose.yml`.

## Backend (.env file)

Create a `.env` file in `bee-app-backend/` directory:

```env
# External PostgreSQL Configuration
# PostgreSQL is on a different VM - access via IP address
POSTGRES_HOST=100.120.42.59
POSTGRES_PORT=5432
POSTGRES_USER=postgres
POSTGRES_PASSWORD=your_secure_password_here
POSTGRES_DB=bee_logistics_db

# JWT Configuration
JWT_SECRET=your-secret-key-minimum-32-characters-long-for-production
JWT_ISSUER=BeeLogisticsApi
JWT_AUDIENCE=BeeLogisticsClient
JWT_EXPIRY_MINUTES=60

# CORS Configuration
# Note: CORS works at the origin level (protocol + domain + port)
# All paths on these domains are automatically included - no need to specify paths
FRONTEND_URL=https://mybeeapp.com
BACKOFFICE_URL=https://backoffice.mybeeapp.com

# Development URLs (optional, for local testing)
FRONTEND_URL_DEV=http://localhost:3000
BACKOFFICE_URL_DEV=http://localhost:3001

# Environment
ASPNETCORE_ENVIRONMENT=Production

# Note: Redis is automatically connected via docker-compose service name "redis:6379"
# No REDIS_URL needed - it's hardcoded to use the Redis container in this compose file
```

### Using External PostgreSQL

The docker-compose.yml is configured to use an external PostgreSQL instance on a different VM. Make sure:
1. PostgreSQL is accessible from the Docker container via IP address (e.g., `100.120.42.59`)
2. The connection string in `.env` points to the correct IP and port
3. Ensure PostgreSQL allows connections from your Docker network (check `pg_hba.conf` and firewall rules)
4. The PostgreSQL server's firewall allows connections from your Docker host IP

## Frontend (.env file)

Create a `.env` file in `bee-app-frontend/` directory:

```env
# API Configuration
# These are build-time variables (VITE_ prefix)
VITE_API_URL=https://api.mybeeapp.com
VITE_SIGNALR_URL=https://api.mybeeapp.com

# Development URLs (for local development)
# VITE_API_URL=http://localhost:8080
# VITE_SIGNALR_URL=http://localhost:8080
```

## Back-Office (.env file)

Create a `.env` file in `bee-app-back-office/` directory:

```env
# API Configuration
# Next.js uses NEXT_PUBLIC_ prefix for client-side environment variables
NEXT_PUBLIC_API_URL=https://api.mybeeapp.com

# Development URL (for local development)
# NEXT_PUBLIC_API_URL=http://localhost:8080
```

## CORS Configuration

### Important: CORS Origins Include All Paths

When you configure CORS with origins like:
- `https://mybeeapp.com`
- `https://backoffice.mybeeapp.com`

**All paths on these domains are automatically included**. You don't need to specify:
- `https://mybeeapp.com/dashboard`
- `https://mybeeapp.com/bookings`
- `https://backoffice.mybeeapp.com/crm`

CORS works at the **origin level** (protocol + domain + port), not at the path level.

### Example:
- ✅ `https://mybeeapp.com` → Includes ALL paths: `/`, `/dashboard`, `/bookings`, etc.
- ✅ `https://backoffice.mybeeapp.com` → Includes ALL paths: `/`, `/crm`, `/dashboard`, etc.
- ❌ `https://mybeeapp.com/dashboard` → This is NOT how CORS works

## Docker Network Configuration

All three projects (backend, frontend, back-office) are configured to use the external Docker network `npm-desktop_default` from Nginx Proxy Manager. This allows:
- Automatic connection to Nginx Proxy Manager network
- Easy reverse proxy configuration
- Container-to-container communication via service names

## Usage

1. Copy `.env.example` to `.env` (if available) or create `.env` manually in each project directory
2. Fill in your production values
3. Run `docker-compose up -d` in each project directory:
   ```bash
   cd bee-app-backend && docker-compose up -d
   cd bee-app-frontend && docker-compose up -d
   cd bee-app-back-office && docker-compose up -d
   ```

Docker Compose will automatically:
- Load variables from `.env` file
- Connect containers to the `npm-desktop_default` network
- Allow Nginx Proxy Manager to route traffic to your containers

## Security Notes

- **Never commit `.env` files to git** - they contain sensitive information
- Add `.env` to `.gitignore`
- Use different `.env` files for different environments (dev, staging, production)
- Consider using Docker secrets for production deployments

