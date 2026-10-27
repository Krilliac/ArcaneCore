# Running ArcaneCore in Docker

Modelled on AzerothCore's `apps/docker` and `docker-compose.yml`: one multi-stage `apps/docker/Dockerfile` with a
`world`, `realm` and `tools` target, and a compose file with the database, the two daemons, one-shot tools and an
optional monitoring profile.

```sh
cp apps/docker/.env.example .env      # set both passwords (required) and ARCANE_REALM_ADDRESS
mkdir -p env/data env/logs            # put your extracted dbc/, maps/, vmaps/, mmaps/ in env/data
docker compose up -d --build
```

- **Security**: the database credentials have no defaults; `docker compose` stops with an error until `.env` sets
  `DOCKER_DB_ROOT_PASSWORD`, `DOCKER_DB_USER` and `DOCKER_DB_PASSWORD`. Only the realm (3724) and world (8085) ports are
  published on every interface. MariaDB (3306), Prometheus (9090) and Grafana (3000, anonymous viewer enabled) are
  published on `127.0.0.1` only; set `DOCKER_DB_EXTERNAL_BIND`, `DOCKER_PROMETHEUS_EXTERNAL_BIND` or
  `DOCKER_GRAFANA_EXTERNAL_BIND` to `0.0.0.0` to expose one, behind a firewall. The application user has every privilege on
  the three ArcaneCore schemas, so anyone who reaches the database with it controls the realm.
- **Database**: MariaDB 11.4. On first start of the volume `apps/docker/db-init` creates `arcanecore_auth`,
  `arcanecore_characters` and `arcanecore_world`; the daemons create and upgrade the tables
  (the default `Database:Upgrade` policy).
- **World content**: import ClassicDB with the tools image, for example
  `docker compose --profile tools run --rm arcane-tools --help` (files under `env/import` are at `/arcanecore/import`).
- **Configuration**: any key as an environment variable with `__` for `:` (docs/reference/configuration.md), in
  `docker-compose.override.yml`. The heartbeat is set to `Stdout`; `docker compose stop` gives the world 90 s to drain saves.
- **Client data**: nothing ships. `env/data` is mounted read-only at `/arcanecore/data`.
- **Accounts**: the tools image carries the account tool (`arcane-account`) and `arcane-db` beside the content importer. Compose gives
  the tools service one connection string per schema (`Database__Auth__`, `Database__Characters__` and `Database__World__ConnectionString`),
  because `arcane-db` resolves each component separately and a component without its own string would fall back to the auth one. Run them with `--entrypoint dotnet`:
  `docker compose --profile tools run --rm --entrypoint dotnet arcane-tools /arcanecore/account/arcane-account.dll create NAME`
  (it prompts for the password; or pass `-e ARCANE_ACCOUNT_PASSWORD`), and `... /arcanecore/dbupgrade/arcane-db.dll status`.
- **Monitoring**: `ARCANE_METRICS_ENABLED=true docker compose --profile monitoring up -d` starts Prometheus (:9090)
  and Grafana (:3000) with the sample dashboard (docs/ops/metrics.md) provisioned. Prometheus scrapes two targets
  (`apps/docker/prometheus.yml`): `arcane-world:9464` (`Ops__Metrics__PrometheusPrefix`) and `arcane-realm:9465`
  (`Ops__Metrics__RealmPrometheusPrefix`; the Realm daemon reads only its own key). These ports stay on the compose
  network and are not published.

## Debug or Release

The `CONFIGURATION` build argument of `apps/docker/Dockerfile` defaults to `Release`. The live server runs the Debug configuration, and
the images follow that when asked: `docker build -f apps/docker/Dockerfile --target world --build-arg CONFIGURATION=Debug -t arcanecore/world:debug .`
(compose: `args: CONFIGURATION: Debug` under `build:` in `docker-compose.override.yml`). Both are published: `edge` is Debug and `edge-release` is Release
(see below).

## Image tags and the registry

[`.github/workflows/docker-publish.yml`](../../.github/workflows/docker-publish.yml) builds `world`, `realm` and `tools` in both
configurations (Debug and Release) for `linux/amd64` and `linux/arm64` and pushes them to `ghcr.io/krilliac/arcanecore-world`,
`arcanecore-realm` and `arcanecore-tools`. The owner and repository names are hard-coded in the docs and in `docker-compose.prod.yml`
(`ARCANE_IMAGE_REPO` overrides the image path); change them when the repository moves.

| Run | Tags pushed per image |
|---|---|
| Nightly (cron) | `edge` (Debug), `edge-release` (Release), `sha-<7 hex>-debug`, `sha-<7 hex>-release` |
| `v*` tag | the tag (Release) and `<tag>-debug` (Debug); no `sha-` tag |
| Hand run on a branch | `manual-sha-<7 hex>-debug` and `manual-sha-<7 hex>-release`, a prefix no nightly tag has |

Every tag names its configuration, so the two configurations and the different kinds of run never overwrite each other's tags. A hand run
on a `v*` tag is refused, because it would re-push the release tag. The nightly run is skipped when `main` has no new commit since the last
successful scheduled run; a push to `main` does not start it. A pull request that touches the Dockerfile, the build props, a project file or
a lock file (not C# sources alone; `docker-smoke` builds the amd64 image from those) runs the same multi-arch build without pushing, so a
`linux/arm64` failure fails that check (it is not best effort there).

Outside pull requests `linux/arm64` is best effort: if the multi-arch build fails before anything was pushed, the same tags are pushed for
`linux/amd64` only, the run stays green, and the run summary and a warning annotation name the image and configuration that lack arm64.
The skip check compares commits only, so a missed arm64 image is not retried until `main` moves (or by a hand run). Git tags ending in
`-debug` are reserved: the workflow refuses a `v*-debug` tag.

GHCR packages start **private**. After the first push, the repository owner makes each of the three packages public once (GitHub:
profile or organization, Packages, the package, Package settings, Change visibility, Public) so a VPS can pull without a login; until
then `docker pull` answers `denied` and a VPS needs `docker login ghcr.io` with a token that has `read:packages`. The workflow never
changes visibility.

### Pruning

Old `sha-` tags are not pruned yet (about 4 new tags per image per night). Deleting package versions needs a token with package
delete permission on the package, and a multi-arch image is an index plus per-platform manifests that are untagged versions: a naive
"delete old versions" would delete manifests that a live tag still needs. That cannot be verified without a first push, so it is a
follow-up: after the packages exist, add a pruning job that deletes only versions whose tags are all `sha-*` and older than 30 days,
and check it on a copy first.

Pick a version and pull it: set `ARCANE_IMAGE_TAG` in `.env` (for example to `edge`, or to a release tag), then
`docker compose -f docker-compose.prod.yml pull`.

## Deploying to a VPS

The production file [`docker-compose.prod.yml`](../../docker-compose.prod.yml) runs the same services as the development file but pulls
`ghcr.io/krilliac/arcanecore-{realm,world,tools}:${ARCANE_IMAGE_TAG}` (nothing is built on the VPS; `ARCANE_IMAGE_REPO` points it at a
fork's images), rotates every service's `json-file` log (5 files of 10 MB), restarts everything `unless-stopped` and keeps the daemon
logs in a named volume. Each daemon has a healthcheck that opens a TCP connection to its own port from inside the container: the
runtime image has no `curl`, and neither daemon serves an HTTP health endpoint (the Prometheus listeners are off unless
`Ops:Metrics:Enabled` is set). The realm opens 3724 after its auth schema is current, the world opens 8085 after all three schemas are.

1. **Host**: any Linux host with Docker Engine and the compose plugin. The images are multi-arch, so x86-64 and arm64 hosts both work.
   Check the repository out at the release tag (the compose file mounts `apps/docker/db-init` and the monitoring files from it).
2. **Sizing**: the repository holds no measured RAM or CPU figures, so measure on your own load. Start the stack, let players or the
   playerbots run, and read `docker stats`; the world and realm export tick, packet and memory series through `Ops:Metrics`
   (docs/ops/metrics.md), which the monitoring profile graphs. The MariaDB buffer pool is the other large consumer. Size the VPS from
   the peak you see, plus headroom for the 90 s shutdown save drain and for `mariadb-dump`.
3. **Configure**: `cp apps/docker/.env.example .env`; set the two passwords, `ARCANE_IMAGE_TAG` and
   `ARCANE_REALM_ADDRESS=<public IP>:8085` (the address the realm list hands to clients, so it must be reachable from players).
4. **Firewall**: expose only **3724** (logon) and **8085** (world). MariaDB, Prometheus and Grafana already bind `127.0.0.1` on the
   host; reach them from your machine through an SSH tunnel (`ssh -L 3000:127.0.0.1:3000 user@vps`), and leave
   `DOCKER_*_EXTERNAL_BIND` alone. Docker publishes ports by editing iptables ahead of host firewalls such as ufw, so a ufw deny
   rule does not close a published port: do not publish what must stay private, and filter at the provider's firewall too.
5. **Client data**: nothing ships. Extract `dbc/`, `maps/`, `vmaps/` and `mmaps/` from your own 1.12.1 client and upload them to the
   directory `DOCKER_VOL_DATA` names (default `./env/data`, mounted read-only at `/arcanecore/data`), for example
   `rsync -a data/ user@vps:/path/to/checkout/env/data/`. The world sets `ClientData:DbcDirectory` to `/arcanecore/data/dbc` and
   `World:Maps:DataDirectory` to `/arcanecore/data`. Without the files it still starts and warns once per missing DBC;
   `ClientData:Strict` (`ClientData__Strict=true`) makes that fatal.
6. **Start**: `docker compose -f docker-compose.prod.yml pull && docker compose -f docker-compose.prod.yml up -d`, then
   `docker compose -f docker-compose.prod.yml ps` until the three services are `healthy`. Create the first account with the tools
   image (see Accounts above, with `-f docker-compose.prod.yml`).

### Moving an existing MariaDB

Dump the three schemas on the old host, then restore them into the container **before the realm and world first start**:

```sh
mariadb-dump -u USER -p --single-transaction --routines --events --databases arcanecore_auth arcanecore_characters arcanecore_world > arcanecore.sql
docker compose -f docker-compose.prod.yml up -d arcane-database
docker compose -f docker-compose.prod.yml exec -T arcane-database sh -c 'export MYSQL_PWD="$MARIADB_ROOT_PASSWORD"; exec mariadb -uroot' < arcanecore.sql
docker compose -f docker-compose.prod.yml up -d
```

The first start of the empty volume creates the schemas and the application user's grants (`apps/docker/db-init`); the restore then
replaces their contents. On start the daemons bring the schema to the image's version under `Database:Upgrade` (the default
policy upgrades); for a deliberate, reviewed upgrade run `arcane-db plan` first, as in the [upgrade runbook](../ops/database-upgrade.md).

### Updating

Set the new `ARCANE_IMAGE_TAG`, check the repository out at the same tag, take a backup (below), then
`docker compose -f docker-compose.prod.yml pull && docker compose -f docker-compose.prod.yml up -d`. Compose recreates only the
containers whose image changed; the world gets 90 s to save. To go back, set the old tag and run `up -d` again (a schema upgrade the
new version already applied is not undone: restore the backup for that).

### Backups

[`tools/ops/deploy/backup-database.sh`](../../tools/ops/deploy/backup-database.sh) dumps the three schemas into
`arcanecore-<UTC time>.sql.gz` under `BACKUP_DIR` and deletes dumps older than `KEEP_DAYS` (default 14). It runs `mariadb-dump`
inside the database container with `--single-transaction`, so the realm and world keep running. The root password reaches it through the
`MYSQL_PWD` environment variable inside the container, so it is on no command line. `BACKUP_DIR` defaults to `/var/backups/arcanecore`
(outside the checkout; the dumps hold every account and character, and `*.sql.gz` is git-ignored and refused by the repository hygiene test):

```sh
COMPOSE_FILE=docker-compose.prod.yml BACKUP_DIR=/srv/backups tools/ops/deploy/backup-database.sh
# cron, daily 04:17:   17 4 * * *  cd /path/to/checkout && COMPOSE_FILE=docker-compose.prod.yml BACKUP_DIR=/srv/backups tools/ops/deploy/backup-database.sh
```

By hand:

```sh
docker compose -f docker-compose.prod.yml exec -T arcane-database sh -c 'export MYSQL_PWD="$MARIADB_ROOT_PASSWORD"; exec mariadb-dump -uroot --single-transaction --databases arcanecore_auth arcanecore_characters arcanecore_world' | gzip > backup.sql.gz
gunzip -c backup.sql.gz | docker compose -f docker-compose.prod.yml exec -T arcane-database sh -c 'export MYSQL_PWD="$MARIADB_ROOT_PASSWORD"; exec mariadb -uroot'   # restore, with the realm and world stopped
```

Copy the dumps off the VPS; a backup on the same disk is not one.

## Not in an image

- **Gateway** (`src/ArcaneCore.Gateway`): not an image. It is documented as built and mock-client tested but not accepted with a real
  client, needs a world in `Cluster:Worker` mode with a shared link secret and matching revision and schema versions, and loses its
  clients on any restart (docs/areas/gateway.md).
- **Atlas** (`tools/ArcaneCore.Atlas`): not an image. It references `tests/ThirdParty` (DotRecast), which `.dockerignore` excludes, so an
  image would need that folder in the build context and a restore of the Atlas project.
