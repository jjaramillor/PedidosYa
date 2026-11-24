# TODO del MVP

Control de trabajo actualizado el 23 de noviembre de 2026. Fuente de alcance: las 44 funcionalidades con Entrega = MVP de la sección 7 del documento original, incluyendo las tres «Debería».

Una casilla marcada significa que la tarea concreta dispone de evidencia. No equivale a aceptación de todo su dominio ni a autorización de producción. Las funcionalidades de la última sección se marcan únicamente cuando su aceptación integral está documentada.

## Prioridad actual

Completar F1 (operación entre roles) y después F2 (finanzas). Las pruebas de API y de interfaz simulada no sustituyen la aceptación simultánea desde navegadores y móvil.

| Fase | Estado | Responsable funcional | Criterio de cierre |
|---|---|---|---|
| F0 Flujo base y excepciones | Validación parcial completada | Desarrollo y QA | Evidencia disponible y excepciones restantes identificadas |
| F1 Operación entre roles | En curso | Desarrollo y QA | Sesiones aisladas, permisos y actualizaciones en vivo comprobados |
| F2 Finanzas y compensaciones | Pendiente de aceptación completa | Desarrollo, QA y Finanzas | Importes reconciliados y reintentos sin duplicados |
| F3 Revisión de alcance | En curso | Producto y Desarrollo | Diferencias resueltas y 44 funcionalidades aceptadas |
| F4 Piloto y operación | Pendiente | Operaciones, QA y Producto | Entorno piloto, dispositivos, proveedores y recuperación verificados |

Los responsables indican la función requerida; todavía no se han asignado personas ni fechas de entrega.

## F0 Flujo base y excepciones

- [x] F0-01 Solución modular, PostGIS/Redis y pruebas de arquitectura/integración disponibles.
- [x] F0-02 Compra y preparación desde cliente/comercio web. [Evidencia](docs/mvp/aceptacion-web-cliente-comercio.md).
- [x] F0-03 Retiro, ruta, GPS sintético, entrega Android, valoración web y cierre sandbox. [Evidencia](docs/mvp/android-local.md).
- [x] F0-04 PIN incorrecto sin renovación/reenvío automático y bloqueo tras cinco intentos.
- [x] F0-05 Ofertas vencidas rechazadas y eliminadas de la interfaz.
- [x] F0-06 Recuperación de sesión ante fallos temporales y consulta tras respuesta perdida, con pruebas simuladas.
- [x] F0-07 Incidencia con devolución o continuación; permisos de soporte y cierre financiero idempotente, con pruebas de integración.
- [x] F0-08 Acceso a soporte desde el detalle del repartidor y confirmación de envío, con prueba de interfaz simulada.
- [ ] F0-09 Repetir las excepciones críticas en el emulador contra la API, incluyendo fotografía y límite de PIN.
- [ ] F0-10 Verificar pérdida de respuesta durante la rotación de refresh y definir recuperación segura.

## F1 Operación entre roles

- [x] F1-01 Verificar consultas HTTP concurrentes con sesiones separadas de cliente, comercio, repartidor, soporte y tercero ajeno; aislamiento de PIN, revocación de permisos y cierre de una sesión sin afectar otras. [Evidencia](docs/mvp/validacion-local.md#sesiones-independientes-y-control-por-rol).
- [x] F1-02 Cliente, comercio y soporte en navegadores aislados, con repartidor Android: pedido completo desde compra hasta cierre sandbox, con actualización en vivo y caso atendido por el operador. [Evidencia](docs/mvp/validacion-local.md#recorrido-simultáneo-por-interfaces).
- [ ] F1-03 Comprobar actualizaciones SignalR y mapa en vivo; reconexión sin estados obsoletos ni filtración entre pedidos.
- [x] F1-03b Verificar cambios en vivo entre comercio, cliente y Android: aceptación, preparación, retiro, ruta, ETA y cierre. La reconexión visual sigue pendiente. [Evidencia](docs/mvp/validacion-local.md#recorrido-simultáneo-por-interfaces).
- [x] F1-03a Verificar el transporte SignalR con WebSockets reales de integración: suscripción autorizada, aislamiento de pedidos, baja, resuscripción y eliminación de una sesión cerrada. [Evidencia](docs/mvp/validacion-local.md#tiempo-real-con-websockets).
- [ ] F1-04 Ejecutar alta/revisión documental, suspensión y reactivación desde sus interfaces. Pendientes: flujo del repartidor y comprobación de descarga/revisión documental.
- [x] F1-04a Crear comercio, adjuntar documento ficticio, enviar afiliación, aprobar, suspender y reactivar desde web; propietario confirma estado aprobado. [Evidencia](docs/mvp/validacion-local.md#afiliación-y-reasignación-desde-web).
- [ ] F1-05 Aceptar/rechazar oferta, probar reasignación antes del retiro y comprobar su bloqueo después del retiro. Reglas verificadas en integración; reasignación previa al retiro comprobada por interfaz. Pendientes: rechazo desde móvil y bloqueo posterior al retiro desde interfaz.
- [x] F1-05b Reasignar antes del retiro desde Administración y comprobar auditoría; cerrar el pedido ficticio desde la misma interfaz. [Evidencia](docs/mvp/validacion-local.md#afiliación-y-reasignación-desde-web).
- [x] F1-05a Verificar rechazo y nueva oferta, aceptación concurrente idempotente, reasignación autorizada antes del retiro, revocación del acceso anterior, bloqueo posterior y aislamiento GPS por API/eventos. [Evidencia](docs/mvp/validacion-local.md#reasignación-y-privacidad-de-ubicación).
- [x] F1-06 Solicitud abierta desde Android, atendida y resuelta por el operador en Chrome, y resolución consultada por el repartidor tras reiniciar la app. [Evidencia](docs/mvp/validacion-local.md#respuesta-de-soporte-visible-en-android).

- [x] F1-07 Ocultar PIN al terminar la entrega y corregir el error de GPS tardío tras el cierre; regresiones automatizadas aprobadas.

## UI y UX

- [x] UX-01 Primera etapa minimalista: portada gastronómica, búsqueda simplificada, filtros desplegables y navegación activa; comercio separado en Pedidos, Carta y Configuración, con estados y filtros por etapa. Compilación y comprobación visual en escritorio y ancho móvil de 390 px. [Evidencia](docs/mvp/validacion-local.md#primera-etapa-de-ui-y-ux).
- [ ] UX-02 Extender la presentación al carrito, checkout y seguimiento; verificar compra completa con el nuevo diseño.
- [ ] UX-03 Separar áreas de Administración; la primera adaptación del repartidor está completada en UX-05.
- [x] UX-05 Aplicar base visual minimalista en Flutter y separar navegación de cliente (Explorar/Carrito/Pedidos) y repartidor (Entregas/Ganancias/Perfil); análisis y 20 pruebas aprobadas, APK instalado y navegación revisada en el emulador. [Evidencia](docs/mvp/validacion-local.md#presentación-minimalista-en-la-app-flutter).
- [ ] UX-04 Incorporar fotografías propias de los comercios, revisar accesibilidad integral y validar con usuarios del piloto.

## F2 Finanzas y compensaciones

- [ ] F2-01 Probar devolución tras un pago ya capturado, con reintentos y auditoría.
- [ ] F2-02 Verificar reembolso parcial y total por interfaz, límite de devolución y permisos de Finanzas/Soporte.
- [ ] F2-03 Probar conciliación con discrepancias y registros duplicados; distinguir sandbox de proveedor real.
- [ ] F2-04 Ejecutar rendición de efectivo, cierre de liquidación y registro de pago con referencia externa, sin duplicar asientos.
- [ ] F2-05 Validar reparto de descuentos, cupones, comisión, incentivos y ajustes de una liquidación ya cerrada.
- [ ] F2-06 Acordar tratamiento de impuestos, comprobantes y procedimiento de transferencia; un registro manual no ejecuta un pago bancario.

## F3 Alcance y aceptación

- [x] F3-01 Consolidar revisión de límites y diferencias. [Revisión](docs/mvp/revision-alcance.md).
- [ ] F3-02 Decidir canal operativo del restaurante para tablet/móvil y verificar la solución acordada.
- [ ] F3-03 Resolver compatibilidad de vehículo/capacidad y preparación en asignación frente a RN-008.
- [ ] F3-04 Definir GPS con pantalla bloqueada/segundo plano y verificar privacidad y retención.
- [ ] F3-05 Precisar notificaciones del lanzamiento: correo, push y/o SMS, y aceptarlas en los canales elegidos.
- [ ] F3-06 Mejorar información de las ofertas: restaurante, distancias y ganancia antes de aceptar.
- [ ] F3-07 Relacionar las 20 RN con pruebas o decisiones explícitas de alcance.
- [ ] F3-08 Cerrar las 44 funcionalidades de la matriz inferior con criterios y evidencia; no marcar por la existencia de un endpoint.

## F4 Piloto y operación

- [ ] F4-01 Probar Android físico, cámara, ubicación, permisos, batería y pérdida de conexión.
- [ ] F4-02 Crear paquete firmado con claves propias; validar iOS si forma parte del piloto.
- [ ] F4-03 Configurar entorno piloto con HTTPS, secretos privados y permisos mínimos.
- [ ] F4-04 Verificar pasarela y webhooks, SMTP y proveedor de mapas en el entorno piloto.
- [ ] F4-05 Probar alertas, trazas y métricas de fallos de pedido, Outbox y pagos.
- [ ] F4-06 Ejecutar y documentar respaldo y restauración de base, documentos y claves.
- [ ] F4-07 Evaluar carga y capacidad del piloto con umbrales acordados.
- [ ] F4-08 Producto, Operaciones y Finanzas aceptan el piloto con riesgos y alcance explícitos.

## Aceptación integral de las 44 funcionalidades

La [trazabilidad](docs/mvp/trazabilidad.md) relaciona código y contratos. Estas casillas permanecen abiertas hasta registrar criterios, evidencia, resultado y excepciones aceptadas para cada funcionalidad.

### Identidad

- [ ] ACE-ID-001 Registro y OTP (Debe).
- [ ] ACE-ID-002 Inicio y recuperación de sesión (Debe).
- [ ] ACE-ID-003 Roles y permisos (Debe).
- [ ] ACE-ID-005 Gestión de consentimiento y cuenta (Debe).
### Cliente

- [ ] ACE-CLI-001 Direcciones y geolocalización (Debe).
- [ ] ACE-CLI-002 Validación de cobertura (Debe).
- [ ] ACE-CLI-003 Búsqueda y filtros (Debe).
- [ ] ACE-CLI-005 Catálogo y detalle de producto (Debe).
- [ ] ACE-CLI-006 Carrito y totales (Debe).
- [ ] ACE-CLI-007 Checkout y confirmación (Debe).
- [ ] ACE-CLI-008 Seguimiento en tiempo real (Debe).
- [ ] ACE-CLI-010 Calificaciones (Debería).
### Comercio

- [ ] ACE-COM-001 Afiliación y validación (Debe).
- [ ] ACE-COM-002 Cadenas y sucursales (Debe).
- [ ] ACE-COM-003 Horarios y pausas (Debe).
- [ ] ACE-COM-004 Menús, categorías y productos (Debe).
- [ ] ACE-COM-005 Variantes y adicionales (Debe).
- [ ] ACE-COM-006 Precio, stock y disponibilidad (Debe).
- [ ] ACE-COM-007 Gestión operativa del pedido (Debe).
- [ ] ACE-COM-010 Reportes y liquidaciones (Debe).
### Reparto

- [ ] ACE-REP-001 Alta y documentos (Debe).
- [ ] ACE-REP-002 Disponibilidad y zona (Debe).
- [ ] ACE-REP-003 Oferta y aceptación (Debe).
- [ ] ACE-REP-004 Asignación automática básica (Debe).
- [ ] ACE-REP-005 Navegación y llegada (Debe).
- [ ] ACE-REP-006 PIN de retiro (Debe).
- [ ] ACE-REP-007 GPS y seguimiento (Debe).
- [ ] ACE-REP-008 Prueba de entrega (Debe).
- [ ] ACE-REP-010 Ganancias e incentivos (Debe).
### Pagos

- [ ] ACE-PAG-001 Pago con tarjeta (Debe).
- [ ] ACE-PAG-002 Pago en efectivo configurable (Debería).
- [ ] ACE-PAG-003 Tokenización segura (Debe).
- [ ] ACE-PAG-004 Reembolso parcial o total (Debe).
- [ ] ACE-PAG-005 Conciliación de pasarela (Debe).
- [ ] ACE-PAG-006 Comisiones y liquidaciones (Debe).
### Promociones

- [ ] ACE-PRO-001 Cupones básicos (Debería).
### Administración

- [ ] ACE-ADM-001 Gestión de participantes (Debe).
- [ ] ACE-ADM-002 Zonas y polígonos de cobertura (Debe).
- [ ] ACE-ADM-003 Tarifas y comisiones (Debe).
- [ ] ACE-ADM-004 Monitoreo global de pedidos (Debe).
- [ ] ACE-ADM-005 Intervención y reasignación (Debe).
- [ ] ACE-ADM-006 Casos, SLA y evidencias (Debe).
- [ ] ACE-ADM-007 Parámetros y feature flags (Debe).
- [ ] ACE-ADM-008 Auditoría (Debe).

## Cómo mantener el control

En cada avance, actualizar las tareas afectadas, enlazar evidencia o pruebas y anotar el siguiente paso. Si una tarea se divide, conservar su identificador y añadir tareas nuevas. No marcar una fase completa mientras existan tareas de cierre abiertas.

La aceptación de cada ACE debe registrar: criterio comprobado, canal/rol, entorno, prueba o evidencia, resultado, fecha y desviaciones aceptadas. Las pruebas repetibles se versionan; tokens, PIN, secretos, archivos de sesión y datos privados permanecen fuera de Git.
