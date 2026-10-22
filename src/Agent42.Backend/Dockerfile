# Build context must be the repository root (so project references resolve):
#   docker build -f src/Agent42.Backend/Dockerfile -t agent42-backend .

# signal-cli, pinned (see the signal-cli stage below). Global ARGs: declared before the first FROM so every stage sees them.
ARG SIGNAL_CLI_VERSION=0.14.9
ARG SIGNAL_CLI_SHA256=c32b87f587198cbd277c9d71228716ee6047ee2b63e8e88728e8280d9407d65a
ARG SIGNAL_CLI_RELEASED=2026-10-07

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

# Node.js for the shell of Copilot fleet agents and chats (ADR 0097, 0107). Taken from the official image on the
# current LTS line, because Ubuntu 24.04's apt `nodejs` is the end-of-life 18.x.
FROM node:24-bookworm-slim AS node

# signal-cli for the chat gateway's Signal adapter (ADR 0111, phase 3). The backend runs it as a CHILD process, only
# while it holds the gateway lease, so a revision rollover never runs two daemons on one account store. The JVM build:
# the GraalVM native build is marked experimental upstream. Version and checksum were resolved from the GitHub release
# on 2026-10-21, never written from memory; bump both together. Signal expires clients after three months, which is
# what SIGNAL_CLI_RELEASED is for (Config health warns at 75 days). GPL-3.0: run as a separate program (aggregation).
# The ARGs themselves are at the top of this file: only an ARG before the first FROM reaches every stage.
FROM eclipse-temurin:25-jre-noble@sha256:3f2223fe04b750aa5a8179e29b5c9a1ddf7665fc9bb08bfe11ad01d143bc13a5 AS jre

FROM ubuntu:24.04 AS signal-cli
ARG SIGNAL_CLI_VERSION
ARG SIGNAL_CLI_SHA256
# The digest is checked by sha256sum: a mismatch fails the build before anything is extracted.
ADD https://github.com/AsamK/signal-cli/releases/download/v${SIGNAL_CLI_VERSION}/signal-cli-${SIGNAL_CLI_VERSION}.tar.gz /tmp/signal-cli.tar.gz
RUN echo "${SIGNAL_CLI_SHA256}  /tmp/signal-cli.tar.gz" | sha256sum -c - \
    && mkdir -p /opt/signal-cli \
    && tar -xzf /tmp/signal-cli.tar.gz -C /opt/signal-cli --strip-components=1

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SIGNAL_CLI_VERSION
ARG SIGNAL_CLI_RELEASED
WORKDIR /app

# Python 3 and Node.js are runtimes for the built-in shell of Copilot fleet agents and chats (ADR 0097, 0107). The
# app runs non-root, so nothing can be installed at runtime: whatever the shell can run is baked in here.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# The JRE signal-cli needs (25+), and signal-cli itself. Dormant unless ChatGateway:Signal:Enabled is set.
COPY --from=jre /opt/java/openjdk /opt/java/openjdk
COPY --from=signal-cli /opt/signal-cli /opt/signal-cli
ENV JAVA_HOME=/opt/java/openjdk \
    SIGNAL_CLI_VERSION=${SIGNAL_CLI_VERSION} \
    SIGNAL_CLI_RELEASED=${SIGNAL_CLI_RELEASED}

COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["dotnet", "Agent42.Backend.dll"]
