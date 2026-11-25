# One image definition, two targets: `api` (web app + REST API + UI) and `worker` (background processing).
# Build:  docker compose build        Run:  docker compose up -d
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FlowMind.sln ./
COPY src/ src/
RUN dotnet publish src/FlowMind.API    -c Release -o /out/api    --nologo \
 && dotnet publish src/FlowMind.Worker -c Release -o /out/worker --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
# curl is only used by the container health check.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data /shared-docs && chown app:app /data /shared-docs
ENV DOTNET_ENVIRONMENT=Production ASPNETCORE_ENVIRONMENT=Production \
    Storage__RootPath=/data
USER app
VOLUME /data

FROM base AS api
WORKDIR /app
COPY --from=build --chown=app:app /out/api ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=5 CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "FlowMind.API.dll"]

FROM base AS worker
WORKDIR /app
COPY --from=build --chown=app:app /out/worker ./
ENTRYPOINT ["dotnet", "FlowMind.Worker.dll"]
