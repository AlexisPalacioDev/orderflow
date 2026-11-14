import type { Order, Product } from '../types';
import { StatusBadge } from './StatusBadge';

interface OrderListProps {
  orders: Order[];
  products: Product[];
  loading: boolean;
  error: string | null;
}

const REJECTION_REASON_LABELS: Record<string, string> = {
  insufficient_stock: 'Stock insuficiente',
  unknown_sku: 'SKU desconocido',
};

function formatLocalDateTime(isoString: string): string {
  const date = new Date(isoString);
  if (Number.isNaN(date.getTime())) {
    return isoString;
  }
  return new Intl.DateTimeFormat('es', {
    dateStyle: 'short',
    timeStyle: 'medium',
  }).format(date);
}

function formatRejectionReason(reason: string | null): string {
  if (!reason) {
    return '—';
  }
  return REJECTION_REASON_LABELS[reason] ?? reason;
}

export function OrderList({ orders, products, loading, error }: OrderListProps) {
  const productNames = new Map(products.map((product) => [product.sku, product.nombre]));

  return (
    <section className="card" aria-labelledby="order-list-heading">
      <div className="card__header">
        <h2 id="order-list-heading" className="card__title">
          Pedidos recientes
        </h2>
        <span className="card__meta">Actualización automática cada 2 s</span>
      </div>

      {error && (
        <div className="alert alert--warning" role="status" aria-live="polite">
          {error}
        </div>
      )}

      {loading && orders.length === 0 ? (
        <div className="skeleton-list" aria-busy="true" aria-label="Cargando pedidos">
          <div className="skeleton-row" />
          <div className="skeleton-row" />
          <div className="skeleton-row" />
        </div>
      ) : orders.length === 0 ? (
        <p className="empty-state">Aún no hay pedidos</p>
      ) : (
        <div className="table-scroll">
          <table className="orders-table">
            <thead>
              <tr>
                <th scope="col">Cliente</th>
                <th scope="col">Producto</th>
                <th scope="col">Cantidad</th>
                <th scope="col">Estado</th>
                <th scope="col">Motivo de rechazo</th>
                <th scope="col">Creado</th>
              </tr>
            </thead>
            <tbody>
              {orders.map((order) => {
                const productName = productNames.get(order.sku);
                return (
                  <tr key={order.id}>
                    <td className="orders-table__client">{order.clienteNombre}</td>
                    <td>
                      {productName ? (
                        <>
                          <span>{productName}</span>{' '}
                          <span className="sku-code">({order.sku})</span>
                        </>
                      ) : (
                        <span className="sku-code">{order.sku}</span>
                      )}
                    </td>
                    <td className="orders-table__qty">{order.cantidad}</td>
                    <td>
                      <StatusBadge status={order.estado} />
                    </td>
                    <td className="orders-table__reason">
                      {formatRejectionReason(order.motivoRechazo)}
                    </td>
                    <td className="orders-table__date">
                      {formatLocalDateTime(order.creadoEn)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
