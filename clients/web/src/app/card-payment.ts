import { AfterViewInit, Component, input, OnDestroy, output, signal } from '@angular/core';

@Component({
  selector: 'card-payment',
  template: ` <p>La tarjeta se ingresa en los campos seguros de Mercado Pago.</p>
    @if (error()) {
      <p role="alert">{{ error() }}</p>
    }
    <form id="card-payment-form">
      <label
        >Número de tarjeta
        <div id="card-number" class="hosted-field"></div
      ></label>
      <label
        >Vencimiento
        <div id="card-expiry" class="hosted-field"></div
      ></label>
      <label
        >Código de seguridad
        <div id="card-security" class="hosted-field"></div
      ></label>
      <label>Nombre del titular<input id="card-holder" autocomplete="cc-name" /></label>
      <label>Emisor<select id="card-issuer"></select></label>
      <label>Cuotas<select id="card-installments"></select></label>
      <label>Tipo de documento<select id="card-document-type"></select></label>
      <label>Documento del titular<input id="card-document-number" /></label>
      <label>Correo<input id="card-email" type="email" [value]="email()" /></label>
      <button type="submit" [disabled]="!ready() || disabled()">
        Confirmar pedido con tarjeta
      </button>
    </form>`,
  styles: `
    label {
      display: block;
      margin: 12px 0;
    }
    input,
    select {
      display: block;
      padding: 12px;
      width: 100%;
      box-sizing: border-box;
    }
    .hosted-field {
      height: 44px;
      border: 1px solid #b7c6bc;
      border-radius: 8px;
      padding: 8px;
      box-sizing: border-box;
    }
    button {
      padding: 14px;
      background: #244c3d;
      color: white;
      border: 0;
      border-radius: 8px;
    }
  `,
})
export class CardPayment implements AfterViewInit, OnDestroy {
  publicKey = input.required<string>();
  amount = input.required<number>();
  email = input.required<string>();
  disabled = input(false);
  token = output<string>();
  error = signal('');
  ready = signal(false);
  private form: any;
  private destroyed = false;
  async ngAfterViewInit() {
    try {
      const { loadMercadoPago } = await import('@mercadopago/sdk-js');
      await loadMercadoPago();
      if (this.destroyed) return;
      const constructor = (window as any).MercadoPago;
      if (!constructor || !this.publicKey()) throw new Error();
      const provider = new constructor(this.publicKey(), { locale: 'es-PE' });
      this.form = provider.cardForm({
        amount: (this.amount() / 100).toFixed(2),
        iframe: true,
        form: {
          id: 'card-payment-form',
          cardNumber: { id: 'card-number' },
          expirationDate: { id: 'card-expiry' },
          securityCode: { id: 'card-security' },
          cardholderName: { id: 'card-holder' },
          issuer: { id: 'card-issuer' },
          installments: { id: 'card-installments' },
          identificationType: { id: 'card-document-type' },
          identificationNumber: { id: 'card-document-number' },
          cardholderEmail: { id: 'card-email' },
        },
        callbacks: {
          onFormMounted: (error: unknown) => {
            if (error) this.error.set('No se pudieron cargar los campos seguros.');
            else this.ready.set(true);
          },
          onError: () => this.error.set('Revisa los datos de la tarjeta e inténtalo nuevamente.'),
          onSubmit: (event: Event) => {
            event.preventDefault();
            if (this.disabled()) return;
            const data = this.form.getCardFormData();
            if (Number(data.installments) !== 1) {
              this.error.set('Este servicio admite un solo pago.');
              return;
            }
            this.token.emit(
              JSON.stringify({
                token: data.token,
                paymentMethodId: data.paymentMethodId,
                issuerId: data.issuerId || null,
                documentType: data.identificationType,
                documentNumber: data.identificationNumber,
              }),
            );
          },
        },
      });
    } catch {
      this.error.set('No se pudo conectar con la pasarela de tarjetas.');
    }
  }
  ngOnDestroy() {
    this.destroyed = true;
    this.form?.unmount();
  }
}
