# EP-01 Identidad y acceso

Revisión del 14 de octubre de 2026. Fuente: [backlog original](../Backlog_MVP_plataforma_delivery.xlsx), hoja Backlog, filas 2 a 6. La extracción literal, incluidos todos los criterios, está en [backlog-original.json](backlog-original.json), con el hash SHA-256 del archivo fuente.

Se implementa la épica en el backend .NET. Las interfaces Flutter/Angular y la publicación en un proveedor externo no forman parte del código existente de esta iteración.

| HU | Título original | Prioridad y fase del backlog | Implementación |
|---|---|---|---|
| HU-ID-001 | Registro y verificación OTP | Debe, MVP | Validación de datos/políticas, OTP de un solo uso, cuenta verificada, auditoría y Outbox |
| HU-ID-002 | Inicio y recuperación de sesión | Debe, MVP | Login y recuperación por OTP, JWT, refresh rotativo, listado y revocación de sesiones |
| HU-ID-003 | Roles y permisos | Debe, MVP | RBAC, roles configurables, permisos definidos, asignación a usuarios y administración protegida |
| HU-ID-004 | Identidad federada | Podría, Fase 2 | OpenID Connect con autorización por código, PKCE, state, nonce y firma verificada |
| HU-ID-005 | Consentimiento y gestión de cuenta | Debe, MVP | Historial de consentimiento, nombre técnico, bloqueo/desactivación/cierre de cuenta |

HU-ID-004 se incluye por la solicitud de implementar toda la épica, aunque el backlog la ubica en Fase 2. El Excel original se conserva sin modificar sus estados Propuesto.

Los criterios originales son generales: éxito con trazabilidad, rechazo de datos inválidos y prevención de duplicidades/accesos indebidos. Se concretan abajo en reglas observables y pruebas.

## HU-ID-001 Registro y verificación OTP

La solicitud requiere email válido, displayName de hasta 100 caracteres, acceptTerms y acceptPrivacy. El cliente puede enviar termsVersion y privacyVersion obtenidos de /policies; versiones antiguas se rechazan antes de modificar datos. El desafío conserva ambas versiones aceptadas hasta la verificación.

El código tiene seis dígitos, dura cinco minutos y permite cinco fallos. Se calcula un HMAC vinculado al desafío. Hay cooldown persistido de sesenta segundos por correo; un nuevo desafío invalida el anterior. Usuario, consentimientos iniciales, rol customer, sesión y evento UserRegistered se confirman atómicamente al verificar.

Pruebas: IdentityIntegrationTests y Registration_persists_customer_role_consents_and_hashed_refresh. Cubren caducidad, reenvío, concurrencia, uso único, rollback y cifrado del contenido pendiente.

## HU-ID-002 Inicio y recuperación de sesión

/login solo autentica usuarios activos existentes. Para cuentas desconocidas o inactivas se devuelve la misma respuesta 202 con un desafío no elegible, sin correo ni creación de usuario. /recovery usa OTP y, al verificar, revoca todas las sesiones anteriores antes de crear una nueva.

Cada sesión tiene un id y una expiración absoluta de treinta días. Los JWT duran quince minutos y contienen sub/sid/jti. Cada acceso protegido comprueba sesión y estado de cuenta en PostgreSQL; cerrar sesión invalida inmediatamente el JWT restante.

Refresh tokens son aleatorios de 256 bits y se guardan como SHA-256. Cada recuperación rota el token. Reutilizar uno consumido revoca toda esa sesión, incluso el token emitido por una recuperación concurrente. El cliente debe serializar refresh y no repetir un token después de un resultado incierto; en ese caso puede recuperar acceso mediante OTP.

Pruebas: Recovery_revokes_old_sessions_and_creates_one_new_session, Refresh_rotates_and_reuse_revokes_the_session_family, Concurrent_refresh_detects_reuse_without_minting_multiple_active_families, Expired_session_cannot_be_refreshed_or_validated y Revocation_is_immediate_and_cannot_revoke_another_users_session.

## HU-ID-003 Roles y permisos

customer es el rol inicial sin permisos administrativos. admin tiene identity.roles.manage, identity.users.manage e identity.audit.read. El operador inicializa el primer administrador mediante CLI para una cuenta previamente verificada; no existe un endpoint anónimo de elevación.

Se permite crear, actualizar y eliminar roles personalizados, usar únicamente permisos conocidos y asignar conjuntos de roles a usuarios activos. Los roles del sistema no se editan/eliminan. Un rol asignado no se elimina y debe quedar al menos un admin activo al quitar roles, bloquear, desactivar o cerrar cuentas.

Los permisos se consultan en cada solicitud, sin confiar en permisos históricos del JWT. Cambios de permisos afectan al acceso con el mismo token. Las escrituras repiten el control de permiso dentro de la transacción administrativa. PUT de un conjunto igual no agrega otra auditoría/evento.

Pruebas: Customer_cannot_escalate_and_admin_permissions_are_loaded_from_database, Last_admin_is_protected_from_role_removal_blocking_and_closure, Custom_role_crud_is_idempotent_and_assigned_roles_cannot_be_deleted y Unknown_permissions_and_roles_fail_without_mutation.

## HU-ID-004 Identidad federada

El proveedor está desactivado por defecto. Se configura una autoridad HTTPS autorizada, client id, secreto si corresponde y callback HTTPS fijo. El cliente inicia un flujo con aceptación de políticas; el backend genera state, nonce y PKCE S256 y conserva el verifier cifrado.

La respuesta debe volver al mismo navegador mediante una cookie HttpOnly de cinco minutos. El state se consume antes del intercambio externo para impedir repetición. El código se intercambia en el endpoint obtenido por discovery. Se valida firma asimétrica, emisor, audiencia, vigencia, nonce, sub, iat, azp cuando corresponde y email_verified.

La cuenta externa se identifica por issuer y subject. Un correo que ya pertenece a una cuenta local no se vincula silenciosamente: se devuelve 409 y se indica entrar por OTP. Nuevas cuentas federadas reciben customer y sus consentimientos; ninguna recibe admin.

Pruebas: FederationSecurityTests verifica discovery/JWKS/intercambio con transporte HTTP controlado y tokens RSA, además de rechazo de emisor/audiencia/nonce/vigencia/correo incorrectos. Las pruebas con base comprueban state, vínculo al navegador, repetición, expiración y colisión de correo.

No se ha configurado ni probado un proveedor comercial real. Esa verificación necesita datos propios del proveedor y un callback HTTPS accesible.

## HU-ID-005 Consentimiento y gestión de cuenta

Se conserva un historial inmutable de terms, privacy y marketing con versión, aceptación y fecha. Actualizar el mismo estado es idempotente. Retirar marketing mantiene la cuenta activa; retirar términos o privacidad desactiva la cuenta y revoca sesiones. Bloquear/desactivar también invalida OTP pendientes. Un admin puede reactivar/bloquear cuentas abiertas; reactivar no resucita sesiones ni códigos anteriores.

El usuario puede cambiar su nombre técnico, desactivar su cuenta o cerrarla. Cerrar anonimiza email/nombre de Identity, elimina vínculos externos y desafíos, limpia correos OTP pendientes y revoca sesiones. No modifica perfiles ni tablas de otros contextos; publica account-closed para futuras integraciones. Auditoría y registros de consentimiento conservan el identificador técnico anonimizado.

Pruebas: Consent_is_versioned_idempotent_and_marketing_can_be_withdrawn, Withdrawing_required_consent_deactivates_and_revokes_sessions, Blocking_revokes_access_and_reactivation_requires_new_login, Account_name_can_change_without_changing_commercial_profile y Closing_account_anonymizes_identity_clears_challenges_and_revokes_access.

## Persistencia, contratos y validación

La migración identity-002 amplía el baseline sin modificar identity-001. Conserva usuarios anteriores, asigna customer y migra la aceptación de términos existente; no inventa aceptación histórica de privacidad. Invalida los desafíos anteriores que no capturaban privacidad explícita. Los JWT antiguos sin sid dejan de ser válidos: se requiere iniciar sesión de nuevo.

- [Contrato HTTP y configuración](api.md).
- [Resultados y límites de validación](validacion.md).
- [Arquitectura original](../Arquitectura_funcional_y_dominios_MVP_delivery.docx).

La revisión incorpora el principio original de propiedad de datos: Identity conserva cuentas técnicas y acceso. El perfil comercial del cliente seguirá perteneciendo a Clientes y direcciones.
