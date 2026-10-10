# ArcaneCore container images (docs/guide/docker.md), modelled on AzerothCore apps/docker/Dockerfile:
# one multi-stage file, a shared build stage and one runtime target per daemon (worldserver, authserver there;
# world, realm here) plus a tools target for the content importer and database upgrade.
#   docker build -f apps/docker/Dockerfile --target world -t arcanecore/world .
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG CONFIGURATION=Release
WORKDIR /src
# Restore first so the package layer is cached across source edits.
COPY global.json Directory.Build.props ArcaneCore.slnx ./
COPY src/ src/
COPY tools/ tools/
RUN dotnet restore src/ArcaneCore.World/ArcaneCore.World.csproj \
 && dotnet restore src/ArcaneCore.Realm/ArcaneCore.Realm.csproj \
 && dotnet restore tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj \
 && dotnet restore tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj
RUN dotnet publish src/ArcaneCore.World/ArcaneCore.World.csproj -c $CONFIGURATION --no-restore -o /out/world \
 && dotnet publish src/ArcaneCore.Realm/ArcaneCore.Realm.csproj -c $CONFIGURATION --no-restore -o /out/realm \
 && dotnet publish tools/ArcaneCore.ContentImporter/ArcaneCore.ContentImporter.csproj -c $CONFIGURATION --no-restore -o /out/tools \
 && dotnet publish tools/ArcaneCore.DbUpgrade/ArcaneCore.DbUpgrade.csproj -c $CONFIGURATION --no-restore -o /out/dbupgrade

# Shared runtime skeleton (AzerothCore "skeleton"/"runtime"): non-root user, data and log directories.
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime
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

# AzerothCore "db-import"/"tools": one-shot helpers, run with `docker compose run --rm tools ...`.
FROM runtime AS tools
ARG USER_ID=1000
ARG GROUP_ID=1000
WORKDIR /arcanecore/tools
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/tools/ ./
COPY --from=build --chown=${USER_ID}:${GROUP_ID} /out/dbupgrade/ /arcanecore/dbupgrade/
ENTRYPOINT ["dotnet", "arcane-content-importer.dll"]
