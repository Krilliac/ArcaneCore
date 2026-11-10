# ArcaneCore container images (docs/guide/docker.md), modelled on AzerothCore apps/docker/Dockerfile:
# one multi-stage file, a shared build stage and one runtime target per daemon (worldserver, authserver there;
# world, realm here) plus a tools target for the content importer and database upgrade.
#   docker build -f apps/docker/Dockerfile --target world -t arcanecore/world .
#
# Base images use stable .NET 10 SDK 10.0.403 and runtime 10.0.14 to match global.json and net10.0.
# The old .NET 11 RC image digests do not apply to these images. Verify and pin their multi-arch digests
# before deploying where immutable base-image inputs are required; CI smoke-builds all targets.

# --platform=$BUILDPLATFORM: the build stage always runs natively on the builder. The published output is framework-dependent (no
# RuntimeIdentifier, no ReadyToRun), so it is architecture-neutral and a linux/arm64 image (docker-publish.yml) needs no emulation
# of the compile; only the small runtime stages below are per-platform. The apphost the publish also writes is never used (the
# entrypoints are `dotnet X.dll`).
# CONFIGURATION: Release by default; pass --build-arg CONFIGURATION=Debug for a Debug image (docs/guide/docker.md, "Debug or Release").
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ARG CONFIGURATION=Release
WORKDIR /src
# .editorconfig: the in-image build enforces the same code-style rules as CI (EnforceCodeStyleInBuild, warnings are errors).
COPY global.json Directory.Build.props Directory.Packages.props ArcaneCore.slnx .editorconfig ./
COPY src/ src/
COPY tools/ tools/
# Locked mode: the restore must match the packages.net10.lock.json committed beside each project (exact versions and content hashes),
# so the image cannot build against a package graph CI never saw.
RUN dotnet restore src/ArcaneCore.World/ArcaneCore.World.csproj --locked-mode && dotnet restore src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj --locked-mode \
 && dotnet restore src/ArcaneCore.Realm/ArcaneCore.Realm.csproj --locked-mode \
 && dotnet restore tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj --locked-mode \
 && dotnet restore tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj --locked-mode \
 && dotnet restore tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj --locked-mode
# The Playerbots pack (docs/design/hot-reload-tiers.md, 4.6) references World, so World cannot reference it: it is published on its
# own, and its PublishPackBesideWorldHost target (WorldHostPublishDir) places the assembly beside the world executable, where Program.cs
# loads it (its packages are all World's).
RUN dotnet publish src/ArcaneCore.World/ArcaneCore.World.csproj -c $CONFIGURATION --no-restore -o /out/world && dotnet publish src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj -c $CONFIGURATION --no-restore -o /out/playerbots -p:WorldHostPublishDir=/out/world/ \
 && dotnet publish src/ArcaneCore.Realm/ArcaneCore.Realm.csproj -c $CONFIGURATION --no-restore -o /out/realm \
 && dotnet publish tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj -c $CONFIGURATION --no-restore -o /out/tools \
 && dotnet publish tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj -c $CONFIGURATION --no-restore -o /out/dbupgrade \
 && dotnet publish tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj -c $CONFIGURATION --no-restore -o /out/account

# Shared runtime skeleton (AzerothCore "skeleton"/"runtime"): non-root user, data and log directories.
FROM mcr.microsoft.com/dotnet/runtime:10.0.14 AS runtime
ARG USER_ID=1000
ARG GROUP_ID=1000
# No DOTNET_gcServer here: an environment setting overrides runtimeconfig.json, and the World ships Server GC on purpose
# (ArcaneCore.World.csproj, measured in docs/integration/perf-limits-20261008.md). Set DOTNET_gcServer=0 per service to opt out.
ENV DOTNET_TieredPGO=1 \
    TZ=Etc/UTC
RUN (getent group ${GROUP_ID} || groupadd -g ${GROUP_ID} arcane) \
 && (getent passwd ${USER_ID} || useradd -u ${USER_ID} -g ${GROUP_ID} -m arcane) \
 && mkdir -p /arcanecore/data /arcanecore/logs \
 && chown -R ${USER_ID}:${GROUP_ID} /arcanecore
USER ${USER_ID}:${GROUP_ID}

FROM runtime AS world
ARG USER_ID=1000
ARG GROUP_ID=1000
WORKDIR /arcanecore/world
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/world/ ./
# 8085 world, 9464 Prometheus metrics (Ops:Metrics, off unless enabled).
EXPOSE 8085 9464
VOLUME ["/arcanecore/data", "/arcanecore/logs"]
ENTRYPOINT ["dotnet", "ArcaneCore.World.dll"]

FROM runtime AS realm
ARG USER_ID=1000
ARG GROUP_ID=1000
WORKDIR /arcanecore/realm
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/realm/ ./
EXPOSE 3724 9465
VOLUME ["/arcanecore/logs"]
ENTRYPOINT ["dotnet", "ArcaneCore.Realm.dll"]

# AzerothCore "db-import"/"tools": one-shot helpers, run with `docker compose run --rm tools ...`. The entrypoint is the content importer;
# the database upgrade tool (/arcanecore/dbupgrade/arcane-db.dll) and the account tool (/arcanecore/account/arcane-account.dll) are run
# with `--entrypoint dotnet`.
FROM runtime AS tools
ARG USER_ID=1000
ARG GROUP_ID=1000
WORKDIR /arcanecore/tools
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/tools/ ./
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/dbupgrade/ /arcanecore/dbupgrade/
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/account/ /arcanecore/account/
ENTRYPOINT ["dotnet", "arcane-content-importer.dll"]
