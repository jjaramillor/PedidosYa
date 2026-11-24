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


## Reasignación y privacidad de ubicación

Avance del 23 de noviembre de 2026. Pruebas repetibles contra PostGIS/Redis verifican la reasignación desde `assigned` y desde `arrived`: solo un operador autorizado puede ejecutarla y el candidato debe ser elegible. El nuevo repartidor recibe una entrega en `assigned`; el anterior pierde lectura, acciones, envío GPS y el pedido en su lista. Tras retirar con PIN, la reasignación se rechaza con 409 y se conserva el repartidor vigente. La intervención queda auditada.

Otra prueba verifica rechazo de oferta, selección de otro candidato y dos aceptaciones simultáneas de la misma oferta sin duplicar asignación ni entrega.

Se reprodujo y corrigió una exposición de ubicación histórica: al cambiar el repartidor, la consulta podía seguir devolviendo la posición anterior, incluso antes de consumir el evento de asignación. Ahora la consulta exige coincidencia con la asignación vigente y selecciona únicamente posiciones del repartidor de la entrega. Se conservan los registros para su política de retención existente; no se borran para simular el resultado.

Los eventos GPS nuevos incluyen `CourierId`. El consumidor ignora las coordenadas de un repartidor que ya no está asignado. Los eventos antiguos pendientes sin ese campo solo solicitan una actualización del cliente, sin reenviar coordenadas. La prueba usa WebSockets reales de integración para comprobar que posiciones antiguas en Outbox no se filtren y que una ubicación nueva del reemplazo sí se reciba.

La aceptación visual de rechazo/reasignación continúa abierta en F1-05. Este avance no cambia pantallas ni requiere un APK nuevo.

Resultado: 17 pruebas de integración MVP aprobadas, cero fallos y cero omitidas. Evidencia local: `TestResults/mvp-reasignacion.trx`. No se repitieron las pruebas Flutter porque este avance solo modifica backend y pruebas de integración.


## Afiliación y reasignación desde web

Validación del 23 de noviembre de 2026 en el entorno local sandbox, con cuentas y documentos ficticios.

F1-04a: desde «Mi comercio» se creó «Comercio QA afiliación visual», se adjuntó un PDF sintético y se envió la solicitud. Administración mostró el estado pendiente y permitió aprobar, suspender y aprobar nuevamente, registrando un motivo en cada intervención. La sesión del propietario confirmó el estado final `approved`. No se crearon sucursales ni pedidos bajo este comercio. [Captura del comercio reactivado](comercio-qa-reactivado.jpg).

La descarga documental se intentó desde Administración, pero no se pudo confirmar su finalización con la herramienta del navegador. Esa comprobación y el ciclo equivalente del repartidor permanecen abiertos en F1-04; la carga de un documento no acredita su revisión.

F1-05b: se preparó por API el pedido ficticio `4612be99-91dd-4450-a44e-76cae48f5cb2` por S/32,56, hasta `ready` con oferta aceptada por `courier-41d27e69@mvp.test`. Desde Administración en Chrome se seleccionó como sustituto `courier-7d170163@mvp.test`, se indicó «Reasignación visual QA antes del retiro» y se pulsó «Reasignar». La pantalla mostró «Entrega reasignada». Una consulta de solo lectura confirmó `courier.reassigned` y la nueva asignación en la auditoría. La captura de Chrome no pudo guardarse por un fallo de la herramienta; este paso dispone de observación de interfaz y auditoría, sin imagen.

Se canceló exclusivamente ese fixture desde Administración con el motivo «Cierre del fixture de reasignación visual QA; sin cobro real». La pantalla mostró `cancelled` y la auditoría confirmó `order.cancelled`. Un intento previo de preparación cuya oferta había vencido se canceló mediante la API del propietario. No se modificaron pedidos completados. La preparación por API no se contabiliza como aceptación visual de compra, oferta o retiro.

Dos correcciones surgieron de la validación: los botones de las tarjetas permiten salto de línea para no desbordar su ancho; los repartidores incluyen el correo junto al nombre en Administración y en el selector de reasignación. Antes de la corrección el botón de suspensión excedía el borde de su tarjeta; después, la inspección geométrica no detectó desbordamiento horizontal en las filas revisadas.

La compilación de producción Angular (`npm run build`) pasó con los avisos CommonJS existentes de Leaflet y Mercado Pago. Este avance modifica únicamente presentación web; se conservan las 17 pruebas de integración MVP del avance anterior y no se repitieron ni se recompiló Android. F1-05 conserva pendientes el rechazo desde móvil y el bloqueo posterior al retiro desde interfaz, ya cubiertos por integración.


## Primera etapa de UI y UX

Implementación del 23 de noviembre de 2026: base visual con tonos neutros, verde profundo y acento terracota; portada gastronómica con ilustración SVG local, tarjetas de restaurantes y una búsqueda principal. Los filtros adicionales permanecen disponibles en un desplegable. La dirección de búsqueda ocupa una franja propia y cambiar una dirección guardada repite la consulta. Se conserva la información de precio, tiempo, distancia y valoración; la ausencia de puntuación se muestra como «Nuevo».

El comercio abre en Pedidos y separa Carta y Configuración. Los filtros muestran nuevos, cocina, listos e historial con contadores. Los estados están traducidos y los pedidos muestran una referencia abreviada. El selector de sucursal queda disponible en las tres secciones; las acciones de negocio y permisos del backend no se modifican. Un comercio sin sucursal recibe orientación para configurarla.

Comprobaciones por navegador contra la API local: búsqueda sin coincidencias y restauración del resultado, apertura de filtros, navegación entre las tres secciones del comercio, lista de historial y detalle de un pedido cancelado sin acciones de preparación. No se crearon compras ni se modificaron pedidos para esta revisión. Se comprobó que el ancho de página no excediera el viewport en escritorio (1280 px) y móvil (390 px); en móvil la navegación principal permite desplazamiento horizontal dentro de su área. No equivale a una auditoría completa de accesibilidad ni a una nueva aceptación de checkout.

Evidencia: [cliente escritorio](ui-cliente-minimalista.jpg), [cliente móvil](ui-cliente-movil.jpg), [comercio: historial adaptable](ui-comercio-minimalista.jpg), [comercio móvil](ui-comercio-movil.jpg). Las ilustraciones son decorativas y no representan fotografías de platos ofrecidos por los comercios.

`npm run build` aprobado, sin avisos nuevos; permanecen los avisos CommonJS de Leaflet y Mercado Pago. Se formatearon los archivos editados con Prettier. Esta etapa cambia presentación y filtrado local; no se repitieron las pruebas del backend ni Flutter. Carrito, checkout, seguimiento, Administración y repartidor mantienen pendientes su rediseño específico en el TODO.


## Presentación minimalista en la app Flutter

Primera adaptación del 23 de noviembre de 2026. El tema Material 3 comparte la paleta de la web: fondo neutro, verde profundo y acento terracota, campos con contorno, tarjetas sin elevación y botones de al menos 48 px. Los encabezados y estados de entrega están en español. Se conservan los controles de PIN, evidencia, soporte y GPS.

La navegación del repartidor separa Entregas, Ganancias y Perfil. El inicio prioriza disponibilidad, ofertas y entregas; la afiliación y el acceso al cliente se encuentran en Perfil. Al abrir una entrega se oculta esa navegación para concentrarse en la tarea. En el cliente, Explorar, Carrito y Pedidos son destinos separados; añadir un producto muestra confirmación y acceso al carrito, y confirmar compra abre Pedidos.

Verificación automatizada: análisis Flutter sin observaciones y 20 pruebas aprobadas. Las regresiones de sesión, expiración de ofertas, PIN, soporte y GPS tardío continúan pasando. Una prueba nueva comprueba navegación y estados vacíos del cliente a 360 px sin excepciones de layout. Las expectativas de navegación de perfil y del estado traducido se actualizaron en las regresiones existentes.

APK debug compilado e instalado mediante actualización, conservando la sesión ficticia del emulador. Archivo local: `.artifacts/mobile/Delivery-emulator-ui-debug.apk`; log: `D:/Development/PedidosYa/temp/mvp-ui-mobile-build.log`. No se instalaron herramientas nuevas. La verificación visual no crea compras ni activa disponibilidad; no sustituye la aceptación integral del checkout, cámara, GPS o un dispositivo físico.

En el emulador Android se observaron el inicio de Entregas con disponibilidad desactivada, el cambio a Perfil, el acceso a cliente y la navegación a Carrito y Pedidos con sus estados vacíos. Evidencia: [repartidor](app-repartidor-minimalista.png) y [cliente](app-cliente-minimalista.png). Se usó la cuenta ficticia de repartidor existente para comprobar ambos espacios; no se repitió una compra completa.


## Recuperación tras perder la respuesta de renovación

F0-10 verificada el 23 de noviembre de 2026. El backend mantiene refresh tokens de un solo uso: si la rotación se confirma y la respuesta no llega al cliente, este conserva el token anterior. Reutilizarlo provoca 401 y revoca esa familia de sesión. No se introducen excepciones, períodos de gracia ni recuperación del token consumido. El usuario ingresa nuevamente mediante OTP de login; las demás sesiones de la cuenta siguen activas. La recuperación de cuenta explícita conserva su política independiente de revocar sesiones anteriores.

La prueba de integración descarta el cuerpo de una respuesta HTTP de refresh exitosa, reenvía el token anterior y comprueba rechazo, revocación de esa sesión, conservación de otra sesión del usuario y creación de una familia nueva por OTP. La sesión nueva se valida en el servicio y su refresh se utiliza correctamente por HTTP. El reloj de pruebas avanza para respetar el cooldown OTP. Este escenario reproduce el resultado de una respuesta perdida después del commit, sin simular un corte TCP real.

En Flutter, una excepción de red conserva la credencial y no reintenta la renovación automáticamente. Un rechazo definitivo elimina el refresh guardado y el access token en memoria, con indicación de ingresar por código. La pantalla de repartidor vuelve al acceso y limpia los datos operativos de la sesión, incluyendo PIN, evidencia y solicitudes de soporte; detiene GPS. Las renovaciones manuales y automáticas comparten ahora una sola solicitud pendiente, evitando consumir el mismo token concurrentemente desde la misma instancia. No se repiten mutaciones por una respuesta perdida.

Pruebas móviles nuevas: respuesta de refresh perdida después de rotación y recuperación por OTP; renovación manual/automática concurrente; retorno al ingreso por rechazo de sesión durante operación. Los fallos temporales de red y HTTP 429/500/503 conservan sus regresiones. El cliente móvil anidado muestra su error de autenticación y permite volver al espacio raíz; no se incorpora una redirección global de todas las pantallas.

Validación: 23 pruebas `EpicOneIntegrationTests` aprobadas, cero omitidas (`TestResults/refresh-response-loss.trx`); análisis Flutter sin observaciones y 23 pruebas Flutter aprobadas (`D:/Development/PedidosYa/temp/refresh-mobile-tests.log`). No se ensayó pérdida física de red en el emulador ni se cerró F1-03. Este avance no modifica backend productivo ni la web.

APK debug recompilado e instalado como actualización en el emulador: `.artifacts/mobile/Delivery-emulator-refresh-recovery-debug.apk`. Log local: `D:/Development/PedidosYa/temp/mvp-refresh-recovery-build.log`. La instalación no constituye validación visual de un corte de red.


## Recuperación del seguimiento web

Avance F1-03c del 23 de noviembre de 2026. La pantalla muestra conectando, conectado, reconectando o sin seguimiento en vivo, con una advertencia de posible información desactualizada durante la interrupción. SignalR reintenta con esperas de 0, 5, 10 y 30 segundos; agotadas estas, la actualización manual crea una conexión nueva sin recargar la página. Se conserva la comprobación de sesión durante la reconexión.

Cada conexión queda asociada a la generación del pedido seleccionado. Las conexiones retiradas no ejecutan lecturas ni resuscripciones para el pedido siguiente. Una conexión inicial fallida tampoco queda retenida como si estuviera iniciada. Si la suscripción es rechazada, no se anuncia seguimiento en vivo. Tras suscribirse o resuscribirse se consulta nuevamente el pedido. Los eventos que llegan durante una lectura producen una lectura final adicional, en lugar de descartarse. Las respuestas tardías de pedido y entrega se aplican solo si el pedido sigue seleccionado.

Prueba visual contra la API local, usando exclusivamente el pedido ficticio cancelado `4612be99-91dd-4450-a44e-76cae48f5cb2`:

1. Abrir su detalle mostró «Conectado · actualizaciones en vivo».
2. Detener la API produjo «Reconectando… La información puede estar desactualizada».
3. El primer reinicio, con compilación, excedió la ventana de reintentos; se mostró «Sin seguimiento en vivo». Con la API disponible, «Actualizar seguimiento» recuperó la conexión sin recargar la página.
4. En una segunda interrupción se reinició el binario ya compilado. Sin pulsar actualizar, SignalR restableció WebSocket y la pantalla volvió a «Conectado · actualizaciones en vivo».

La API quedó ejecutándose con el mismo entorno local de validación y disponibilidad comprobada. No se editaron pedidos ni posiciones. Evidencia: [reconectando](seguimiento-reconectando.jpg) y [recuperado](seguimiento-recuperado.jpg). El mapa conserva la dirección sintética del fixture; este ensayo no demuestra un movimiento GPS ni un cambio de estado ocurrido durante la desconexión. Esa comprobación permanece abierta en F1-03.

Validación automatizada: `npm run test:tracking`, ocho pruebas aprobadas, cubriendo resuscripción/lectura tras reconectar, recuperación manual, fallo inicial, aislamiento de conexiones anteriores, rechazo de suscripción, cierre durante creación, eventos durante lectura y recuperación tras fallo de lectura. El ejecutor usa TypeScript y Node ya instalados, sin dependencias nuevas. `npm run build` aprobado; se mantienen los avisos CommonJS existentes de Leaflet y Mercado Pago. No cambió el backend ni Flutter y no se repitieron sus suites.


## Administración por áreas

UX-03 completada el 23 de noviembre de 2026. Administración ahora separa Pedidos, Participantes, Soporte, Finanzas, Configuración y Auditoría. La descripción y la pestaña activa identifican la tarea actual. El campo de motivo se conserva junto a la navegación y no se pierden formularios al cambiar de área. Las secciones autorizadas se calculan desde los permisos existentes: una cuenta solo de soporte abre Soporte; una cuenta sin permisos no recibe áreas. Los controles del backend mantienen su autorización independiente.

Se recorrieron las seis áreas desde Chrome con la cuenta administradora ficticia existente; Finanzas y Auditoría se comprobaron con su contenido visible. No se ejecutaron cambios de participantes, reembolsos ni liquidaciones desde esa sesión. [Captura de Finanzas](admin-finanzas-separadas.jpg). Una captura posterior de Chrome falló por la herramienta, pero la captura adjunta sí se guardó y se inspeccionó.

`npm run test:tracking` aprobó nueve pruebas: las ocho regresiones del seguimiento y una nueva de visibilidad por permisos para administración. `npm run build` aprobado, con los avisos CommonJS existentes. Este avance no constituye una auditoría completa de accesibilidad ni aceptación en tablet.

## Reembolsos tras captura

F2-01 completada en integración local el 23 de noviembre de 2026. Se prepara y completa una entrega con tarjeta sandbox, captura y distribución financiera. Una devolución parcial de 100 unidades menores se solicita simultáneamente dos veces con la misma clave: queda un único reembolso, una auditoría y un asiento. Reutilizar esa clave con otro importe se rechaza. Repetir directamente el consumo contable de la operación no duplica el asiento.

Se devuelve el remanente con una segunda clave, se repite y se drena Outbox más de una vez. El pago queda `refunded`, el total devuelto coincide con lo capturado, hay dos reembolsos y dos auditorías. Las sumas por cuenta quedan en cero: se revierte completamente la distribución de esa entrega. Una devolución adicional de una unidad menor se rechaza y no genera registros financieros nuevos.

Las pruebas usan PostGIS y Redis aislados en Testcontainers, con datos sintéticos. No se devuelve dinero real ni se modifican pedidos locales de demostración. La validación por interfaz y los permisos funcionales específicos de Finanzas/Soporte siguen en F2-02; el ensayo no reproduce una respuesta perdida dentro de una pasarela real.

## Conciliación y notificaciones duplicadas

F2-03 completada como validación de integración el 23 de noviembre de 2026. Nueve escenarios comparan un pago capturado con una instantánea de proveedor: coincidencia completa y diferencias de estado, monto, capturado, devuelto, moneda, pedido e identificador del pago; además del modo sandbox. Se corrigió la comparación para exigir también coincidencia del identificador remoto con el identificador almacenado.

Cada escenario se concilia dos veces: se conservan dos comprobaciones históricas, sin duplicar ni modificar dinero, asientos o reembolsos. Sandbox informa `Simulated=true`, nunca `Matches=true`, y registra `provider_state='simulated'` aunque los valores coincidan. Un cliente sin permiso financiero recibe 403.

Dos escenarios adicionales envían tres notificaciones firmadas concurrentes de un pago ya capturado, con estado remoto `captured` o un estado `authorized` tardío. No revierten el pago ni generan nuevos mensajes Outbox, asientos o reembolsos. Las firmas usan una clave de fixture. El adaptador solo entrega instantáneas y rechaza operaciones financieras; no realiza solicitudes de red a Mercado Pago. Las opciones del proveedor real se usan únicamente para recorrer la rama de comparación no simulada en ese adaptador: no acreditan integración bancaria real.

Resultado final de estas validaciones: 29 pruebas de integración MVP aprobadas, cero fallos y cero omitidas. Reporte: `TestResults/finance-refund-reconcile.trx`. El log local es `D:/Development/PedidosYa/temp/finance-refund-reconcile.log`. Los webhooks iniciales y las credenciales reales del proveedor requieren la validación del entorno piloto F4-04. Flutter no cambió y no se recompiló el APK en este avance.

La API local se actualizó con el binario Release ya validado, manteniendo el entorno Development con sandbox y los volúmenes privados existentes. `/health/ready` devolvió 200 después del reinicio.


## Rendición, liquidaciones y cupones

F2-04 y F2-05 verificadas el 24 de noviembre de 2026 en PostGIS/Redis aislados de integración. Se añadieron tres casos al recorrido financiero:

- Entrega en efectivo: el saldo por rendir es negativo, un repartidor sin permiso no puede registrar rendición y no se puede ingresar más que la deuda. Dos rendiciones concurrentes con la misma clave generan un único asiento y una auditoría. La liquidación del comercio se cierra concurrentemente sin duplicados y se registra una referencia ficticia de pago externo; el reintento no sobrescribe la referencia inicial. Saldo pendiente cero y asientos balanceados.
- Cupón financiado por comercio y cupón financiado por plataforma: cada uno descuenta 500 unidades menores sobre subtotal 2500. Con comisión de 15 %, el comercio recibe 1700 o 2125 respectivamente; el repartidor conserva su tarifa más propina. Se verifica uso único del cupón y conservación del total entre cuentas.
- Tras pagar una liquidación del repartidor se añade un incentivo de 101 unidades menores, se repite y se reembolsa la compra. La liquidación original queda idéntica; incentivo y devolución forman movimientos pendientes nuevos. El saldo pendiente puede ser negativo para compensaciones futuras: no se realiza un cobro ni una transferencia bancaria. Se conserva balance por asiento.

Pruebas: `Cash_collection_settlement_and_payment_reference_are_idempotent_and_audited` y `Funded_coupon_distribution_and_adjustments_preserve_closed_settlements`. Son operaciones sintéticas y referencias `FICTICIO-*`; la aceptación por interfaz y los procedimientos financieros del piloto siguen separados.

## Ofertas informadas en Android

F3-06 completada el 24 de noviembre de 2026. Las ofertas pendientes y vigentes incorporan `preview`: nombre del restaurante, distancia desde la última ubicación declarada hasta el restaurante, distancia de entrega, ganancia estimada (tarifa de reparto más propina), moneda, método de pago y efectivo a recaudar cuando corresponda. Las distancias son aproximadas en línea recta, no rutas ni tiempos de navegación. No se incluyen domicilio, coordenadas del cliente ni PIN. El historial no conserva esa vista previa y otro cliente no recibe las ofertas del repartidor.

Flutter muestra estos datos antes de aceptar. Si la API no proporciona el detalle, la aceptación queda deshabilitada y se indica actualizar; se conserva la opción de rechazar. La hora límite se muestra en la zona del dispositivo. Durante la prueba se detectó desfase del reloj del emulador: la caducidad ahora usa `Date` de las respuestas de la API y tiempo transcurrido monotónico, con retiro conservador en el límite de precisión del encabezado. Una regresión verifica que el reloj del servidor prevalezca sobre el reloj local. Si no se recibe un encabezado válido, se conserva la última referencia disponible o el reloj local antes de calibrarse; la API siempre aplica su propia caducidad.

Prueba visual con el pedido sintético `debb7e17-8b8a-44a0-b0e8-e778994384be`, preparado por API hasta oferta pendiente. Android consultó la oferta y mostró Sucursal QA 41d27e69, 0,0 km hasta el restaurante, 1,6 km de entrega, ganancia estimada PEN 5,56 y pago con tarjeta sin cobro de efectivo. Se pulsó Rechazar, la oferta desapareció y la auditoría registró `offer.rejected`. Después se canceló únicamente ese fixture mediante la API del comercio y se dejó al repartidor QA no disponible. No se modificaron pedidos completados.

[Captura del detalle de oferta](oferta-detalle-android.png). La captura se obtuvo antes del ajuste de reloj; el diseño no cambió después. El ajuste de caducidad se validó automáticamente y se incluyó en el APK final. Esto no acredita navegación por carretera ni aceptación completa de F1-05: sigue pendiente la reasignación después del retiro por interfaz.

Resultado: 34 pruebas de integración MVP aprobadas, cero omitidas (`TestResults/five-tasks-finance-offers.trx`), análisis Flutter sin observaciones y 26 pruebas Flutter aprobadas. El widget del detalle se comprueba a 360 px. APK final compilado e instalado como `.artifacts/mobile/Delivery-emulator-offers-debug.apk`; logs locales `D:/Development/PedidosYa/temp/five-tasks-mobile.log` y `D:/Development/PedidosYa/temp/five-tasks-apk.log`. No hay cambios Angular en este avance.

## Trazabilidad y ensayo de recuperación

La [matriz de las 20 RN](reglas-negocio.md) se cotejó con las filas del DOCX original y registra su SHA-256. Distingue pruebas ejecutadas, revisión de código, diferencias de alcance y decisiones pendientes. F3-07 cierra trazabilidad; no certifica cumplimiento integral de cada RN. Los pendientes sobre vehículo/preparación, retención GPS, segmentación, impuestos y normativa permanecen visibles.

F4-06 dispone de [script y ensayo reproducible de respaldo/restauración](respaldo-restauracion.md). Se restauraron 89 tablas cotejadas, 19 documentos, un archivo de claves y configuración privada; ACL protegida, archivos idénticos por SHA-256 y contabilidad balanceada. La base se restauró en un contenedor sin red, distinto de la aplicación. La configuración privada y los respaldos no se incorporan a Git. El procedimiento documenta sus límites y lo que falta repetir en el piloto.


## Compra, reconexión y reembolsos por interfaz

Validación del 24 de noviembre de 2026. Pedido nuevo `525ccab1-efea-4287-aefe-0cbf9015ea3a`, Sucursal QA 41d27e69, una unidad de Menú de validación. Cliente en IAB y operación en Chrome, con cuentas ficticias existentes. Total sandbox S/32,56: productos 25,00, entrega 6,56 y servicio 1,00.

**UX-02:** desde la carta se añadió el producto, se abrió el carrito, se calculó el total, se eligió tarjeta de prueba y se confirmó. La compra se realizó por interfaz; el comercio y el reparto se avanzaron por API para preparar los escenarios siguientes. El cliente mostró finalmente Completado, sin PIN terminal. El checkout separa productos/entrega de resumen/pago, con disposición de dos columnas en escritorio y una en móvil. Se comprobó ancho 390 px sin desbordamiento horizontal. Evidencia: [resumen](checkout-resumen-minimalista.jpg), [móvil](checkout-movil.jpg) y [pedido completado](checkout-pedido-completado.jpg).

**F1-03:** un proxy temporal Node, enlazado exclusivamente a loopback `localhost:4201`, reenvió la aplicación al frontend habitual. Se interrumpieron conexiones y negociaciones de `/hubs/orders`, manteniendo HTTP disponible. El proxy de validación conserva las cookies y adapta el origen al frontend local esperado; no se modificó la política de seguridad del backend ni se publicó un puerto externo. No equivale a una prueba de pérdida de Wi-Fi del teléfono.

Antes del corte el cliente mostraba Listo para recoger y solo el marcador verde de destino. Durante el corte conservó ese estado y advirtió que podía estar desactualizado. Por API se registraron llegada, retiro con PIN, ruta y un GPS sintético nuevo. Al restablecer el canal, sin pulsar Actualizar seguimiento, el cliente mostró En camino, ETA de siete minutos y un segundo marcador naranja. La inspección del SVG del mapa pasó de un marcador de ubicación a dos, con colores de destino/repartidor; el transporte restableció WebSocket. [Captura de recuperación](reconexion-estado-gps.jpg). El fixture usa coordenadas sintéticas fuera de Lima. El proxy fue detenido al finalizar.

**F1-05:** desde Administración se seleccionó el mismo pedido ya `in_transit`, el repartidor QA de reemplazo y un motivo. Reasignar mostró «Una entrega recogida no puede reasignarse». El repartidor original se conservó. Este resultado completa la evidencia previa de aceptación/rechazo Android y reasignación antes del retiro. [Bloqueo tardío](reasignacion-tardia-bloqueada.jpg).

**F2-02:** después de completar entrega y captura por API, Administración mostró cobrado 32,56 y disponible 32,56. Intentar devolver 33,00 produjo «La devolución supera el cobro disponible», sin registrar devolución. Desde el pedido se devolvió 1,00; la pantalla mostró devuelto 1,00 y disponible 31,56. Desde el caso «Reembolso UI QA» se devolvió el remanente de 31,56 y se resolvió el caso. El detalle final mostró cobrado 32,56, devuelto 32,56 y disponible cero. [Saldo final](reembolso-total-interfaz.jpg). Las acciones visuales se ejecutaron con administrador ficticio; el alcance de Finanzas/Soporte se comprueba por separado mediante HTTP con roles limitados.

Se añadió consulta del resumen de pago para `finance.manage` o `payments.refund` sin conceder lectura del pedido completo. La nueva prueba verifica resumen y reembolso permitidos para esos permisos, rechazo para soporte sin autorización de devolución y operación sin permisos financieros, y conservación del acceso restringido al pedido completo. No se expone el resumen financiero a un tercero sin permisos.

El navegador conserva la misma clave de idempotencia al reintentar una devolución con el mismo pedido, importe y motivo hasta recibir respuesta exitosa. Un cambio de esa combinación es otra intención; el backend mantiene el límite acumulado. Tras confirmar se limpia el importe y se consulta el saldo de nuevo. Esta prueba no corta físicamente una respuesta de reembolso: la conservación de clave tiene regresión web, y la idempotencia financiera ya está cubierta en integración. No se promete persistencia de esa intención después de recargar/cerrar el navegador.

Estado final por consulta de solo lectura: pedido `closed`, pago `refunded`, capturado y devuelto 3256 unidades menores; caso `resolved`; ambos repartidores QA no disponibles. No se modificaron pedidos completados de ensayos anteriores. Los tokens del preparador se mantuvieron en memoria y el proceso se cerró.

Validación: 35 pruebas de integración MVP, 23 pruebas de identidad y 11 pruebas web aprobadas. Compilación Angular aprobada con los avisos CommonJS existentes. Análisis Flutter sin observaciones y 26 pruebas aprobadas; APK actualizado como `.artifacts/mobile/Delivery-emulator-affiliation-debug.apk` con confirmación textual al enviar solicitudes de afiliación.

## Afiliación nativa pendiente y quinto entregable

F1-04 no se marca completada. Se creó por API únicamente la identidad ficticia `qa24b@mvp.test` y se preparó una imagen sintética sin validez de identificación en la galería del emulador. No se envió una solicitud de repartidor ni se aprobó esa cuenta; se comprobó que no existe perfil en `dispatch.couriers` para su usuario.

Durante la entrada del correo, el controlador nativo quedó bloqueado con una solicitud activa. Reiniciar el kernel y probar el controlador alternativo no recuperó el control; ambos comparten el mismo bloqueo. Se detuvieron los intentos de entrada nativa. El acceso de la cuenta QA anterior se cerró como parte del cambio de sesión de prueba. La revisión de imágenes en Administración y la descarga PDF quedaron implementadas, pero no se consideran aceptadas por falta de comprobación visual final.

La quinta tarea se sustituyó explícitamente por [ACE-ID-003, aceptación local de roles y permisos](aceptacion-id-003.md): 23 pruebas de identidad, criterios documentados y creación/actualización de rol sin privilegios desde Chrome, con protección de roles de sistema observada. El rol de prueba se eliminó por API autorizada sin asignarlo a cuentas. El resto de funcionalidades conserva sus casillas abiertas.
