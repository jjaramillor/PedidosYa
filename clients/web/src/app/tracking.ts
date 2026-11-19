import { Injectable, inject } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { Api } from './api';

@Injectable({ providedIn: 'root' })
export class Tracking {
  private api = inject(Api);
  private connection?: HubConnection;
  private order?: string;
  private listener?: () => void;
  async watch(order: string, listener: () => void) {
    if (!this.connection) {
      this.connection = new HubConnectionBuilder()
        .withUrl('/hubs/orders', { headers: { 'X-CSRF-TOKEN': await this.api.browserToken() } })
        .withAutomaticReconnect([5000, 10000, 30000])
        .build();
      this.connection.onreconnecting(() => {
        void this.api.call('/identity/me').catch(() => {});
      });
      this.connection.on('orderChanged', (message: { orderId: string }) => {
        if (message.orderId === this.order) this.listener?.();
      });
      this.connection.onreconnected(async () => {
        if (this.order) await this.connection!.invoke('Subscribe', this.order);
        this.listener?.();
      });
      await this.connection.start();
    }
    if (this.order && this.order !== order) await this.connection.invoke('Unsubscribe', this.order);
    this.order = order;
    this.listener = listener;
    await this.connection.invoke('Subscribe', order);
  }
  async stop() {
    this.order = undefined;
    this.listener = undefined;
    await this.connection?.stop();
    this.connection = undefined;
  }
}
