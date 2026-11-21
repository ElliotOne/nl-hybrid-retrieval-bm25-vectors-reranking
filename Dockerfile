FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props HybridRetrieval.slnx ./
COPY src/HybridRetrieval/HybridRetrieval.csproj src/HybridRetrieval/packages.lock.json src/HybridRetrieval/
RUN dotnet restore src/HybridRetrieval/HybridRetrieval.csproj --locked-mode
COPY src/ src/
COPY data/ data/
RUN dotnet publish src/HybridRetrieval/HybridRetrieval.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:11.0
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
RUN mkdir -p /app/artifacts && chown $APP_UID:$APP_UID /app/artifacts
USER $APP_UID
ENTRYPOINT ["dotnet", "HybridRetrieval.dll"]
CMD ["--help"]
