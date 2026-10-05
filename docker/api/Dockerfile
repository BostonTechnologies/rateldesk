# 1) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8222
# Keep GSS authentication and IANA time zones on the supported glibc runtime.
# The Alpine legacy hostname resolver can corrupt allocations during concurrent GSS negotiation.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 libicu74 tzdata wget \
 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
EXPOSE 8222 8222

# 2) Build
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TIMESTAMP=

# Copy global build inputs and the target dependency graph first for a reusable restore layer.
COPY global.json ./
COPY Directory.Build.props Directory.Build.targets ./
COPY src/Helpdesk.API/*.csproj             src/Helpdesk.API/
COPY src/Helpdesk.Application/*.csproj     src/Helpdesk.Application/
COPY src/Helpdesk.Infrastructure/*.csproj  src/Helpdesk.Infrastructure/
COPY src/Helpdesk.Infrastructure.SqliteMigrations/*.csproj src/Helpdesk.Infrastructure.SqliteMigrations/
COPY src/Helpdesk.Shared/*.csproj          src/Helpdesk.Shared/
COPY src/Helpdesk.ServiceDefaults/*.csproj src/Helpdesk.ServiceDefaults/

RUN dotnet restore src/Helpdesk.API/Helpdesk.API.csproj

# Copy only the source projects included in the API dependency graph.
COPY src/Helpdesk.API/             src/Helpdesk.API/
COPY src/Helpdesk.Application/     src/Helpdesk.Application/
COPY src/Helpdesk.Infrastructure/  src/Helpdesk.Infrastructure/
COPY src/Helpdesk.Infrastructure.SqliteMigrations/ src/Helpdesk.Infrastructure.SqliteMigrations/
COPY src/Helpdesk.Shared/          src/Helpdesk.Shared/
COPY src/Helpdesk.ServiceDefaults/ src/Helpdesk.ServiceDefaults/

# Publish (no-restore to use cached restore layer)
RUN mkdir /out \
 && dotnet publish src/Helpdesk.API/Helpdesk.API.csproj \
      -c $BUILD_CONFIGURATION -o /out --no-restore -v:n \
      -p:UseAppHost=false \
      -p:Version=$VERSION \
      -p:SourceRevisionId=$SOURCE_REVISION_ID \
      -p:BuildTimestamp=$BUILD_TIMESTAMP \
      -p:ContinuousIntegrationBuild=true

# 3) Final
FROM base AS final
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "Helpdesk.API.dll"]
