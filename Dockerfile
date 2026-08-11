FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["GymBeamShiftsController.sln", "./"]
COPY ["GymBeamShiftsControllerX/GymBeamShiftsControllerX.csproj", "GymBeamShiftsControllerX/"]
RUN dotnet restore "GymBeamShiftsControllerX/GymBeamShiftsControllerX.csproj"

COPY . .
RUN dotnet publish "GymBeamShiftsControllerX/GymBeamShiftsControllerX.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0-bookworm-slim AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    curl \
    ca-certificates \
    fonts-liberation \
    libnss3 \
    libatk-bridge2.0-0 \
    libxss1 \
    libasound2 \
    libgbm1 \
    libgtk-3-0 \
    && rm -rf /var/lib/apt/lists/*

ENV CHROME_BIN=/usr/bin/chromium
ENV DOTNET_EnableDiagnostics=0

RUN mkdir -p /app/runtime-data \
    && chown -R app:app /app

COPY --from=build --chown=app:app /app/publish .

USER app
ENTRYPOINT ["dotnet", "GymBeamShiftsControllerX.dll"]
