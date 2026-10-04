# GitLab CI/CD Setup Guide

## Overview

This project includes a comprehensive GitLab CI/CD pipeline that automates building, testing, and Docker image creation for the Bee Logistics Backend API.

## Pipeline Stages

### 1. **Restore** (`restore`)
- Restores NuGet packages for all projects
- Caches packages for faster subsequent builds
- Runs on: merge requests, main, and develop branches

### 2. **Build** (`build`)
- Builds the entire solution in Release configuration
- Validates that all projects compile successfully
- Runs on: merge requests, main, and develop branches

### 3. **Test** (`test`)
- Runs all unit tests using xUnit
- Generates code coverage reports (Cobertura format)
- Coverage reports are displayed in GitLab UI
- Runs on: merge requests, main, and develop branches

### 4. **Publish** (`publish`)
- Publishes the API project for deployment
- Creates deployment artifacts
- **Note**: `appsettings.Production.json` is gitignored for security
- Configuration should be provided via environment variables at runtime
- See `appsettings.Production.json.example` for configuration structure
- Runs on: merge requests, main, and develop branches

### 5. **Docker** (`docker:build`)
- Builds Docker image using the Dockerfile
- Tags images based on branch/merge request:
  - `main` branch → `latest` tag
  - Merge requests → `mr-{MR_ID}` tag
  - Other branches → commit SHA tag
- Pushes to GitLab Container Registry
- Runs on: merge requests, main, and develop branches

## Configuration

### Application Configuration

**Important**: `appsettings.Production.json` is gitignored for security reasons. Configuration should be provided via:

1. **Environment Variables** (Recommended for CI/CD and Docker):
   - ASP.NET Core automatically reads configuration from environment variables
   - Use double underscores (`__`) for nested configuration (e.g., `ConnectionStrings__DefaultConnection`)
   - See `docker-compose.yml` for examples

2. **Runtime Configuration**:
   - Mount `appsettings.Production.json` as a volume in Docker
   - Or use a secrets management system (Azure Key Vault, AWS Secrets Manager, etc.)

3. **Template File**:
   - `appsettings.Production.json.example` is committed to the repo as a reference
   - Copy and customize for your environment (but don't commit the actual file!)

### Required GitLab Variables

No additional variables are required by default. The pipeline uses GitLab's built-in CI/CD variables:
- `CI_REGISTRY` - GitLab Container Registry URL
- `CI_REGISTRY_IMAGE` - Auto-generated registry image path
- `CI_REGISTRY_USER` - Registry username (auto-provided)
- `CI_REGISTRY_PASSWORD` - Registry password (auto-provided)

### Optional Configuration

#### Custom Docker Registry

If you want to push to a custom Docker registry instead of GitLab's, uncomment and modify these lines in `.gitlab-ci.yml`:

```yaml
variables:
  DOCKER_REGISTRY: "your-registry.com"
  DOCKER_REGISTRY_IMAGE: "your-registry.com/beelogistics-api"
```

Then add these GitLab CI/CD variables in your project settings:
- `DOCKER_REGISTRY_USER` - Your registry username
- `DOCKER_REGISTRY_PASSWORD` - Your registry password

And update the `docker:build` job's `before_script`:
```yaml
before_script:
  - docker login -u $DOCKER_REGISTRY_USER -p $DOCKER_REGISTRY_PASSWORD $DOCKER_REGISTRY
```

## Triggering the Pipeline

The pipeline automatically runs on:
- **Merge Requests** to `main` branch
- **Pushes** to `main` branch
- **Pushes** to `develop` branch

To trigger manually:
1. Go to CI/CD → Pipelines
2. Click "Run pipeline"
3. Select your branch

## Artifacts

### Build Artifacts
- Published API output: `publish/` directory
- Available for 1 week
- Can be downloaded from GitLab UI

### Test Coverage
- Coverage reports in Cobertura format
- Displayed in GitLab UI under Coverage
- Available for 1 week

### Docker Images
- Stored in GitLab Container Registry
- Accessible at: `$CI_REGISTRY_IMAGE`
- Can be pulled using:
  ```bash
  docker pull $CI_REGISTRY_IMAGE:latest
  ```

## Local Testing

To test the pipeline locally (requires GitLab Runner):

```bash
# Install GitLab Runner
# Then run:
gitlab-runner exec docker restore
gitlab-runner exec docker build
gitlab-runner exec docker test
gitlab-runner exec docker publish
```

## Troubleshooting

### Build Fails
- Check that all NuGet packages are available
- Verify .NET 10.0 SDK is specified correctly
- Check for compilation errors in logs

### Tests Fail
- Review test output in GitLab CI logs
- Ensure test database connections are mocked (tests should not require real DB)
- Check that all test dependencies are included

### Docker Build Fails
- Verify Dockerfile is correct
- Check that Docker service is available
- Ensure registry credentials are set

### Coverage Not Showing
- Verify `coverlet.runsettings` exists
- Check that tests are actually running
- Ensure coverage format matches GitLab's expected format

## Next Steps

### Add Deployment Stage

To add automatic deployment, uncomment the `deploy:production` job in `.gitlab-ci.yml` and configure:

1. Add deployment commands (SSH, kubectl, etc.)
2. Set up deployment environment variables
3. Configure deployment target URL
4. Set `when: manual` for safety (or `when: on_success` for auto-deploy)

### Add Code Quality Checks

Consider adding:
- SonarQube analysis
- Code linting (StyleCop, etc.)
- Security scanning (SAST)

### Add Performance Tests

Add a separate stage for:
- Load testing
- Integration tests
- API performance benchmarks

## Files

- `.gitlab-ci.yml` - Main CI/CD pipeline configuration
- `coverlet.runsettings` - Test coverage configuration
- `Dockerfile` - Docker image build instructions
- `appsettings.Production.json.example` - Production configuration template (safe to commit)
- `appsettings.Production.json` - Production configuration (gitignored, use environment variables instead)

## Support

For issues or questions about the CI/CD pipeline, check:
- GitLab CI/CD documentation: https://docs.gitlab.com/ee/ci/
- .NET Docker images: https://hub.docker.com/_/microsoft-dotnet-sdk
