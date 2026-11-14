import type { OrderStatus } from '../types';

interface StatusBadgeProps {
  status: OrderStatus;
}

const STATUS_META: Record<OrderStatus, { label: string; className: string }> = {
  Pending: {
    label: 'Pendiente',
    className: 'badge badge--pending',
  },
  Confirmed: {
    label: 'Confirmado',
    className: 'badge badge--confirmed',
  },
  Rejected: {
    label: 'Rechazado',
    className: 'badge badge--rejected',
  },
};

export function StatusBadge({ status }: StatusBadgeProps) {
  const meta = STATUS_META[status];
  return (
    <span className={meta.className}>
      <span className="badge__dot" aria-hidden="true" />
      {meta.label}
    </span>
  );
}
