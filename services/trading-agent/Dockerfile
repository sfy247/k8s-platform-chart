# ─────────────────────────────────────────────────────────────
# Multi-stage build for the trading agent.
#
# Tests run inside the build: an image that ships is an image whose tests
# passed, with no separate step to forget.
# ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first so restore is cached independently of source changes.
COPY ClaudeTradingAgent.sln Directory.Build.props ./
COPY src/Strategy/Strategy.csproj                 src/Strategy/
COPY src/MarketData/MarketData.csproj             src/MarketData/
COPY src/RiskManagement/RiskManagement.csproj     src/RiskManagement/
COPY src/Execution/Execution.csproj               src/Execution/
COPY src/Portfolio/Portfolio.csproj               src/Portfolio/
COPY src/Persistence/Persistence.csproj       src/Persistence/
COPY src/TradingAgent/TradingAgent.csproj         src/TradingAgent/
COPY tests/TradingAgent.Tests.csproj              tests/
RUN dotnet restore ClaudeTradingAgent.sln

COPY src/ src/
COPY tests/ tests/

FROM build AS test
RUN dotnet test tests/TradingAgent.Tests.csproj --no-restore

FROM build AS publish
RUN dotnet publish src/TradingAgent/TradingAgent.csproj \
      -c Release -o /app/publish --no-restore

# ─────────────────────────────────────────────────────────────
# aspnet, not runtime: the agent serves health and metrics endpoints, so it
# needs the ASP.NET Core shared framework.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_URLS=http://0.0.0.0:8080

COPY --from=publish /app/publish .

# Configuration the app reads at runtime. Values come from the environment
# and from Secrets — never from the image.
COPY config/ ./config/

# Numeric uid, matching the chart's podSecurityContext.runAsUser. The chart
# enforces runAsNonRoot, so a root image would be rejected at admission.
EXPOSE 8080

USER 1000:1000

ENTRYPOINT ["dotnet", "TradingAgent.dll"]
