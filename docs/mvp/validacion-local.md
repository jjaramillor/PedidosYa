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


## Recorrido simultáneo por interfaces

Aceptación realizada el 23 de noviembre de 2026. Pedido `622a8dd0-ab3a-41b9-8a96-533e7979561d`, Sucursal QA 41d27e69, tarjeta sandbox, total S/32,56.

Las sesiones permanecieron abiertas simultáneamente: cliente en IAB `localhost:4200`, comercio en IAB `[::1]:4200`, operador en Chrome y repartidor en el emulador Android. El operador usó la cuenta administradora ficticia existente; el control de un rol de soporte limitado se verifica por separado en las pruebas de aislamiento HTTP.

Recorrido observado:

1. Cliente añadió un menú, cotizó y confirmó desde la web. Una dirección fuera de la zona fue rechazada; al seleccionar la dirección QA correspondiente se obtuvo una cotización válida.
2. Comercio ingresó por OTP de Mailpit, aceptó, preparó y marcó listo. El cliente reflejó aceptación y los estados posteriores sin pulsar actualizar seguimiento.
3. Android recuperó su sesión, declaró disponibilidad, recibió y aceptó la oferta del pedido nuevo, y registró llegada.
4. El repartidor abrió «Solicitar ayuda con esta entrega». El operador recibió el caso, lo atendió y guardó una resolución con estado `resolved` desde Chrome.
5. El PIN de retiro se consultó en la interfaz del comercio. Android confirmó retiro e inicio de ruta.
6. El repartidor activó ubicación. El cliente mostró «En camino» y ETA de cinco minutos. Se persistieron tres posiciones sintéticas.
7. El PIN de entrega se obtuvo en la interfaz del cliente; Android confirmó entrega. La web recibió «Completado» sin actualización manual y el backend quedó `closed` / `delivered` / pago `captured`.

La sucursal QA utiliza coordenadas ficticias fuera de Lima; el GPS del emulador se situó en esa zona para el ensayo. No se editaron estados en la base para avanzar el negocio. Las consultas SQL se usaron solo para identificar la ubicación del fixture y comprobar los resultados.

| Resultado financiero | PEN |
|---|---:|
| Captura sandbox | 32,56 |
| Comercio | 21,25 |
| Repartidor | 5,56 |
| Plataforma | 5,75 |

Evidencia: [cliente completado](simultaneo-cliente-completado.jpg), [Android en ruta](simultaneo-android-ruta.jpg), [Android entregado](simultaneo-android-entregado.jpg). La captura del cliente omite el área que mostraba el código de entrega.

Correcciones derivadas de la prueba:

- La API deja de devolver el PIN de retiro tras recoger y ambos códigos cuando la entrega termina. Las regresiones comprueban entrega y devolución, incluso con acceso de operador.
- Un envío GPS pendiente podía recibir el rechazo esperado del backend tras entregar y mostrar un falso error. El temporizador se cancela inmediatamente después de confirmar el cierre y sus respuestas tardías no sustituyen el resultado de la acción. Un test de interfaz simula expresamente esa respuesta tardía.

Validación posterior: 14 pruebas de integración MVP aprobadas (`TestResults/mvp-simultaneo.trx`), análisis Flutter sin observaciones y 17 pruebas Flutter aprobadas. El recorrido completo se ejecutó antes de estas dos correcciones; sus regresiones se comprobaron automatizadamente.

F1-02 queda completada. La reconexión visual tras pérdida de red, participantes/suspensiones y reasignaciones permanecen pendientes. La consulta de la resolución desde Android se completó en el avance descrito a continuación. Este ensayo no aprueba pagos reales ni las 44 funcionalidades completas.

El APK corregido se compiló e instaló como `.artifacts/mobile/Delivery-emulator-simultaneo-debug.apk`; log local `D:/Development/PedidosYa/temp/mvp-simultaneo-build.log`.


## Respuesta de soporte visible en Android

F1-06 completada el 23 de noviembre de 2026. El detalle de la entrega incorpora «Consultar respuesta de soporte»: obtiene las solicitudes de la cuenta, selecciona la última del pedido abierto y consulta su estado y resolución en el servidor. No muestra casos de otros pedidos. Si no existen solicitudes, informa ese estado; una solicitud resuelta o cerrada permite pedir ayuda de nuevo si surge otro problema.

Se recompiló e instaló `.artifacts/mobile/Delivery-emulator-soporte-debug.apk`. Tras reiniciar la app y recuperar la sesión, el repartidor abrió el pedido `622a8dd0-ab3a-41b9-8a96-533e7979561d` y consultó el caso creado durante el recorrido simultáneo. Android mostró «Última solicitud: Resuelta» y el texto guardado desde Chrome: «Prueba simultánea: caso recibido desde Android; retiro y entrega continúan con PIN».

[Evidencia de resolución en Android](simultaneo-soporte-resuelto-android.jpg). El caso se mantuvo en `resolved`; no se volvió a crear ni se cerró por una llamada técnica para completar esta validación.

Análisis Flutter sin observaciones y 19 pruebas aprobadas, con regresiones para selección de caso del pedido correcto y ausencia de solicitudes. Compilación debug aprobada; log local `D:/Development/PedidosYa/temp/mvp-soporte-respuesta-build.log`. Los permisos del backend no cambiaron y se conservan las 14 pruebas de integración aprobadas del avance anterior.

La consulta es manual y muestra la última solicitud del pedido. No introduce notificaciones push ni un historial completo de conversaciones. Reconexión visual, revisión/suspensión de participantes y reasignación siguen en el TODO.
