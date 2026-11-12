FROM node:20-alpine AS frontend
WORKDIR /build/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY global.json Directory.Build.props nuget.config Allowlist.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore Allowlist.slnx
COPY --from=frontend /build/src/Allowlist.Api/wwwroot/ ./src/Allowlist.Api/wwwroot/
RUN dotnet build src/Allowlist.Api/Allowlist.Api.csproj --no-restore -c Release
RUN dotnet publish src/Allowlist.Api/Allowlist.Api.csproj --no-restore --no-build -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5106
EXPOSE 5106
COPY --from=build /publish/ ./
RUN mkdir -p /app/appdata && chown -R "$APP_UID" /app/appdata
USER $APP_UID
ENTRYPOINT ["dotnet", "Allowlist.Api.dll"]
