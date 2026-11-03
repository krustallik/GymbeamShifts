# Development and verification

## Toolchain

- .NET 8 SDK for both applications and the Admin Manager test project.
- .NET 9 SDK for `GymBeamShiftsControllerX.Tests`.
- A compatible Chrome or Chromium installation for browser workflow tests.
- Docker Engine and Compose v2 only for the container and deployment validation steps in CI.

## Build and test

From the repository root:

```bash
dotnet restore GymBeamShiftsController.sln
dotnet build GymBeamShiftsController.sln --configuration Release --no-restore
dotnet test GymBeamShiftsController.sln --configuration Release --no-build
dotnet format GymBeamShiftsController.sln --verify-no-changes --no-restore
```

Useful focused commands:

```bash
dotnet test GymBeamShiftsController.sln --configuration Release --filter FullyQualifiedName~ShiftCheckerEligibilityTests
dotnet test GymBeamShiftsController.sln --configuration Release --filter FullyQualifiedName~AuthenticationSecurityIntegrationTests
```

The GitHub Actions workflow runs the full solution tests and separate filtered passes for unit, integration, security, and deployment tests. It then validates deployment scripts, Compose, Docker builds, the managed-bot deployment fixture, and Caddy configuration. The Docker-specific steps need a Linux Docker runner; they are not required to build or run the .NET tests locally.

The last verification for this documentation pass completed **501 tests: 296 bot tests and 205 manager tests, with no failures or skips**. That test result verifies the current source checkout; the portfolio edits themselves are documentation and image assets only.

## Safe evaluation

The screenshots in `docs/images/` were captured from the actual local application interfaces using synthetic rules, bot names, and runtime status data. The demo processes use temporary directories outside the repository and dummy credentials. They do not log in to the shift portal, send Telegram messages, use production configuration, or attach to Docker.

Running the bot itself requires valid portal credentials and may open an authenticated browser session. Running the full Compose stack also binds ports 80/443, uses the Docker socket, and expects Linux-style per-instance files and Caddy routes. Treat it as an operations deployment, not as an isolated read-only demo. Follow [DOCKER_RUN.md](../DOCKER_RUN.md) only on a disposable host or after preparing a dedicated isolated environment.

The tracked Compose and Caddy runtime files still include legacy deployment defaults. They were not changed during this documentation-only pass; review them in a separate infrastructure change before deploying to another host or presenting a sanitized public fork.

## Runtime configuration

The placeholders in `deploy/examples/` document bot configuration fields. Keep actual `.env` files and per-instance `appconfig.json` files outside Git. Never copy real account credentials or Telegram secrets into an issue, screenshot, demo fixture, or commit.
