# MeepleLedger web app (A-23). Build from the repo root, so the .dockerignore there applies:
#   docker build -f MeepleLedger/Dockerfile -t meepleledger:dev .
#
# Two stages: the SDK image builds, the much smaller ASP.NET runtime image runs. Nothing from the
# build stage reaches the final image except the published output.

# ---- Build stage: the .NET SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only the project file and restore first. This layer is cached until the .csproj changes,
# so editing a .cs or .razor file does not re-download every NuGet package.
COPY MeepleLedger/MeepleLedger.csproj MeepleLedger/
RUN dotnet restore MeepleLedger/MeepleLedger.csproj

# Now the source. Every source edit starts again from here, after the restore.
COPY MeepleLedger/ MeepleLedger/
RUN dotnet publish MeepleLedger/MeepleLedger.csproj --configuration Release --no-restore --output /app/publish

# ---- Run stage: the ASP.NET runtime only, no SDK ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# The .NET images listen on port 8080 by default (ASPNETCORE_HTTP_PORTS), which a non-root user is
# allowed to open. There is no HTTPS inside the container; whatever sits in front of it does TLS.
EXPOSE 8080

COPY --from=build /app/publish .

# Run as the non-root "app" user the .NET images ship with, not as root
USER $APP_UID

# No settings are baked in. The in-memory Provider needs none (ADR-0001); anything else comes from
# environment variables at run time, e.g. SearchIndex__Provider=Postgres.
ENTRYPOINT ["dotnet", "MeepleLedger.dll"]
