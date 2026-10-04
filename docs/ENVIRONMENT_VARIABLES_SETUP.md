# Environment Variables Setup Guide

This guide explains where to configure environment variables for different deployment scenarios.

## Table of Contents

1. [GitLab CI/CD Variables](#gitlab-cicd-variables)
2. [Docker Compose (.env file)](#docker-compose-env-file)
3. [Docker Runtime](#docker-runtime)
4. [Kubernetes](#kubernetes)
5. [Other Deployment Methods](#other-deployment-methods)

---

## GitLab CI/CD Variables

**Use for**: Variables needed during the CI/CD pipeline (build, test, deploy)

### Where to Set

1. Go to your GitLab project
2. Navigate to **Settings** → **CI/CD** → **Variables**
3. Click **Expand** next to "Variables"
4. Click **Add variable**

### When to Use

- Secrets needed during build (e.g., NuGet feed credentials)
- Docker registry credentials
- Deployment credentials
- Test database connection strings (if needed)

### Example Variables

```
DOCKER_REGISTRY_USER=your-username
DOCKER_REGISTRY_PASSWORD=your-password
NUGET_FEED_TOKEN=your-nuget-token
```

### Security Best Practices

- ✅ Mark sensitive variables as **Protected** (only available in protected branches)
- ✅ Mark sensitive variables as **Masked** (hidden in logs)
- ✅ Use **File** type for large values or certificates
- ❌ Never commit secrets to `.gitlab-ci.yml`

---

## Docker Compose (.env file)

**Use for**: Local development and Docker Compose deployments

### Where to Create

Create a `.env` file in the **root directory** (same level as `docker-compose.yml`):

```
bee-app-backend-v2/
├── .env                    ← Create this file here
├── docker-compose.yml
├── Dockerfile
└── ...
```

### File Format

Create a `.env` file in the root directory with these variables:

```bash
# ============================================
# Database Configuration
# ============================================
POSTGRES_HOST=your-postgres-host
POSTGRES_PORT=5432
POSTGRES_USER=postgres
POSTGRES_PASSWORD=your_postgres_password
POSTGRES_DB=bee_logistics

# ============================================
# JWT Authentication
# ============================================
JWT_SECRET=your-jwt-secret-key-minimum-32-characters-long
JWT_ISSUER=BeeLogisticsApi
JWT_AUDIENCE=BeeLogisticsClient
JWT_EXPIRY_MINUTES=60

# ============================================
# CORS Configuration
# ============================================
FRONTEND_URL=https://mybeeapp.com
BACKOFFICE_URL=https://backoffice.mybeeapp.com
FRONTEND_URL_DEV=http://localhost:3000
BACKOFFICE_URL_DEV=http://localhost:3001

# ============================================
# Email Configuration (SMTP)
# ============================================
EMAIL_HOST=smtp.your-email-provider.com
EMAIL_PORT=465
EMAIL_USERNAME=your-email@domain.com
EMAIL_PASSWORD=your-email-password
EMAIL_FROM=noreply@yourdomain.com
EMAIL_USE_SSL=true

# ============================================
# Payment Gateways
# ============================================
# Active gateway for NEW payments/top-ups/withdrawals: "paymongo" or "xendit".
# Existing records always route by the provider stored on the record, so keep
# both providers configured while either has in-flight payments.
PAYMENTS_ACTIVE_GATEWAY=paymongo

# --- PayMongo (active) ---
PAYMONGO_SECRET_KEY=sk_live_your-paymongo-secret-key
PAYMONGO_BASE_URL=https://api.paymongo.com/
# Signing secret (whsk_...) returned when creating the webhook (POST v1/webhooks)
PAYMONGO_WEBHOOK_SECRET=whsk_your-webhook-secret
PAYMONGO_CHECKOUT_SUCCESS_URL=https://mybeeapp.com/payment/success
PAYMONGO_CHECKOUT_CANCEL_URL=https://mybeeapp.com/payment/cancelled
# Disbursement rail: instapay (instant, <= PHP 50k) or pesonet
PAYMONGO_TRANSFER_PROVIDER=instapay
# Merchant source account (PayMongo wallet) for outbound driver-withdrawal transfers
PAYMONGO_SOURCE_ACCOUNT_NUMBER=
PAYMONGO_SOURCE_ACCOUNT_NAME=
PAYMONGO_SOURCE_ACCOUNT_BIC=

# --- Xendit (kept for in-flight records / switchable) ---
XENDIT_API_KEY=your-xendit-api-key
XENDIT_BASE_URL=https://api.xendit.co/
XENDIT_WEBHOOK_TOKEN=your-webhook-token

# ============================================
# MQTT Configuration
# ============================================
MQTT_HOST=mqtt.your-domain.com
MQTT_PORT=80
MQTT_USERNAME=your-mqtt-username
MQTT_PASSWORD=your-mqtt-password
MQTT_USE_SSL=false
MQTT_IGNORE_CERTIFICATE_ERRORS=false

# ============================================
# RabbitMQ Configuration
# ============================================
RABBITMQ_HOST=your-rabbitmq-host
RABBITMQ_PORT=5672
RABBITMQ_USER=guest
RABBITMQ_PASSWORD=guest
```

**Quick Start**: Copy the template above and save it as `.env` in your project root, then fill in your actual values.

### Security

- ✅ `.env` is already in `.gitignore` (won't be committed)
- ✅ Create `.env.example` with placeholder values (optional, for documentation)
- ❌ Never commit `.env` to Git

### Usage

Docker Compose automatically loads `.env` file:

```bash
docker-compose up -d
```

The variables are referenced in `docker-compose.yml` using `${VARIABLE_NAME}` syntax.

---

## Docker Runtime

**Use for**: Running Docker containers directly (without Docker Compose)

### Method 1: Environment File

Create a `.env` file and use `--env-file`:

```bash
docker run --env-file .env beelogistics-api:latest
```

### Method 2: Individual Variables

Pass variables directly:

```bash
docker run \
  -e ConnectionStrings__DefaultConnection="Host=db;Database=bee_logistics;..." \
  -e JwtSettings__Secret="your-secret" \
  beelogistics-api:latest
```

### Method 3: Docker Compose Environment Section

In `docker-compose.yml`, the `environment:` section already maps variables:

```yaml
services:
  backend:
    environment:
      ConnectionStrings__DefaultConnection: Host=${POSTGRES_HOST};...
      JwtSettings__Secret: ${JWT_SECRET}
```

---

## Kubernetes

**Use for**: Kubernetes deployments

### Method 1: ConfigMap + Secret

#### Create ConfigMap (non-sensitive):

```yaml
# configmap.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: beelogistics-config
data:
  ASPNETCORE_ENVIRONMENT: "Production"
  JwtSettings__Issuer: "BeeLogisticsApi"
  JwtSettings__Audience: "BeeLogisticsClient"
```

#### Create Secret (sensitive):

```yaml
# secret.yaml
apiVersion: v1
kind: Secret
metadata:
  name: beelogistics-secrets
type: Opaque
stringData:
  ConnectionStrings__DefaultConnection: "Host=db;Database=bee_logistics;Username=postgres;Password=secret"
  JwtSettings__Secret: "your-jwt-secret"
  Email__Password: "email-password"
```

#### Apply to Deployment:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: beelogistics-api
spec:
  template:
    spec:
      containers:
      - name: api
        image: beelogistics-api:latest
        envFrom:
        - configMapRef:
            name: beelogistics-config
        - secretRef:
            name: beelogistics-secrets
```

### Method 2: External Secrets Operator

For production, use External Secrets Operator to sync from:
- AWS Secrets Manager
- Azure Key Vault
- HashiCorp Vault
- Google Secret Manager

---

## Other Deployment Methods

### Azure App Service

1. Go to **Configuration** → **Application settings**
2. Add environment variables
3. Use double underscores for nested: `ConnectionStrings__DefaultConnection`

### AWS ECS/Fargate

1. Use **ECS Task Definition** → **Environment variables**
2. Or use **AWS Secrets Manager** with IAM roles

### Heroku

```bash
heroku config:set ConnectionStrings__DefaultConnection="Host=..."
heroku config:set JwtSettings__Secret="..."
```

Or use Heroku Dashboard → Settings → Config Vars

---

## Environment Variable Naming Convention

ASP.NET Core uses **double underscores (`__`)** for nested configuration:

| JSON Path | Environment Variable |
|-----------|---------------------|
| `ConnectionStrings.DefaultConnection` | `ConnectionStrings__DefaultConnection` |
| `JwtSettings.Secret` | `JwtSettings__Secret` |
| `Cors.AllowedOrigins[0]` | `Cors__AllowedOrigins__0` |
| `Email.Password` | `Email__Password` |

### Array Values

For arrays, use indexed notation:

```bash
Cors__AllowedOrigins__0=https://mybeeapp.com
Cors__AllowedOrigins__1=https://backoffice.mybeeapp.com
```

---

## Production: No Secrets in Config Files

**Never put real secrets in committed `appsettings.json` or `appsettings.*.json`.** Use only placeholders in any committed config files. Production must set all sensitive values via:

- **Environment variables** (e.g. `ConnectionStrings__DefaultConnection`, `JwtSettings__Secret`)
- **Secrets manager** (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault, etc.)
- **User secrets** (local dev: `dotnet user-secrets set "Key" "Value"`)

Required for production: `ConnectionStrings__DefaultConnection`, `JwtSettings__Secret`, `Seed__DefaultPassword`, and when using S3 (FileStorage:UseLocal=false): `S3__AccessKey`, `S3__SecretKey`. Payment gateways: the active gateway (`Payments__ActiveGateway`) must have its credentials set (`PayMongo__SecretKey` + `PayMongo__WebhookSecret`, or `Xendit__ApiKey` + webhook auth), and any CONFIGURED gateway must also have its webhook secret — startup refuses to boot otherwise. Set Email, MQTT, and other provider credentials via env or secrets.

---

## Required Environment Variables

### Critical (Application won't start without these):

- `ConnectionStrings__DefaultConnection` - PostgreSQL connection string
- `JwtSettings__Secret` - JWT signing key (minimum 32 characters)
- `Seed__DefaultPassword` - Required for database seeding (initial admin users)

### When FileStorage:UseLocal is false (S3/R2):

- `S3__AccessKey` - S3-compatible storage access key (no default)
- `S3__SecretKey` - S3-compatible storage secret key (no default)

### Important (Recommended):

- `ConnectionStrings__Redis` - Redis connection (falls back to in-memory if not set)
- `Cors__AllowedOrigins__0` - Frontend URL for CORS
- `Email__Host`, `Email__Username`, `Email__Password` - Email configuration
- `RabbitMQ__Host`, `RabbitMQ__Username`, `RabbitMQ__Password` - Message queue
- `Payments__ActiveGateway` - Which gateway new payments use (`paymongo` | `xendit`)
- `PayMongo__SecretKey` + `PayMongo__WebhookSecret` - Required when PayMongo is active or configured
- `Xendit__WebhookToken` - Required in production when Xendit is configured (webhook verification)

### Optional:

- `Xendit__ApiKey` - Xendit gateway (required only when Xendit is active; enables refunds/reconciliation of legacy Xendit records)
- `Mqtt__Host`, `Mqtt__Username`, `Mqtt__Password` - MQTT configuration
- `S3__Endpoint` - S3 endpoint (when using S3 storage)

---

## Testing Your Configuration

### Check if variables are loaded:

```bash
# In Docker container
docker exec -it beeapp-backend env | grep ConnectionStrings

# Or check application logs
docker logs beeapp-backend | grep "ConnectionStrings"
```

### Verify in Application:

The application logs configuration on startup. Check logs for:
- `"Configuring Redis cache: ..."`
- `"CORS Allowed Origins: ..."`
- Errors about missing required configuration

---

## Quick Reference

| Scenario | Location | File/Place |
|----------|----------|------------|
| **GitLab CI/CD** | GitLab UI | Settings → CI/CD → Variables |
| **Local Docker Compose** | Project root | `.env` file |
| **Docker Run** | Command line | `-e` flags or `--env-file` |
| **Kubernetes** | Cluster | ConfigMap + Secret |
| **Azure App Service** | Azure Portal | Configuration → App settings |
| **AWS ECS** | AWS Console | Task Definition → Environment |

---

## Security Checklist

- ✅ Never commit `.env` files
- ✅ Use secrets management for production
- ✅ Rotate secrets regularly
- ✅ Use different secrets for dev/staging/prod
- ✅ Mark sensitive GitLab variables as Protected and Masked
- ✅ Use least privilege for service accounts
- ✅ Encrypt secrets at rest and in transit

---

## Troubleshooting

### Variable not being read?

1. Check naming: Use `__` (double underscore) for nested config
2. Check case sensitivity (Linux is case-sensitive)
3. Restart the application after changing variables
4. Check application logs for configuration errors

### Docker Compose not loading .env?

1. Ensure `.env` is in the same directory as `docker-compose.yml`
2. Check file permissions
3. Verify variable names match `${VARIABLE_NAME}` in compose file

### Kubernetes secrets not working?

1. Ensure secrets/configmaps are in the same namespace
2. Check `envFrom` syntax in deployment
3. Verify secret keys match environment variable names
