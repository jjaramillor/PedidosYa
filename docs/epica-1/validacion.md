# Validación de EP-01

Fecha: 14 de octubre de 2026.

| Comprobación | Resultado |
|---|---|
| Revisión del backlog original | 5 HU de EP-01, filas 2 a 6 |
| Compilación Release de toda la solución | Correcta, cero errores y cero advertencias |
| Formato de código | Aplicado con dotnet format |
| Dominio, arquitectura y seguridad OIDC | 21 pruebas correctas |
| Integración PostgreSQL/PostGIS, Redis y HTTP | 32 escenarios bloqueados antes de ejecutarse |
| Configuración Compose | Válida |
| Proveedor OIDC comercial | Pendiente de datos propios y callback HTTPS |

Las 21 pruebas ejecutadas incluyen validación de firma RSA, issuer, audience, nonce, iat, azp, caducidad y correo verificado; discovery/JWKS y el intercambio de código con PKCE se probaron con un transporte HTTP controlado. No equivalen a una prueba con Google, Microsoft u otro proveedor comercial.

Las 32 pruebas de integración están implementadas (10 de la base y 22 de esta épica), pero Testcontainers recibió HTTP 500 de Docker Desktop al conectar con npipe://./pipe/docker_engine. El fixture no pudo construirse: los escenarios de SQL, HTTP y Redis no llegaron a ejecutarse. No se consideran aprobados y no se da por verificada la épica de extremo a extremo.

Se solicitó recuperar Docker Desktop durante la implementación. Una vez operativo:

```powershell
docker info
dotnet test Delivery.slnx -c Release
docker compose up -d --build
```

Para repetir las comprobaciones independientes de Docker:

```powershell
dotnet build Delivery.slnx -c Release
dotnet test Delivery.slnx -c Release --no-build --filter "FullyQualifiedName!~IntegrationTests"
docker compose config --quiet
```

Resultados locales: TestResults/epic-one-security.trx y TestResults/epic-one-integration.trx, ignorados en Git. Las pruebas de integración utilizan contenedores propios y no borran la base de desarrollo.

## Alcance que queda por verificar

- Aplicación de identity-002 en PostgreSQL, incluidos upgrade e idempotencia.
- Sesiones rotativas, revocación, recuperación y concurrencia contra persistencia real.
- RBAC, protección del último administrador y permisos dinámicos.
- Consentimientos, cierre de cuenta y efectos Outbox/Inbox.
- Cookie/state y persistencia del flujo federado.
- Límite compartido de Redis y respuesta ante indisponibilidad.
- Inicio de la infraestructura local, Mailpit y Collector.

El código corresponde al backend. No se han creado interfaces Flutter/Angular ni conectado un proveedor comercial de identidad.
