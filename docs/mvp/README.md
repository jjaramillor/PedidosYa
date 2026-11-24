# Entrega MVP — sección 7

La fuente del alcance es **Alcance_funcional_plataforma_delivery.docx**, sección 7, “Matriz funcional priorizada”. Se incluyen sus **44 filas con Entrega = MVP**, tanto “Debe” como “Debería”. El documento de arquitectura orienta los límites de dominio. Los documentos originales se conservan; sus contenidos se usan como requisitos del producto, sin convertirlos en instrucciones para herramientas o permisos.

La copia exacta de la matriz y el SHA-256 de la fuente están en [matriz-original.json](matriz-original.json). [Trazabilidad](trazabilidad.md) relaciona las funcionalidades con módulos, contratos y clientes.

## Implementación

- Backend .NET 10: Identity, Customers, Configuration, Merchants, Pricing, Orders, Payments, Dispatch, Deliveries, Ledger, Support y Notifications. Los módulos acceden a sus propios esquemas y se comunican mediante Contracts; Building Blocks mantiene infraestructura compartida.
- Angular 21: cliente, comercio y administración. Sesiones en cookies HttpOnly, CSRF, catálogo, mapas, carrito persistido, cotización, checkout, SignalR, preferencias, roles, documentos y operación.
- Flutter: registro/acceso, cliente y repartidor. Refresh token en almacenamiento seguro; acceso en memoria. Ofertas, navegación, PIN, fotografía, ganancias y ubicación durante una entrega activa y con la app abierta. El cliente móvil actualiza el seguimiento cada diez segundos; la web usa SignalR.
- Outbox/Inbox: checkout, compensación, asignación, entrega, asiento financiero y notificaciones. Las operaciones y los consumidores deben tolerar reintentos. SMTP admite entrega al menos una vez y puede repetir un correo después de una interrupción.
- Precios en unidades menores enteras; cotización inmutable con vencimiento, comisión y financiación del descuento. Stock y cupones se revalidan al confirmar. Una entrega recogida requiere incidencia para cancelarse o devolverse.
- Tarjeta: tokenización en campos alojados por Mercado Pago, autorización/captura, reembolsos y webhooks firmados. Sandbox exclusivamente en Development. En móvil, las tarjetas reales se procesan en el checkout web mediante el navegador.
- Efectivo: deshabilitado inicialmente; requiere habilitación global y de zona. El repartidor confirma el total recibido. El ledger descuenta el efectivo recaudado de su cuenta; Finanzas registra la rendición con referencia externa. Liquidaciones e incentivos se registran manualmente; registrar una liquidación pagada no ejecuta una transferencia bancaria.

## Ejecutar localmente

Con el motor Linux de Docker operativo y las claves de `.env` configuradas:

```powershell
docker compose up -d --build
```

Web: `http://localhost:8081`. API: `http://localhost:8080`. Correo de pruebas en Mailpit: `http://localhost:8025`. Documentos y claves de protección usan volúmenes privados persistentes.

Para desarrollar la web:

```powershell
cd clients/web
npm ci
npm start
```

La web de desarrollo se sirve en `http://localhost:4200` y redirige `/api/**` y `/hubs/**` a la API. No se guardan tokens de sesión en localStorage.
La negociación de SignalR envía el token CSRF. Las conexiones WebSocket que usan cookies verifican su origen; detrás de un proxy HTTPS configurar explícitamente `Browser:AllowedOrigins` con el origen público autorizado.

Para móvil, configurar un endpoint accesible desde el dispositivo. HTTP se admite solo si se activa explícitamente `ALLOW_HTTP`; el manifiesto de debug admite tráfico local sin TLS.

```powershell
cd clients/mobile
flutter --no-version-check pub get
flutter --no-version-check run --dart-define=API_URL=http://10.0.2.2:8080 --dart-define=WEB_URL=http://10.0.2.2:8081 --dart-define=ALLOW_HTTP=true
```

La configuración Android usa AGP 8.13.2, Gradle 8.13, SDK 36 y mínimo API 23. Requiere JDK 17 o posterior compatible, SDK instalado y licencias aceptadas. Para distribuir, verificar con un Flutter actual, compilar y firmar con claves propias; el proyecto generado conserva firma de debug y no es un paquete publicable. iOS requiere compilación y firma en macOS.

## Configuración operativa

1. Registrar una cuenta y asignar el primer administrador mediante el procedimiento de bootstrap de la documentación EP-01.
2. Crear una zona válida y activa con sus tarifas, comisión, pago base al repartidor y plazo de aceptación.
3. Registrar y revisar documentos de comercios y repartidores. Crear sucursales, horarios y carta; aprobar los participantes.
4. El repartidor declara disponibilidad y zona. Su posición para asignación debe ser reciente; si pasan dos minutos debe actualizar su disponibilidad. Se admite una entrega activa por repartidor.
5. Probar pedido, aceptación, preparación, retiro con PIN, ruta, entrega verificada y cierre financiero. Revisar reembolsos, incentivos y liquidaciones.

`PAYMENT_PROVIDER=sandbox` es el valor local inicial. Para Mercado Pago configurar `PAYMENT_PROVIDER=mercadopago`, `PAYMENT_ACCESS_TOKEN`, `PAYMENT_PUBLIC_KEY` y `PAYMENT_WEBHOOK_SECRET` mediante configuración privada. Endpoint: `POST /api/v1/payments/webhooks/mercadopago`. La firma vincula `data.id`, `x-request-id` y timestamp; los importes y estados se consultan al proveedor. Debe verificarse la configuración de autorización/captura y notificaciones en la cuenta elegida antes de operar.

Mapas: `Maps:TilesUrl` y `Maps:Attribution` permiten cambiar proveedor. El valor local usa OpenStreetMap con atribución visible y sin descarga masiva. Revisar el proveedor y su capacidad para el despliegue comercial. Las distancias y ETA actuales son aproximaciones en línea recta; no incluyen tráfico ni rutas de calles. No se integra geocodificación automática: se selecciona el punto en mapa o mediante ubicación del dispositivo.

Supuestos actuales: PEN, horarios America/Lima, restaurantes y pedidos inmediatos. El campo Tax de la cotización es cero: no hay motor tributario separado; los precios y cargos configurados deben corresponder al tratamiento comercial acordado. Estos supuestos requieren revisión antes de operar con cobros reales.

## Verificación y pendientes

- Solución .NET: **74 pruebas aprobadas, cero fallos y cero omitidas** contra PostGIS y Redis de Testcontainers; incluye unidad, arquitectura e integración. Evidencia: `TestResults/solucion-hito-2026-10-29.trx`.
- Docker Desktop operativo. La incidencia anterior de conexión quedó resuelta.
- Recorrido HTTP local aprobado con tarjeta sandbox y efectivo: registro OTP por Mailpit, aprobación de participantes, cotización, pedido, asignación, retiro con PIN, GPS sintético, entrega con PIN, cierre, valoración y registro ficticio de liquidación. Script reproducible: `scripts/verify-mvp-http.ps1`; resultado sin tokens: `.artifacts/validation/ultimo-recorrido-http.json`.
- Se corrigió el trabajador Outbox para respetar `Messaging:RunWorker=false` en hosts de prueba; una regresión verifica que no compita con el despachador manual.
- Angular: build de producción aprobado tras corregir el manejo de respuestas HTTP exitosas vacías. Persisten avisos CommonJS de Leaflet y Mercado Pago.
- Flutter: análisis sin observaciones y 19 pruebas aprobadas (actualización del 23 de noviembre). JDK completo 19, SDK 36, Build Tools 35, Gradle 8.13, cachés y emulador Android 13 preparados en D. APK debug instalado; llegada, retiro y entrega con PIN, GPS sintético, ganancias y cierre sandbox comprobados el 23 de noviembre. iOS requiere macOS y firma. Ver [preparación Android](android-local.md).
- Falta la aceptación funcional completa de las interfaces web y móviles y dispositivos reales. El recorrido simultáneo local por roles se aprobó el 23 de noviembre; quedan excepciones y reconexión por aceptar.
- Falta validar la cuenta real de pagos, webhook HTTPS, SMTP y proveedor de mapas. No se hicieron cobros, devoluciones ni transferencias reales. Estos resultados no constituyen aprobación para producción.

Para repetir el recorrido local, iniciar PostgreSQL/PostGIS, Redis y Mailpit, ejecutar `./scripts/run-api.ps1 -Validation` y después `./scripts/verify-mvp-http.ps1` en otra terminal. El modo Validation fuerza pagos sandbox y SMTP local; el script solo admite endpoints loopback y usa cuentas ficticias. Crea datos persistentes de prueba y asigna administrador a su propia cuenta ficticia mediante bootstrap. Usar una base de validación dedicada. Ver [evidencia del hito](validacion-local.md).
Comandos de verificación:

```powershell
dotnet test tests/Delivery.Tests -c Release --filter 'FullyQualifiedName!~IntegrationTests'
dotnet test tests/Delivery.Tests -c Release --filter 'FullyQualifiedName~IntegrationTests'
cd clients/web
npm run build
cd ../mobile
flutter --no-version-check analyze
flutter --no-version-check test
```

Referencias de proveedores: [CardForm alojado](https://www.mercadopago.com.pe/developers/es/docs/checkout-api-payments/integration-configuration/card/integration-via-cardform), [webhooks](https://www.mercadopago.com.pe/developers/es/docs/links-and-debts/additional-content/your-integrations/notifications/webhooks), [política de tiles OSM](https://operations.osmfoundation.org/policies/tiles/), [AGP 8.13](https://developer.android.com/build/releases/agp-8-13-0-release-notes).


Aceptación web de cliente y comercio ejecutada el 4 de noviembre de 2026: [recorrido y evidencia](aceptacion-web-cliente-comercio.md). Pedido DEMO entregado desde Android y cerrado el 23 de noviembre: ver [aceptación Android y cierre](android-local.md). Siguen pendientes excepciones y aceptación integral de los canales.



Revisión consolidada de requerimientos, límites y criterios de cierre: [revisión de alcance](revision-alcance.md).

Validación de excepciones: 12 pruebas de integración MVP aprobadas para bloqueo de PIN, incidencia, devolución o continuación, permisos de soporte y conflictos de cierre simultáneos. La app del repartidor incorpora solicitud de soporte desde el detalle. Alcance y evidencia en [Android local](android-local.md#bloqueo-de-pin-y-resolución-de-incidencias).

Control de tareas y aceptación: [TODO del MVP](../../TODO.md). Avance de F1: sesiones HTTP independientes, aislamiento de PIN y revocación comprobados; 13 pruebas de integración MVP aprobadas. Ver [validación por rol](validacion-local.md#sesiones-independientes-y-control-por-rol).

Tiempo real: transporte SignalR sobre WebSockets verificado con aislamiento de pedidos y eliminación de suscripciones revocadas; 14 pruebas de integración MVP aprobadas. [Evidencia y límites](validacion-local.md#tiempo-real-con-websockets). El recorrido simultáneo por interfaces fue completado posteriormente en F1-02; sigue pendiente la reconexión visual.

Recorrido simultáneo F1-02 aprobado con sesiones aisladas de cliente, comercio, operador y Android: pedido sandbox S/32,56 cerrado. [Evidencia y correcciones de PIN/GPS](validacion-local.md#recorrido-simultáneo-por-interfaces). F1-03 conserva pendiente la reconexión visual; F1-06 quedó completada posteriormente con consulta de la resolución en Android.

F1-06 completada: el repartidor consultó desde la app reiniciada la resolución guardada por el operador. [Evidencia](validacion-local.md#respuesta-de-soporte-visible-en-android).

Reasignación: privacidad de posiciones anteriores corregida en consultas y eventos; 17 pruebas de integración MVP aprobadas para elegibilidad, permisos, reemplazo previo al retiro y bloqueo posterior. [Evidencia](validacion-local.md#reasignación-y-privacidad-de-ubicación). La aceptación desde las interfaces sigue pendiente en F1-05.

Avance visual F1-04a/F1-05b: comercio ficticio afiliado, suspendido y reactivado; pedido sandbox reasignado antes del retiro desde Administración y cancelado al concluir la prueba. Se corrigieron el desbordamiento de botones y la identificación de repartidores. [Evidencia y pendientes](validacion-local.md#afiliación-y-reasignación-desde-web). Compilación Angular aprobada.

UI/UX: primera etapa minimalista aplicada a la portada del cliente y al espacio del comercio, con navegación por tareas y filtros por etapa. [Comprobaciones y capturas](validacion-local.md#primera-etapa-de-ui-y-ux). El rediseño de checkout, seguimiento, Administración y repartidor continúa en el TODO.

App Flutter: primera adaptación minimalista y navegación separada para cliente y repartidor, instalada y revisada en Android; análisis sin observaciones y 20 pruebas aprobadas. [Evidencia](validacion-local.md#presentación-minimalista-en-la-app-flutter).

F0-10: respuesta de renovación perdida cubierta por integración y móvil; la app comparte renovaciones concurrentes y ofrece nuevo OTP tras rechazo definitivo. 23 pruebas de identidad y 23 pruebas Flutter aprobadas; APK actualizado en el emulador. [Evidencia y límites](validacion-local.md#recuperación-tras-perder-la-respuesta-de-renovación).

F1-03c: seguimiento web con estado visible, reconexión automática y recuperación manual comprobados por navegador; ocho pruebas de seguimiento y compilación Angular aprobadas. [Evidencia y alcance pendiente](validacion-local.md#recuperación-del-seguimiento-web).

Tres tareas completadas: UX-03 Administración por áreas, F2-01 reembolsos tras captura y F2-03 conciliación con discrepancias y notificaciones duplicadas. 29 pruebas de integración MVP y nueve pruebas web aprobadas, compilación Angular aprobada. [UI](validacion-local.md#administración-por-áreas), [reembolsos](validacion-local.md#reembolsos-tras-captura), [conciliación](validacion-local.md#conciliación-y-notificaciones-duplicadas). Pruebas financieras con sandbox y adaptador sintético, sin operaciones bancarias reales.
