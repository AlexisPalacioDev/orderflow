import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { OrderForm } from './components/OrderForm';
import { OrderList } from './components/OrderList';
import type { Order, Product } from './types';

const SAMPLE_PRODUCTS: Product[] = [
  { sku: 'ABC-01', nombre: 'Teclado Mecánico Compacto' },
  { sku: 'XYZ-20', nombre: 'Webcam 4K USB-C' },
];

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('OrderForm and OrderList', () => {
  it('shows validation errors and does not call the API when submitted invalid', async () => {
    const user = userEvent.setup();
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const onOrderCreated = vi.fn();

    render(
      <OrderForm
        products={SAMPLE_PRODUCTS}
        onOrderCreated={onOrderCreated}
      />
    );

    const qtyInput = screen.getByLabelText('Cantidad');
    await user.clear(qtyInput);
    await user.type(qtyInput, '0');
    await user.click(screen.getByRole('button', { name: 'Crear pedido' }));

    expect(screen.getByText('El nombre del cliente es obligatorio.')).toBeInTheDocument();
    expect(screen.getByText('Debes seleccionar un producto.')).toBeInTheDocument();
    expect(screen.getByText('La cantidad debe estar entre 1 y 100.')).toBeInTheDocument();
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(onOrderCreated).not.toHaveBeenCalled();
  });

  it('shows the server field error and alert when the API returns 400 ProblemDetails', async () => {
    const user = userEvent.setup();
    const serverMessage = "El SKU 'ABC-01' no existe en el catálogo.";

    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: {
            sku: [serverMessage],
          },
        }),
        {
          status: 400,
          headers: { 'Content-Type': 'application/problem+json' },
        }
      )
    );

    render(
      <OrderForm
        products={SAMPLE_PRODUCTS}
        onOrderCreated={vi.fn()}
      />
    );

    await user.type(screen.getByLabelText('Cliente'), 'Ana García');
    await user.selectOptions(screen.getByLabelText('Producto'), 'ABC-01');
    await user.click(screen.getByRole('button', { name: 'Crear pedido' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'No se pudo crear el pedido. Revisa los campos marcados.'
    );
    expect(screen.getByText(serverMessage)).toBeInTheDocument();
  });

  it('renders status badges for Pending, Confirmed and Rejected orders', () => {
    const orders: Order[] = [
      {
        id: '11111111-1111-1111-1111-111111111111',
        clienteNombre: 'Ana',
        sku: 'ABC-01',
        cantidad: 2,
        estado: 'Pending',
        creadoEn: '2026-11-14T20:00:00Z',
        actualizadoEn: '2026-11-14T20:00:00Z',
        motivoRechazo: null,
      },
      {
        id: '22222222-2222-2222-2222-222222222222',
        clienteNombre: 'Luis',
        sku: 'ABC-01',
        cantidad: 1,
        estado: 'Confirmed',
        creadoEn: '2026-11-14T20:01:00Z',
        actualizadoEn: '2026-11-14T20:01:02Z',
        motivoRechazo: null,
      },
      {
        id: '33333333-3333-3333-3333-333333333333',
        clienteNombre: 'Marta',
        sku: 'XYZ-20',
        cantidad: 3,
        estado: 'Rejected',
        creadoEn: '2026-11-14T20:02:00Z',
        actualizadoEn: '2026-11-14T20:02:02Z',
        motivoRechazo: 'insufficient_stock',
      },
    ];

    render(
      <OrderList
        orders={orders}
        products={SAMPLE_PRODUCTS}
        loading={false}
        error={null}
      />
    );

    const pendingBadge = screen.getByText('Pendiente');
    const confirmedBadge = screen.getByText('Confirmado');
    const rejectedBadge = screen.getByText('Rechazado');

    expect(pendingBadge).toHaveClass('badge--pending');
    expect(confirmedBadge).toHaveClass('badge--confirmed');
    expect(rejectedBadge).toHaveClass('badge--rejected');
    expect(screen.getByText('Stock insuficiente')).toBeInTheDocument();
  });
});
