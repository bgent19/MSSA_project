# MeepleLedger embed worker (A-23). Build from the repo root, so the .dockerignore there applies:
#   docker build -f MeepleLedger.Worker/Dockerfile -t meepleledger-worker:dev .
#
# Two stages: the SDK image builds, the .NET runtime image runs. The worker is a console app, not a
# web app, so it needs the plain runtime image rather than the ASP.NET one.

# ---- Build stage: the .NET SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project file first and restore, so a source edit does not invalidate the package layer
COPY MeepleLedger.Worker/MeepleLedger.Worker.csproj MeepleLedger.Worker/
RUN dotnet restore MeepleLedger.Worker/MeepleLedger.Worker.csproj

COPY MeepleLedger.Worker/ MeepleLedger.Worker/
RUN dotnet publish MeepleLedger.Worker/MeepleLedger.Worker.csproj --configuration Release --no-restore --output /app/publish

# ---- Run stage: the .NET runtime only, no SDK ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Run as the non-root "app" user the .NET images ship with, not as root
USER $APP_UID

# No settings are baked in. ServiceBus__ConnectionString, AzureOpenAI__* and Cosmos__* come from
# environment variables at run time; without them the worker says what is missing and exits.
ENTRYPOINT ["dotnet", "MeepleLedger.Worker.dll"]
