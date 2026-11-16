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

Todos los servicios de `docker-compose.yml` (`postgres`, `rabbitmq`, `orders-api`, `inventory-worker` y `web`) están configurados con `restart: unless-stopped`.

Servicios y URLs por defecto (puertos configurables en `.env`):

| Servicio | URL | Credenciales / Notas |
| --- | --- | --- |
| Panel web (`web`) | http://localhost:8080 | Nginx sirve el frontend y proxy `/api/` -> `orders-api` |
| Orders API (`orders-api`) | http://localhost:5080 | Endpoints `/products`, `/orders`, `/health/live`, `/health/ready` |
| RabbitMQ Management UI (`rabbitmq`) | http://localhost:15672 | Usuario y contraseña por defecto en `.env.example`: `orderflow` / `orderflow` |

## 2. Arquitectura

```text
+-----------------+  POST/GET /api/*  +------------+   TX: Order + Outbox   +------------+
|    Panel Web    | ----------------> | Orders API | ---------------------> |  Outbox    |
| (React + nginx) | <---------------- | (.NET 8)   | <----+                 | (orders db)|
+-----------------+ polling cada 2 s  +------------+      |                 +-----+------+
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
  OrderFlow.Contracts/ # Eventos (OrderCreated, StockReserved, StockRejected) y constantes AMQP
  OrderFlow.Messaging/ # BrokerConnection, OutboxPublisher, OutboxMessage, IOutboxDbContext, RetryDecision
  Orders.Api/          # API mínima .NET 8, OrderEndpoints, StockResultConsumer y migraciones EF
  Inventory.Worker/    # Worker .NET 8, ReserveStockHandler (Application/), OrderCreatedConsumer y migraciones EF
tests/
  Orders.Tests/        # Pruebas unitarias y de persistencia para Orders.Api y RetryDecision
  Inventory.Tests/     # Pruebas de reserva de stock e idempotencia para Inventory.Worker
infra/postgres/        # Script init-databases.sh para crear las bases orders e inventory
k8s/                   # Manifiestos Kubernetes (ConfigMap, Secret ejemplo, Job de migración, Deployments, Service)
scripts/               # Demo automatizada de entrega duplicada (demo-idempotency.sh)
seed/                  # Catálogo y stock inicial compartido (products.json)
```

## 3. Decisiones y trade-offs

- **ASP.NET Core 8 Minimal API**: reduce ceremonias frente a controladores MVC para un servicio pequeño de 4 rutas de negocio más sondas nativas (`AddHealthChecks` / `MapHealthChecks`) y respuestas `ProblemDetails` (`AddProblemDetails`, incluyendo JSON malformado en `POST /orders`). Si el dominio creciera en decenas de rutas y filtros, conviene modularizar por grupos o pasar a controladores.
- **`RabbitMQ.Client` directo en lugar de MassTransit**: permite controlar de forma explícita la topología durable (`orderflow.events`, `orderflow.retry`, `orderflow.dlx`, colas de trabajo, colas `.retry` con TTL y colas `.dlq`), *publisher confirms*, *ack* manual y conteo de intentos con `x-attempt` sin ocultar la mecánica del broker detrás de abstracciones. El código común vive en `OrderFlow.Messaging` (`BrokerConnection`, `OutboxPublisher`, `OutboxMessage`, `RetryDecision`).
- **Declaración completa de topología en ambos servicios**: cada servicio declara todos los exchanges, colas y *bindings* al conectar porque con `mandatory: false`, un mensaje publicado antes de que exista la cola del consumidor se descartaría incluso tras recibir *publisher confirm*.
- **Outbox transaccional en ambos servicios**: `Orders.Api` guarda el pedido y el evento `OrderCreated` en la misma transacción; `Inventory.Worker` guarda `ProcessedEvent`, el descuento de stock y `StockReserved`/`StockRejected` en una sola transacción. Evita la doble escritura entre PostgreSQL y RabbitMQ, a costa de un retardo acotado por `OUTBOX_POLL_MS` (1000 ms por defecto).
- **Un contenedor PostgreSQL con dos bases de datos (`orders` e `inventory`)**: simplifica el arranque local manteniendo aislamiento de esquemas y contextos EF Core independientes (`OrdersDbContext` e `InventoryDbContext`), fácil de separar en dos instancias físicas cambiando la cadena de conexión.
- **Polling cada 2 s en el frontend en lugar de SignalR/WebSockets**: el panel pausa el intervalo cuando la pestaña está oculta, cancela peticiones en vuelo al desmontar con `AbortController` y encola un refresco tras crear un pedido si hay un *poll* en curso. El orden más reciente primero viene del servidor (`GET /orders`). El trade-off es una latencia máxima de ~2 s para ver cambios externos.
- **Migraciones EF Core y *seed* configurables**: en `Orders.Api`, `RUN_MIGRATIONS` (por defecto `true` para Compose) aplica `MigrateAsync()` y carga `seed/products.json` al arrancar; con `--migrate` el binario aplica migraciones, ejecuta el *seed* y termina con código `0`, usado en Kubernetes mediante `Job`. `Inventory.Worker` aún ejecuta sus migraciones siempre al arrancar (ignora `RUN_MIGRATIONS`).
- **SQLite en memoria para las pruebas unitarias**: permite ejecutar `dotnet test` sin Docker ni PostgreSQL externo; no reemplaza la verificación contra PostgreSQL real para bloqueo de filas concurrentes ni violación de clave única (`PostgresException`).

## 4. Idempotencia

El sistema tolera entregas repetidas del broker (*at-least-once delivery*) en ambos sentidos:

1. **En `Inventory.Worker` (`ReserveStockHandler.HandleAsync`)**: primero ejecuta una lectura rápida sin seguimiento (`SELECT` sobre `processed_events` por `EventId`); si no existe, inicia una única transacción en PostgreSQL donde:
   1. Inserta el registro en `processed_events` (`EventId` como clave primaria; en carrera concurrente, `PostgresException` con `SqlState == 23505` revierte y devuelve `AlreadyProcessed`).
   2. Ejecuta el descuento condicional atómico: `UPDATE stock SET disponible = disponible - @cantidad WHERE sku = @sku AND disponible >= @cantidad` (vía `ExecuteUpdateAsync`).
   3. Inserta el evento `StockReserved` o `StockRejected` en `outbox_messages` y confirma la transacción.
2. **En `Orders.Api` (`StockResultConsumer`)**: actualiza el pedido de forma condicional solo cuando sigue en `Pending` (`WHERE id = @orderId AND estado = 'Pending'`). Cualquier evento duplicado o contradictorio posterior se ignora y se confirma (`ack`).

**Límite del diseño actual**: la clave de idempotencia es `eventId`. Como cada pedido emite exactamente un `OrderCreated` (con el `Id` del registro de outbox), las reentregas de ese evento son seguras; un segundo tipo de evento futuro sobre el mismo pedido requeriría una clave compuesta o por `orderId` + tipo.

**Alcance de las pruebas de idempotencia**: las pruebas unitarias de duplicados son secuenciales y ejercitan la lectura previa rápida sobre `processed_events`; la ruta concurrente queda protegida por la clave primaria en `processed_events` más el `UPDATE` condicional dentro de la transacción, y está cubierta por la demo en ejecución con pedidos en paralelo, no por una prueba automatizada.

Cómo demostrarlo en vivo (con la pila arriba; requiere `docker compose`, `curl` y `python3` en el anfitrión):

```bash
./scripts/demo-idempotency.sh
```

El script crea un pedido, espera a que pase a `Confirmed`, lee el evento original de `outbox_messages`, vuelve a publicarlo en `orderflow.events` mediante la API HTTP de RabbitMQ, compara el stock antes y después, e imprime la fila correspondiente en `processed_events`.

## 5. Manejo de fallos

| Escenario | Qué ocurre en el sistema |
| --- | --- |
| **`inventory-worker` caído** | `Orders.Api` sigue aceptando pedidos (`201`, `Pending`). RabbitMQ retiene `order.created` en la cola durable `inventory.order-created`; al volver el worker, procesa los mensajes acumulados. |
| **RabbitMQ caído al publicar** | `Orders.Api` persiste el pedido y el registro en `outbox_messages`. `OutboxPublisher` reintenta con *backoff* exponencial (hasta 30 s) y solo marca `PublishedAt` tras *publisher confirm*. |
| **Evento duplicado** | `Inventory.Worker` detecta la clave primaria repetida en `processed_events` y hace `ack` sin volver a descontar; `Orders.Api` solo transiciona pedidos en estado `Pending`. |
| **Falla transitoria de base de datos** | Si el manejador lanza excepción, el consumidor republica el mensaje al exchange `orderflow.retry` hacia la cola `.retry` (`inventory.order-created.retry` o `orders.stock-result.retry`) con `x-message-ttl` = `RETRY_DELAY_MS` (5000 ms por defecto) y espera *publisher confirm* antes de hacer `ack`. Al vencer el TTL, RabbitMQ lo devuelve a `orderflow.events` conservando la *routing key* original. |
| **Mensaje que falla siempre / mensaje venenoso** | Tras agotar `MAX_DELIVERY_ATTEMPTS` (5 intentos por defecto vía header `x-attempt`: 4 esperas de 5 s = 20 s más el tiempo que tarda cada intento en fallar; con la base de datos caída cada intento tarda ~5 s en vencer por *timeout*, por lo que el mensaje llega a la DLQ tras ~40 s), el consumidor hace `BasicNack(..., requeue: false)` y RabbitMQ lo enruta vía `orderflow.dlx` hacia la cola `.dlq`. Si el JSON es inválido o el tipo/routing key es desconocido, va directo a `.dlq` sin reintentos. |
| **`orders-api` se reinicia con mensajes pendientes** | Los mensajes no confirmados en `orders.stock-result` vuelven a entregarse y los registros del *outbox* con `PublishedAt IS NULL` se publican al reconectar. |
| **Dos pedidos compiten por el último stock** | `ExecuteUpdateAsync` traduce el descuento a un `UPDATE` atómico condicional en PostgreSQL (`disponible >= cantidad`). Solo la transacción ganadora afecta 1 fila (`Confirmed`); la otra afecta 0 filas y emite `StockRejected` (`insufficient_stock`). |

**Configuración de `RETRY_DELAY_MS`**: este valor queda fijado cuando se declaran por primera vez las colas `.retry`. RabbitMQ no permite cambiar argumentos de una cola existente, de modo que modificar `RETRY_DELAY_MS` en un broker ya inicializado hará que ambos servicios fallen con `PRECONDITION_FAILED` hasta eliminar las dos colas `.retry` (es seguro borrarlas cuando están vacías). Ambos servicios deben usar el mismo valor.

**Qué NO está cubierto actualmente**:

- Si una caída dura más que la ventana de reintentos y envía `OrderCreated` a `inventory.order-created.dlq`, el pedido permanece en `Pending` hasta que el mensaje se reprocese desde la DLQ.
- Si `StockReserved` agota los reintentos y termina en `orders.stock-result.dlq`, el stock ya quedó descontado en `inventory` y el pedido permanece en `Pending` hasta que un operador reprocese el mensaje desde la DLQ (en RabbitMQ Management UI: inspeccionar `orders.stock-result.dlq` y mover con *Shovel* o republicar el payload hacia `orderflow.events` con su *routing key* original `stock.reserved`). No hay compensación automática ni expiración de pedidos `Pending`.
- Los registros publicados de `outbox_messages` (`PublishedAt != null`) no se purgan ni archivan automáticamente.

## 6. Tests

Backend (ejecuta los proyectos `.NET` dentro del contenedor oficial SDK 8.0):

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test
```

Frontend con solo Docker (funciona en un clon limpio sin Node instalado):

```bash
docker run --rm -v "$PWD/frontend":/app -w /app node:22-alpine sh -c "npm ci && npm test -- --run"
```

Alternativa local para el frontend (requiere Node 22+):

```bash
cd frontend && npm ci && npm test -- --run
```

Qué prueba cada caso (14 casos en backend, 4 en frontend):

- **Inventory.Tests (`ReserveStockHandlerTests`, 4 casos)**:
  - `SameOrderCreatedHandledTwice_DiscountsOnceAndCreatesSingleOutbox`: entregar dos veces el mismo `OrderCreated` al mismo manejador descuenta una sola vez y genera un único mensaje de outbox.
  - `SameEventHandledByTwoHandlerInstancesSequentially_DiscountsStockOnce`: dos instancias de `ReserveStockHandler` con `InventoryDbContext` independientes sobre la misma conexión SQLite procesan el evento de forma secuencial una sola vez.
  - `InsufficientStock_LeavesStockUnchangedAndEmitsRejected`: stock insuficiente no altera el inventario y encola `StockRejected` con `insufficient_stock`.
  - `UnknownSku_EmitsStockRejectedWithUnknownSku`: un SKU inexistente encola `StockRejected` con `unknown_sku`.
- **Orders.Tests (`OrderFlowTests` y `RetryDecisionTests`, 10 casos)**:
  - `OrderValidator_InvalidInput_ReportsExpectedFieldKey` (3 casos): verifica qué clave exacta de error (`clienteNombre`, `sku`, `cantidad`) se produce para cada entrada inválida.
  - `CreateOrderEndpoint_RejectsUnknownSkuAndAcceptsSeededSku`: recorre `HandleCreateOrderAsync` con SQLite real y comprueba error `sku` (400) ante SKU desconocido y creación `Pending` (201) ante SKU existente.
  - `CreateOrderWithOutbox_PersistsPendingOrderAndOutboxMessage`: verifica que `CreateOrderWithOutboxAsync` guarda el pedido `Pending` y el `OrderCreated` en outbox.
  - `CreateOrderWithOutbox_WhenOutboxInsertFails_RollsBackOrder`: fuerza fallo al insertar en `outbox_messages` y comprueba que no queda ningún pedido persistido.
  - `StockResult_ConfirmsPendingOrderAndIgnoresContradictorySecondResult`: confirma un pedido `Pending` e ignora un rechazo posterior sobre el mismo pedido ya confirmado.
  - `RetryDecisionTests.For_ReturnsExpectedAction` (3 casos): valida la decisión pura `Retry` vs `DeadLetter` según intento actual y máximo configurado.
- **Frontend (`frontend/src/OrderFlow.test.tsx`, 4 casos)**:
  - Muestra errores de validación en el formulario sin llamar a la API cuando los campos son inválidos.
  - Mapea un `400 ProblemDetails` del servidor mostrando el error en el campo afectado y la alerta visible dentro del formulario.
  - Renderiza las insignias de los tres estados (`Pendiente`, `Confirmado`, `Rechazado`) con su texto, estilo distintivo y motivo de rechazo.
  - Verifica que `useOrders` encola un refresco si `refresh` se invoca con un *poll* en vuelo y aborta la petición activa al desmontar.

## 7. Kubernetes y escalado

En `k8s/` se incluyen los manifiestos base para `orders-api` e `inventory-worker`:

- `k8s/configmap.yaml`: configuración no sensible (`ASPNETCORE_URLS`, `RABBITMQ_HOST`, `RABBITMQ_PORT`, `OUTBOX_POLL_MS`, `RETRY_DELAY_MS`, `MAX_DELIVERY_ATTEMPTS`, `RUN_MIGRATIONS: "false"`, `SEED_PATH`).
- `k8s/secret.example.yaml`: plantilla con valores ficticios para cadenas de conexión y credenciales de RabbitMQ.
- `k8s/migrations-job.yaml`: `Job` (`orders-db-migrate`, con `ttlSecondsAfterFinished: 600`) que ejecuta la imagen de `orders-api` con `--migrate` para aplicar migraciones y *seed*. **No se ordena automáticamente**: hay que aplicarlo primero y esperar (`kubectl wait --for=condition=complete job/orders-db-migrate`) antes de aplicar el `Deployment` (y cambiar su nombre o borrar el `Job` previo en cada despliegue porque su plantilla es inmutable). La sonda `/health/ready` solo verifica conectividad con la base de datos, no la versión del esquema; para garantizar orden automático en producción conviene usar un `initContainer` o un *hook* de Helm/Argo.
- `k8s/deployment.yaml` y `k8s/service.yaml`: `orders-api` con 2 réplicas, `RUN_MIGRATIONS=false`, usuario no *root* (`1654`), sistema de archivos raíz de solo lectura, límites de CPU/memoria, sondas `/health/live` y `/health/ready` y `Service` `ClusterIP`.
- `k8s/inventory-worker-deployment.yaml`: `inventory-worker` con 1 réplica, sin `Service` ni `livenessProbe` (documentado en el manifiesto). `RUN_MIGRATIONS` y `--migrate` existen solo en `Orders.Api`; el worker sigue ejecutando sus migraciones al arrancar, por lo que debe mantenerse en 1 réplica en los manifiestos hasta mover sus migraciones a un `Job`.

**Comportamiento al escalar a N réplicas**: los `OutboxPublisher` de múltiples réplicas consultan `outbox_messages WHERE published_at IS NULL` sin bloqueo de filas (`FOR UPDATE SKIP LOCKED`), por lo que un mismo evento puede publicarse más de una vez antes de marcar `PublishedAt`. Esto es inocuo porque ambos consumidores son idempotentes (`processed_events` y actualización condicional sobre `Pending`); si el volumen creciera, se elimina la publicación duplicada seleccionando lotes con `FOR UPDATE SKIP LOCKED` en PostgreSQL.

## 8. Qué haría distinto con más tiempo

- Agregar un mecanismo de expiración/timeout para pedidos que permanezcan en `Pending` más allá de un umbral configurable.
- Instrumentar ambos servicios con OpenTelemetry y propagar un `correlationId` de extremo a extremo.
- Añadir una prueba de concurrencia contra PostgreSQL real con Testcontainers (junto con pruebas de topología AMQP con RabbitMQ).
- Incorporar limpieza periódica de `outbox_messages` ya publicadas (`PublishedAt IS NOT NULL`) y una herramienta operativa para reinyectar mensajes en `.dlq`.
- Extraer una clase base de consumidor para eliminar las ~150 líneas duplicadas entre `OrderCreatedConsumer` y `StockResultConsumer`.
- Usar un *exchange* de reintento dedicado por consumidor para que los reintentos diferidos no alcancen a futuros suscriptores del *topic exchange* compartido.
- Utilizar *quorum queues* con `delivery-limit` para mensajes que provoquen la caída del proceso antes de responder.

## 9. Uso de IA

Esta solución se desarrolló con asistencia de IA, como permite el enunciado: se usó para generar código, pruebas y documentación, y para auditar el resultado. La arquitectura, el contrato de eventos y la lógica de transacciones y reintentos se validaron ejecutando las pruebas y el entorno completo en Docker, incluidos los escenarios de fallo descritos en este documento.
