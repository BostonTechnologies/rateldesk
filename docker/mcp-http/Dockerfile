FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.23 AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8223
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
RUN apk add --no-cache icu-libs
RUN addgroup -S -g 10001 helpdesk && adduser -S -D -H -u 10001 -G helpdesk helpdesk
EXPOSE 8223
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 CMD wget -q -O /dev/null http://127.0.0.1:8223/health/live || exit 1

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23 AS build
WORKDIR /src
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TIMESTAMP=

COPY global.json ./
COPY Directory.Build.props Directory.Build.targets ./
COPY src/Helpdesk.AgentClient/*.csproj      src/Helpdesk.AgentClient/
COPY src/Helpdesk.Mcp.Core/*.csproj         src/Helpdesk.Mcp.Core/
COPY src/Helpdesk.Mcp.Http/*.csproj         src/Helpdesk.Mcp.Http/
COPY src/Helpdesk.ServiceDefaults/*.csproj  src/Helpdesk.ServiceDefaults/
COPY src/Helpdesk.Shared/*.csproj           src/Helpdesk.Shared/
COPY src/Helpdesk.Setup/*.csproj            src/Helpdesk.Setup/
RUN dotnet restore src/Helpdesk.Mcp.Http/Helpdesk.Mcp.Http.csproj

COPY src/Helpdesk.AgentClient/      src/Helpdesk.AgentClient/
COPY src/Helpdesk.Mcp.Core/         src/Helpdesk.Mcp.Core/
COPY src/Helpdesk.Mcp.Http/         src/Helpdesk.Mcp.Http/
COPY src/Helpdesk.ServiceDefaults/  src/Helpdesk.ServiceDefaults/
COPY src/Helpdesk.Shared/           src/Helpdesk.Shared/
COPY src/Helpdesk.Setup/            src/Helpdesk.Setup/
RUN mkdir /out \
 && dotnet publish src/Helpdesk.Mcp.Http/Helpdesk.Mcp.Http.csproj \
      -c $BUILD_CONFIGURATION -o /out --no-restore -v:n \
      /p:UseAppHost=false \
      /p:Version=$VERSION \
      /p:SourceRevisionId=$SOURCE_REVISION_ID \
      /p:BuildTimestamp=$BUILD_TIMESTAMP \
      /p:ContinuousIntegrationBuild=true

FROM base AS final
WORKDIR /app
COPY --from=build --chown=helpdesk:helpdesk /out ./
COPY --chown=helpdesk:helpdesk licenses/ ./licenses/
USER helpdesk
ENTRYPOINT ["dotnet", "Helpdesk.Mcp.Http.dll"]
