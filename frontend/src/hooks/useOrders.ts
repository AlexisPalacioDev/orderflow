import { useCallback, useEffect, useRef, useState } from 'react';
import { listOrders } from '../api/ordersApi';
import type { Order } from '../types';

const POLL_INTERVAL_MS = 2000;
const POLL_ERROR_MESSAGE = 'No se pudo actualizar la lista. Reintentando…';

export interface UseOrdersResult {
  orders: Order[];
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
}

export function useOrders(): UseOrdersResult {
  const [orders, setOrders] = useState<Order[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<string | null>(null);

  const mountedRef = useRef(true);
  const inFlightRef = useRef(false);
  const pendingRefreshRef = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    if (inFlightRef.current) {
      pendingRefreshRef.current = true;
      return;
    }

    inFlightRef.current = true;
    try {
      do {
        pendingRefreshRef.current = false;
        const controller = new AbortController();
        abortControllerRef.current = controller;

        try {
          const data = await listOrders(controller.signal);
          if (!mountedRef.current || controller.signal.aborted) {
            return;
          }
          // Orders arrive pre-sorted newest-first from GET /orders; server ordering is the single source of truth.
          setOrders(data);
          setError(null);
        } catch (err) {
          if (err instanceof DOMException && err.name === 'AbortError') {
            return;
          }
          if (!mountedRef.current) {
            return;
          }
          setError(POLL_ERROR_MESSAGE);
        } finally {
          if (abortControllerRef.current === controller) {
            abortControllerRef.current = null;
          }
        }
      } while (pendingRefreshRef.current && mountedRef.current);
    } finally {
      inFlightRef.current = false;
      if (mountedRef.current) {
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    mountedRef.current = true;
    void refresh();

    let intervalId: number | null = null;

    const startPolling = () => {
      if (intervalId !== null) {
        return;
      }
      intervalId = window.setInterval(() => {
        if (document.visibilityState !== 'hidden') {
          void refresh();
        }
      }, POLL_INTERVAL_MS);
    };

    const stopPolling = () => {
      if (intervalId !== null) {
        window.clearInterval(intervalId);
        intervalId = null;
      }
    };

    const handleVisibilityChange = () => {
      if (document.visibilityState === 'hidden') {
        stopPolling();
      } else {
        void refresh();
        startPolling();
      }
    };

    if (document.visibilityState !== 'hidden') {
      startPolling();
    }

    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      mountedRef.current = false;
      abortControllerRef.current?.abort();
      stopPolling();
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [refresh]);

  return { orders, loading, error, refresh };
}
