# Validación — 14 de octubre de 2026

| Comprobación | Resultado |
|---|---|
| SDK .NET | 10.0.400 disponible |
| Compilación Release de los cinco proyectos | Correcta, cero errores y cero advertencias |
| Pruebas de dominio y arquitectura | 8 correctas |
| Docker Compose: validación de configuración | Correcta |
| Inicio de infraestructura Compose | Bloqueado por Docker Desktop |
| Pruebas de integración | 10 bloqueadas por error de inicialización del fixture Docker |

Las pruebas de integración se ejecutaron, pero el constructor de Testcontainers no pudo conectarse al motor. Docker respondió HTTP 500 (InternalServerError) en el endpoint npipe://./pipe/docker_engine; el contexto desktop-linux tampoco respondió correctamente. Por ello no se ejecutaron sus escenarios de negocio y no se consideran aprobadas.

La API y el envío real por Mailpit no se pudieron arrancar de extremo a extremo en este entorno. La configuración del Collector tampoco se pudo verificar dentro de un contenedor.

Los resultados de pruebas están disponibles localmente en TestResults/unit-architecture.trx y TestResults/integration.trx (ignorados en control de versiones). Esta carpeta se regenera al ejecutar las pruebas.

Después de recuperar Docker Desktop:

```powershell
docker info
docker compose up -d --build
dotnet test Delivery.slnx -c Release
```

Se crearon claves aleatorias de desarrollo en .env, sin mostrarlas en logs. No se inicializó Git ni se publicaron cambios porque la carpeta no era un repositorio Git.
