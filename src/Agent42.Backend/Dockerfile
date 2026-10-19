# Build context must be the repository root (so project references resolve):
#   docker build -f src/Agent42.Backend/Dockerfile -t agent42-backend .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Central Package Management: all versions live in Directory.Packages.props at the
# repo root, so restore needs it present before the project files (NU1015 otherwise).
COPY Directory.Packages.props ./
COPY src/Agent42.Backend/Agent42.Backend.csproj src/Agent42.Backend/
COPY src/Agent42.Agents/Agent42.Agents.csproj src/Agent42.Agents/
COPY src/Agent42.ServiceDefaults/Agent42.ServiceDefaults.csproj src/Agent42.ServiceDefaults/
RUN dotnet restore src/Agent42.Backend/Agent42.Backend.csproj

COPY src/Agent42.Backend/ src/Agent42.Backend/
COPY src/Agent42.Agents/ src/Agent42.Agents/
COPY src/Agent42.ServiceDefaults/ src/Agent42.ServiceDefaults/

RUN dotnet publish src/Agent42.Backend/Agent42.Backend.csproj -c Release -o /app --no-restore

# Node.js for the shell of Copilot fleet agents (ADR 0097). Taken from the official image on the
# current LTS line, because Ubuntu 24.04's apt `nodejs` is the end-of-life 18.x.
FROM node:24-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Python 3 and Node.js are runtimes for the built-in shell of Copilot fleet agents (ADR 0097). The
# app runs non-root, so nothing can be installed at runtime: whatever the shell can run is baked in here.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["dotnet", "Agent42.Backend.dll"]
