export type TrackingState = 'idle' | 'connecting' | 'live' | 'reconnecting' | 'offline';
export interface TrackingConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, order: string): Promise<unknown>;
  on(name: string, callback: (message: { orderId: string }) => void): void;
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
}

/** Owns one order subscription; retired connections cannot refresh another order. */
export class TrackingSession {
  private connection?: TrackingConnection;
  private order?: string;
  private listener?: () => void;
  private generation = 0;
  constructor(
    private create: () => Promise<TrackingConnection>,
    private changed: (state: TrackingState) => void,
  ) {}

  async watch(order: string, listener: () => void) {
    const generation = ++this.generation;
    const previous = this.connection;
    this.connection = undefined;
    this.order = order;
    this.listener = listener;
    this.changed('connecting');
    await previous?.stop();
    if (generation !== this.generation) return;
    let connection: TrackingConnection | undefined;
    try {
      connection = await this.create();
      if (generation !== this.generation) {
        await connection.stop();
        return;
      }
      this.connection = connection;
      const current = () => generation === this.generation && this.connection === connection;
      connection.on('orderChanged', (message) => {
        if (current() && message.orderId === this.order) this.listener?.();
      });
      connection.onreconnecting(() => {
        if (current()) this.changed('reconnecting');
      });
      connection.onclose(() => {
        if (current()) this.changed('offline');
      });
      connection.onreconnected(() => {
        void subscribe();
      });
      const subscribe = async () => {
        if (!current()) return;
        try {
          await connection!.invoke('Subscribe', order);
          if (current()) {
            this.changed('live');
            this.listener?.();
          }
        } catch {
          if (current()) {
            this.changed('offline');
            this.connection = undefined;
            await connection!.stop().catch(() => {});
          }
        }
      };
      await connection.start();
      if (!current()) {
        await connection.stop();
        return;
      }
      await subscribe();
    } catch (error) {
      if (generation === this.generation) {
        this.connection = undefined;
        this.changed('offline');
      }
      await connection?.stop().catch(() => {});
      throw error;
    }
  }
  async stop() {
    ++this.generation;
    this.order = undefined;
    this.listener = undefined;
    const connection = this.connection;
    this.connection = undefined;
    this.changed('idle');
    await connection?.stop();
  }
}

/** Coalesces bursts while guaranteeing a trailing read for an event received during a read. */
export class TrackingRefresh {
  private version = 0;
  private active = false;
  constructor(private refresh: () => Promise<void>) {}
  async request() {
    ++this.version;
    if (this.active) return;
    this.active = true;
    try {
      let version: number;
      do {
        version = this.version;
        await this.refresh();
      } while (version !== this.version);
    } finally {
      this.active = false;
    }
  }
}
