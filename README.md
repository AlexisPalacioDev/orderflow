# OrderFlow

Sistema de gestión de pedidos con reserva asíncrona de inventario en **.NET 8**, **PostgreSQL 16** y **RabbitMQ 3**.

## Arquitectura

```
src/
  OrderFlow.Contracts/   # Eventos de integración y constantes de topología RabbitMQ
  Orders.Api/            # API mínima ASP.NET Core 8 + OutboxPublisher + StockResultConsumer
  Inventory.Worker/      # Worker .NET 8 + ReserveStockHandler + OrderCreatedConsumer + OutboxPublisher
tests/
  Orders.Tests/          # Pruebas unitarias y de persistencia (SQLite en memoria)
  Inventory.Tests/       # Pruebas de reserva e idempotencia (SQLite en memoria)
seed/
  products.json          # Catálogo y stock inicial compartido entre ambos servicios
```

### Decisiones técnicas clave

- **Outbox transaccional (ambos servicios)**: El cambio de estado y el evento saliente (`outbox_messages`) se guardan en la misma transacción de base de datos. Un `BackgroundService` (`OutboxPublisher`) publica los mensajes pendientes con *publisher confirms* y backoff exponencial si el broker no está disponible.
- **Idempotencia en Inventario**: `ReserveStockHandler` inserta el `eventId` en `processed_events` (PK) dentro de la misma transacción que el descuento atómico condicional (`WHERE sku = @sku AND disponible >= @cantidad` mediante `ExecuteUpdateAsync`), evitando doble descuento y sobreventa concurrente.
- **Idempotencia en Pedidos**: `StockResultConsumer` actualiza el pedido solo cuando su estado es `Pending` (`WHERE id = @id AND estado = 'Pending'`). Cualquier resultado duplicado o tardío se ignora y se confirma (`ack`).
- **Topología RabbitMQ**:
  - Exchange topic durable: `orderflow.events` (`order.created`, `stock.reserved`, `stock.rejected`).
  - Exchange dead-letter durable: `orderflow.dlx`.
  - Colas durables: `inventory.order-created` y `orders.stock-result`, con `prefetch = 10`, *ack* manual y envío a `.dlq` tras 3 intentos fallidos (`x-retry-count`).

## Requisitos y configuración

Solo se requiere Docker y Docker Compose (no es necesario instalar .NET en la máquina anfitriona).

```bash
cp .env.example .env
docker compose up --build -d
```

Puertos publicados por defecto (configurables en `.env`):
- **Orders API**: `http://localhost:5080` (`API_PORT`)
- **RabbitMQ Management UI**: `http://localhost:15672` (`RABBIT_UI_PORT`)

## Endpoints de Orders API

- `GET /products` — Lista el catálogo de productos (`sku`, `nombre`).
- `POST /orders` — Crea un pedido (`clienteNombre`, `sku`, `cantidad` entre 1 y 100) en estado `Pending`. Devuelve `201` o `400` con `ProblemDetails`.
- `GET /orders` — Lista los pedidos ordenados del más reciente al más antiguo.
- `GET /orders/{id}` — Obtiene un pedido por ID (`404` si no existe).
- `GET /health/live` — Liveness probe.
- `GET /health/ready` — Readiness probe (verifica conexión a PostgreSQL).

### Ejemplos rápidos

```bash
curl -s http://localhost:5080/products
curl -s -X POST http://localhost:5080/orders \
  -H 'content-type: application/json' \
  -d '{"clienteNombre":"Ana","sku":"ABC-01","cantidad":2}'
```

## Pruebas automatizadas

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test
```

## Demostración de idempotencia

Con la pila levantada (`docker compose up -d`):

```bash
./scripts/demo-idempotency.sh
```
