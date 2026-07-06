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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["dotnet", "Agent42.Backend.dll"]
