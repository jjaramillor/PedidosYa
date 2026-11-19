# Inventario de documentos generados

Este inventario reproduce lo anunciado en los mensajes del chat. No reconstruye los documentos que no se pudieron descargar.

| Documento | Estado |
|---|---|
| Alcance funcional inicial (mensaje) | Guardado completo en Markdown |
| Alcance funcional y matriz priorizada (documento referenciado) | Original disponible: Alcance_funcional_plataforma_delivery.docx |
| Backlog ejecutable del MVP (documento referenciado) | Original disponible: Backlog_MVP_plataforma_delivery.xlsx |
| Arquitectura funcional del MVP (documento referenciado) | Original disponible: Arquitectura_funcional_y_dominios_MVP_delivery.docx |
| Modelo de datos lógico por contexto | Anunciado como siguiente paso; no hay evidencia de documento generado en el historial disponible |

## Alcance y matriz priorizada

He guardado el alcance funcional y completado el siguiente paso: la matriz priorizada del producto.

El documento incluye:

- Modelo de negocio y actores.
- Aplicaciones y módulos.
- Alcance funcional por dominio.
- Ciclo de vida del pedido.
- 20 reglas de negocio.
- 64 funcionalidades identificadas y priorizadas.
- Clasificación MoSCoW.
- Separación por MVP y fases posteriores.
- Criterios de salida del MVP.
- Supuestos, exclusiones y ruta de trabajo.

:chatgpt-content-reference{index="1"}

El siguiente paso será transformar la matriz en un backlog ejecutable: **épicas, historias de usuario, criterios de aceptación, dependencias y prioridad por iteración**.

## Backlog ejecutable

He transformado la matriz en un backlog ejecutable para el MVP.

Incluye:

- 8 épicas.
- 64 historias de usuario.
- 44 historias asignadas al MVP.
- Formato estándar: **Como, quiero, para**.
- Tres escenarios de aceptación por historia en formato **Dado, Cuando, Entonces**.
- Reglas de negocio.
- Dependencias entre historias.
- Priorización MoSCoW.
- Plan propuesto de seis iteraciones.
- Campos editables para puntos y estado.
- Listas desplegables y filtros.
- Resumen del MVP y definición de terminado.

:chatgpt-content-reference{index="1"}

El siguiente paso es definir la **arquitectura funcional y los dominios del sistema**, estableciendo bounded contexts, responsabilidades, relaciones, eventos y propietarios de datos antes de diseñar la base de datos y las APIs.

## Arquitectura funcional

He completado la arquitectura funcional del MVP.

El documento contiene:

- 12 contextos funcionales delimitados.
- Clasificación entre dominios núcleo, soporte y genéricos.
- Responsabilidades y exclusiones de cada dominio.
- Propiedad exclusiva de los datos.
- Relaciones síncronas, comandos y eventos.
- Catálogo inicial de eventos de negocio.
- Flujo completo del pedido.
- Estrategia de consistencia, Outbox e idempotencia.
- Saga de cancelación y compensaciones.
- Arquitectura tecnológica recomendada.
- Decisión de comenzar con un monolito modular.
- Criterios para extraer futuros microservicios.
- Trazabilidad con las ocho épicas del backlog.
- Diagrama funcional incorporado.

:chatgpt-content-reference{index="1"}

El siguiente paso será desarrollar el **modelo de datos lógico por contexto**, identificando agregados, entidades, relaciones, estados, snapshots y límites entre esquemas.
