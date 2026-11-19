# Delivery — Plataforma MVP

Base de una plataforma propia de delivery, en .NET 10, organizada como monolito modular. Identity implementa las cinco HU de EP-01: OTP, sesiones recuperables/revocables, roles y permisos, identidad federada y consentimiento/gestión de cuenta.

La revisión del backlog original, los criterios refinados, contratos y estado de pruebas están en [EP-01](docs/epica-1/README.md).
La implementación de las 44 funcionalidades MVP de la sección 7 se documenta en [Entrega MVP](docs/mvp/README.md), con clientes Angular/Flutter, módulos de comercio y reparto, pagos, liquidaciones y operación. La aceptación completa sigue pendiente de ejecutar integración y validar proveedores y paquetes móviles.

## Ejecutar

Requisitos: SDK .NET 10.0.400 y Docker con motor de contenedores Linux operativo.

```powershell
Copy-Item .env.example .env
# Editar .env para asignar claves locales propias de al menos 32 bytes.
docker compose up -d --build
```

Si ya existe .env, conservarlo. La API se sirve en http://localhost:8080; Mailpit en http://localhost:8025; Jaeger en http://localhost:16686; métricas del Collector en http://localhost:8889/metrics. Todos los puertos publicados se restringen a localhost.

Para depurar la API fuera del contenedor:

```powershell
docker compose up -d postgres redis mailpit jaeger otel
./scripts/run-api.ps1
```

El script carga únicamente las variables locales necesarias desde .env y restaura el entorno al terminar. PostgreSQL/Redis deben estar listos antes de iniciar la API. La aplicación aplica la migración inicial bajo un bloqueo transaccional; versiones ya aplicadas no se repiten.

## Probar el flujo

1. Enviar `POST /api/v1/identity/otp/request` con `{"email":"ana@example.test","displayName":"Ana","acceptTerms":true,"acceptPrivacy":true}`. Devuelve 202 con challengeId y expiresAt.
2. Abrir el correo en Mailpit y obtener el código de seis dígitos.
3. Enviar `POST /api/v1/identity/otp/verify` con `{"challengeId":"<id>","code":"<codigo>"}`. Devuelve JWT de 15 minutos y el usuario.
4. Enviar `GET /api/v1/identity/me` con `Authorization: Bearer <token>`.

Para iniciar sesión sin repetir el formulario de registro, usar POST /api/v1/identity/login con email/deviceName y verificar el OTP. POST /recovery revoca sesiones anteriores después de verificar. El correo se normaliza a minúsculas; no se duplica el usuario ni se cambia su perfil al autenticar.

`GET /health/live` comprueba el proceso. `GET /health/ready` comprueba PostgreSQL y Redis. No se revelan errores internos en las respuestas.

## Validación

```powershell
dotnet build Delivery.slnx -c Release
dotnet test Delivery.slnx -c Release
# Pruebas que no necesitan Docker:
dotnet test Delivery.slnx -c Release --filter "FullyQualifiedName!~IntegrationTests"
docker compose config --quiet
```

Las pruebas de integración crean contenedores aislados de PostgreSQL/PostGIS y Redis con Testcontainers; no utilizan la base local de desarrollo. Se requiere Docker operativo y acceso inicial a las imágenes. El envío SMTP se reemplaza por un capturador en pruebas para poder verificar caducidad, reintentos y concurrencia.

- [Arquitectura y decisiones](docs/iteracion-cero/arquitectura.md)
- [Contrato y criterios de HU-ID-001](docs/iteracion-cero/HU-ID-001.md)
- [Operación y límites](docs/iteracion-cero/operacion.md)
- [Documentación del chat de origen](docs/README.md)

Esta entrega comprende el backend y la infraestructura local. El proveedor OpenID Connect queda desactivado hasta configurarlo con datos propios. Los clientes Flutter/Angular y otros contextos funcionales se desarrollarán en iteraciones posteriores.
