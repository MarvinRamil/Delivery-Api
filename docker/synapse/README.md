# Synapse — Matrix booking chat

The homeserver behind per-booking driver↔customer chat. bee-backend talks to it as a Matrix
**Application Service**, not as a client: it holds the appservice tokens and acts on behalf of
namespaced users. There is no Matrix SDK in the backend and no per-user credential anywhere.

**One on-prem instance serves every environment.** Environments are kept apart by appservice
namespace, not by separate homeservers — see [Environment isolation](#environment-isolation), which
is the part of this document most worth reading before changing anything.

Everything here ships **off**. With `Matrix__Enabled=false` the backend registers options and an
HttpClient nothing calls, and booking creation behaves exactly as it did before.

## Layout

Synapse is pointed at the whole `conf/` directory (`SYNAPSE_CONFIG_PATH=/config`) and merges every
`*.yaml` in it in filename order. The split is by *what is secret*:

| Path | Committed? | What it is |
|---|---|---|
| `conf/10-homeserver.yaml` | yes | All non-secret config, including `server_name`. |
| `conf/log.config` | yes | Python logging config. |
| `conf/99-secrets.yaml` | **no** | Database password, Synapse's own secrets. |
| `conf/bee-appservice-dev.yaml` | **no** | Dev appservice registration (tokens). |
| `*.example` beside each | yes | Templates for the rendered files. |

## Topology

Synapse is on-prem. Dev runs on-prem too; production will run in the cloud. **Both environments
use the same public paths anyway**, deliberately.

```
                      dev                              production (deferred)
                      ---                              ---------------------
  API -> Synapse      https://matrix.bee-app.tech      https://matrix.bee-app.tech
  Synapse -> API      https://dev-api.bee-app.tech     https://appservice.bee-app.tech
                              /appservice                       /appservice
```

Dev could take a LAN shortcut — both ends are on-prem, and it measures **0.87 ms over the LAN
against 408 ms through Cloudflare**. It deliberately does not. Dev exists to exercise the path
production will use: real DNS, real TLS, real Cloudflare, real WAF. A bot-protection or routing
problem then surfaces in dev instead of ambushing the prod cutover, and promoting to production
becomes a copy of a proven configuration rather than a fresh one. The 400 ms is background event
delivery, not user-facing latency, so the parity is cheap.

Clients (driver app, customer web) use `https://matrix.bee-app.tech` in every environment —
drivers are on cellular, so the homeserver was always going to be internet-facing.

**Verified** with Synapse's real user agent: `PUT https://dev-api.bee-app.tech/appservice/...`
reaches the backend rather than being turned away by Cloudflare, which does not block
`Synapse/1.159.0`. The endpoint is now served by `MatrixAppServiceController`; when this was first
probed the controller did not exist yet and the same check returned 404, which was equally good
evidence — the point of the probe is the *absence of a 403*, not the status code itself. Re-test
after any Cloudflare change: a 403 here means the transcript has silently stopped filling.

> **Production is deferred.** Only `appservice-dev.yaml` is loaded. Its prod counterpart is written
> and ready with its own token pair and namespace, but commented out of `app_service_config_files`.

**Production chat will depend on the homelab being up.** This is accepted, not overlooked. Matrix
is isolated so it degrades alone: `Matrix__Enabled` gates the whole feature, the health check
reports Degraded rather than Unhealthy so the API stays in rotation, and room provisioning runs
through MassTransit consumers that retry. Bookings and payments are unaffected by a chat outage.

> A long outage outlives the retry budget (1s/5s/30s, then 1/5/15 min), after which the message
> lands in the error queue and that booking would never get a room. `BookingRoomBackstopSweep`
> covers that gap: the `booking-chat-backstop` Hangfire job runs hourly at :17 and provisions rooms
> for bookings that have none — the same belt-and-braces pattern `GetCancelledBookingsSinceQuery`
> already uses for payments. The offset from the hour keeps it off the other hourly sweeps.

## Environment isolation

Dev and production share this homeserver. What separates them is the `exclusive` namespaces in
their registration files:

| | dev | production |
|---|---|---|
| `Matrix__EnvironmentPrefix` | `dev` | `prod` |
| Users | `@bee_u_dev_<id>` | `@bee_u_prod_<id>` |
| Room aliases | `#booking-dev-<number>` | `#booking-prod-<number>` |
| Bot | `@bee-dev` | `@bee` |
| Registration id | `bee-appservice-dev` | `bee-appservice-prod` |
| Transaction URL | `https://dev-api.bee-app.tech/appservice` | `https://api.bee-app.tech/appservice` |
| Tokens | its own `as_token`/`hs_token` pair | its own pair |

**Why this is a correctness requirement, not tidiness.** `BookingNumber` is unique only *within* a
database (unique index, `BookingsDbContext.cs:92`) and is minted from the clock —
`BKG-{yyyyMMdd}-{Ticks % 1000000}`, `Booking.cs:276`. Dev and production have separate databases, so
both can produce `BKG-20260823-000123` on the same day. Room creation treats the alias as an
idempotency key and **adopts** an existing room on `M_ROOM_IN_USE` rather than failing — so without
the prefix, a dev booking that drew production's number would silently join a real customer's chat.

`exclusive: true` makes Synapse enforce this server-side: the dev appservice physically cannot
create or claim a `prod` user or alias, even with a bug in our code. Separate token pairs mean a
leaked dev token cannot reach production rooms either.

**Production is registered but not loaded.** `appservice-prod.yaml` exists with its own token pair
and its own exclusive namespace, but is commented out of `app_service_config_files` while the cloud
backend is out of scope. That is deliberate: an appservice pointing at a URL that is not serving is
a trap, and a commented-out line is honest about the state. Its `as_token` correctly returns
`M_UNKNOWN_TOKEN` while unloaded — verified.

To turn it on: uncomment the line, point `appservice.bee-app.tech` at the **cloud backend** (not at
this homeserver), add the Cloudflare WAF skip, and set `MATRIX_ENABLED=true` for prod.

**Staging is not registered.** Nobody uses it, so it stays on `Matrix__Enabled=false`. To add it
later: a third registration file here, plus `MATRIX_ENVIRONMENT_PREFIX=staging` in
`.gitlab-ci.yml`. The startup guard refuses to run an enabled environment with no prefix, so this
cannot be half-done.

**The trade-off, stated plainly:** one instance is one blast radius. A Synapse outage or a bad
upgrade takes chat down in dev and production together, and they share a database server. The
namespaces prevent data crossing between environments; they do not prevent a shared outage.

## Two facts that are expensive to get wrong

**`server_name` is permanent.** It is the MXID domain — the part after the colon in
`@bee_u_prod_…:matrix.bee-app.tech` — and it is baked into every user id, room id and event ever
created. Changing it is not a migration, it is a reset. Treat it like a primary key, not a hostname.
It is set to the homeserver's own hostname, so **no `.well-known` delegation is needed anywhere**;
clients resolve the server straight from an MXID.

**`as_token` and `hs_token` are different secrets going opposite directions.**
`as_token` is bee → Synapse and grants the power to act as any user in that environment's namespace.
`hs_token` is Synapse → bee and is the only thing proving an inbound transaction is genuine.
Reusing one value for both means anyone who finds the appservice endpoint also holds the credential
that can impersonate every user. `MatrixOptions.Validate()` refuses to start on it.

## Provisioning on Proxmox

Synapse runs on its **own VM with its own Postgres**, not on the shared `DB-VM` and not as a
container beside the API. The reason is recovery semantics rather than performance: losing chat
history is survivable and losing bookings is not, so the two want different backup cadences and a
restore you can rehearse independently. Synapse upgrades also run schema migrations that can be
long and lock-heavy, which is not something to do on the box serving payments. Co-locating Synapse
with its own database keeps the whole thing one unit to snapshot, upgrade and restore.

The homelab LAN was a `/25` (`.1`–`.126` only) and has reportedly been widened to `/24`. **Before
building on any address above `.126`, ping `10.10.10.1` from inside the guest and confirm a reply** —
high addresses previously got ARP on the bridge but were never routed, and that failure is silent
and cost a full debug cycle on the Sentry VM.

| | |
|---|---|
| Type | VM (Debian 12 / Ubuntu 24.04) running Synapse **and** its Postgres |
| vCPU | 2 |
| RAM | 6 GB — roughly 2 GB Synapse, 2 GB Postgres, rest OS and page cache |
| Balloon min | 2 GB. The host runs 1.66x overcommit against 62 GiB; what matters is the balloon floor, currently 44 GiB. This takes it to ~46 GiB, well clear of the ~62 GiB danger line |
| Disk | 60 GB — media store, the room DAG, and the database. Uploads are capped at 10 MB each |
| IP | `.1`–`.126` known-good. Occupied: `.1` gw, `.2` pve, `.3`, `.4`, `.7`, `.8`, `.9`, `.45`, `.91` Zammad, `.114`, `.117` Vault, `.126` Sentry |
| Postgres | local to this VM. `shared_buffers` ≈ 25% of RAM |
| Ingress | reverse proxy `matrix.bee-app.tech` → `:8008` |

Do **not** start VM 105 (SEQ-LOGGING) or 116 (terraform) while Sentry (103) runs — the host cannot
fund all of them.

### Live deployment

Provisioned 2026-08-23. Ubuntu 24.04.3, 2 cores, 5.7 GiB RAM, 57 GB disk.

| | |
|---|---|
| Host | `10.10.10.152` |
| Synapse | 1.159.0, matrix.org apt repo, `matrix-synapse.service` |
| Listener | `0.0.0.0:8008`, client resource only (no federation) |
| Postgres | 16.15, local, bound to `127.0.0.1` only, `shared_buffers=1536MB` |
| Database | `synapse`, `UTF8 / C / C` — verified |
| Config | `/etc/matrix-synapse/conf.d/` — `10-bee-homeserver.yaml`, `99-bee-secrets.yaml`, `appservice-{dev,prod}.yaml` |
| Secrets | `/root/matrix/secrets.env`, `0600` root-only. **Never printed to a terminal.** |
| Admin | `@beeadmin`, token in `ADMIN_TOKEN` for the retention job |
| Firewall | ufw inactive — Synapse is LAN-reachable on 8008 |

Verified on install: appservice registration works; `M_EXCLUSIVE` correctly blocks the dev
appservice from the prod namespace; an unknown token gets `M_UNKNOWN_TOKEN`;
`m.login.application_service` mints a client token, which is the mechanism `/api/matrix/session`
will use.

### What the backend needs

`Matrix__HomeserverUrl` = `http://10.10.10.152:8008`. CI passes it with **no fallback** on purpose — a container-name default would resolve nowhere yet still satisfy startup
validation, turning a missing variable into "no booking ever gets a chat room". Unset means empty,
which fails fast at startup instead.

The `synapse` service in `docker-compose.yml` is for **local development only** (it sits behind the
`matrix` profile). Deployed environments use this VM.

### What must be backed up

Three things, and the first is the one people forget:

1. `/data/signing.key` — **losing it is unrecoverable**. Every room and event is signed with it.
2. The `synapse` database.
3. `/data/media_store` — attachments. Lossy-recoverable: the events survive, the files do not.

Federation is off and there is no `8448` listener, so only `8008` needs to be reachable, and only
from the reverse proxy and the backend.

## First-time setup

### 1. Database

On the Matrix VM's local Postgres. The collation is not optional; Synapse checks it at startup and
refuses to run otherwise, and it cannot be changed afterwards without a dump and reload.

```sql
CREATE ROLE synapse WITH LOGIN PASSWORD '<from vault>';
CREATE DATABASE synapse
  ENCODING 'UTF8'
  LC_COLLATE 'C'
  LC_CTYPE 'C'
  TEMPLATE template0
  OWNER synapse;
```

### 2. Secrets

One token pair per environment, plus Synapse's own secrets:

```bash
openssl rand -hex 32   # dev as_token
openssl rand -hex 32   # dev hs_token   — must differ from as_token
openssl rand -hex 32   # macaroon_secret_key
openssl rand -hex 32   # form_secret
```

Store the appservice tokens in Vault at `bee/config` as `Matrix__AsToken`, `Matrix__HsToken`,
`Matrix__AdminToken` — per environment, alongside the Didit and Zammad secrets. Synapse cannot read
Vault, so the deploy renders its config files from the same values.

```bash
cp conf/99-secrets.yaml.example    conf/99-secrets.yaml
cp bee-appservice-dev.yaml.example conf/bee-appservice-dev.yaml
# then substitute every REPLACE_FROM_VAULT
```

All rendered files are gitignored. Never commit them.

### 3. Signing key

```bash
docker run --rm -v beeapp_synapse_data:/data \
  -e SYNAPSE_SERVER_NAME=matrix.bee-app.tech -e SYNAPSE_REPORT_STATS=no \
  matrixdotorg/synapse:latest generate
```

This writes `/data/signing.key`. **Losing it is not recoverable** — back it up with the database.
Delete the `homeserver.yaml` the generator also drops into `/data`; ours lives in `/config`.

### 4. Run

```bash
docker compose --profile matrix up -d synapse
curl -fsS http://localhost:8008/_matrix/client/versions
```

The backend surfaces the same check as `matrix` on `/health/ready`. It reports **Degraded, never
Unhealthy** — a chat outage must not pull the API out of rotation.

### 5. Reverse proxy / Cloudflare

Point `matrix.bee-app.tech` at `10.10.10.152:8008`. `server_name` is the same hostname, so there is
no `.well-known` file to serve anywhere.

Four Cloudflare settings matter:

| Setting | Why |
|---|---|
| **Bot Fight Mode off / WAF skip for `/_matrix/*`** | The one that will bite. Matrix clients are non-browser agents; Cloudflare answers those with 403 "error code: 1010" — the same failure already hit on the GitLab API. |
| **Cache bypass for `/_matrix/*`** | JSON is not in the default cache set, so this is belt-and-braces, but a cached `/sync` would be a baffling bug. |
| **Client sync timeout under ~90s** | Cloudflare cuts connections at 100s (error 524) and `/sync` long-polls. `matrix-js-sdk` defaults to 30s, so this only bites if someone tunes it up. |
| Upload limit | Cloudflare free allows 100 MB; `max_upload_size` is 10 MB. No conflict. |

Plus one on the **other** hostname: `appservice.bee-app.tech` needs its own **WAF skip / Bot Fight
Mode off**, because that is where on-prem Synapse pushes room events to the cloud production
backend. It is a separate hostname precisely so this exemption never has to be applied to
`api.bee-app.tech`.

**Both appservice callbacks go through Cloudflare**, including dev's — see
[Topology](#topology) for why that is deliberate. So the WAF skip applies to
`dev-api.bee-app.tech` now and `appservice.bee-app.tech` when production lands. Currently
unblocked: verified with `Synapse/1.159.0` as the user agent.

### 6. Admin token (retention job only)

The appservice has no admin rights and should not. The retention job purges expired rooms with a
separate Synapse admin account's token:

```bash
docker exec -it beeapp-synapse register_new_matrix_user \
  -c /config/10-homeserver.yaml -c /config/99-secrets.yaml -a -u beeadmin http://localhost:8008
```

Registration is closed, so this CLI is the only way in. Log in once as that account to mint a token
and store it as `Matrix__AdminToken` in Vault.

## Securing a public homeserver

`matrix.bee-app.tech` is internet-facing and always had to be — drivers are on cellular, not the
LAN. What follows is what stops it becoming someone else's chat server.

### Verified from the public internet

| Probe | Result |
|---|---|
| `POST /_matrix/client/v3/register` | `403 M_FORBIDDEN` — registration disabled |
| `GET /_matrix/federation/v1/version` | `404` — no federation resource is served |
| `GET /_matrix/client/v3/publicRooms` | `401` — no unauthenticated room discovery |
| `GET /_synapse/admin/v1/rooms` | `404` — the edge block below is in place. Was `401` (reachable, token required) before it was applied |

### Applied on the host

**Firewall.** Ingress is Cloudflare → NPM (`10.10.10.4`) → Synapse. `ufw` allows `8008` from
**only** `10.10.10.4` (the proxy) and `10.10.10.3` (the on-prem dev backend). Everything else on
the LAN is denied, so a compromised host elsewhere in the homelab cannot reach Synapse directly.
Production arrives through `.4` like any other internet client.

> When changing these rules over SSH, arm a dead-man switch first:
> `systemd-run --on-active=300 --unit=ufw-deadman /usr/sbin/ufw --force disable`, then cancel it
> with `systemctl stop ufw-deadman.timer` once you have confirmed you still have a shell.

**Rate limits** (`conf.d/20-bee-hardening.yaml`). `@beeadmin` is the only account with a password
and `m.login.password` is advertised publicly, so login is the one brute-forceable surface.
Verified: three bad attempts, then `M_LIMIT_EXCEEDED`. Appservices set `rate_limited: false` in
their registration files, so this cannot throttle room provisioning — verified separately.

Also set there: `allow_guest_access: false`, and public room directory off with and without
federation.

### At the Cloudflare edge

These cannot be done from the host:

| Rule | State | Why |
|---|---|---|
| **Block `/_synapse/*`** | **applied** — see the conflict below | The admin API has no business being internet-reachable. Note `/_synapse/admin/v1/server_version` answers **unauthenticated** and hands out the exact Synapse version — free reconnaissance. |
| **Block `/_matrix/static/*`** | **applied** | Serves the "Synapse is running" landing page. Pure fingerprinting; no client needs it. |
| **Bot Fight Mode off / WAF skip on `/_matrix/*`** | unverified | Matrix clients are non-browser agents and Cloudflare answers those with 403 "error code: 1010" — the failure already hit on the GitLab API. Without this the apps simply cannot connect. |
| **Same skip on `appservice.bee-app.tech`** | not needed yet | Where on-prem Synapse pushes room events to the cloud production backend, with `Synapse/1.x` as its user agent. Separate hostname so this exemption never touches `api.bee-app.tech`. Only bites once production is loaded. |

States above were re-probed from the public internet on 2026-08-23. "Unverified" means exactly that:
a plain `curl` — itself a non-browser agent — gets 200 from `/_matrix/client/versions`, which is
encouraging but is not proof that a real client under Bot Fight Mode would. Confirm with an actual
Matrix client before trusting it.

#### Room purging goes around this block, on purpose

**The retention job needs the path that block closes.** `MatrixAppServiceClient` issues
`DELETE _synapse/admin/v2/rooms/{roomId}` with `Matrix:AdminToken`, driven by the
`booking-chat-purge` Hangfire job at 03:40 UTC daily. `/_synapse/*` on the public hostname answers
**404** — from the proxy, not from Synapse; the body is openresty's, and Synapse's admin API is
healthy on the host.

This applied to **dev as much as production**. Dev deliberately reaches Synapse over the public path
(`MATRIX_HOMESERVER_URL=https://matrix.bee-app.tech`, see [Topology](#topology)) rather than taking
the LAN shortcut, so its admin calls went the same way and hit the same 404.

**How this failed is worse than not purging.** `PurgeRoomAsync` used to treat any 404 as "the room
is already gone", which is right when Synapse says it and badly wrong when a proxy does: the sweep
marked every room purged, committed that, stopped retrying them, and logged
`"Purged N of N expired rooms"` while every room was still on the homeserver. A silent no-op would
have been the *good* version of this bug.

Fixed in two parts (issue #81):

1. **`Matrix:AdminApiUrl`** sends admin calls to the host directly — `http://10.10.10.152:8008` on
   dev. It falls back to `Matrix:HomeserverUrl` when unset, so an environment that does not set it
   is unaffected. **Only** `PurgeRoomAsync` uses it; every client-server and appservice call keeps
   the public path, so the parity the [Topology](#topology) decision is about still holds. The admin
   API is backend-only and never client-facing, which is what makes this a different question from
   where client traffic goes.
2. **A 404 is only "already gone" if it carries a Matrix error body.** A proxy's HTML 404 now throws
   with the host named in the message. This is the half that matters: it makes any future
   misrouting loud, whatever the cause.

The alternative — an IP allowlist carve-out in the `/_synapse/*` block — was rejected. It keeps the
admin API internet-exposed for one source, needs per-environment upkeep, and buys nothing over
talking to the host on a network where the firewall already permits it.

Verify by watching a `booking-chat-purge` run, not by curling the endpoint: the job authenticates
with `AdminToken`, and an unauthenticated probe cannot tell "blocked at the proxy" from "reachable,
token required".

### Optional, if you want the surface smaller

Setting `password_config.enabled: false` removes password login entirely, leaving no
brute-forceable endpoint at all — the appservice tokens and the stored `ADMIN_TOKEN` keep working.
The cost is recovery: minting a *new* admin token would mean re-enabling it temporarily and
restarting. Left on for now because a 160-bit password behind a 3-attempt limit is not the weak
link.

## What is deliberately off

| Setting | Why |
|---|---|
| Federation (no 8448 listener, empty `federation_domain_whitelist`) | These rooms are two people and a bot from one platform. Federation is pure attack surface. |
| Registration | Every account is created by an appservice. No signup, no password login, no shared secret to leak. |
| Presence | "Where is the driver" is answered by the Map module, not by Synapse fanning out presence on every sync. |
| URL previews | Would have Synapse fetch arbitrary URLs pasted into a booking room — SSRF surface for no product value. |
| Synapse-side retention | The authoritative transcript is `messaging.room_events` in bee's Postgres. Purging Synapse reclaims storage; it does not destroy history. |
| **E2EE** | Booking rooms are unencrypted on purpose. Admin/dispute access requires the appservice to read events, and encryption would leave it holding ciphertext. TLS in transit, Postgres encryption at rest. |
