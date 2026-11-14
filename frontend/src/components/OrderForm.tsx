import { useState, type FormEvent } from 'react';
import { ApiError, createOrder } from '../api/ordersApi';
import type { FieldErrors, Product } from '../types';

interface OrderFormProps {
  products: Product[];
  productsLoading?: boolean;
  productsError?: string | null;
  onOrderCreated: () => Promise<void> | void;
}

function validateOrderInput(clienteNombre: string, sku: string, cantidadRaw: string): FieldErrors {
  const errors: FieldErrors = {};
  const trimmedName = clienteNombre.trim();

  if (!trimmedName) {
    errors.clienteNombre = 'El nombre del cliente es obligatorio.';
  } else if (trimmedName.length > 120) {
    errors.clienteNombre = 'El nombre del cliente no puede superar los 120 caracteres.';
  }

  if (!sku.trim()) {
    errors.sku = 'Debes seleccionar un producto.';
  }

  const parsedQty = Number(cantidadRaw);
  if (!cantidadRaw.trim() || !Number.isInteger(parsedQty) || parsedQty < 1 || parsedQty > 100) {
    errors.cantidad = 'La cantidad debe estar entre 1 y 100.';
  }

  return errors;
}

export function OrderForm({
  products,
  productsLoading = false,
  productsError = null,
  onOrderCreated,
}: OrderFormProps) {
  const [clienteNombre, setClienteNombre] = useState('');
  const [sku, setSku] = useState('');
  const [cantidad, setCantidad] = useState('1');
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [serverError, setServerError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setServerError(null);
    setSuccessMessage(null);

    const clientErrors = validateOrderInput(clienteNombre, sku, cantidad);
    if (Object.keys(clientErrors).length > 0) {
      setFieldErrors(clientErrors);
      return;
    }

    setFieldErrors({});
    setSubmitting(true);

    try {
      const created = await createOrder({
        clienteNombre: clienteNombre.trim(),
        sku: sku.trim(),
        cantidad: Number(cantidad),
      });

      setClienteNombre('');
      setSku('');
      setCantidad('1');
      setFieldErrors({});
      setSuccessMessage(`Pedido ${created.id.slice(0, 8)} creado en estado Pendiente.`);
      await onOrderCreated();
    } catch (err) {
      if (err instanceof ApiError) {
        setFieldErrors(err.fieldErrors);
        setServerError(err.message);
      } else if (err instanceof Error) {
        setServerError(err.message);
      } else {
        setServerError('No se pudo crear el pedido. Inténtalo nuevamente.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <section className="card" aria-labelledby="order-form-heading">
      <h2 id="order-form-heading" className="card__title">
        Nuevo pedido
      </h2>

      <form className="order-form" onSubmit={handleSubmit} noValidate>
        {serverError && (
          <div className="alert alert--error" role="alert" aria-live="assertive">
            {serverError}
          </div>
        )}

        {successMessage && (
          <div className="alert alert--success" role="status" aria-live="polite">
            {successMessage}
          </div>
        )}

        {productsError && (
          <div className="alert alert--warning" role="alert" aria-live="polite">
            {productsError}
          </div>
        )}

        <div className="form-field">
          <label className="form-field__label" htmlFor="clienteNombre">
            Cliente
          </label>
          <input
            id="clienteNombre"
            name="clienteNombre"
            type="text"
            className={`form-field__control ${fieldErrors.clienteNombre ? 'form-field__control--error' : ''}`}
            value={clienteNombre}
            onChange={(e) => {
              setClienteNombre(e.target.value);
              if (fieldErrors.clienteNombre) {
                setFieldErrors((prev) => ({ ...prev, clienteNombre: undefined }));
              }
            }}
            placeholder="Ej. Ana García"
            maxLength={120}
            aria-invalid={Boolean(fieldErrors.clienteNombre)}
            aria-describedby="clienteNombre-hint"
            disabled={submitting}
          />
          <p
            id="clienteNombre-hint"
            className={`form-field__message ${fieldErrors.clienteNombre ? 'form-field__message--error' : ''}`}
          >
            {fieldErrors.clienteNombre ?? 'Nombre del cliente (máximo 120 caracteres).'}
          </p>
        </div>

        <div className="form-field">
          <label className="form-field__label" htmlFor="sku">
            Producto
          </label>
          <select
            id="sku"
            name="sku"
            className={`form-field__control ${fieldErrors.sku ? 'form-field__control--error' : ''}`}
            value={sku}
            onChange={(e) => {
              setSku(e.target.value);
              if (fieldErrors.sku) {
                setFieldErrors((prev) => ({ ...prev, sku: undefined }));
              }
            }}
            aria-invalid={Boolean(fieldErrors.sku)}
            aria-describedby="sku-hint"
            disabled={submitting || productsLoading}
          >
            <option value="">
              {productsLoading ? 'Cargando catálogo…' : 'Selecciona un producto…'}
            </option>
            {products.map((product) => (
              <option key={product.sku} value={product.sku}>
                {product.nombre} ({product.sku})
              </option>
            ))}
          </select>
          <p
            id="sku-hint"
            className={`form-field__message ${fieldErrors.sku ? 'form-field__message--error' : ''}`}
          >
            {fieldErrors.sku ?? 'Producto del catálogo disponible para reservar.'}
          </p>
        </div>

        <div className="form-field">
          <label className="form-field__label" htmlFor="cantidad">
            Cantidad
          </label>
          <input
            id="cantidad"
            name="cantidad"
            type="number"
            min={1}
            max={100}
            step={1}
            className={`form-field__control ${fieldErrors.cantidad ? 'form-field__control--error' : ''}`}
            value={cantidad}
            onChange={(e) => {
              setCantidad(e.target.value);
              if (fieldErrors.cantidad) {
                setFieldErrors((prev) => ({ ...prev, cantidad: undefined }));
              }
            }}
            aria-invalid={Boolean(fieldErrors.cantidad)}
            aria-describedby="cantidad-hint"
            disabled={submitting}
          />
          <p
            id="cantidad-hint"
            className={`form-field__message ${fieldErrors.cantidad ? 'form-field__message--error' : ''}`}
          >
            {fieldErrors.cantidad ?? 'Cantidad entera entre 1 y 100 unidades.'}
          </p>
        </div>

        <button type="submit" className="btn-primary" disabled={submitting}>
          {submitting ? 'Enviando…' : 'Crear pedido'}
        </button>
      </form>
    </section>
  );
}
