/** Keeps the same idempotency key until a refund receives a successful response. */
export class RefundIntent {
  private current?: { order: string; amount: number; reason: string; operationId: string };
  constructor(private newId: () => string) {}
  payload(order: string, amount: number, reason: string) {
    if (
      !this.current ||
      this.current.order !== order ||
      this.current.amount !== amount ||
      this.current.reason !== reason
    )
      this.current = { order, amount, reason, operationId: this.newId() };
    return { operationId: this.current.operationId, amount, reason };
  }
  complete() {
    this.current = undefined;
  }
}
