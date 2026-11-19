import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class Api {
  private csrf = '';
  private refreshing?: Promise<void>;
  async browserToken() {
    if (!this.csrf) await this.resetCsrf();
    return this.csrf;
  }
  async resetCsrf() {
    const response = await fetch('/api/v1/identity/browser/csrf', { credentials: 'same-origin' });
    if (!response.ok) throw new Error('No se pudo iniciar la sesión segura.');
    this.csrf = (await response.json()).token;
  }
  async call<T = any>(path: string, method = 'GET', body?: unknown, retry = true): Promise<T> {
    const unsafe = !['GET', 'HEAD'].includes(method);
    if (unsafe && !this.csrf) await this.resetCsrf();
    const headers: Record<string, string> = {};
    if (unsafe) headers['X-CSRF-TOKEN'] = this.csrf;
    if (body !== undefined && !(body instanceof FormData))
      headers['Content-Type'] = 'application/json';
    const response = await fetch('/api/v1' + path, {
      method,
      credentials: 'same-origin',
      headers,
      body: body === undefined ? undefined : body instanceof FormData ? body : JSON.stringify(body),
    });
    if (response.status === 401 && retry && !path.includes('/identity/browser/')) {
      const renew = async () => {
        try {
          await this.call('/identity/me', 'GET', undefined, false);
          return;
        } catch {}
        await this.resetCsrf();
        await this.call('/identity/browser/refresh', 'POST', {}, false);
        await this.resetCsrf();
      };
      this.refreshing ??= (async () => {
        if (navigator.locks) await navigator.locks.request('delivery.session.refresh', renew);
        else await renew();
      })().finally(() => {
        this.refreshing = undefined;
      });
      await this.refreshing;
      return this.call(path, method, body, false);
    }
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(
        response.status >= 500
          ? 'El servicio no está disponible. Inténtalo de nuevo.'
          : problem.detail || problem.title || 'No se pudo completar la operación.',
      );
    }
    const content = await response.text();
    return content ? JSON.parse(content) as T : undefined as T;
  }
}

