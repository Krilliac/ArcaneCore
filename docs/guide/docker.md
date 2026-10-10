# Running ArcaneCore in Docker

Modelled on AzerothCore's `apps/docker` and `docker-compose.yml`: one multi-stage `apps/docker/Dockerfile` with a
`world`, `realm` and `tools` target, and a compose file with the database, the two daemons, one-shot tools and an
optional monitoring profile.

```sh
cp apps/docker/.env.example .env      # set passwords and ARCANE_REALM_ADDRESS
mkdir -p env/data env/logs            # put your extracted dbc/, maps/, vmaps/, mmaps/ in env/data
docker compose up -d --build
```

- **Database**: MariaDB 11.4. On first start of the volume `apps/docker/db-init` creates `arcanecore_auth`,
  `arcanecore_characters` and `arcanecore_world`; the daemons create and upgrade the tables
  (the default `Database:Upgrade` policy).
- **World content**: import ClassicDB with the tools image, for example
  `docker compose --profile tools run --rm arcane-tools --help` (files under `env/import` are at `/arcanecore/import`).
- **Configuration**: any key as an environment variable with `__` for `:` (docs/reference/configuration.md), in
  `docker-compose.override.yml`. The heartbeat is set to `Stdout`; `docker compose stop` gives the world 90 s to drain saves.
- **Client data**: nothing ships. `env/data` is mounted read-only at `/arcanecore/data`.
- **Accounts**: `ArcaneCore.AccountTool` is not in an image yet; create accounts from a host build or a GM command.
- **Monitoring**: `ARCANE_METRICS_ENABLED=true docker compose --profile monitoring up -d` starts Prometheus (:9090)
  and Grafana (:3000) with the sample dashboard (docs/ops/metrics.md) provisioned.
