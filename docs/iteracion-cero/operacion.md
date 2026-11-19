# Operación local y límites

Documento histórico de la Iteración cero. Sesiones, permisos y el limitador distribuido se ampliaron en [EP-01](../epica-1/api.md).

## Configuración

.env es local y está ignorado. .env.example contiene únicamente valores de ejemplo. La API valida longitud mínima de las claves JWT/OTP al iniciar y exige SMTP TLS fuera de Development. No publica códigos OTP mediante endpoints.

Las claves de Data Protection se conservan en el volumen api-keys para que un reinicio no vuelva ilegibles los mensajes pendientes. En ejecución nativa se guardan en src/Delivery.Api/keys (ignorado). Para producción se debe proporcionar un almacén compartido y proteger estas claves en reposo con un certificado o servicio de claves. El volumen local por sí solo no ofrece ese cifrado en reposo.

Compose es infraestructura de desarrollo: Mailpit no entrega a destinatarios externos, Jaeger utiliza almacenamiento efímero y la red interna usa tráfico sin TLS. Para un entorno real configurar un proveedor SMTP, TLS, secretos propios, respaldo y acceso restringido de la base/Redis.

## Mensajería

- Mensajes y estados viven en PostgreSQL; Redis no es el broker.
- Reintentos exponenciales de 2 a 300 segundos, hasta diez intentos.
- Consultar mensajes agotados: `SELECT id,type,attempts,last_error FROM platform.outbox WHERE processed_at IS NULL AND attempts >= 10;`.
- Tras corregir la causa, el operador puede reprogramar el mensaje por su id: restablecer attempts y next_attempt_at. Un OTP vencido no se vuelve a enviar; solicitar uno nuevo.
- El payload se vacía al finalizar. Los pendientes contienen datos cifrados de correo/OTP.
- Inbox conserva identificadores para deduplicación; no eliminar filas mientras puedan reproducirse los mensajes correspondientes.
- La retención automática de desafíos/eventos y el panel de mensajes fallidos todavía no están implementados. Antes de operar a volumen definir políticas de retención y limpieza.

## Escalado

La espera de reenvío, límite de intentos, consumo único y bloqueo por correo están persistidos y funcionan entre instancias. El límite por IP de ASP.NET Core es local al proceso; antes de escalar o exponer públicamente complementar con un limitador distribuido/gateway y configuración explícita de proxies de confianza. Redis está conectado y comprobado por readiness, reservado para futuros usos de caché/limitación distribuida.

La transacción del dispatcher permanece abierta durante SMTP. Es adecuado para el volumen inicial; a mayor escala usar leases y transporte con idempotency key para reducir tiempo de bloqueos. Duplicados de correo tras caídas son posibles por entrega al menos una vez.

## Pruebas

Las pruebas HTTP usan el host real con base PostGIS y Redis aislados. Sustituyen solo el reloj y transporte de correo; el worker se desactiva mediante Messaging:RunWorker=false y se invoca explícitamente para verificar sus transacciones. Esta opción se usa para pruebas o procesos separados; en Compose el worker permanece activo.

La configuración y las versiones de paquetes están fijadas en csproj. Ejecutar build con warnings como errores. Las pruebas de arquitectura restringen dependencias entre proyectos y mantienen el dominio libre de infraestructura.

Los resultados de ejecución y cualquier bloqueo del entorno se registran en validacion.md.
