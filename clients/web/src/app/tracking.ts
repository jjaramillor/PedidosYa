import { Injectable, inject, signal } from '@angular/core';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { Api } from './api';
import { TrackingSession, TrackingState } from './tracking-session';

@Injectable({ providedIn: 'root' })
export class Tracking {
  private api = inject(Api);
  readonly state = signal<TrackingState>('idle');
  private session = new TrackingSession(
    async () => {
      const connection = new HubConnectionBuilder()
        .withUrl('/hubs/orders', { headers: { 'X-CSRF-TOKEN': await this.api.browserToken() } })
        .withAutomaticReconnect([0, 5000, 10000, 30000])
        .build();
      connection.onreconnecting(() => { void this.api.call('/identity/me').catch(() => {}); });
      return connection;
    },
    (state) => this.state.set(state),
  );
  watch(order: string, listener: () => void) {
    return this.session.watch(order, listener);
  }
  stop() {
    return this.session.stop();
  }
}
