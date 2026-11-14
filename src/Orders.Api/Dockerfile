FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/OrderFlow.Contracts/OrderFlow.Contracts.csproj src/OrderFlow.Contracts/
COPY src/Orders.Api/Orders.Api.csproj src/Orders.Api/
RUN dotnet restore src/Orders.Api/Orders.Api.csproj

COPY src/OrderFlow.Contracts/ src/OrderFlow.Contracts/
COPY src/Orders.Api/ src/Orders.Api/
RUN dotnet publish src/Orders.Api/Orders.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

COPY --from=build /app/publish .
COPY seed/products.json ./seed/products.json

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app

HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Orders.Api.dll"]
