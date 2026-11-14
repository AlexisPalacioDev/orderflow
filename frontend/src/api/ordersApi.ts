import type { ApiErrorPayload, CreateOrderRequest, FieldErrors, Order, Product } from '../types';

const API_BASE = '/api';

interface ProblemDetailsShape {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error implements ApiErrorPayload {
  readonly status: number;
  readonly fieldErrors: FieldErrors;

  constructor(status: number, fieldErrors: FieldErrors, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.fieldErrors = fieldErrors;
  }
}

export function mapProblemDetails(body: unknown, status: number): ApiErrorPayload {
  const problem = (typeof body === 'object' && body !== null ? body : {}) as ProblemDetailsShape;
  const fieldErrors: FieldErrors = {};
  const extraMessages: string[] = [];

  if (problem.errors && typeof problem.errors === 'object') {
    for (const [rawKey, messages] of Object.entries(problem.errors)) {
      if (!Array.isArray(messages) || messages.length === 0) {
        continue;
      }

      const firstMessage = String(messages[0]);
      const normalized = rawKey.toLowerCase();

      if (normalized === 'clientenombre') {
        fieldErrors.clienteNombre = firstMessage;
      } else if (normalized === 'sku') {
        fieldErrors.sku = firstMessage;
      } else if (normalized === 'cantidad') {
        fieldErrors.cantidad = firstMessage;
      } else {
        extraMessages.push(firstMessage);
      }
    }
  }

  const hasFieldErrors = Object.keys(fieldErrors).length > 0;
  let message = 'No se pudo completar la solicitud.';

  if (status === 400) {
    if (extraMessages.length > 0) {
      message = extraMessages.join(' ');
    } else if (hasFieldErrors) {
      message = 'No se pudo crear el pedido. Revisa los campos marcados.';
    } else if (problem.detail) {
      message = problem.detail;
    } else {
      message = 'Los datos del pedido no son válidos.';
    }
  } else if (status >= 500) {
    message = 'El servidor no pudo procesar la solicitud. Inténtalo nuevamente.';
  } else if (problem.detail) {
    message = problem.detail;
  }

  return { fieldErrors, message };
}

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${API_BASE}${path}`, {
      ...init,
      headers: {
        Accept: 'application/json',
        ...(init?.headers ?? {}),
      },
    });
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      throw err;
    }
    throw new ApiError(0, {}, 'No hay conexión con el servidor. Verifica tu red e inténtalo de nuevo.');
  }

  if (!response.ok) {
    let body: unknown = null;
    try {
      body = await response.json();
    } catch {
      body = null;
    }

    const mapped = mapProblemDetails(body, response.status);
    throw new ApiError(response.status, mapped.fieldErrors, mapped.message);
  }

  return (await response.json()) as T;
}

export function listProducts(signal?: AbortSignal): Promise<Product[]> {
  return requestJson<Product[]>('/products', { signal });
}

export function listOrders(signal?: AbortSignal): Promise<Order[]> {
  return requestJson<Order[]>('/orders', { signal });
}

export function createOrder(payload: CreateOrderRequest): Promise<Order> {
  return requestJson<Order>('/orders', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(payload),
  });
}
