# Marbots server image — Gravicode Studios, led by Kang Fadhil.
# Build from the Marbots folder:   docker build -f install/docker/Dockerfile -t marbots .
# Multi-arch (no emulation for the build): docker buildx build --platform linux/amd64,linux/arm64 -f install/docker/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Marbots.Abstractions/Marbots.Abstractions.csproj src/Marbots.Abstractions/
COPY src/Marbots.Storage/Marbots.Storage.csproj src/Marbots.Storage/
COPY src/Marbots.Providers/Marbots.Providers.csproj src/Marbots.Providers/
COPY src/Marbots.Kernel/Marbots.Kernel.csproj src/Marbots.Kernel/
COPY src/Marbots.Runtime/Marbots.Runtime.csproj src/Marbots.Runtime/
COPY src/Marbots.Server/Marbots.Server.csproj src/Marbots.Server/
RUN dotnet restore src/Marbots.Server/Marbots.Server.csproj -a $TARGETARCH
COPY src/ src/
COPY skills/ skills/
RUN dotnet publish src/Marbots.Server/Marbots.Server.csproj -c Release -a $TARGETARCH --no-restore --self-contained false \
      -p:Version=$VERSION -p:DebugType=none -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="Marbots" \
      org.opencontainers.image.description="Multi-agent collaboration platform (Boss Man orchestration, skills, MCP, approvals)" \
      org.opencontainers.image.vendor="Gravicode Studios" \
      org.opencontainers.image.source="https://github.com/DotNetVibeCoderz/Vibe_Dev" \
      org.opencontainers.image.licenses="MIT"
# git for skill installs from git; python3/node are optional and can be added in a derived image.
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# URLS (unprefixed) overrides the "Urls" in appsettings.json; ASPNETCORE_URLS would not.
ENV Marbots__DataDirectory=/data \
    URLS=http://+:8080 \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false
VOLUME /data
EXPOSE 8080
RUN mkdir -p /data && chown app:app /data
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD curl -fs http://localhost:8080/api/v1/system >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Marbots.Server.dll"]
