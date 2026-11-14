FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/OrderFlow.Contracts/OrderFlow.Contracts.csproj src/OrderFlow.Contracts/
COPY src/OrderFlow.Messaging/OrderFlow.Messaging.csproj src/OrderFlow.Messaging/
COPY src/Inventory.Worker/Inventory.Worker.csproj src/Inventory.Worker/
RUN dotnet restore src/Inventory.Worker/Inventory.Worker.csproj

COPY src/OrderFlow.Contracts/ src/OrderFlow.Contracts/
COPY src/OrderFlow.Messaging/ src/OrderFlow.Messaging/
COPY src/Inventory.Worker/ src/Inventory.Worker/
RUN dotnet publish src/Inventory.Worker/Inventory.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime
WORKDIR /app

COPY --from=build /app/publish .
COPY seed/products.json ./seed/products.json

USER app

ENTRYPOINT ["dotnet", "Inventory.Worker.dll"]
