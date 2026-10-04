# MQTT Environment Isolation Implementation

## What was implemented

This implementation isolates MQTT traffic by environment so `dev`, `staging`, and `prod` do not consume each other's location messages.

### Backend changes

- Added shared topic convention helper:
  - `src/Modules/BeeLogistics.Modules.Map/Application/Services/MqttTopicConvention.cs`
- Updated MQTT token generation:
  - `src/Modules/BeeLogistics.Modules.Map/Infrastructure/Services/MqttTokenService.cs`
  - Token now includes:
    - `env` claim (`dev`, `staging`, `prod`)
    - env-specific `topic` claim
- Updated MQTT credentials endpoint:
  - `src/Modules/BeeLogistics.Modules.Map/Presentation/Controllers/MqttCredentialsController.cs`
  - Returns env-specific `topic`
  - Returns `environment` field in response
  - Includes environment in generated client ID
- Updated MQTT subscriber:
  - `src/Modules/BeeLogistics.Modules.Map/Infrastructure/Services/MqttLocationSubscriberService.cs`
  - Resolves `Mqtt:Environment` (fallback to `ASPNETCORE_ENVIRONMENT`)
  - Uses env-specific default subscribe topic
  - Enforces topic format per environment:
    - `prod`: `beelogistics/drivers/{driverId}/location`
    - non-prod: `{env}/beelogistics/drivers/{driverId}/location`
  - Rejects topics from wrong environment

### CI/CD changes

- Updated `.gitlab-ci.yml` to inject MQTT environment and topic by deployment environment.
- Added generated `build.env` variables:
  - `MQTT_ENV`
  - `MQTT_LOCATION_TOPIC`
- Added container env injection:
  - `Mqtt__Environment=${MQTT_ENV}`
  - `Mqtt__LocationTopic=${MQTT_LOCATION_TOPIC}`

Environment mapping in CI:

- Dev:
  - `MQTT_ENV=dev`
  - `MQTT_LOCATION_TOPIC=dev/beelogistics/drivers/+/location`
- Staging:
  - `MQTT_ENV=staging`
  - `MQTT_LOCATION_TOPIC=staging/beelogistics/drivers/+/location`
- Production:
  - `MQTT_ENV=prod`
  - `MQTT_LOCATION_TOPIC=beelogistics/drivers/+/location`

### Local appsettings defaults

- Updated `src/BeeLogistics.Api/appsettings.json` MQTT defaults for local development:
  - `Mqtt:Environment = dev`
  - `Mqtt:LocationTopic = dev/beelogistics/drivers/+/location`

## Driver app changes

- Updated:
  - `bee-driver/features/driver/services/mqttLocationService.ts`
- Default app environment behavior:
  - if `EXPO_PUBLIC_APP_ENV` is set, use it
  - else use runtime fallback:
    - `__DEV__ === true` -> `dev`
    - release build -> `staging` (temporary testing phase behavior)
- Default topic prefix behavior:
  - `prod` -> `beelogistics/drivers`
  - non-prod -> `{env}/beelogistics/drivers`
- Existing override still supported:
  - `EXPO_PUBLIC_MQTT_TOPIC_PREFIX`

## Build/run behavior for driver app

- `npx expo run:android`
  - treated as `dev` by default (unless `EXPO_PUBLIC_APP_ENV` overrides)
- `npx expo run:android --variant release`
  - treated as `staging` by default for now

For explicit control, set:

- `EXPO_PUBLIC_APP_ENV=dev|staging|prod`

## Recommended next steps

- Later, switch release default from `staging` to explicit env-based release profiles.
- Add separate broker credentials per environment when ready.
- Keep CI variables as source of truth for backend environment routing.

