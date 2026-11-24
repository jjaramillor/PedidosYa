# Hito: validación local integrada

Resultado: 74 pruebas .NET aprobadas (38 de unidad/arquitectura y 36 de integración), sin fallos ni omitidas. PostgreSQL/PostGIS y Redis reales mediante Testcontainers. Angular compila en producción.

El recorrido HTTP independiente terminó con dos pedidos cerrados, uno con tarjeta sandbox y otro en efectivo, ambos por S/ 34.56. Ejecución `41d27e69`. Se verificaron OTP vía Mailpit, participantes aprobados, catálogo, cotización, aceptación comercial, asignación, retiro y entrega con PIN, ubicación sintética, captura, valoración y liquidación marcada como ficticia. No hubo dinero real ni correo externo.

Pedidos:
- Tarjeta: `eebb41f2-ba48-47b9-92ce-719f05918a00`.
- Efectivo: `df1acc65-7a4f-4783-9414-819787796ad7`.

Cambios verificados:
- Redis de integración permite FLUSHDB solo en el contenedor aislado de pruebas.
- Los fixtures fuerzan pagos sandbox y desactivan credenciales externas y bootstrap heredados.
- Outbox respeta la configuración final del host para ejecución manual; la nueva regresión falló antes de la corrección y pasó después.
- El cliente web acepta respuestas exitosas vacías sin intentar deserializarlas como JSON.
- `run-api.ps1 -Validation` aísla almacenamiento de documentos y claves y fuerza SMTP local y pagos simulados.
- `verify-mvp-http.ps1` crea cuentas y geometrías sintéticas; conserva únicamente un resumen sin credenciales en `.artifacts/validation/ultimo-recorrido-http.json`.

Servicios locales de revisión: API `http://localhost:8080`, web de desarrollo `http://127.0.0.1:4200`, Mailpit `http://localhost:8025`. Infraestructura Compose bajo proyecto `pedidosya-validation`.

Pendientes: aceptación completa de clientes por rol, compilación y prueba de aplicaciones móviles, configuración y ensayo de proveedor real en entorno de pruebas, despliegue HTTPS y validación operativa. Los documentos originales de alcance y arquitectura no se modificaron.

Verificación en navegador: acceso OTP, carga completa de cuenta y notificaciones, cierre y nuevo inicio de sesión, y ambos pedidos completados visibles. La respuesta vacía del carrito ya no genera error JSON. [Captura](web-pedidos-validados.png). Esta comprobación no sustituye la aceptación completa por roles.


## Sesiones independientes y control por rol

Actualización del 23 de noviembre de 2026. La prueba `Independent_role_sessions_enforce_access_and_revocation_during_parallel_reads` usa cinco clientes HTTP independientes contra el host de integración: cliente comprador, dueño del restaurante, repartidor asignado, operador de soporte y cliente ajeno.

Comprobaciones aprobadas:

- Los cuatro participantes autorizados consultan concurrentemente el mismo pedido; el tercero ajeno recibe 403.
- Solo el comercio ve el PIN de retiro, solo el comprador ve el de entrega y el repartidor no recibe ninguno de los dos. El tercero no consulta la entrega.
- El cliente no puede suplantar al restaurante para marcar el pedido listo y el repartidor no puede consultar el pago del comprador.
- El repartidor abre un caso por HTTP. Soporte accede con un rol limitado a `support.manage` y `orders.operate`; un tercero recibe 404 para el caso privado.
- Al retirar ese rol, el mismo JWT deja de permitir el acceso al pedido y al caso en las solicitudes siguientes.
- Cerrar la sesión del comprador invalida su token, mientras el restaurante mantiene acceso con su sesión independiente.

Resultado: 13 pruebas de integración MVP aprobadas, cero fallos y cero omitidas, contra PostGIS y Redis de Testcontainers. Evidencia local: `TestResults/mvp-roles.trx`. Código reproducible en `tests/Delivery.Tests/MvpIntegrationTests.cs`.

Esta prueba cubre autenticación, autorización, aislamiento y lecturas HTTP concurrentes. No cubre todavía varios navegadores operando simultáneamente, propagación SignalR en vivo ni reconexión visual. Su seguimiento está en [TODO del MVP](../../TODO.md), fase F1.


## Tiempo real con WebSockets

Avance del 23 de noviembre de 2026. La prueba `SignalR_transport_isolates_orders_and_removes_logged_out_subscriptions` abre conexiones WebSocket reales contra el host de integración y usa handshake e invocaciones del protocolo JSON de SignalR. No sustituye el hub por un mock ni instala dependencias adicionales.

Comprobaciones: cliente y comercio autorizados reciben `orderChanged` para el pedido suscrito; un usuario ajeno no puede suscribirse ni recibe actualizaciones; publicar otro identificador de pedido no filtra eventos; desuscribir elimina la suscripción del registro y resuscribir restablece la recepción. Cerrar la sesión HTTP del cliente elimina su suscripción en la publicación siguiente y no le entrega esa actualización, aunque el socket siga abierto. Las respuestas de invocaciones posteriores sirven de barrera para detectar mensajes inesperados.

El recorrido simultáneo por interfaces F1-02 se intentó con cliente IAB en localhost, comercio IAB en IPv6 loopback y soporte en Chrome. El acceso a IPv4 del servidor web fue rechazado; IPv6 cargó la aplicación. El control del navegador dejó de responder y continuó fallando tras reiniciarlo. No se confirmó un pedido nuevo ni se completó ese recorrido: F1-02 y la aceptación visual de mapa/reconexión F1-03 permanecen abiertos. La preparación de sesiones no acredita el flujo de negocio.

Esta prueba cubre transporte y autorización de eventos publicados de forma controlada; la recuperación automática del cliente Angular y los cambios de estado vistos simultáneamente en pantalla requieren su propia aceptación.

Resultado: 14 pruebas de integración MVP aprobadas, cero fallos y cero omitidas contra PostGIS/Redis. Evidencia local: `TestResults/mvp-tiempo-real.trx`. La suite completa de la solución y las pruebas Flutter no se repitieron en este avance, que modifica únicamente pruebas y seguimiento documental.
