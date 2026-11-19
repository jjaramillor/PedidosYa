# API y operación de EP-01

Todas las rutas siguientes comienzan con /api/v1/identity. OpenAPI se publica en /openapi/v1.json cuando ASPNETCORE_ENVIRONMENT=Development.

| Método | Ruta | Acceso | Resultado |
|---|---|---|---|
| GET | /policies | Público | Versiones de términos, privacidad y marketing |
| POST | /otp/request | Público, limitado | Desafío de registro/login OTP |
| POST | /otp/verify | Público, limitado | Sesión y tokens |
| POST | /login | Público, limitado | Desafío para cuenta existente |
| POST | /recovery | Público, limitado | Desafío para recuperar acceso y revocar sesiones |
| POST | /sessions/refresh | Refresh token, limitado | JWT y refresh nuevo |
| GET | /sessions | Sesión activa | Sesiones propias |
| DELETE | /sessions/{id} | Sesión activa | Revoca solo una sesión propia |
| POST | /logout | Sesión activa | Revoca la sesión actual |
| POST | /logout-all | Sesión activa | Revoca todas las sesiones propias |
| GET | /me | Sesión activa | Identidad propia |
| GET | /access | Sesión activa | Roles y permisos vigentes |
| GET | /consents | Sesión activa | Historial propio, hasta 100 registros |
| PUT | /consents | Sesión activa | Guarda estado de consentimiento |
| PATCH | /account | Sesión activa | Cambia nombre técnico |
| POST | /account/deactivate | Sesión activa | Desactiva cuenta y revoca sesiones |
| DELETE | /account | Sesión activa | Cierra y anonimiza Identity |
| GET | /admin/permissions | identity.roles.manage | Permisos disponibles |
| GET/PUT/DELETE | /admin/roles y /admin/roles/{id} | identity.roles.manage | Administración de roles |
| GET/PUT | /admin/users/{id}/roles | identity.roles.manage | Roles de una cuenta |
| PUT | /admin/users/{id}/status | identity.users.manage | active, blocked o deactivated |
| GET | /admin/audit?limit=50 | identity.audit.read | Auditoría, límite 1 a 200 |
| POST | /federation/start | Público, limitado | URL de autorización y cookie state |
| GET | /federation/callback?state=...&code=... | Cookie del navegador, limitado | Sesión federada |
| POST | /federation/complete | Cookie del navegador, limitado | Variante de finalización con JSON |

## Entradas y respuestas

Registro:

```json
{"email":"ana@example.test","displayName":"Ana","acceptTerms":true,"acceptPrivacy":true,"deviceName":"Web","termsVersion":"2026-10","privacyVersion":"2026-10"}
```

Login/recuperación:

```json
{"email":"ana@example.test","deviceName":"Web"}
```

El 202 devuelve challengeId/expiresAt. La verificación recibe challengeId/code. La respuesta de sesión contiene accessToken, expiresAt, user, refreshToken y sessionId. Mantener los tokens fuera de URLs y logs; el frontend debe guardarlos en un mecanismo adecuado para su plataforma.

Refresh recibe refreshToken. Consentimiento recibe kind/version/accepted. Nombre de cuenta recibe displayName. Crear/actualizar rol recibe name/permissions; asignación recibe roles. Las rutas protegidas utilizan Authorization: Bearer.

Errores usan Problem Details con code, correlationId y traceId; validación de correo, registro y formato OTP añade errors por campo. Los códigos de uso único desconocidos, vencidos o consumidos no distinguen la causa.

Redis aplica un límite compartido de veinte solicitudes de autenticación por IP y sesenta segundos. ASP.NET añade un límite local equivalente. Cooldown y límites OTP permanecen en PostgreSQL. Si Redis no está disponible, el acceso falla cerrado con 503. No se confía en X-Forwarded-For sin configuración explícita de proxy.

## Primer administrador

1. Registrar y verificar la cuenta administradora por OTP.
2. Ejecutar en el servidor, con acceso confiable a configuración y base:

```powershell
./scripts/run-api.ps1 -BootstrapAdminEmail admin@example.test
```

El proceso aplica migraciones, asigna admin con auditoría y termina sin iniciar HTTP. No crea cuentas ni verifica correos en nombre del operador. Repetir la operación no duplica el rol.

## Configurar OpenID Connect

Configuración de entorno de ejemplo, usando datos propios del proveedor:

```text
Identity__Federation__Enabled=true
Identity__Federation__Authority=https://issuer.example
Identity__Federation__ClientId=delivery-client
Identity__Federation__ClientSecret=<secreto si el cliente es confidencial>
Identity__Federation__RedirectUri=https://api.example/api/v1/identity/federation/callback
```

Registrar exactamente ese callback en el proveedor. La autoridad debe exponer discovery y JWKS y entregar un ID token con correo verificado. Se aceptan firmas RS256/RS384/RS512/ES256; se rechazan tokens sin firma y HMAC externo.

El frontend hace POST /federation/start con displayName, acceptTerms, acceptPrivacy y versiones opcionales; después navega a authorizationUrl. El callback valida la cookie y devuelve la sesión en el cuerpo JSON, sin redirigir tokens. Para un BFF o cliente móvil será necesario adaptar la entrega de sesión a su interfaz conservando state, nonce y PKCE.

## Límites de operación

- Persistir y proteger claves Data Protection y claves JWT/OTP. Cambiar OtpKey invalida desafíos pendientes.
- HTTPS es necesario para desplegar el flujo de autenticación.
- Marketing es opcional. El backend registra versiones; los textos reales de políticas deben ser presentados por la interfaz del producto.
- El cierre afecta a Identity. Datos de Pedidos/Pagos u otros contextos requieren políticas e integración propias cuando esos módulos existan.
- No se incluye una tarea automática de retención de desafíos, tokens consumidos, flujos y auditoría. Definir retención antes de operar a volumen.
- Los permisos son globales; permisos por sucursal/tenant se añadirán con los módulos correspondientes.

## Referencias de implementación

- [JWT bearer en ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0).
- [OpenID Connect Core](https://openid.net/specs/openid-connect-core-1_0.html).
- [RFC 9700 y rotación de refresh tokens](https://datatracker.ietf.org/doc/html/rfc9700/).

