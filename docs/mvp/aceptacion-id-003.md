# ACE-ID-003 — Roles y permisos

Aceptación técnica y funcional local del 24 de noviembre de 2026. Fuente: fila ID-003 de la matriz MVP y criterios HU-ID-003 documentados en [Épica 1](../epica-1/README.md). Actor: Administración. Esta aceptación cubre esta funcionalidad, no la seguridad completa de producción ni las otras 43 funcionalidades.

| Criterio | Evidencia | Resultado |
|---|---|---|
| El rol inicial no concede administración y no permite elevar privilegios por HTTP | `Registration_persists_customer_role_consents_and_hashed_refresh`, `Customer_cannot_escalate_and_admin_permissions_are_loaded_from_database` | Aprobado |
| Crear y actualizar roles personalizados con permisos conocidos | Prueba `Custom_role_crud_is_idempotent_and_assigned_roles_cannot_be_deleted`; creación y actualización desde Chrome del rol `qa-ui-role-24`, sin permisos | Aprobado |
| Rechazar permisos/roles inexistentes sin escrituras parciales | `Unknown_permissions_and_roles_fail_without_mutation` | Aprobado |
| Roles de sistema inmutables; no eliminar un rol asignado | Validaciones de RoleService; regresiones de roles y prueba web del contrato `systemRole`. En Chrome, Guardar y Eliminar deshabilitados al seleccionar Administrador | Aprobado |
| Asignar y retirar conjuntos de roles con efecto inmediato usando el mismo JWT | `Customer_cannot_escalate_and_admin_permissions_are_loaded_from_database` | Aprobado |
| Mantener al menos un administrador activo al retirar rol, bloquear o cerrar cuenta | `Last_admin_is_protected_from_role_removal_blocking_and_closure` | Aprobado |
| Escrituras idempotentes con trazabilidad administrativa | Regresión de CRUD personalizado: PUT repetido no duplica auditoría; borrado repetido tolerado por API | Aprobado |
| Mostrar solo áreas autorizadas y proteger operaciones en servidor | Prueba web de visibilidad de áreas; aislamiento HTTP de roles; prueba de resumen financiero y reembolsos con Finanzas, Soporte y Operación | Aprobado |

## Verificación de interfaz

Desde la cuenta administradora ficticia existente se consultó el catálogo de roles y permisos, se creó «Rol QA sin permisos» y se actualizó a «Rol QA revisado sin permisos». No se asignó el rol a una cuenta ni se concedió acceso nuevo. Se seleccionó Administrador y se observó la deshabilitación de modificación y eliminación. [Captura](roles-sistema-protegidos.jpg).

Se añadió «Nuevo rol», confirmación del código para eliminar un rol personalizado y protección de campos de roles de sistema. No se pulsó eliminación desde la interfaz durante la prueba. El fixture sin asignaciones y sin privilegios se eliminó después por API administrativa autenticada, verificando su nombre, naturaleza no sistémica y permisos vacíos. Las restricciones y el borrado por API están cubiertos por integración.

## Validación y alcance

- 23 pruebas `EpicOneIntegrationTests` aprobadas, cero omitidas: `TestResults/acceptance-id-003.trx`.
- 35 pruebas `MvpIntegrationTests` aprobadas: `TestResults/five-ui-refund-permissions.trx`, incluyendo permisos financieros de alcance limitado.
- 11 pruebas web aprobadas y compilación Angular aprobada; logs locales en `D:/Development/PedidosYa/temp/five-ui-web-tests.log` y `five-ui-web-build.log`.

Las pruebas se ejecutan sobre base y Redis aislados; la comprobación visual usa cuentas ficticias locales. No se acredita asignación de permisos a personas reales, separación organizacional del piloto, gestión de secretos del despliegue ni aprobación de Operaciones/Producto. Esos puntos conservan sus tareas F4.
