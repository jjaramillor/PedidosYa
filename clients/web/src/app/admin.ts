import { Component, inject, input, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from './api';
import { RefundIntent } from './refund-intent';
import { visibleAdminSections, canDeleteCustomRole, AdminRole } from './admin-sections';

@Component({
  selector: 'admin-workspace',
  imports: [CommonModule, FormsModule],
  templateUrl: './admin.html',
  styleUrl: './app.css',
})
export class AdminWorkspace implements OnInit, OnDestroy {
  section = signal('orders');
  sections() {
    return visibleAdminSections(this.permissions());
  }
  sectionDescription() {
    return this.sections().find((section) => section.id === this.section())?.description || '';
  }

  courierName(item: any) {
    const user = this.users().find((user) => user.id === item.id);
    const name = user?.displayName || 'Repartidor';
    return user?.email ? `${name} · ${user.email}` : name;
  }
  private api = inject(Api);
  permissions = input<string[]>([]);
  busy = signal(false);
  error = signal('');
  notice = signal('');
  merchants = signal<any[]>([]);
  couriers = signal<any[]>([]);
  users = signal<any[]>([]);
  orders = signal<any[]>([]);
  zones = signal<any[]>([]);
  parameters = signal<any[]>([]);
  audit = signal<any[]>([]);
  cases = signal<any[]>([]);
  reason = '';
  courierId = '';
  selectedOrder = signal<any>(null);
  selectedCase = signal<any>(null);
  refundAmount = 0;
  payment = signal<any>(null);
  private refundIntent = new RefundIntent(() => crypto.randomUUID());
  documentPreview = signal<{ url: string; name: string } | null>(null);
  closeDocument() {
    const current = this.documentPreview();
    if (current) URL.revokeObjectURL(current.url);
    this.documentPreview.set(null);
  }
  resolution = '';
  caseState = 'investigating';
  zoneId = '';
  zone = {
    name: '',
    active: true,
    deliveryBase: 5,
    perKilometer: 1,
    serviceFee: 1,
    commission: 15,
    courierBase: 4,
    acceptanceSeconds: 120,
    cashEnabled: false,
  };
  points = [
    { latitude: -12.0, longitude: -77.1 },
    { latitude: -12.0, longitude: -77.0 },
    { latitude: -12.1, longitude: -77.0 },
    { latitude: -12.1, longitude: -77.1 },
  ];
  coupon = {
    code: '',
    startsAt: '',
    endsAt: '',
    minimumOrder: 0,
    discountPercent: 10,
    maximumDiscount: 10,
    totalLimit: 100,
    perCustomerLimit: 1,
    merchantFunded: false,
    active: true,
  };
  reconciliation = signal<any[]>([]);
  userSearch = '';
  statement = signal<any>(null);
  participantId = '';
  participantKind = 'merchant';
  can(permission: string) {
    return this.permissions().includes(permission);
  }
  money(value: number) {
    return new Intl.NumberFormat('es-PE', { style: 'currency', currency: 'PEN' }).format(
      value / 100,
    );
  }
  ngOnDestroy() {
    this.closeDocument();
  }
  async ngOnInit() {
    if (!this.sections().some((section) => section.id === this.section()))
      this.section.set(this.sections()[0]?.id || '');
    await this.run(this.reload.bind(this));
  }
  async run(work: () => Promise<void>) {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    try {
      await work();
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'Operación fallida.');
    } finally {
      this.busy.set(false);
    }
  }
  async reload() {
    if (this.can('participants.manage')) {
      this.merchants.set(await this.api.call('/merchants'));
      this.couriers.set(await this.api.call('/admin/couriers'));
    }
    if (this.can('identity.users.manage')) await this.searchUsers();
    if (this.can('orders.operate')) this.orders.set(await this.api.call('/orders'));
    if (this.can('configuration.manage')) {
      this.zones.set(await this.api.call('/admin/zones'));
      this.parameters.set(await this.api.call('/admin/parameters'));
    }
    if (this.can('platform.audit.read')) this.audit.set(await this.api.call('/admin/audit'));
    if (this.can('support.manage')) this.cases.set(await this.api.call('/support/cases'));
  }
  private requiredReason() {
    if (!this.reason.trim()) throw new Error('Indica un motivo antes de modificar la operación.');
    return this.reason.trim();
  }
  async searchUsers() {
    this.users.set(
      await this.api.call('/identity/admin/users?search=' + encodeURIComponent(this.userSearch)),
    );
  }
  async userStatus(id: string, status: string) {
    await this.api.call('/identity/admin/users/' + id + '/status', 'PUT', { status });
    await this.searchUsers();
  }
  async decide(kind: string, id: string, status: string) {
    const path =
      kind === 'merchant'
        ? '/merchants/' + id + '/decision'
        : '/admin/couriers/' + id + '/decision';
    await this.api.call(path, 'PUT', { status, reason: this.requiredReason() });
    await this.reload();
  }
  async document(id: string) {
    const response = await fetch('/api/v1/documents/' + id, { credentials: 'same-origin' });
    if (!response.ok) throw new Error('Documento no disponible.');
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    this.closeDocument();
    if (['image/png', 'image/jpeg'].includes(blob.type)) {
      this.documentPreview.set({ url, name: 'Documento de afiliación' });
      return;
    }
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'documento-afiliacion.pdf';
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    this.notice.set('Documento preparado para descarga.');
  }
  editZone(item: any) {
    this.zoneId = item.id;
    const z = item.configuration;
    this.zone = {
      ...z,
      deliveryBase: z.deliveryBase / 100,
      perKilometer: z.perKilometer / 100,
      serviceFee: z.serviceFee / 100,
      commission: z.commissionBasisPoints / 100,
      courierBase: z.courierBase / 100,
    };
    const polygon = JSON.parse(z.polygonGeoJson);
    this.points = polygon.coordinates[0]
      .slice(0, -1)
      .map(([longitude, latitude]: number[]) => ({ longitude, latitude }));
  }
  async saveZone() {
    const coordinates = this.points.map((p) => [Number(p.longitude), Number(p.latitude)]);
    coordinates.push(coordinates[0]);
    await this.api.call(
      '/admin/zones' + (this.zoneId ? '/' + this.zoneId : ''),
      this.zoneId ? 'PUT' : 'POST',
      {
        ...this.zone,
        deliveryBase: Math.round(this.zone.deliveryBase * 100),
        perKilometer: Math.round(this.zone.perKilometer * 100),
        serviceFee: Math.round(this.zone.serviceFee * 100),
        commissionBasisPoints: Math.round(this.zone.commission * 100),
        courierBase: Math.round(this.zone.courierBase * 100),
        polygonGeoJson: JSON.stringify({ type: 'Polygon', coordinates: [coordinates] }),
        reason: this.requiredReason(),
      },
    );
    await this.reload();
    this.notice.set('Zona y tarifas guardadas.');
  }
  async flag(name: string, value: boolean) {
    await this.api.call('/admin/parameters/' + encodeURIComponent(name), 'PUT', {
      value,
      reason: this.requiredReason(),
    });
    await this.reload();
  }
  async saveCoupon() {
    await this.api.call('/admin/coupons', 'POST', {
      code: this.coupon.code,
      branchId: null,
      startsAt: new Date(this.coupon.startsAt).toISOString(),
      endsAt: new Date(this.coupon.endsAt).toISOString(),
      minimumOrder: Math.round(this.coupon.minimumOrder * 100),
      discountBasisPoints: Math.round(this.coupon.discountPercent * 100),
      maximumDiscount: Math.round(this.coupon.maximumDiscount * 100),
      totalLimit: this.coupon.totalLimit,
      perCustomerLimit: this.coupon.perCustomerLimit,
      merchantFunded: this.coupon.merchantFunded,
      active: this.coupon.active,
      reason: this.requiredReason(),
    });
    this.notice.set('Cupón creado.');
  }
  async openOrder(id: string) {
    this.selectedOrder.set(await this.api.call('/orders/' + id));
    this.payment.set(null);
    if (this.can('finance.manage') || this.can('payments.refund')) await this.loadPayment(id);
  }
  async cancel() {
    await this.api.call('/orders/' + this.selectedOrder().id + '/actions', 'POST', {
      operationId: crypto.randomUUID(),
      state: 'cancelled',
      reason: this.requiredReason(),
    });
    await this.openOrder(this.selectedOrder().id);
    await this.reload();
  }
  async reassign() {
    await this.api.call('/admin/orders/' + this.selectedOrder().id + '/courier', 'PUT', {
      courierId: this.courierId,
      reason: this.requiredReason(),
    });
    this.notice.set('Entrega reasignada.');
  }
  async loadPayment(id: string) {
    this.payment.set(await this.api.call('/orders/' + id + '/payment'));
  }
  async refund() {
    const id = this.selectedOrder().id;
    const amount = Math.round(this.refundAmount * 100);
    const reason = this.requiredReason();
    if (!Number.isSafeInteger(amount) || amount <= 0)
      throw new Error('Indica un importe de devolución mayor que cero.');
    if (this.payment()?.orderId !== id) await this.loadPayment(id);
    if (amount > this.payment().captured - this.payment().refunded)
      throw new Error('La devolución supera el cobro disponible.');
    await this.api.call(
      '/admin/orders/' + id + '/refunds',
      'POST',
      this.refundIntent.payload(id, amount, reason),
    );
    this.refundIntent.complete();
    this.refundAmount = 0;
    this.notice.set('Reembolso registrado.');
    await this.loadPayment(id);
  }
  async reconcile() {
    this.reconciliation.set(await this.api.call('/admin/payments/reconcile', 'POST', {}));
  }
  async openCase(id: string) {
    this.selectedCase.set(await this.api.call('/support/cases/' + id));
  }
  async resolve() {
    await this.api.call('/support/cases/' + this.selectedCase().case.id, 'PUT', {
      state: this.caseState,
      resolution: this.resolution,
      assignedTo: null,
    });
    await this.openCase(this.selectedCase().case.id);
    await this.reload();
  }
  async report() {
    this.statement.set(
      await this.api.call('/statements/' + this.participantKind + '/' + this.participantId),
    );
  }
  async closeSettlement() {
    await this.api.call(
      '/admin/settlements/' + this.participantKind + '/' + this.participantId,
      'POST',
      { operationId: crypto.randomUUID(), reason: this.requiredReason() },
    );
    await this.report();
  }
  async incentive() {
    await this.api.call('/admin/orders/' + this.selectedOrder().id + '/incentives', 'POST', {
      operationId: crypto.randomUUID(),
      courierId: this.courierId,
      amount: Math.round(this.refundAmount * 100),
      reason: this.requiredReason(),
    });
    this.notice.set('Incentivo registrado en la cuenta del repartidor.');
  }
  paymentReference = '';
  async markPaid(id: string) {
    await this.api.call('/admin/settlements/' + id + '/paid', 'PUT', {
      externalReference: this.paymentReference,
      reason: this.requiredReason(),
    });
    await this.report();
  }
  async cashCollection() {
    await this.api.call('/admin/couriers/' + this.participantId + '/cash-collections', 'POST', {
      operationId: crypto.randomUUID(),
      amount: Math.round(this.refundAmount * 100),
      externalReference: this.paymentReference,
      reason: this.requiredReason(),
    });
    await this.report();
  }
  roles = signal<AdminRole[]>([]);
  rolePermissions = signal<string[]>([]);
  roleDeleteConfirmation = '';
  roleId = '';
  roleName = '';
  roleSelection: Record<string, boolean> = {};
  roleUser = '';
  assignedRoles: Record<string, boolean> = {};
  async loadRoles() {
    this.roles.set(await this.api.call('/identity/admin/roles'));
    this.rolePermissions.set(await this.api.call('/identity/admin/permissions'));
    if (!this.users().length) await this.searchUsers();
  }
  editRole(item: any) {
    this.roleId = item.id;
    this.roleName = item.name;
    this.roleSelection = Object.fromEntries(
      item.permissions.map((permission: string) => [permission, true]),
    );
  }
  newRole() {
    this.roleId = '';
    this.roleName = '';
    this.roleSelection = {};
    this.roleDeleteConfirmation = '';
  }
  isSystemRole() {
    return this.roles().some((role) => role.id === this.roleId && role.systemRole);
  }
  canDeleteRole() {
    return canDeleteCustomRole(this.roles(), this.roleId);
  }
  async deleteRole() {
    if (!this.canDeleteRole() || this.roleDeleteConfirmation !== this.roleId)
      throw new Error('Confirma el código de un rol personalizado.');
    await this.api.call('/identity/admin/roles/' + encodeURIComponent(this.roleId), 'DELETE');
    this.newRole();
    await this.loadRoles();
    this.notice.set('Rol eliminado.');
  }
  async saveRole() {
    if (this.isSystemRole()) throw new Error('Los roles del sistema no se editan.');
    await this.api.call('/identity/admin/roles/' + encodeURIComponent(this.roleId), 'PUT', {
      name: this.roleName,
      permissions: this.rolePermissions().filter((p) => this.roleSelection[p]),
    });
    await this.loadRoles();
    this.notice.set('Rol guardado.');
  }
  async loadAssignments() {
    const roles = await this.api.call('/identity/admin/users/' + this.roleUser + '/roles');
    this.assignedRoles = Object.fromEntries(
      roles.map((role: any) => [typeof role === 'string' ? role : role.id, true]),
    );
  }
  async assignRoles() {
    await this.api.call('/identity/admin/users/' + this.roleUser + '/roles', 'PUT', {
      roles: this.roles()
        .filter((role) => this.assignedRoles[role.id])
        .map((role) => role.id),
    });
    this.notice.set('Roles asignados.');
  }
}
