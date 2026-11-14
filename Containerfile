# Imagem do CopilotTeamsBridge para Podman rootless. Veja docs/tutorial-configuracao.md, seção 10.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/CopilotTeamsBridge.Worker/CopilotTeamsBridge.Worker.csproj src/CopilotTeamsBridge.Worker/
RUN dotnet restore src/CopilotTeamsBridge.Worker/CopilotTeamsBridge.Worker.csproj
COPY src/ src/
RUN dotnet publish src/CopilotTeamsBridge.Worker/CopilotTeamsBridge.Worker.csproj \
    -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Segredos montados em /secrets (somente leitura). A configuração local fica em /app/appsettings.Local.json.
ENV DOTNET_NOLOGO=1 \
    DOTNET_gcServer=0 \
    TEAMSBRIDGE_TeamsBridge__CorrelationKeyFile=/secrets/correlation.key \
    TEAMSBRIDGE_TeamsBridge__AgentHost__TokenFile=/secrets/agent-host.token
USER $APP_UID
ENTRYPOINT ["dotnet", "copilot-teams-bridge.dll"]
