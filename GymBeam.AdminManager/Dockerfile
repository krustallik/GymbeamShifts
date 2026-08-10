FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["GymBeam.AdminManager/GymBeam.AdminManager.csproj", "GymBeam.AdminManager/"]
RUN dotnet restore "GymBeam.AdminManager/GymBeam.AdminManager.csproj"

COPY ["GymBeam.AdminManager/", "GymBeam.AdminManager/"]
RUN dotnet publish "GymBeam.AdminManager/GymBeam.AdminManager.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

RUN mkdir -p /app/data \
    && chown app:app /app/data \
    && chmod 700 /app/data

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV DOTNET_EnableDiagnostics=0

COPY --from=build --chown=app:app /app/publish .
COPY --chown=app:app GymBeam.AdminManager/Provisioning/bot-appconfig.json /app/templates/bot-appconfig.json

USER app
ENTRYPOINT ["dotnet", "GymBeam.AdminManager.dll"]
