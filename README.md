# OrderFlow

Sistema de gestión de pedidos con reserva asíncrona de inventario mediante arquitectura orientada a eventos, patrón *Outbox* transaccional e idempotencia en base de datos.

## 1. Qué es y cómo correrlo

OrderFlow recibe pedidos desde un panel web, los registra de inmediato en estado `Pending` y coordina la reserva de inventario de forma asíncrona a través de RabbitMQ hasta resolver cada pedido como `Confirmed` o `Rejected`.

Solo se requiere Docker y Docker Compose (no hace falta instalar .NET ni Node en el equipo anfitrión para ejecutar la pila).

```bash
cp .env.example .env   # opcional: usa valores por defecto si se omite
docker compose up --build
```

Para ejecutarlo en segundo plano:

```bash
docker compose up --build -d
```

Servicios y URLs por defecto (puertos configurables en `.env`):

| Servicio | URL | Credenciales / Notas |
| --- | --- | --- |
| Panel web (`web`) | http://localhost:8080 | Nginx sirve el frontend y proxy `/api/` -> `orders-api` |
| Orders API (`orders-api`) | http://localhost:5080 | Endpoints `/products`, `/orders`, `/health/live`, `/health/ready` |
| RabbitMQ Management UI (`rabbitmq`) | http://localhost:15672 | Usuario y contraseña por defecto en `.env.example`: `orderflow` / `orderflow` |

## 2. Arquitectura

```text
+------------+   POST/GET /api/*   +------------+   TX: Order + Outbox   +------------+
| Panel Web  | ------------------> | Orders API | ---------------------> |  Outbox    |
| (React+Ngx)| <------------------ | (.NET 8)   | <----+                 | (orders db)|
+------------+   polling cada 2 s  +------------+      |                 +-----+------+
                                                       |                       |
                                        StockReserved /|                       | OutboxPublisher
                                         StockRejected |                       v
                                                +------+-------+        +--------------+
                                                |   RabbitMQ   | <----- |   RabbitMQ   |
                                                | (stock-res.) |        | (events ex.) |
                                                +------+-------+        +------+-------+
                                                       ^                       |
                                                       | OutboxPublisher       | order.created
                                                       |                       v
                                                +------+-------+        +------------------+
                                                | Outbox       | <----- | Inventory Worker |
                                                | (inv. db)    |   TX   | (.NET 8)         |
                                                +--------------+        +------------------+
```

Mapa de carpetas:

```text
frontend/              # SPA React 18 + TypeScript + Vite, pruebas Vitest, Dockerfile Nginx
src/
  OrderFlow.Contracts/ # Eventos (OrderCreated, StockReserved, StockRejected) y topología AMQP
  Orders.Api/          # API mínima .NET 8, OutboxPublisher, StockResultConsumer y migraciones EF
  Inventory.Worker/    # Worker .NET 8, ReserveStockHandler, OrderCreatedConsumer y OutboxPublisher
tests/
  Orders.Tests/        # Pruebas unitarias y de persistencia para Orders.Api
  Inventory.Tests/     # Pruebas de reserva de stock e idempotencia para Inventory.Worker
infra/postgres/        # Script init-databases.sh para crear las bases orders e inventory
k8s/                   # Manifiestos de diseño para Kubernetes (ConfigMap, Secret ejemplo, Deployments, Service)
scripts/               # Demo automatizada de entrega duplicada (demo-idempotency.sh)
seed/                  # Catálogo y stock inicial compartido (products.json)
```

## 3. Decisiones y trade-offs

- **ASP.NET Core 8 Minimal API**: reduce ceremonias frente a controladores MVC para un servicio pequeño de 6 endpoints; si el dominio creciera en decenas de rutas y filtros, conviene modularizar por grupos o pasar a controladores.
- **`RabbitMQ.Client` directo en lugar de MassTransit**: permite controlar de forma explícita la topología durable (`orderflow.events`, `orderflow.dlx`, colas `.dlq`), *publisher confirms*, *ack* manual y reintentos con `x-retry-count` sin ocultar la mecánica del broker detrás de abstracciones. A cambio, el manejo de serialización y reconexión queda en código propio (`BrokerConnection`, `OutboxPublisher`, consumidores).
- **Outbox transaccional en ambos servicios**: `Orders.Api` guarda el pedido y el evento `OrderCreated` en la misma transacción; `Inventory.Worker` guarda `ProcessedEvent`, el descuento de stock y `StockReserved`/`StockRejected` en una sola transacción. Evita el problema de doble escritura entre PostgreSQL y RabbitMQ, a costa de un retardo acotado por `OUTBOX_POLL_MS` (1000 ms por defecto).
- **Un contenedor PostgreSQL con dos bases de datos (`orders` e `inventory`)**: simplifica el arranque local manteniendo aislamiento de esquemas y contextos EF Core independientes (`OrdersDbContext` e `InventoryDbContext`), fácil de separar en dos instancias físicas cambiando la cadena de conexión.
- **Polling cada 2 s en el frontend en lugar de SignalR/WebSockets**: el panel pausa el intervalo cuando la pestaña está oculta y cubre el caso de uso sin mantener conexiones persistentes ni *backplane* en caso de escalar `orders-api` a múltiples réplicas. El trade-off es una latencia máxima de ~2 s en ver transiciones externas.
- **Migraciones EF Core y *seed* al arranque**: `Program.cs` aplica `MigrateAsync()` y carga `seed/products.json` con reintentos exponenciales para que `docker compose up` funcione con un solo comando. En despliegues multi-réplica se debe mover a un *Job* dedicado.
- **SQLite en memoria para las pruebas unitarias**: permite ejecutar `dotnet test` en ~1 s sin Docker ni PostgreSQL externo; no reemplaza pruebas contra PostgreSQL real para verificar bloqueo de filas concurrentes (`ExecuteUpdateAsync` sí se valida en ejecución real con Compose).

## 4. Idempotencia

El sistema tolera entregas repetidas del broker (*at-least-once delivery*) en ambos sentidos:

1. **En `Inventory.Worker` (`ReserveStockHandler.HandleAsync`)**: dentro de una transacción de PostgreSQL, primero inserta `eventId` en `processed_events` (`EventId` como clave primaria). Si el `eventId` ya existe (por lectura previa o violación de clave única en carrera concurrente), revierte la transacción y devuelve `AlreadyProcessed`, confirmando (`ack`) el mensaje sin volver a descontar stock ni emitir un segundo evento en el *outbox*. En el mismo bloque transaccional ejecuta el descuento condicional atómico:
   `UPDATE stock SET disponible = disponible - @cantidad WHERE sku = @sku AND disponible >= @cantidad` (vía `ExecuteUpdateAsync`).
2. **En `Orders.Api` (`StockResultConsumer.ApplyResultAsync`)**: actualiza el pedido de forma condicional solo cuando sigue en `Pending` (`WHERE id = @orderId AND estado = 'Pending'`). Cualquier evento duplicado o contradictorio posterior se ignora y se confirma (`ack`).

Cómo demostrarlo en vivo (con la pila arriba):

```bash
./scripts/demo-idempotency.sh
```

El script crea un pedido, espera a que pase a `Confirmed`, extrae el evento original de `outbox_messages`, vuelve a publicarlo directamente en el exchange `orderflow.events` mediante la API HTTP de RabbitMQ y comprueba que el stock en PostgreSQL permanece intacto y con un único registro en `processed_events`. En la suite automatizada, este comportamiento lo cubren `SameOrderCreatedHandledTwice_DiscountsOnceAndCreatesSingleOutbox` y `StockResult_ConfirmsPendingOrderAndIgnoresContradictorySecondResult`.

## 5. Manejo de fallos

| Escenario | Qué ocurre en el sistema |
| --- | --- |
| **`inventory-worker` caído** | `Orders.Api` sigue aceptando pedidos (`201`, `Pending`). RabbitMQ retiene `order.created` en la cola durable `inventory.order-created`; al volver el worker, procesa los mensajes acumulados. |
| **RabbitMQ caído al publicar** | `Orders.Api` persiste el pedido y el registro en `outbox_messages`. `OutboxPublisher` reintenta con *backoff* exponencial (hasta 30 s) y solo marca `PublishedAt` tras *publisher confirm*. |
| **Evento duplicado** | `Inventory.Worker` detecta la clave primaria repetida en `processed_events` y hace `ack` sin volver a descontar; `Orders.Api` solo transiciona pedidos en estado `Pending`. |
| **Mensaje que falla siempre** | Tras 3 intentos fallidos (`MaxDeliveryAttempts = 3` mediante encabezado `x-retry-count`), el consumidor ejecuta `BasicNack(..., requeue: false)` y RabbitMQ enruta el mensaje vía `orderflow.dlx` hacia la cola `.dlq` correspondiente. |
| **`orders-api` se reinicia con mensajes pendientes** | Los mensajes no confirmados en `orders.stock-result` vuelven a entregarse y los registros del *outbox* con `PublishedAt IS NULL` se publican al reconectar. |
| **Dos pedidos compiten por el último stock** | `ExecuteUpdateAsync` traduce el descuento a un `UPDATE` atómico condicional en PostgreSQL (`disponible >= cantidad`). Solo la transacción ganadora afecta 1 fila (`Confirmed`); la otra afecta 0 filas y emite `StockRejected` (`insufficient_stock`). |

**Qué NO está cubierto actualmente**:

- Si `inventory-worker` nunca se recupera o un mensaje cae a `.dlq`, el pedido puede permanecer en `Pending` indefinidamente (no hay expiración ni *timeout* automático).
- No existe reproceso automático ni interfaz de inspección para las colas `.dlq`.
- Los registros publicados de `outbox_messages` (`PublishedAt != null`) no se purgan ni archivan automáticamente.

## 6. Tests

Backend (ejecuta los proyectos `.NET` dentro del contenedor oficial SDK 8.0):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test
```

Frontend (Vitest + Testing Library en `frontend/`):

```bash
cd frontend && npm test -- --run
```

Qué prueba cada caso:

- **Inventory.Tests (`ReserveStockHandlerTests`)**:
  - `SameOrderCreatedHandledTwice_DiscountsOnceAndCreatesSingleOutbox`: entregar dos veces el mismo `OrderCreated` descuenta stock una sola vez y genera un único mensaje en el *outbox*.
  - `InsufficientStock_LeavesStockUnchangedAndEmitsRejected`: stock insuficiente no altera el inventario y encola `StockRejected` con `insufficient_stock`.
  - `UnknownSku_EmitsStockRejectedWithUnknownSku`: un SKU inexistente encola `StockRejected` con `unknown_sku`.
- **Orders.Tests (`OrderFlowTests`)**:
  - `OrderValidator_ValidatesFieldsAsExpected` (5 casos): valida cliente requerido, SKU existente en catálogo y rango de cantidad `1..100`.
  - `StockResult_ConfirmsPendingOrderAndIgnoresContradictorySecondResult`: confirma un pedido `Pending` e ignora un rechazo posterior sobre el mismo pedido ya confirmado.
  - `CreateOrder_PersistsOrderAndOutboxInSingleTransaction`: verifica que `CreateOrderWithOutboxAsync` persiste el pedido `Pending` y su `OrderCreated` sin publicar en la misma transacción.
- **Frontend (`frontend/src/OrderFlow.test.tsx`)**:
  - Muestra errores de validación en el formulario sin llamar a la API cuando los campos son inválidos.
  - Mapea un `400 ProblemDetails` del servidor mostrando el error en el campo afectado y la alerta visible dentro del formulario.
  - Renderiza las insignias de los tres estados (`Pendiente`, `Confirmado`, `Rechazado`) con su texto, estilo distintivo y motivo de rechazo.

## 7. Kubernetes

En `k8s/` se incluyen los manifiestos base para `orders-api` y `inventory-worker`:

- `k8s/configmap.yaml`: configuración no sensible (`ASPNETCORE_URLS`, `RABBITMQ_HOST`, `RABBITMQ_PORT`, `OUTBOX_POLL_MS`, `SEED_PATH`).
- `k8s/secret.example.yaml`: plantilla con valores ficticios para cadenas de conexión y credenciales de RabbitMQ (el secreto real nunca debe guardarse en Git).
- `k8s/deployment.yaml` y `k8s/service.yaml`: `orders-api` con 2 réplicas, usuario no *root* (`1654`), sistema de archivos raíz de solo lectura, límites de CPU/memoria, sondas `/health/live` y `/health/ready` y `Service` `ClusterIP`.
- `k8s/inventory-worker-deployment.yaml`: `inventory-worker` sin `Service` (no expone puertos HTTP). Escalarlo a múltiples réplicas es seguro porque `processed_events` impide reprocesar un mismo `eventId` y el `UPDATE` condicional de stock serializa la competencia por SKU en PostgreSQL.

En un clúster productivo completo, el despliegue se completaría con:

- **PostgreSQL y RabbitMQ gestionados** (o mediante operadores dedicados con almacenamiento persistente y alta disponibilidad) en lugar de contenedores locales.
- **Nginx + Ingress** (o Gateway API) para servir el panel estático y enrutar `/api/` hacia el `Service` `orders-api`.
- **HorizontalPodAutoscaler (HPA)** sobre `orders-api` según CPU o latencia HTTP, y escalado del worker según profundidad de cola si aumenta la carga.
- **Migraciones EF Core como `Job` previo al despliegue** (o *initContainer* coordinado), evitando que múltiples réplicas compitan ejecutando `MigrateAsync()` al arrancar.

## 8. Qué haría distinto con más tiempo

- Agregar un mecanismo de expiración/timeout para pedidos que permanezcan en `Pending` más allá de un umbral configurable (pasándolos a `Rejected` o estado de expiración).
- Instrumentar ambos servicios con OpenTelemetry y un `correlationId` propagado desde la petición HTTP hasta los eventos AMQP y logs estructurados.
- Incorporar SignalR o Server-Sent Events para actualizar el estado en el panel en tiempo real sin polling periódico.
- Añadir pruebas de integración reales con Testcontainers (PostgreSQL + RabbitMQ) para validar concurrencia y topología AMQP de extremo a extremo.
- Añadir pruebas de contrato entre `OrderFlow.Contracts`, `Orders.Api` y el cliente TypeScript del frontend para detectar cambios de esquema antes del despliegue.
- Implementar limpieza o particionado periódico de `outbox_messages` ya publicadas (`PublishedAt IS NOT NULL`) y una herramienta operativa para inspeccionar y reinyectar mensajes en `.dlq`.

## 9. Uso de IA

Durante el desarrollo se utilizó asistencia de IA para acelerar la generación de código repetitivo, estructura inicial y borradores de documentación y pruebas. Cada decisión técnica, contrato entre servicios, consulta SQL, configuración de contenedores y caso de prueba fue revisado, ajustado y verificado manualmente ejecutando el sistema y las suites automatizadas.
