import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { Api } from './api';

@Component({
  selector: 'merchant-workspace',
  imports: [FormsModule, CommonModule],
  templateUrl: './merchant.html',
  styleUrl: './app.css',
})
export class MerchantWorkspace implements OnInit {
  workspace = signal('orders');
  orderFilter = signal('active');
  readonly orderStages = [
    { id: 'active', label: 'En curso' },
    { id: 'confirmed', label: 'Nuevos' },
    { id: 'preparing', label: 'En cocina' },
    { id: 'ready', label: 'Listos' },
    { id: 'history', label: 'Historial' },
  ];
  ordersFor(stage: string) {
    const active = ['confirmed', 'accepted', 'preparing', 'ready'];
    return this.orders().filter((item) =>
      stage === 'active'
        ? active.includes(item.state)
        : stage === 'history'
          ? !active.includes(item.state)
          : stage === 'preparing'
            ? ['accepted', 'preparing'].includes(item.state)
            : item.state === stage,
    );
  }
  stateLabel(state: string) {
    const labels: Record<string, string> = {
      confirmed: 'Nuevo',
      accepted: 'Aceptado',
      preparing: 'En cocina',
      ready: 'Listo para retiro',
      in_transit: 'En camino',
      delivered: 'Entregado',
      closed: 'Completado',
      cancelled: 'Cancelado',
      pending_payment: 'Pago pendiente',
    };
    return labels[state] || state;
  }
  editingBranchId = '';
  editBranch() {
    const item = this.branches().find((branch) => branch.id === this.branchId);
    if (!item) return;
    const time = (minute: number) =>
      String(Math.floor(minute / 60)).padStart(2, '0') + ':' + String(minute % 60).padStart(2, '0');
    this.editingBranchId = item.id;
    this.merchantId = item.merchantId;
    this.branch = {
      name: item.name,
      latitude: item.location.latitude,
      longitude: item.location.longitude,
      minimumOrder: item.minimumOrder / 100,
      preparationMinutes: item.preparationMinutes,
      active: item.active,
      from: time(item.hours[0]?.fromMinute || 540),
      to: time(item.hours[0]?.toMinute || 1320),
    };
  }
  private api = inject(Api);
  busy = signal(false);
  error = signal('');
  notice = signal('');
  businesses = signal<any[]>([]);
  branches = signal<any[]>([]);
  products = signal<any[]>([]);
  orders = signal<any[]>([]);
  merchantId = '';
  branchId = '';
  productId = '';
  selectedOrder = signal<any>(null);
  delivery = signal<any>(null);
  statement = signal<any>(null);
  application = { name: '', taxId: '', bankAccount: '', documentIds: [] as string[] };
  branch = {
    name: '',
    latitude: -12.0464,
    longitude: -77.0428,
    minimumOrder: 0,
    preparationMinutes: 20,
    active: true,
    from: '09:00',
    to: '22:00',
  };
  product = {
    name: '',
    category: '',
    description: '',
    imageUrl: '',
    allergens: '',
    price: 0,
    stock: 10,
    available: true,
    modifiers: [] as any[],
  };
  option = { name: '', price: 0, group: 'extras', minimumSelections: 0, maximumSelections: 5 };
  memberEmail = '';
  memberId = '';
  memberRole = 'operator';
  team = signal<any[]>([]);
  async loadTeam() {
    this.team.set(await this.api.call('/merchant/branches/' + this.branchId + '/members'));
  }
  async saveMember() {
    await this.api.call('/merchant/branches/' + this.branchId + '/members', 'PUT', {
      userId: '00000000-0000-0000-0000-000000000000',
      email: this.memberEmail,
      role: this.memberRole,
    });
    await this.loadTeam();
    this.notice.set('Equipo actualizado.');
  }
  async removeMember(userId: string) {
    await this.api.call('/merchant/branches/' + this.branchId + '/members', 'PUT', {
      userId,
      role: 'remove',
    });
    await this.loadTeam();
  }
  reason = '';
  async ngOnInit() {
    await this.run(this.reload.bind(this));
  }
  async run(work: () => Promise<void>) {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set('');
    this.notice.set('');
    try {
      await work();
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'Operación fallida.');
    } finally {
      this.busy.set(false);
    }
  }
  money(value: number) {
    return new Intl.NumberFormat('es-PE', { style: 'currency', currency: 'PEN' }).format(
      value / 100,
    );
  }
  async reload() {
    this.businesses.set(await this.api.call('/merchants'));
    this.branches.set(
      (await this.api.call('/merchant/branches')).map((item: any) => ({
        ...item.branch,
        hours: item.hours,
        pausedUntil: item.pausedUntil,
        active: item.active,
      })),
    );
    this.merchantId ||= this.businesses()[0]?.id || '';
    this.branchId ||= this.branches()[0]?.id || '';
    if (this.branchId) await this.selectBranch();
  }
  async upload(event: Event) {
    const files = (event.target as HTMLInputElement).files;
    if (!files) return;
    for (const file of Array.from(files)) {
      const form = new FormData();
      form.append('file', file);
      const stored = await this.api.call('/documents', 'POST', form);
      this.application.documentIds.push(stored.id);
    }
    this.notice.set('Documentos adjuntados.');
  }
  async apply() {
    await this.api.call('/merchants', 'POST', this.application);
    this.notice.set('Solicitud enviada para revisión.');
    await this.reload();
  }
  async saveBranch() {
    const minute = (value: string) => {
      const [h, m] = value.split(':').map(Number);
      return h * 60 + m;
    };
    await this.api.call(
      '/merchants/' +
        this.merchantId +
        '/branches' +
        (this.editingBranchId ? '/' + this.editingBranchId : ''),
      this.editingBranchId ? 'PUT' : 'POST',
      {
        name: this.branch.name,
        location: { latitude: this.branch.latitude, longitude: this.branch.longitude },
        minimumOrder: Math.round(this.branch.minimumOrder * 100),
        preparationMinutes: this.branch.preparationMinutes,
        active: this.branch.active,
        pausedUntil: null,
        hours: Array.from({ length: 7 }, (_, day) => ({
          day,
          fromMinute: minute(this.branch.from),
          toMinute: minute(this.branch.to),
        })),
      },
    );
    await this.reload();
    this.notice.set('Sucursal guardada.');
    this.editingBranchId = '';
  }
  async selectBranch() {
    this.products.set(await this.api.call('/merchant/branches/' + this.branchId + '/products'));
    this.orders.set(await this.api.call('/orders?branchId=' + this.branchId));
    this.selectedOrder.set(null);
  }
  async pause(minutes: number) {
    const item = this.branches().find((b) => b.id === this.branchId);
    await this.api.call('/merchants/' + item.merchantId + '/branches/' + item.id, 'PUT', {
      name: item.name,
      location: item.location,
      minimumOrder: item.minimumOrder,
      preparationMinutes: item.preparationMinutes,
      active: item.active,
      hours: item.hours,
      pausedUntil: minutes ? new Date(Date.now() + minutes * 60000).toISOString() : null,
    });
    await this.reload();
    this.notice.set(minutes ? 'Pedidos pausados durante una hora.' : 'Pedidos habilitados.');
  }
  editProduct(item: any) {
    this.productId = item.id;
    this.product = {
      ...item,
      price: item.price / 100,
      imageUrl: item.imageUrl || '',
      allergens: item.allergens.join(', '),
      modifiers: item.modifiers.map((m: any) => ({ ...m, price: m.price / 100 })),
    };
  }
  newProduct() {
    this.productId = '';
    this.product = {
      name: '',
      category: '',
      description: '',
      imageUrl: '',
      allergens: '',
      price: 0,
      stock: 10,
      available: true,
      modifiers: [],
    };
  }
  addOption() {
    if (!this.option.name.trim()) return;
    this.product.modifiers.push({ id: crypto.randomUUID(), ...this.option });
    this.option = { ...this.option, name: '', price: 0 };
  }
  async saveProduct() {
    const body = {
      ...this.product,
      price: Math.round(this.product.price * 100),
      imageUrl: this.product.imageUrl || null,
      allergens: this.product.allergens
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean),
      modifiers: this.product.modifiers.map((m) => ({ ...m, price: Math.round(m.price * 100) })),
    };
    await this.api.call(
      '/merchant/branches/' +
        this.branchId +
        '/products' +
        (this.productId ? '/' + this.productId : ''),
      this.productId ? 'PUT' : 'POST',
      body,
    );
    await this.selectBranch();
    this.newProduct();
    this.notice.set('Producto guardado.');
  }
  async openOrder(id: string) {
    this.selectedOrder.set(await this.api.call('/orders/' + id));
    try {
      this.delivery.set(await this.api.call('/deliveries/' + id));
    } catch {
      this.delivery.set(null);
    }
  }
  async action(state: string) {
    if (!this.reason.trim()) throw new Error('Indica el motivo de la acción.');
    await this.api.call('/orders/' + this.selectedOrder().id + '/actions', 'POST', {
      operationId: crypto.randomUUID(),
      state,
      reason: this.reason,
    });
    await this.openOrder(this.selectedOrder().id);
    this.orders.set(await this.api.call('/orders?branchId=' + this.branchId));
  }
  async report() {
    this.statement.set(await this.api.call('/statements/merchant/' + this.merchantId));
  }
}
