#!/usr/bin/env bash
set -euo pipefail

API_PORT="${API_PORT:-5080}"
RABBIT_UI_PORT="${RABBIT_UI_PORT:-15672}"
RABBITMQ_USER="${RABBITMQ_USER:-orderflow}"
RABBITMQ_PASSWORD="${RABBITMQ_PASSWORD:-orderflow}"
POSTGRES_USER="${POSTGRES_USER:-orderflow}"
POSTGRES_ORDERS_DB="${POSTGRES_ORDERS_DB:-orders}"
POSTGRES_INVENTORY_DB="${POSTGRES_INVENTORY_DB:-inventory}"
SKU="ABC-01"

echo "1) Creating order via POST /orders..."
ORDER_JSON=$(curl -sf -X POST "http://localhost:${API_PORT}/orders" \
  -H "content-type: application/json" \
  -d "{\"clienteNombre\":\"Demo Idempotencia\",\"sku\":\"${SKU}\",\"cantidad\":2}")
echo "$ORDER_JSON"

ORDER_ID=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])' <<< "$ORDER_JSON")
echo "Order ID: ${ORDER_ID}"

echo "2) Waiting for order to become Confirmed..."
for _ in $(seq 1 20); do
  STATUS=$(curl -sf "http://localhost:${API_PORT}/orders/${ORDER_ID}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["estado"])')
  if [ "$STATUS" = "Confirmed" ]; then
    echo "Order status: ${STATUS}"
    break
  fi
  sleep 0.5
done

echo "3) Reading original OrderCreated payload from orders outbox..."
PAYLOAD=$(docker compose exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_ORDERS_DB}" -t -A -c \
  "SELECT payload FROM outbox_messages WHERE payload LIKE '%${ORDER_ID}%' LIMIT 1;")
EVENT_ID=$(python3 -c 'import json,sys; print(json.load(sys.stdin)["eventId"])' <<< "$PAYLOAD")
echo "Captured eventId: ${EVENT_ID}"

echo "4) Stock BEFORE duplicate publish:"
STOCK_BEFORE=$(docker compose exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_INVENTORY_DB}" -t -A -c \
  "SELECT sku || ':' || disponible FROM stock WHERE sku = '${SKU}';")
echo "$STOCK_BEFORE"

echo "5) Republishing identical OrderCreated event via RabbitMQ Management HTTP API..."
RABBIT_BODY=$(python3 -c '
import json, sys
payload = sys.stdin.read().strip()
print(json.dumps({
    "properties": {"delivery_mode": 2, "content_type": "application/json"},
    "routing_key": "order.created",
    "payload": payload,
    "payload_encoding": "string"
}))
' <<< "$PAYLOAD")

curl -sf -u "${RABBITMQ_USER}:${RABBITMQ_PASSWORD}" \
  -H "content-type: application/json" \
  -X POST "http://localhost:${RABBIT_UI_PORT}/api/exchanges/%2F/orderflow.events/publish" \
  -d "$RABBIT_BODY"
echo ""

sleep 3

echo "6) Stock AFTER duplicate publish:"
STOCK_AFTER=$(docker compose exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_INVENTORY_DB}" -t -A -c \
  "SELECT sku || ':' || disponible FROM stock WHERE sku = '${SKU}';")
echo "$STOCK_AFTER"

echo "7) ProcessedEvent rows for ${EVENT_ID}:"
docker compose exec -T postgres psql -U "${POSTGRES_USER}" -d "${POSTGRES_INVENTORY_DB}" -t -A -c \
  "SELECT event_id, processed_at FROM processed_events WHERE event_id = '${EVENT_ID}';"

if [ "$STOCK_BEFORE" = "$STOCK_AFTER" ]; then
  echo "OK: Stock remained unchanged (${STOCK_AFTER}) after duplicate OrderCreated delivery."
else
  echo "FAIL: Stock changed from ${STOCK_BEFORE} to ${STOCK_AFTER}!" >&2
  exit 1
fi
