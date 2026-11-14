import { useEffect, useState } from 'react';
import { listProducts } from './api/ordersApi';
import { OrderForm } from './components/OrderForm';
import { OrderList } from './components/OrderList';
import { useOrders } from './hooks/useOrders';
import type { Product } from './types';

export function App() {
  const [products, setProducts] = useState<Product[]>([]);
  const [productsLoading, setProductsLoading] = useState<boolean>(true);
  const [productsError, setProductsError] = useState<string | null>(null);
  const { orders, loading, error, refresh } = useOrders();

  useEffect(() => {
    const controller = new AbortController();

    listProducts(controller.signal)
      .then((catalog) => {
        setProducts(catalog);
        setProductsError(null);
      })
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        setProductsError('No se pudo cargar el catálogo de productos.');
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setProductsLoading(false);
        }
      });

    return () => controller.abort();
  }, []);

  return (
    <div className="page">
      <header className="page-header">
        <div className="page-header__inner">
          <h1 className="page-header__title">OrderFlow</h1>
          <p className="page-header__subtitle">
            Registro de pedidos y verificación asíncrona de inventario
          </p>
        </div>
      </header>

      <main className="layout-grid">
        <OrderForm
          products={products}
          productsLoading={productsLoading}
          productsError={productsError}
          onOrderCreated={refresh}
        />
        <OrderList
          orders={orders}
          products={products}
          loading={loading}
          error={error}
        />
      </main>
    </div>
  );
}
