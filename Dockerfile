# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy everything and restore+publish in one step. A hand-maintained per-module COPY
# list used to live here for layer caching, but nothing kept it in sync with
# src/Modules/* -- it went stale (a deleted module, several new ones missing) and broke
# the production image build. dotnet publish restores itself, so there is no separate
# restore layer to protect.
COPY . .
RUN dotnet publish src/BeeLogistics.Api/BeeLogistics.Api.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install curl for healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy published files
COPY --from=build /app/publish .

# Note: appsettings.json is optional - not in git repo for security
# All configuration comes from environment variables set in docker-compose.yml
# The app will work without appsettings.json using only environment variables

# Declare volume for persistent uploads (stored under App_Data/uploads)
VOLUME ["/app/App_Data/uploads"]

# Expose port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Note: Environment variables should be passed via docker-compose or runtime
# The appsettings.json will be used as defaults, and environment variables will override them
ENTRYPOINT ["dotnet", "BeeLogistics.Api.dll"]
