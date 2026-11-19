# HU-ID-001 — Registro y autenticación OTP

Documento histórico de la Iteración cero. El contrato ampliado y los criterios vigentes están en [EP-01](../epica-1/README.md).

Como cliente, quiero demostrar control de mi correo mediante un código de un solo uso para crear mi cuenta o acceder a ella sin contraseña.

Esta definición documenta la implementación actual; no sustituye el backlog original pendiente de recuperación.

## Contrato HTTP

| Método y ruta | Entrada | Éxito |
|---|---|---|
| POST /api/v1/identity/otp/request | email, displayName, acceptTerms | 202: challengeId, expiresAt |
| POST /api/v1/identity/otp/verify | challengeId, code | 200: accessToken, expiresAt, user |
| GET /api/v1/identity/me | JWT Bearer | 200: id, email, displayName |

Errores: 400 para datos inválidos, 401 para desafío desconocido/vencido/consumido/bloqueado, 429 por espera de reenvío o límite por IP. Las respuestas de error usan application/problem+json.

## Reglas implementadas

- Correo normalizado y validado; máximo 254 caracteres.
- Nombre obligatorio, máximo 100 caracteres; aceptación de términos obligatoria.
- Código aleatorio criptográfico de seis dígitos; válido por cinco minutos.
- Hash HMAC-SHA256 vinculado al identificador del desafío, comparado en tiempo constante.
- Máximo cinco verificaciones incorrectas por desafío.
- Espera mínima de sesenta segundos entre solicitudes por correo, persistida en PostgreSQL.
- Una nueva solicitud invalida desafíos anteriores pendientes.
- Límite HTTP de veinte solicitudes OTP por IP y minuto, compartido entre solicitud/verificación dentro del proceso.
- El usuario se crea al verificar el correo, nunca al solicitar el código.
- Restricción única por correo; verificación concurrente admite un único éxito.
- JWT firmado HS256, quince minutos, emisor/audiencia validados y tolerancia de reloj de cinco segundos.
- Los tokens no incluyen roles administrativos; este slice solo crea clientes.
- El código no aparece en las respuestas ni logs. El contenido del correo pendiente se protege con ASP.NET Data Protection.
- Nuevos registros emiten identity.user-registered.v1; inicios de sesión posteriores reutilizan el usuario.

## Criterios de aceptación

1. **Dado** un correo válido, nombre y aceptación, **cuando** solicito acceso, **entonces** recibo un identificador y un correo OTP; todavía no existe un usuario nuevo.
2. **Dado** un código vigente, **cuando** lo verifico, **entonces** se crea la cuenta o se recupera la existente, se entrega JWT y se consume el código.
3. **Dado** un código consumido o vencido, **cuando** lo verifico, **entonces** recibo 401 sin crear usuarios ni emitir otro evento de registro.
4. **Dado** un desafío vigente, **cuando** fallo cinco veces, **entonces** incluso el código correcto deja de ser aceptado.
5. **Dado** un desafío pendiente, **cuando** solicito uno nuevo después del cooldown, **entonces** el anterior queda invalidado.
6. **Dadas** solicitudes o verificaciones concurrentes, **cuando** compiten por el mismo correo/desafío, **entonces** se mantiene el cooldown y hay un solo registro/verificación exitosa.
7. **Dado** un fallo del transporte, **cuando** se procesa Outbox, **entonces** Inbox se revierte y el mensaje queda disponible para reintento.
8. **Dado** un JWT válido, **cuando** consulto /me, **entonces** recibo mi perfil; sin JWT recibo 401.

## Alcance posterior

SMS, identidad federada, recuperación mediante otro factor, refresh tokens, revocación de sesiones, cambio de correo y administración de roles quedan para siguientes historias. La API necesita HTTPS en su despliegue real.
