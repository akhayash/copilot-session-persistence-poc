FROM node:24-bookworm-slim AS client-build
WORKDIR /src/ClientApp
COPY src/CopilotSessionPersistencePoc/ClientApp/package*.json ./
RUN npm ci
COPY src/CopilotSessionPersistencePoc/ClientApp/ ./
RUN npm run build -- --logLevel error

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS app-build
WORKDIR /src
COPY CopilotSessionPersistencePoc.slnx ./
COPY src/CopilotSessionPersistencePoc/CopilotSessionPersistencePoc.csproj src/CopilotSessionPersistencePoc/
RUN dotnet restore src/CopilotSessionPersistencePoc/CopilotSessionPersistencePoc.csproj
COPY src/CopilotSessionPersistencePoc/ src/CopilotSessionPersistencePoc/
COPY --from=client-build /src/wwwroot src/CopilotSessionPersistencePoc/wwwroot
RUN dotnet publish src/CopilotSessionPersistencePoc/CopilotSessionPersistencePoc.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:SkipClientBuild=true \
    -p:CopilotSkipCliDownload=true \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=app-build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CopilotSessionPersistencePoc.dll"]
