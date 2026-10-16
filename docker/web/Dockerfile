# 1) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.23 AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8111
# Enable full globalization support (ICU) for culture-sensitive components
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0
EXPOSE 8111 8111

# 2) Build
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23 AS build
WORKDIR /src
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TIMESTAMP=

# Copy global build inputs and only the Web dependency graph before restore.
COPY global.json ./
COPY Directory.Build.props Directory.Build.targets ./
COPY src/HelpDesk.NewWeb/*.csproj          src/HelpDesk.NewWeb/
COPY src/Helpdesk.AgentClient/*.csproj     src/Helpdesk.AgentClient/
COPY src/Helpdesk.ServiceDefaults/*.csproj src/Helpdesk.ServiceDefaults/
COPY src/Helpdesk.Shared/*.csproj          src/Helpdesk.Shared/

RUN dotnet restore src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj

COPY src/HelpDesk.NewWeb/          src/HelpDesk.NewWeb/
COPY src/Helpdesk.AgentClient/     src/Helpdesk.AgentClient/
# The shared headless validator compiles the same serving options source.
COPY src/Helpdesk.Mcp.Http/Configuration/AuthentikMcpOptions.cs src/Helpdesk.Mcp.Http/Configuration/
COPY src/Helpdesk.ServiceDefaults/ src/Helpdesk.ServiceDefaults/
COPY src/Helpdesk.Shared/          src/Helpdesk.Shared/

# Publish the Web project
RUN dotnet publish src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj \
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
ENTRYPOINT ["dotnet", "HelpDesk.NewWeb.dll"]
