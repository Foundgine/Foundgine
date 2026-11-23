# Canonical Advanced sample: the in-memory semantic MCP/intent API (Semantic/Api/Mcp).
# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Api/Mcp/Foundgine.SupplyChain.Advanced.Mcp.Api.csproj
RUN dotnet publish src/csharp/samples/Foundgine.SupplyChain.Advanced/Semantic/Api/Mcp/Foundgine.SupplyChain.Advanced.Mcp.Api.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet","Foundgine.SupplyChain.Advanced.Mcp.Api.dll"]
