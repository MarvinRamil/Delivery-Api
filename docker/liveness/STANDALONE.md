# Standalone YOLO Liveness Service

The liveness detection service runs independently from the main backend stack.

## Quick Start

```bash
# Start the service
docker-compose -f docker-compose.liveness.yml up -d

# Stop the service
docker-compose -f docker-compose.liveness.yml down

# View logs
docker logs beeapp-liveness

# Check status
docker ps --filter "name=beeapp-liveness"
```

## Accessing the Service

The service is accessible on:
- **From host machine**: `http://localhost:8000`
- **From other containers**: Connect the container to the `bee-app-backend-v2_liveness_network` network

## Connecting Other Containers

If you need your backend to connect to the liveness service, add it to the same network:

```yaml
# In your backend's docker-compose.yml
services:
  backend:
    # ... other config ...
    networks:
      - your_backend_network
      - liveness_network

networks:
  liveness_network:
    external: true
    name: bee-app-backend-v2_liveness_network
```

Then your backend can access it at: `http://liveness:8000`

## Health Check

```bash
curl http://localhost:8000/v1/health
```

Expected response:
```json
{
  "status": "healthy",
  "model_loaded": true,
  "device": "cpu",
  "version": "1.0.0",
  "uptime_seconds": 123.45
}
```

## API Endpoints

- `GET /v1/health` - Health check endpoint
- `POST /v1/predict` - Submit an image for liveness detection

See the main repository documentation for API details.
