# Ensayo local de respaldo y restauración

Ejecutado el 24 de noviembre de 2026. Alcance: entorno `pedidosya-validation`, PostgreSQL/PostGIS, documentos privados, claves de Data Protection y configuración `.env`. No se restauró encima de la aplicación ni se alteró su base original.

## Procedimiento reproducible

Desde la raíz del repositorio, con PowerShell 7 y Docker Desktop en modo Linux:

1. Detener la API local y asegurar que no existan otros escritores en el entorno de validación. El script rechaza la ejecución si el puerto 8080 continúa escuchando.
2. Ejecutar `./scripts/verify-local-restore.ps1`. El destino por defecto es `D:/Development/PedidosYa/backups`; se exige disco D y el contenedor de validación conocido.
3. El script crea una carpeta única, restringe su ACL a la cuenta actual, SYSTEM y Administradores, copia documentos, claves y configuración, y ejecuta `pg_dump -Fc` consistente con la API detenida.
4. Se crea un contenedor PostGIS con `--network none`, sin puertos publicados y con una etiqueta única. Se espera a que finalice su inicialización y se crea una base vacía desde `template0`. Así se evita el conflicto con esquemas `tiger` que trae la imagen inicial.
5. Se restaura con `pg_restore --no-owner --no-acl --exit-on-error`. Se comparan conteos de las tablas fuera de los esquemas de sistema y `public`, y se comprueba que los asientos contables estén balanceados. Las extensiones de PostGIS también se recrean desde el dump, pero sus tablas públicas no forman parte del cotejo de conteos.
6. Documentos, claves y configuración se copian a `restored/` y se verifican por SHA-256 contra respaldo y origen. Los hashes de claves/configuración no se publican en documentación ni en Git.
7. Se guarda `verification.json` local y se elimina solo el contenedor temporal cuya etiqueta coincide con el identificador de este ensayo. La carpeta de respaldo se conserva. Reiniciar la API con `./scripts/run-api.ps1 -Validation`.

El script no inicia la API ni borra respaldos. Si falla, el respaldo parcial permanece para diagnóstico; no equivale a restauración aprobada. `restore.log` conserva los errores de PostgreSQL dentro del directorio privado.

## Resultado

- Respaldo verificado: `D:/Development/PedidosYa/backups/8907bc5825eb4b2580d69b1595f5b450`.
- 89 tablas cotejadas; 19 archivos de documentos, una clave de Data Protection y un archivo de configuración privada restaurados sin diferencias.
- Contabilidad balanceada; ACL protegida; restauración aislada de la red.
- El contenedor temporal fue retirado. La API original se reinició y su disponibilidad se comprobó por separado.
- Reporte privado: `verification.json` en la carpeta indicada. Log local: `D:/Development/PedidosYa/temp/restore-local-verification.log`.

## Límites

Este es un ensayo local de recuperación de datos y archivos, no un despliegue recuperado del piloto. No se restauran roles/ACL PostgreSQL del origen: los objetos restaurados pertenecen al usuario del contenedor aislado. No se demuestra arranque de una segunda API con secretos restaurados ni descifrado funcional de cada dato protegido; se verificó identidad de los archivos de claves/configuración. Antes del piloto se deben configurar usuarios mínimos, cifrado y destino externo del respaldo, retención, prueba de recuperación de aplicación, RPO/RTO y responsables operativos. El respaldo contiene datos y secretos privados y permanece local, fuera de Git.
