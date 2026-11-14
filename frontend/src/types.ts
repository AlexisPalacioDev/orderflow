export type OrderStatus = 'Pending' | 'Confirmed' | 'Rejected';

export interface Product {
  sku: string;
  nombre: string;
}

export interface Order {
  id: string;
  clienteNombre: string;
  sku: string;
  cantidad: number;
  estado: OrderStatus;
  creadoEn: string;
  actualizadoEn: string;
  motivoRechazo: string | null;
}

export interface CreateOrderRequest {
  clienteNombre: string;
  sku: string;
  cantidad: number;
}

export interface FieldErrors {
  clienteNombre?: string;
  sku?: string;
  cantidad?: string;
}

export interface ApiErrorPayload {
  fieldErrors: FieldErrors;
  message: string;
}
