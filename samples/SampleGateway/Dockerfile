FROM node:24-bookworm-slim AS frontend
WORKDIR /ui
COPY src/Wafish.ReactUi/ClientApp/package*.json ./
RUN npm ci --no-fund
COPY src/Wafish.ReactUi/ClientApp/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY --from=frontend /usr/local/bin/node /usr/local/bin/node
COPY . .
COPY --from=frontend /wwwroot src/Wafish.ReactUi/wwwroot
RUN dotnet publish samples/SampleGateway/SampleGateway.csproj -c Release -o /app/publish /p:UseAppHost=false /p:SkipReactBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN chown -R app:app /app
USER $APP_UID
EXPOSE 80 5000 5002
ENTRYPOINT ["dotnet", "SampleGateway.dll"]
