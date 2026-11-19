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
