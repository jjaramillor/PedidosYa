# Aceptación web: cliente y comercio

Fecha: 4 de noviembre de 2026 (America/Lima).

Recorrido realizado desde el navegador contra la API local, PostgreSQL/PostGIS, Redis y Mailpit. Se usaron cuentas ficticias ya existentes y tarjeta sandbox sin cobro real.

Pedido: `e24d9414-268a-4c9b-b836-a1f45e06507d`.
Restaurante: Fogón de Lince - DEMO · Lince.
Productos: arroz con pollo S/ 24, chicha morada S/ 8 y ají de gallina S/ 26.
Total: S/ 64 (productos S/ 58, entrega S/ 5 y servicio S/ 1).

Comprobaciones aprobadas:
- Recuperación de sesión de cliente y carga del carrito persistente.
- Guardado de dirección ficticia dentro de cobertura en Lince.
- Búsqueda de restaurante, apertura de carta y agregado de producto.
- Cotización y confirmación con tarjeta de prueba; carrito vaciado.
- Confirmación asíncrona del pago sandbox.
- Cierre de sesión de cliente e ingreso del comerciante mediante OTP recibido en Mailpit.
- Selección de sucursal, recepción del pedido y visualización de líneas y stock descontado.
- Acciones comerciales: confirmado → aceptado → preparando → listo para recoger.
- Nuevo ingreso del cliente mediante OTP; notificaciones de confirmación, aceptación, preparación y pedido listo.
- Listado y detalle del cliente muestran «Listo para recoger»; estado `ready` confirmado mediante consulta de solo lectura a la base.

Corrección aplicada: al abrir o recibir una actualización de seguimiento, el resumen del pedido en el listado se sincroniza con el estado del detalle. Antes podían mostrar estados diferentes. Compilación Angular de producción aprobada; permanecen avisos CommonJS de Leaflet y Mercado Pago. No se repitió la suite .NET porque el cambio afecta únicamente al estado visual del cliente Angular.

Evidencias:
- [Comercio con pedido listo](web-comercio-listo-2026-11-04.png).
- [Cliente con listado y seguimiento](web-cliente-listo-2026-11-04.png).

Alcance pendiente: retiro y entrega por repartidor desde móvil, valoración y cierre financiero de este pedido; prueba de dos sesiones simultáneas en navegadores aislados y actualización en vivo entre roles. Este pedido queda listo para continuar esa fase, sin repartidor activo asignado. El recorrido no constituye aceptación de todas las HU ni validación de producción.

Servicios de revisión: web http://127.0.0.1:4200, API http://localhost:8080 y Mailpit http://localhost:8025. Se reiniciaron los servicios locales que estaban apagados; se conservaron los datos de validación.


## Continuación del 23 de noviembre de 2026

El pedido de este recorrido se retomó desde el emulador Android: llegada, retiro con PIN, ruta, GPS sintético y entrega con PIN. El backend cerró el pedido y capturó S/64 en sandbox. El cliente ingresó nuevamente por OTP, verificó «Completado» en listado y detalle y guardó la valoración. El repartidor consultó su ganancia de S/4. Ver [evidencia Android y cierre financiero](android-local.md).

Esta continuación resuelve retiro, entrega, valoración y registro financiero de este pedido. La liquidación bancaria, la visualización simultánea en vivo y la aceptación de excepciones permanecen pendientes.
