# ArcaneCore container images (docs/guide/docker.md), modelled on AzerothCore apps/docker/Dockerfile:
# one multi-stage file, a shared build stage and one runtime target per daemon (worldserver, authserver there;
# world, realm here) plus a tools target for the content importer and database upgrade.
#   docker build -f apps/docker/Dockerfile --target world -t arcanecore/world .
#
# Base images: the SDK tag must satisfy global.json (11.0.100-rc.2.26504.105, rollForward latestFeature) and the runtime tag must
# run net11.0 (Directory.Build.props). Both are pinned by manifest-list digest (multi-arch) so a re-pushed tag cannot change the
# build; Dependabot (.github/dependabot.yml, docker ecosystem) proposes tag and digest bumps together. When global.json moves to a
# new SDK, move these tags with it: .github/workflows/docker-smoke.yml builds every target so a mismatch fails CI.

# --platform=$BUILDPLATFORM: the build stage always runs natively on the builder. The published output is framework-dependent (no
# RuntimeIdentifier, no ReadyToRun), so it is architecture-neutral and a linux/arm64 image (docker-publish.yml) needs no emulation
# of the compile; only the small runtime stages below are per-platform. The apphost the publish also writes is never used (the
# entrypoints are `dotnet X.dll`).
# CONFIGURATION: Release by default; pass --build-arg CONFIGURATION=Debug for a Debug image (docs/guide/docker.md, "Debug or Release").
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.2@sha256:e2438fd800a09d2d761aefe4edd30675c8f1db8958e4423c0844cc9a0240e60f AS build
ARG CONFIGURATION=Release
WORKDIR /src
# .editorconfig: the in-image build enforces the same code-style rules as CI (EnforceCodeStyleInBuild, warnings are errors).
COPY global.json Directory.Build.props Directory.Packages.props ArcaneCore.slnx .editorconfig ./
COPY src/ src/
COPY tools/ tools/
# Locked mode: the restore must match the packages.lock.json committed beside each project (exact versions and content hashes),
# so the image cannot build against a package graph CI never saw.
RUN dotnet restore src/ArcaneCore.World/ArcaneCore.World.csproj --locked-mode && dotnet restore src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj --locked-mode \
 && dotnet restore src/ArcaneCore.Realm/ArcaneCore.Realm.csproj --locked-mode \
 && dotnet restore tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj --locked-mode \
 && dotnet restore tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj --locked-mode \
 && dotnet restore tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj --locked-mode
# The Playerbots pack (docs/design/hot-reload-tiers.md, 4.6) references World, so World cannot reference it: it is published on its
# own and its assembly placed beside the world executable, where Program.cs loads it (its packages are all World's).
RUN dotnet publish src/ArcaneCore.World/ArcaneCore.World.csproj -c $CONFIGURATION --no-restore -o /out/world && dotnet publish src/ArcaneCore.Playerbots/ArcaneCore.Playerbots.csproj -c $CONFIGURATION --no-restore -o /out/playerbots && cp /out/playerbots/ArcaneCore.Playerbots.dll /out/playerbots/ArcaneCore.Playerbots.pdb /out/world/ \
 && dotnet publish src/ArcaneCore.Realm/ArcaneCore.Realm.csproj -c $CONFIGURATION --no-restore -o /out/realm \
 && dotnet publish tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj -c $CONFIGURATION --no-restore -o /out/tools \
 && dotnet publish tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj -c $CONFIGURATION --no-restore -o /out/dbupgrade \
 && dotnet publish tools/ArcaneCore.AccountTool/ArcaneCore.AccountTool.csproj -c $CONFIGURATION --no-restore -o /out/account

# Shared runtime skeleton (AzerothCore "skeleton"/"runtime"): non-root user, data and log directories.
FROM mcr.microsoft.com/dotnet/runtime:11.0.0-rc.2@sha256:c2fe4a619dab039d442e63ca56ed3ab1c10d0a0a59b778741d002bbb53c6c637 AS runtime
ARG USER_ID=1000
ARG GROUP_ID=1000
ENV DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1 \
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
