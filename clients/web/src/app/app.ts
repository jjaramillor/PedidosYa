import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from './api';
import { MerchantWorkspace } from './merchant';
import { AdminWorkspace } from './admin';
import { Tracking } from './tracking';
import { CardPayment } from './card-payment';
import { LocationMap } from './location-map';
@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule, MerchantWorkspace, AdminWorkspace, CardPayment, LocationMap],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private api = inject(Api);
  private tracking = inject(Tracking);
  private refreshingOrder = false;
  cardToken = '';
  async onCardToken(token: string) {
    if (!this.checkoutOperation || !this.cardToken) this.cardToken = token;
    await this.run(this.checkout.bind(this));
  }
  marketing = false;
  closeConfirmation = '';
  accountName = '';
  notifications = signal<any[]>([]);
  cases = signal<any[]>([]);
  selectedCase = signal<any>(null);
  caseMessage = '';
  async readCase(id: string) {
    this.selectedCase.set(await this.api.call('/support/cases/' + id));
  }
  async noteCase() {
    await this.api.call('/support/cases/' + this.selectedCase().case.id + '/notes', 'POST', {
      message: this.caseMessage,
    });
    this.caseMessage = '';
    await this.readCase(this.selectedCase().case.id);
  }
  caseEvidence: string[] = [];
  async uploadCaseEvidence(event: Event) {
    const files = (event.target as HTMLInputElement).files;
    if (!files) return;
    for (const file of Array.from(files)) {
      const data = new FormData();
      data.append('file', file);
      const document = await this.api.call('/documents', 'POST', data);
      this.caseEvidence.push(document.id);
    }
    this.notice.set('Evidencia adjuntada a la solicitud.');
  }
  private cartLoaded = false;
  maximumPrice: number | null = null;
  minimumRating = 0;
  maximumMinutes: number | null = null;
  async restoreCart() {
    if (this.cartLoaded) return;
    const cart = await this.api.call('/cart');
    this.cartLoaded = true;
    if (!cart?.lines?.length) return;
    const catalog = await this.api.call('/catalog/branches/' + cart.branchId);
    this.basket.set(
      cart.lines.map((line: any) => {
        const product = catalog.products.find((p: any) => p.id === line.productId);
        return {
          ...line,
          branchId: cart.branchId,
          name: product?.name || 'Producto no disponible',
          unitPrice:
            (product?.price || 0) +
            (product?.modifiers || [])
              .filter((m: any) => line.modifierIds.includes(m.id))
              .reduce((sum: number, m: any) => sum + m.price, 0),
        };
      }),
    );
  }
  async deleteAddress(id: string) {
    await this.api.call('/addresses/' + id, 'DELETE');
    this.addresses.set(await this.api.call('/addresses'));
    this.addressId = this.addresses()[0]?.id || '';
    this.selectAddress();
  }
  async savePreferences() {
    await this.api.call('/identity/consents', 'PUT', {
      kind: 'marketing',
      version: this.policies.marketingVersion,
      accepted: this.marketing,
    });
    if (this.accountName.trim()) {
      await this.api.call('/identity/account', 'PATCH', { displayName: this.accountName.trim() });
      this.user.set(await this.api.call('/identity/me'));
    }
    this.notice.set('Preferencias guardadas.');
  }
  async closeAccount() {
    if (this.closeConfirmation !== 'CERRAR')
      throw new Error('Escribe CERRAR para confirmar la eliminación de tu cuenta.');
    await this.api.call('/identity/me');
    await this.api.resetCsrf();
    await this.api.call('/identity/browser/close-account', 'POST', {});
    await this.tracking.stop().catch(() => {});
    this.clearAccount();
    await this.api.resetCsrf();
    this.notice.set('Tu cuenta ha sido cerrada.');
  }
  async readNotification(id: string) {
    await this.api.call('/notifications/' + id + '/read', 'POST', {});
    this.notifications.set(await this.api.call('/notifications'));
    this.cases.set(
      (await this.api.call('/support/cases')).filter(
        (item: any) => item.openedBy === this.user()?.id,
      ),
    );
  }
  user = signal<any>(null);
  busy = signal(false);
  error = signal('');
  notice = signal('');
  tab = signal('explore');
  email = '';
  name = '';
  register = false;
  consent = false;
  code = '';
  challenge = '';
  policies: any = {};
  permissions = signal<string[]>([]);
  isAdminPermission = (permission: string) =>
    [
      'configuration.manage',
      'participants.manage',
      'orders.operate',
      'support.manage',
      'finance.manage',
      'promotions.manage',
      'identity.users.manage',
      'platform.audit.read',
      'identity.roles.manage',
      'payments.refund',
    ].includes(permission);
  addresses = signal<any[]>([]);
  addressId = '';
  latitude = -12.0464;
  longitude = -77.0428;
  address = { label: 'Casa', street: '', reference: '' };
  search = '';
  category = '';
  radius = 10;
  branches = signal<any[]>([]);
  searched = signal(false);
  catalog = signal<any>(null);
  basket = signal<any[]>([]);
  modifiers: Record<string, boolean> = {};
  checkoutOperation = '';
  coupon = '';
  tip = 0;
  quote = signal<any>(null);
  method = 'cash';
  paymentSettings: any = {};
  orders = signal<any[]>([]);
  selectedOrder = signal<any>(null);
  delivery = signal<any>(null);
  stars = 5;
  courierStars = 5;
  review = '';
  supportSubject = '';
  supportText = '';
  async ngOnInit() {
    await this.run(async () => {
      this.policies = await this.api.call('/identity/policies');
      const sessionStatus = await this.api.call('/identity/browser/status');
      if (sessionStatus.authenticated || sessionStatus.canRefresh)
        try {
          this.user.set(await this.api.call('/identity/me'));
          await this.loadAccount();
        } catch {}
      await this.searchCatalog();
    });
  }
  async run(work: () => Promise<void>) {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      await work();
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'Ocurrió un error.');
    } finally {
      this.busy.set(false);
    }
  }
  money(amount: number) {
    return new Intl.NumberFormat('es-PE', { style: 'currency', currency: 'PEN' }).format(
      amount / 100,
    );
  }
  state(value: string) {
    return (
      (
        {
          checkout_pending: 'Validando pedido',
          payment_pending: 'Esperando pago',
          confirmed: 'Esperando al restaurante',
          accepted: 'Aceptado',
          preparing: 'En preparación',
          ready: 'Listo para recoger',
          picked_up: 'Recogido',
          in_transit: 'En camino',
          delivered: 'Entregado',
          closed: 'Completado',
          cancelled: 'Cancelado',
          incident: 'Incidencia',
          returned: 'Devuelto',
        } as Record<string, string>
      )[value] || value
    );
  }
  async requestCode() {
    const body = this.register
      ? {
          email: this.email,
          displayName: this.name,
          acceptTerms: this.consent,
          acceptPrivacy: this.consent,
          purpose: 'register',
          deviceName: 'Navegador web',
          termsVersion: this.policies.termsVersion,
          privacyVersion: this.policies.privacyVersion,
        }
      : { email: this.email, deviceName: 'Navegador web' };
    const result = await this.api.call(
      this.register ? '/identity/otp/request' : '/identity/login',
      'POST',
      body,
    );
    this.challenge = result.challengeId;
    this.notice.set('Revisa tu correo e introduce el código recibido.');
  }
  async verify() {
    const session = await this.api.call('/identity/browser/verify', 'POST', {
      challengeId: this.challenge,
      code: this.code,
    });
    this.user.set(session.user);
    this.challenge = '';
    this.code = '';
    await this.api.resetCsrf();
    await this.loadAccount();
  }
  async loadAccount() {
    this.permissions.set((await this.api.call('/identity/access')).permissions);
    this.addresses.set(await this.api.call('/addresses'));
    this.addressId = this.addresses()[0]?.id || '';
    this.selectAddress();
    this.paymentSettings = await this.api.call('/payments/settings');
    this.orders.set(await this.api.call('/orders'));
    this.notifications.set(await this.api.call('/notifications'));
    this.cases.set(
      (await this.api.call('/support/cases')).filter(
        (item: any) => item.openedBy === this.user()?.id,
      ),
    );
    const consents = await this.api.call('/identity/consents');
    this.marketing = consents.find((item: any) => item.kind === 'marketing')?.accepted || false;
    this.accountName = this.user()?.displayName || '';
    await this.restoreCart();
  }
  async logout() {
    await this.api.resetCsrf();
    await this.api.call('/identity/browser/logout', 'POST', {});
    await this.tracking.stop().catch(() => {});
    this.clearAccount();
    await this.api.resetCsrf();
  }
  private clearAccount() {
    this.cases.set([]);
    this.selectedCase.set(null);
    this.caseMessage = '';
    this.caseEvidence = [];
    this.latitude = -12.0464;
    this.longitude = -77.0428;
    this.address = { label: 'Casa', street: '', reference: '' };
    this.catalog.set(null);
    this.branches.set([]);
    this.searched.set(false);
    this.modifiers = {};
    this.user.set(null);
    this.permissions.set([]);
    this.addresses.set([]);
    this.addressId = '';
    this.orders.set([]);
    this.selectedOrder.set(null);
    this.delivery.set(null);
    this.notifications.set([]);
    this.basket.set([]);
    this.quote.set(null);
    this.cartLoaded = false;
    this.cardToken = '';
    this.checkoutOperation = '';
    this.marketing = false;
    this.accountName = '';
    this.closeConfirmation = '';
    this.email = '';
    this.name = '';
    this.code = '';
    this.challenge = '';
  }
  selectAddress() {
    const item = this.addresses().find((a) => a.id === this.addressId);
    if (item) {
      this.latitude = item.location.latitude;
      this.longitude = item.location.longitude;
    }
    this.quote.set(null);
  }
  async locate() {
    const location = await new Promise<GeolocationPosition>((resolve, reject) =>
      navigator.geolocation.getCurrentPosition(resolve, reject, {
        enableHighAccuracy: true,
        timeout: 15000,
      }),
    );
    this.latitude = location.coords.latitude;
    this.longitude = location.coords.longitude;
  }
  async saveAddress() {
    await this.api.call('/coverage', 'POST', {
      latitude: this.latitude,
      longitude: this.longitude,
    });
    const created = await this.api.call('/addresses', 'POST', {
      ...this.address,
      location: { latitude: this.latitude, longitude: this.longitude },
    });
    this.addresses.set(await this.api.call('/addresses'));
    this.addressId = created.id;
    this.notice.set('Dirección guardada dentro de la cobertura.');
  }
  async searchCatalog() {
    const query = new URLSearchParams({
      latitude: String(this.latitude),
      longitude: String(this.longitude),
      text: this.search,
      category: this.category,
      radius: String(this.radius),
      onlyOpen: 'true',
      minimumRating: String(this.minimumRating),
    });
    if (this.maximumPrice !== null)
      query.set('maximumPrice', String(Math.round(this.maximumPrice * 100)));
    if (this.maximumMinutes !== null) query.set('maximumMinutes', String(this.maximumMinutes));
    this.branches.set(await this.api.call('/catalog/search?' + query));
    this.searched.set(true);
  }
  async openBranch(id: string) {
    this.catalog.set(await this.api.call('/catalog/branches/' + id));
    this.modifiers = {};
  }
  async addProduct(product: any) {
    if (!this.user()) {
      this.tab.set('account');
      return;
    }
    const branch = this.catalog().branch;
    if (this.basket().length && this.basket()[0].branchId !== branch.id)
      throw new Error('Vacía el carrito antes de cambiar de restaurante.');
    const modifierIds = product.modifiers
      .filter((m: any) => this.modifiers[m.id])
      .map((m: any) => m.id);
    const existing = this.basket().find(
      (item) =>
        item.productId === product.id &&
        JSON.stringify([...item.modifierIds].sort()) === JSON.stringify([...modifierIds].sort()),
    );
    if (existing) {
      existing.quantity++;
      this.basket.set([...this.basket()]);
      await this.persistCart();
      return;
    }
    this.basket.update((items) => [
      ...items,
      {
        productId: product.id,
        name: product.name,
        quantity: 1,
        modifierIds,
        branchId: branch.id,
        unitPrice:
          product.price +
          product.modifiers
            .filter((m: any) => modifierIds.includes(m.id))
            .reduce((sum: number, m: any) => sum + m.price, 0),
      },
    ]);
    await this.persistCart();
  }
  async removeLine(index: number) {
    const branchId = this.basket()[0]?.branchId;
    this.basket.update((items) => items.filter((_, i) => i !== index));
    this.quote.set(null);
    if (this.basket().length) await this.persistCart();
    else if (branchId) await this.api.call('/cart', 'PUT', { branchId, lines: [] });
  }
  private lines() {
    return this.basket().map(({ productId, quantity, modifierIds }) => ({
      productId,
      quantity,
      modifierIds,
    }));
  }
  private async persistCart() {
    await this.api.call('/cart', 'PUT', {
      branchId: this.basket()[0].branchId,
      lines: this.lines(),
    });
    this.quote.set(null);
  }
  async calculate() {
    this.quote.set(null);
    this.cardToken = '';
    this.checkoutOperation = '';
    if (!this.addressId || !this.basket().length)
      throw new Error('Selecciona una dirección y añade productos.');
    this.quote.set(
      await this.api.call('/quotes', 'POST', {
        addressId: this.addressId,
        branchId: this.basket()[0].branchId,
        lines: this.lines(),
        coupon: this.coupon || null,
        tip: Math.round(this.tip * 100),
      }),
    );
  }
  async checkout() {
    if (!this.quote()) throw new Error('Calcula el total antes de confirmar.');
    if (this.method === 'card' && this.paymentSettings.provider !== 'sandbox' && !this.cardToken)
      throw new Error('Completa los campos seguros de la tarjeta.');
    const order = await this.api.call('/orders', 'POST', {
      operationId: (this.checkoutOperation ||= crypto.randomUUID()),
      quoteId: this.quote().id,
      method: this.method,
      cardToken:
        this.method === 'card'
          ? this.paymentSettings.provider === 'sandbox'
            ? JSON.stringify({ token: 'sandbox-approved', paymentMethodId: 'visa', issuerId: null })
            : this.cardToken
          : null,
    });
    try {
      await this.api.call('/cart', 'PUT', { branchId: order.branchId, lines: [] });
    } catch {
      this.notice.set('El pedido fue recibido, pero no se pudo vaciar el carrito guardado.');
    }
    this.basket.set([]);
    this.quote.set(null);
    this.tab.set('orders');
    await this.openOrder(order.id);
    this.orders.set(await this.api.call('/orders'));
    this.notifications.set(await this.api.call('/notifications'));
    const consents = await this.api.call('/identity/consents');
    this.marketing = consents.find((item: any) => item.kind === 'marketing')?.accepted || false;
    this.accountName = this.user()?.displayName || '';
    await this.restoreCart();
  }
  async openOrder(id: string) {
    if (this.selectedOrder()?.id !== id) {
      this.caseEvidence = [];
      this.supportSubject = '';
      this.supportText = '';
    }
    const order = await this.api.call('/orders/' + id);
    this.selectedOrder.set(order);
    this.syncOrderSummary(order);
    try {
      this.delivery.set(await this.api.call('/deliveries/' + id));
    } catch {
      this.delivery.set(null);
    }
    try {
      await this.tracking.watch(id, () => {
        if (this.refreshingOrder) return;
        this.refreshingOrder = true;
        this.refreshOrder(id).finally(() => (this.refreshingOrder = false));
      });
    } catch {
      this.notice.set('Seguimiento en tiempo real no disponible. Puedes actualizar el pedido.');
    }
  }
  private syncOrderSummary(order: any) {
    this.orders.update((items) =>
      items.map((item) => item.id === order.id ? { ...item, state: order.state } : item),
    );
  }
  private async refreshOrder(id: string) {
    try {
      const order = await this.api.call('/orders/' + id);
      if (this.selectedOrder()?.id === id) {
        this.selectedOrder.set(order);
        this.syncOrderSummary(order);
        try {
          this.delivery.set(await this.api.call('/deliveries/' + id));
        } catch {
          this.delivery.set(null);
        }
      }
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'No se pudo actualizar el pedido.');
    }
  }
  async cancelOrder() {
    await this.api.call('/orders/' + this.selectedOrder().id + '/actions', 'POST', {
      operationId: crypto.randomUUID(),
      state: 'cancelled',
      reason: 'Cancelación solicitada por el cliente',
    });
    await this.openOrder(this.selectedOrder().id);
  }
  async rateOrder() {
    await this.api.call('/orders/' + this.selectedOrder().id + '/rating', 'POST', {
      merchantStars: Number(this.stars),
      courierStars: Number(this.courierStars),
      comment: this.review,
    });
    this.notice.set('Tu valoración quedó registrada.');
  }
  async openCase() {
    await this.api.call('/support/cases', 'POST', {
      orderId: this.selectedOrder().id,
      subject: this.supportSubject,
      description: this.supportText,
      evidenceIds: this.caseEvidence,
    });
    this.cases.set(
      (await this.api.call('/support/cases')).filter(
        (item: any) => item.openedBy === this.user()?.id,
      ),
    );
    this.caseEvidence = [];
    this.notice.set('Tu solicitud de ayuda quedó registrada.');
  }
}
