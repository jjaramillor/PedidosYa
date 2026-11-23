# Trazabilidad de las 44 funcionalidades MVP

La tabla relaciona la implementación inicial con la sección 7. **La aceptación integral por funcionalidad sigue pendiente**. Docker ya está operativo; existen pruebas de integración y un recorrido HTTP aprobado, además de aceptación parcial de cliente y comercio web. Consultar [estado documentado](README.md) y [revisión de alcance](revision-alcance.md). Una ruta o pantalla implementada no equivale a aceptación funcional.

Los contratos de API listados usan el prefijo `/api/v1`, salvo el hub de SignalR y los eventos. La matriz original permanece en `matriz-original.json`.

| ID | Funcionalidad original | Módulo | Contrato principal | Cliente / detalle |
|---|---|---|---|---|
| ID-001 | Registro y OTP | Identity | `identity/otp/request; identity/otp/verify; identity/browser/verify` | Web y móvil |
| ID-002 | Inicio y recuperación de sesión | Identity | `identity/login; identity/recovery; identity/sessions/refresh; identity/browser/refresh` | Web y móvil |
| ID-003 | Roles y permisos | Identity | `identity/admin/roles; identity/admin/users/{id}/roles; identity/access` | Web administración |
| ID-005 | Gestión de consentimiento y cuenta | Identity | `identity/consents; identity/account; identity/browser/close-account` | Web cuenta |
| CLI-001 | Direcciones y geolocalización | Customers | `addresses; maps/settings` | Web con mapa; móvil con ubicación |
| CLI-002 | Validación de cobertura | Configuration | `coverage; quotes` | Web y móvil |
| CLI-003 | Búsqueda y filtros | Merchants | `catalog/search` | Web y móvil; filtros extendidos en web |
| CLI-005 | Catálogo y detalle de producto | Merchants | `catalog/branches/{id}` | Web y móvil |
| CLI-006 | Carrito y totales | Pricing | `cart; quotes` | Web y móvil |
| CLI-007 | Checkout y confirmación | Orders, Payments | `orders; orders/{id}/payment` | Web y móvil; tarjeta real en checkout web |
| CLI-008 | Seguimiento en tiempo real | Orders, Deliveries | `orders/{id}; deliveries/{id}; /hubs/orders` | Web SignalR y mapa; móvil actualiza cada 10 s |
| CLI-010 | Calificaciones | Customers | `orders/{id}/rating` | Web y móvil |
| COM-001 | Afiliación y validación | Merchants | `documents; merchants; merchants/{id}/decision` | Web comercio y administración |
| COM-002 | Cadenas y sucursales | Merchants | `merchants/{id}/branches; merchant/branches; merchant/branches/{id}/members` | Web comercio; equipo por correo y roles de sucursal |
| COM-003 | Horarios y pausas | Merchants | `merchants/{merchant}/branches/{branch}` | Web horarios diarios y pausas; API por día/intervalo |
| COM-004 | Menús, categorías y productos | Merchants | `merchant/branches/{id}/products` | Web comercio |
| COM-005 | Variantes y adicionales | Merchants, Pricing | `merchant/branches/{id}/products; cart; quotes` | Web variantes y extras; selección móvil |
| COM-006 | Precio, stock y disponibilidad | Merchants | `merchant/branches/{branch}/products/{id}` | Web comercio |
| COM-007 | Gestión operativa del pedido | Orders, Deliveries | `orders?branchId={id}; orders/{id}/actions; deliveries/{id}` | Web comercio |
| COM-010 | Reportes y liquidaciones | Ledger | `statements/merchant/{id}; admin/settlements/merchant/{id}` | Web comercio y finanzas |
| REP-001 | Alta y documentos | Dispatch | `documents; couriers/apply; admin/couriers/{id}/decision` | Móvil y web administración |
| REP-002 | Disponibilidad y zona | Dispatch, Configuration | `couriers/availability; coverage` | Móvil; posición reciente para ofertas |
| REP-003 | Oferta y aceptación | Dispatch | `couriers/offers; couriers/offers/{id}/decision` | Móvil |
| REP-004 | Asignación automática básica | Dispatch | `Eventos orders.state-changed; trabajador MvpMaintenance` | Asignación automática al candidato elegible cercano |
| REP-005 | Navegación y llegada | Deliveries | `deliveries/{id}/actions: arrived` | Móvil y navegación externa |
| REP-006 | PIN de retiro | Deliveries | `deliveries/{id}/actions: picked_up` | Móvil; comercio obtiene PIN por API/web |
| REP-007 | GPS y seguimiento | Deliveries | `deliveries/{id}/location` | Móvil, solo entrega activa; web seguimiento |
| REP-008 | Prueba de entrega | Deliveries | `documents; deliveries/{id}/actions: delivered` | Móvil PIN o fotografía y confirmación de efectivo |
| REP-010 | Ganancias e incentivos | Ledger | `statements/courier/{id}; admin/orders/{id}/incentives` | Móvil ganancias; web incentivo manual |
| PAG-001 | Pago con tarjeta | Payments | `orders; orders/{id}/payment; payments/webhooks/mercadopago` | Web CardForm; adapter Mercado Pago; sandbox Development |
| PAG-002 | Pago en efectivo configurable | Configuration, Payments, Ledger | `admin/parameters/cash.enabled; admin/zones; admin/couriers/{id}/cash-collections` | Web/móvil; confirmación del repartidor y rendición manual |
| PAG-003 | Tokenización segura | Payments | `payments/settings; orders` | Campos alojados por proveedor; sin PAN/CVV en backend |
| PAG-004 | Reembolso parcial o total | Payments, Ledger | `admin/orders/{id}/refunds` | Web finanzas/soporte con permiso payments.refund |
| PAG-005 | Conciliación de pasarela | Payments | `admin/payments/reconcile` | Web finanzas; sandbox identificado como simulado |
| PAG-006 | Comisiones y liquidaciones | Pricing, Ledger | `statements/{kind}/{id}; admin/settlements/{kind}/{id}; admin/settlements/{id}/paid` | Web; cierre y registro de pago manual |
| PRO-001 | Cupones básicos | Pricing | `admin/coupons; quotes` | Web administración y cliente; cupón móvil |
| ADM-001 | Gestión de participantes | Identity, Merchants, Dispatch | `identity/admin/users; merchants/{id}/decision; admin/couriers/{id}/decision` | Web administración |
| ADM-002 | Zonas y polígonos de cobertura | Configuration | `admin/zones` | Web vértices del polígono; validación PostGIS |
| ADM-003 | Tarifas y comisiones | Configuration, Pricing | `admin/zones; quotes` | Web administración |
| ADM-004 | Monitoreo global de pedidos | Orders | `orders; orders/{id}; orders/{id}/timeline` | Web administración |
| ADM-005 | Intervención y reasignación | Orders, Dispatch, Deliveries | `orders/{id}/actions; admin/orders/{id}/courier` | Web administración; retiro bloquea cancelación/reasignación |
| ADM-006 | Casos, SLA y evidencias | Support | `support/cases; support/cases/{id}; support/cases/{id}/notes` | Web/móvil apertura; web administración atención y evidencia |
| ADM-007 | Parámetros y feature flags | Configuration | `admin/parameters` | Web flags; parámetros adicionales por API |
| ADM-008 | Auditoría | Configuration, Identity | `admin/audit; identity/admin/audit` | Web y API; razones, actor, correlación |
